using System.Runtime.CompilerServices;

namespace StreamManager.Api.Services;

/// <summary>
/// Abstraction for stream processing engines (ksqlDB, Flink, etc.)
/// Implementations provide engine-specific logic while exposing a common interface.
/// </summary>
public interface IStreamQueryEngine
{
    /// <summary>
    /// Gets the name of the engine (e.g., "ksqlDB", "Apache Flink")
    /// </summary>
    string EngineName { get; }
    
    /// <summary>
    /// Executes an ad-hoc streaming query and returns results as they arrive.
    /// Results are streamed line-by-line as JSON strings.
    /// </summary>
    /// <param name="query">The SQL query to execute</param>
    /// <param name="properties">Engine-specific properties (e.g., buffer sizes, parallelism)</param>
    /// <param name="cancellationToken">Cancellation token to stop streaming</param>
    /// <returns>Async enumerable of JSON result lines</returns>
    IAsyncEnumerable<string> ExecuteAdHocQueryAsync(
        string query, 
        Dictionary<string, object>? properties,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Deploys a persistent query (long-running stream processing job).
    /// Creates a new stream/table and starts continuous processing.
    /// </summary>
    /// <param name="name">User-friendly name for the stream</param>
    /// <param name="query">The SQL query to execute continuously</param>
    /// <param name="properties">Engine-specific properties</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Deployment result with job ID and output topic</returns>
    Task<DeploymentResult> DeployPersistentQueryAsync(
        string name,
        string query,
        Dictionary<string, object>? properties,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Stops a running persistent query/job.
    /// The query can potentially be restarted later.
    /// </summary>
    /// <param name="jobId">The engine-specific job/query identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task StopPersistentQueryAsync(
        string jobId,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Deletes a persistent query and cleans up resources.
    /// This operation is typically irreversible.
    /// </summary>
    /// <param name="jobId">The engine-specific job/query identifier</param>
    /// <param name="streamName">The name of the stream/table to drop</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeletePersistentQueryAsync(
        string jobId,
        string? streamName,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Lists all running queries/jobs on the engine.
    /// Useful for debugging and monitoring.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of query information</returns>
    Task<IEnumerable<QueryInfo>> ListQueriesAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of deploying a persistent query
/// </summary>
public record DeploymentResult(
    bool Success,
    string? JobId,
    string? OutputTopic,
    string? StreamName,
    string? ErrorMessage = null)
{
    public static DeploymentResult Failure(string errorMessage) =>
        new(false, null, null, null, errorMessage);
        
    public static DeploymentResult Succeed(string jobId, string outputTopic, string streamName) =>
        new(true, jobId, outputTopic, streamName);
}

/// <summary>
/// Information about a running query
/// </summary>
public record QueryInfo(
    string Id,
    string? Name,
    string Status,
    string QueryString);
