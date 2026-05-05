using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlanner.Models;

public sealed class WorkOrderPartUsage
{
    [JsonPropertyName("partId")]
    [JsonProperty("partId")]
    public string? PartId { get; set; }

    [JsonPropertyName("partNumber")]
    [JsonProperty("partNumber")]
    public string? PartNumber { get; set; }

    [JsonPropertyName("partName")]
    [JsonProperty("partName")]
    public string? PartName { get; set; }

    [JsonPropertyName("quantity")]
    [JsonProperty("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("isAvailable")]
    [JsonProperty("isAvailable")]
    public bool? IsAvailable { get; set; }
}