using System.Text.Json.Serialization;

namespace McpVs2010.Server.Bridge;

internal sealed class BridgeInstance
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("pipeName")]
    public string PipeName { get; set; } = string.Empty;

    [JsonPropertyName("startedAtUtc")]
    public string StartedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("solutionPath")]
    public string? SolutionPath { get; set; }

    [JsonIgnore]
    public string DiscoveryFilePath { get; set; } = string.Empty;
}

internal sealed class BridgeRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; set; }

    [JsonPropertyName("operation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; set; }

    [JsonPropertyName("project")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Project { get; set; }

    [JsonPropertyName("configuration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Configuration { get; set; }

    [JsonPropertyName("platform")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Platform { get; set; }

    [JsonPropertyName("solutionPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SolutionPath { get; set; }

    [JsonPropertyName("saveCurrentSolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SaveCurrentSolution { get; set; }
}

internal sealed class BridgeResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("resultJson")]
    public string? ResultJson { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
