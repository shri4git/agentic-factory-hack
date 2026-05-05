using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;

namespace RepairPlanner.Services;

public sealed class CosmosDbService
{
    private const string TechniciansContainerName = "Technicians";
    private const string PartsInventoryContainerName = "PartsInventory";
    private const string WorkOrdersContainerName = "WorkOrders";

    private readonly CosmosClient _client;
    private readonly Container _techniciansContainer;
    private readonly Container _partsContainer;
    private readonly Container _workOrdersContainer;
    private readonly ILogger<CosmosDbService> _logger;

    public CosmosDbService(string endpoint, string key, string databaseName, ILogger<CosmosDbService> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = new CosmosClient(endpoint, key);

        var database = _client.GetDatabase(databaseName);
        _techniciansContainer = database.GetContainer(TechniciansContainerName);
        _partsContainer = database.GetContainer(PartsInventoryContainerName);
        _workOrdersContainer = database.GetContainer(WorkOrdersContainerName);
    }

    public async Task<List<Technician>> GetAvailableTechniciansWithSkillsAsync(
        IReadOnlyList<string> requiredSkills,
        CancellationToken cancellationToken = default)
    {
        var normalizedSkills = requiredSkills
            .Where(static skill => !string.IsNullOrWhiteSpace(skill))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        try
        {
            var queryText = "SELECT * FROM c WHERE c.available = true";
            var queryDefinition = new QueryDefinition(queryText);

            for (var index = 0; index < normalizedSkills.Length; index++)
            {
                var parameterName = $"@skill{index}";
                queryText += $" AND ARRAY_CONTAINS(c.skills, {parameterName}, true)";
            }

            queryDefinition = new QueryDefinition(queryText);
            for (var index = 0; index < normalizedSkills.Length; index++)
            {
                var parameterName = $"@skill{index}";
                queryDefinition = queryDefinition.WithParameter(parameterName, normalizedSkills[index]);
            }

            var iterator = _techniciansContainer.GetItemQueryIterator<Technician>(
                queryDefinition: queryDefinition);

            var technicians = new List<Technician>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(cancellationToken);
                technicians.AddRange(response);
            }

            _logger.LogInformation(
                "Found {Count} available technicians matching skills",
                technicians.Count);

            return technicians;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(
                ex,
                "Failed to query technicians for skills {Skills}. Cosmos status code: {StatusCode}",
                string.Join(", ", normalizedSkills),
                ex.StatusCode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error while querying technicians for skills {Skills}",
                string.Join(", ", normalizedSkills));
            throw;
        }
    }

    public async Task<List<Part>> GetPartsInventoryAsync(
        IReadOnlyList<string> partNumbers,
        CancellationToken cancellationToken = default)
    {
        var normalizedPartNumbers = partNumbers
            .Where(static partNumber => !string.IsNullOrWhiteSpace(partNumber))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedPartNumbers.Length == 0)
        {
            _logger.LogInformation("Fetched 0 parts");
            return [];
        }

        try
        {
            var predicates = new List<string>(normalizedPartNumbers.Length);

            for (var index = 0; index < normalizedPartNumbers.Length; index++)
            {
                var parameterName = $"@partNumber{index}";
                predicates.Add($"c.partNumber = {parameterName}");
            }

            var queryText = $"SELECT * FROM c WHERE {string.Join(" OR ", predicates)}";
            var queryDefinition = new QueryDefinition(queryText);
            for (var index = 0; index < normalizedPartNumbers.Length; index++)
            {
                var parameterName = $"@partNumber{index}";
                queryDefinition = queryDefinition.WithParameter(parameterName, normalizedPartNumbers[index]);
            }

            var iterator = _partsContainer.GetItemQueryIterator<Part>(
                queryDefinition: queryDefinition);

            var parts = new List<Part>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(cancellationToken);
                parts.AddRange(response);
            }

            _logger.LogInformation("Fetched {Count} parts", parts.Count);
            return parts;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(
                ex,
                "Failed to fetch parts for part numbers {PartNumbers}. Cosmos status code: {StatusCode}",
                string.Join(", ", normalizedPartNumbers),
                ex.StatusCode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error while fetching parts for part numbers {PartNumbers}",
                string.Join(", ", normalizedPartNumbers));
            throw;
        }
    }

    public async Task<string> CreateWorkOrderAsync(
        WorkOrder workOrder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workOrder);

        workOrder.Id = string.IsNullOrWhiteSpace(workOrder.Id)
            ? Guid.NewGuid().ToString("N")
            : workOrder.Id;

        workOrder.Status = string.IsNullOrWhiteSpace(workOrder.Status)
            ? "new"
            : workOrder.Status;

        workOrder.CreatedDate ??= DateTimeOffset.UtcNow;
        workOrder.CreatedAt ??= workOrder.CreatedDate;
        workOrder.AssignedTechnician ??= workOrder.AssignedTo;

        try
        {
            var response = await _workOrdersContainer.CreateItemAsync(
                item: workOrder,
                partitionKey: new PartitionKey(workOrder.Status),
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Created work order {WorkOrderNumber} (id={WorkOrderId}, status={Status})",
                workOrder.WorkOrderNumber,
                response.Resource.Id,
                response.Resource.Status);

            return response.Resource.Id;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(
                ex,
                "Failed to create work order {WorkOrderNumber}. Cosmos status code: {StatusCode}",
                workOrder.WorkOrderNumber,
                ex.StatusCode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error while creating work order {WorkOrderNumber}",
                workOrder.WorkOrderNumber);
            throw;
        }
    }
}