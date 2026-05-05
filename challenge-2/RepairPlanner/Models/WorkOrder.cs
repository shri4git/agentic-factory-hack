using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlanner.Models;

public sealed class WorkOrder
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("workOrderNumber")]
    [JsonProperty("workOrderNumber")]
    public string WorkOrderNumber { get; set; } = string.Empty;

    [JsonPropertyName("machineId")]
    [JsonProperty("machineId")]
    public string MachineId { get; set; } = string.Empty;

    [JsonPropertyName("faultType")]
    [JsonProperty("faultType")]
    public string? FaultType { get; set; }

    [JsonPropertyName("title")]
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    [JsonProperty("priority")]
    public string Priority { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("assignedTo")]
    [JsonProperty("assignedTo")]
    public string? AssignedTo { get; set; }

    [JsonPropertyName("assignedTechnician")]
    [JsonProperty("assignedTechnician")]
    public string? AssignedTechnician { get; set; }

    [JsonPropertyName("createdDate")]
    [JsonProperty("createdDate")]
    public DateTimeOffset? CreatedDate { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonProperty("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("scheduledDate")]
    [JsonProperty("scheduledDate")]
    public DateTimeOffset? ScheduledDate { get; set; }

    [JsonPropertyName("completedDate")]
    [JsonProperty("completedDate")]
    public DateTimeOffset? CompletedDate { get; set; }

    [JsonPropertyName("estimatedDuration")]
    [JsonProperty("estimatedDuration")]
    public int EstimatedDuration { get; set; }

    [JsonPropertyName("actualDuration")]
    [JsonProperty("actualDuration")]
    public int? ActualDuration { get; set; }

    [JsonPropertyName("downtime")]
    [JsonProperty("downtime")]
    public int? Downtime { get; set; }

    [JsonPropertyName("requiredParts")]
    [JsonProperty("requiredParts")]
    public List<WorkOrderPartUsage> RequiredParts { get; set; } = [];

    [JsonPropertyName("partsUsed")]
    [JsonProperty("partsUsed")]
    public List<WorkOrderPartUsage> PartsUsed { get; set; } = [];

    [JsonPropertyName("tasks")]
    [JsonProperty("tasks")]
    public List<RepairTask> Tasks { get; set; } = [];

    [JsonPropertyName("notes")]
    [JsonProperty("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("cost")]
    [JsonProperty("cost")]
    public decimal? Cost { get; set; }
}