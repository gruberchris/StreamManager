using System.Text.Json.Serialization;

namespace StreamManager.Api.Services.Engines;

/// <summary>
/// Models for Flink SQL Gateway REST API responses
/// Based on: https://nightlies.apache.org/flink/flink-docs-stable/docs/dev/table/sql-gateway/rest/
/// </summary>

// Session Management
internal record CreateSessionRequest(
    [property: JsonPropertyName("sessionName")] string SessionName,
    [property: JsonPropertyName("properties")] Dictionary<string, string>? Properties = null);

internal record CreateSessionResponse(
    [property: JsonPropertyName("sessionHandle")] string SessionHandle);

// Statement Execution
internal record ExecuteStatementRequest(
    [property: JsonPropertyName("statement")] string Statement,
    [property: JsonPropertyName("executionConfig")] Dictionary<string, string>? ExecutionConfig = null);

internal record ExecuteStatementResponse(
    [property: JsonPropertyName("operationHandle")] string OperationHandle);

internal record OperationStatusResponse(
    [property: JsonPropertyName("status")] string Status);

internal record FetchResultsResponse(
    [property: JsonPropertyName("results")] FetchResultsData Results,
    [property: JsonPropertyName("resultType")] string ResultType,
    [property: JsonPropertyName("nextResultUri")] string? NextResultUri = null);

internal record FetchResultsData(
    [property: JsonPropertyName("columns")] List<ColumnInfo>? Columns,
    [property: JsonPropertyName("data")] List<RowData>? Data);

internal record ColumnInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("logicalType")] LogicalType LogicalType);

internal record LogicalType(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("nullable")] bool Nullable);

internal record RowData(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("fields")] List<object?> Fields);

// Flink REST API (Job Management)
internal record FlinkJobsResponse(
    [property: JsonPropertyName("jobs")] List<FlinkJobInfo> Jobs);

internal record FlinkJobInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status);

internal record FlinkJobDetailsResponse(
    [property: JsonPropertyName("jid")] string Jid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("start-time")] long StartTime);

internal record FlinkJobTerminationResponse(
    [property: JsonPropertyName("request-id")] string? RequestId);

/// <summary>
/// Internal session management for Flink SQL Gateway
/// </summary>
internal class FlinkSession
{
    public string SessionHandle { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsed { get; set; }
}
