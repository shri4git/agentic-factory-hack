using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;
using RepairPlanner.Services;

namespace RepairPlanner;

// Primary constructor - parameters become fields (like Python's __init__)
public sealed class RepairPlannerAgent(
    AIProjectClient projectClient,
    CosmosDbService cosmosDb,
    IFaultMappingService faultMapping,
    string modelDeploymentName,
    ILogger<RepairPlannerAgent> logger)
{
    private const string AgentName = "RepairPlannerAgent";
    private const string AgentInstructions = """
        You are a Repair Planner Agent for tire manufacturing equipment.
        Generate a repair plan with tasks, timeline, and resource allocation.
        Return the response as valid JSON matching the WorkOrder schema.

        Output JSON with these fields:
        - workOrderNumber, machineId, title, description
        - type: "corrective" | "preventive" | "emergency"
        - priority: "critical" | "high" | "medium" | "low"
        - status, assignedTo (technician id or null), notes
        - estimatedDuration: integer (minutes, e.g. 60 not "60 minutes")
        - requiredParts: [{ partNumber, partName, quantity, isAvailable }]
        - partsUsed: [{ partId, partNumber, quantity }]
        - tasks: [{ sequence, title, description, estimatedDurationMinutes (integer), requiredSkills, safetyNotes }]

        IMPORTANT: All duration fields must be integers representing minutes (e.g. 90), not strings.

        Rules:
        - Assign the most qualified available technician from the provided candidates
        - Include only relevant parts; use an empty array if none are needed
        - Tasks must be ordered, actionable, and safe
        - Return only valid JSON without markdown code fences
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task EnsureAgentVersionAsync(CancellationToken ct = default)
    {
        logger.LogInformation(
            "Creating agent '{AgentName}' with model '{ModelDeploymentName}'",
            AgentName,
            modelDeploymentName);

        var definition = new PromptAgentDefinition(model: modelDeploymentName)
        {
            Instructions = AgentInstructions
        };

        var result = await projectClient.Agents.CreateAgentVersionAsync(
            AgentName,
            new AgentVersionCreationOptions(definition),
            ct);

        logger.LogInformation(
            "Agent version ready for '{AgentName}'",
            AgentName);
    }

    public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(
        DiagnosedFault fault,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fault);

        logger.LogInformation(
            "Planning repair for {MachineId}, fault={FaultType}",
            fault.MachineId,
            fault.FaultType);

        var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
        var requiredPartNumbers = faultMapping.GetRequiredParts(fault.FaultType);

        var technicians = await cosmosDb.GetAvailableTechniciansWithSkillsAsync(requiredSkills, ct);
        var availableParts = await cosmosDb.GetPartsInventoryAsync(requiredPartNumbers, ct);
        var selectedTechnician = SelectBestTechnician(technicians, requiredSkills);

        var prompt = BuildPrompt(fault, requiredSkills, requiredPartNumbers, technicians, availableParts, selectedTechnician);

        logger.LogInformation("Invoking agent '{AgentName}'", AgentName);

        var agent = projectClient.GetAIAgent(name: AgentName);
        var response = await agent.RunAsync(prompt, thread: null, options: null, cancellationToken: ct);
        var responseText = response.Text;

        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new InvalidOperationException("The repair planner agent returned an empty response.");
        }

        var workOrder = ParseWorkOrder(responseText);
        NormalizeWorkOrder(workOrder, fault, requiredSkills, requiredPartNumbers, availableParts, selectedTechnician);

        await cosmosDb.CreateWorkOrderAsync(workOrder, ct);
        return workOrder;
    }

    private static Technician? SelectBestTechnician(
        IReadOnlyList<Technician> technicians,
        IReadOnlyList<string> requiredSkills)
    {
        return technicians
            .OrderByDescending(technician => technician.Skills.Count(skill => requiredSkills.Contains(skill, StringComparer.OrdinalIgnoreCase)))
            .ThenByDescending(technician => technician.Certifications.Count)
            .ThenBy(technician => technician.CurrentAssignments.Count)
            .FirstOrDefault();
    }

    private static string BuildPrompt(
        DiagnosedFault fault,
        IReadOnlyList<string> requiredSkills,
        IReadOnlyList<string> requiredPartNumbers,
        IReadOnlyList<Technician> technicians,
        IReadOnlyList<Part> availableParts,
        Technician? selectedTechnician)
    {
        var promptContext = new
        {
            fault = new
            {
                fault.MachineId,
                fault.FaultType,
                fault.RootCause,
                fault.Severity,
                fault.DetectedAt,
                fault.Metadata,
            },
            requiredSkills,
            requiredPartNumbers,
            candidateTechnicians = technicians.Select(technician => new
            {
                technician.Id,
                technician.Name,
                technician.Role,
                technician.Skills,
                technician.Certifications,
                technician.CurrentAssignments,
                technician.ShiftSchedule,
            }),
            availableParts = availableParts.Select(part => new
            {
                part.Id,
                part.PartNumber,
                part.Name,
                part.QuantityInStock,
                part.Location,
                part.UnitCost,
                part.LeadTimeDays,
            }),
            recommendedTechnicianId = selectedTechnician?.Id,
        };

        var serializedContext = JsonSerializer.Serialize(promptContext, JsonOptions);

        return $"""
            Create a repair work order for the following diagnosed fault.

            Context:
            {serializedContext}

            Requirements:
            - Use machineId and faultType from the provided fault
            - Choose assignedTo from the candidate technicians when possible
            - Use requiredParts for planned parts and keep partsUsed empty unless consumption is already certain
            - estimatedDuration must equal the sum of task durations when possible
            - Keep status as \"new\"
            - Return only valid JSON
            """;
    }

    private static WorkOrder ParseWorkOrder(string responseText)
    {
        var sanitizedResponse = responseText.Trim();

        if (sanitizedResponse.StartsWith("```") && sanitizedResponse.EndsWith("```"))
        {
            var lines = sanitizedResponse.Split('\n');
            sanitizedResponse = string.Join('\n', lines.Skip(1).SkipLast(1)).Trim();
        }

        var workOrder = JsonSerializer.Deserialize<WorkOrder>(sanitizedResponse, JsonOptions);
        if (workOrder is null)
        {
            throw new InvalidOperationException("The repair planner agent returned JSON that could not be parsed into a work order.");
        }

        return workOrder;
    }

    private static void NormalizeWorkOrder(
        WorkOrder workOrder,
        DiagnosedFault fault,
        IReadOnlyList<string> requiredSkills,
        IReadOnlyList<string> requiredPartNumbers,
        IReadOnlyList<Part> availableParts,
        Technician? selectedTechnician)
    {
        workOrder.MachineId = string.IsNullOrWhiteSpace(workOrder.MachineId) ? fault.MachineId : workOrder.MachineId;
        workOrder.FaultType ??= fault.FaultType;
        workOrder.WorkOrderNumber = string.IsNullOrWhiteSpace(workOrder.WorkOrderNumber)
            ? GenerateWorkOrderNumber()
            : workOrder.WorkOrderNumber;
        workOrder.Title = string.IsNullOrWhiteSpace(workOrder.Title)
            ? $"Repair {ToTitle(fault.FaultType)}"
            : workOrder.Title;
        workOrder.Description = string.IsNullOrWhiteSpace(workOrder.Description)
            ? $"Repair required for {fault.MachineId}. Root cause: {fault.RootCause}."
            : workOrder.Description;
        workOrder.Type = NormalizeType(workOrder.Type, fault.Severity);
        workOrder.Priority = NormalizePriority(workOrder.Priority, fault.Severity);
        workOrder.Status = string.IsNullOrWhiteSpace(workOrder.Status) ? "new" : workOrder.Status.ToLowerInvariant();
        workOrder.AssignedTo ??= selectedTechnician?.Id;
        // ??= means "assign if null" (like Python's: x = x or default_value)
        workOrder.AssignedTechnician ??= workOrder.AssignedTo;
        workOrder.CreatedDate ??= DateTimeOffset.UtcNow;
        // ?? means "if null, use this instead" (like Python's "or")
        workOrder.CreatedAt ??= workOrder.CreatedDate;
        workOrder.RequiredParts = MergeRequiredParts(workOrder.RequiredParts, requiredPartNumbers, availableParts);
        workOrder.PartsUsed = MergeUsedParts(workOrder.PartsUsed, availableParts);
        workOrder.Tasks = NormalizeTasks(workOrder.Tasks, requiredSkills, fault);

        if (workOrder.EstimatedDuration <= 0)
        {
            workOrder.EstimatedDuration = workOrder.Tasks.Sum(task => task.EstimatedDurationMinutes);
        }

        if (workOrder.EstimatedDuration <= 0)
        {
            workOrder.EstimatedDuration = 60;
        }
    }

    private static List<WorkOrderPartUsage> MergeRequiredParts(
        IReadOnlyList<WorkOrderPartUsage> existingParts,
        IReadOnlyList<string> requiredPartNumbers,
        IReadOnlyList<Part> availableParts)
    {
        var existingByPartNumber = existingParts
            .Where(part => !string.IsNullOrWhiteSpace(part.PartNumber))
            .ToDictionary(part => part.PartNumber!, StringComparer.OrdinalIgnoreCase);

        var partsByNumber = availableParts.ToDictionary(part => part.PartNumber, StringComparer.OrdinalIgnoreCase);

        var merged = new List<WorkOrderPartUsage>();
        foreach (var partNumber in requiredPartNumbers)
        {
            existingByPartNumber.TryGetValue(partNumber, out var existing);
            partsByNumber.TryGetValue(partNumber, out var inventoryPart);

            merged.Add(new WorkOrderPartUsage
            {
                PartId = inventoryPart?.Id ?? existing?.PartId,
                PartNumber = partNumber,
                PartName = inventoryPart?.Name ?? existing?.PartName,
                Quantity = existing?.Quantity > 0 ? existing.Quantity : 1,
                IsAvailable = inventoryPart?.QuantityInStock > 0,
            });
        }

        return merged;
    }

    private static List<WorkOrderPartUsage> MergeUsedParts(
        IReadOnlyList<WorkOrderPartUsage> existingParts,
        IReadOnlyList<Part> availableParts)
    {
        if (existingParts.Count == 0)
        {
            return [];
        }

        var partsByNumber = availableParts.ToDictionary(part => part.PartNumber, StringComparer.OrdinalIgnoreCase);

        return existingParts.Select(existing =>
        {
            Part? inventoryPart = null;
            if (!string.IsNullOrWhiteSpace(existing.PartNumber))
            {
                partsByNumber.TryGetValue(existing.PartNumber, out inventoryPart);
            }

            return new WorkOrderPartUsage
            {
                PartId = existing.PartId ?? inventoryPart?.Id,
                PartNumber = existing.PartNumber ?? inventoryPart?.PartNumber,
                PartName = existing.PartName ?? inventoryPart?.Name,
                Quantity = existing.Quantity,
                IsAvailable = existing.IsAvailable ?? (inventoryPart?.QuantityInStock > 0),
            };
        }).ToList();
    }

    private static List<RepairTask> NormalizeTasks(
        IReadOnlyList<RepairTask> tasks,
        IReadOnlyList<string> requiredSkills,
        DiagnosedFault fault)
    {
        if (tasks.Count == 0)
        {
            return
            [
                new RepairTask
                {
                    Sequence = 1,
                    Title = $"Investigate {ToTitle(fault.FaultType)}",
                    Description = $"Inspect the machine, confirm the root cause, and perform the required repair for {fault.FaultType}.",
                    EstimatedDurationMinutes = 60,
                    RequiredSkills = requiredSkills.ToList(),
                    SafetyNotes = ["Apply lockout/tagout before starting work."]
                }
            ];
        }

        return tasks
            .OrderBy(task => task.Sequence <= 0 ? int.MaxValue : task.Sequence)
            .Select((task, index) => new RepairTask
            {
                Sequence = index + 1,
                Title = string.IsNullOrWhiteSpace(task.Title) ? $"Repair step {index + 1}" : task.Title,
                Description = task.Description,
                EstimatedDurationMinutes = task.EstimatedDurationMinutes > 0 ? task.EstimatedDurationMinutes : 30,
                RequiredSkills = task.RequiredSkills.Count == 0 ? requiredSkills.ToList() : task.RequiredSkills,
                SafetyNotes = task.SafetyNotes.Count == 0
                    ? ["Apply lockout/tagout before starting work."]
                    : task.SafetyNotes,
            })
            .ToList();
    }

    private static string NormalizeType(string? type, string severity)
    {
        var normalizedType = type?.Trim().ToLowerInvariant();
        if (normalizedType is "corrective" or "preventive" or "emergency")
        {
            return normalizedType;
        }

        return severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)
            ? "emergency"
            : "corrective";
    }

    private static string NormalizePriority(string? priority, string severity)
    {
        var normalizedPriority = priority?.Trim().ToLowerInvariant();
        if (normalizedPriority is "critical" or "high" or "medium" or "low")
        {
            return normalizedPriority;
        }

        return severity.Trim().ToLowerInvariant() switch
        {
            "critical" => "critical",
            "high" => "high",
            "medium" => "medium",
            "low" => "low",
            _ => "medium",
        };
    }

    private static string GenerateWorkOrderNumber()
    {
        return $"WO-{DateTime.UtcNow:yyyy}-{Random.Shared.Next(1, 1000):000}";
    }

    private static string ToTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown Fault";
        }

        var builder = new StringBuilder(value.Length);
        builder.Append(char.ToUpperInvariant(value[0]));

        for (var index = 1; index < value.Length; index++)
        {
            var current = value[index];
            builder.Append(current == '_' ? ' ' : current);
        }

        return builder.ToString();
    }
}