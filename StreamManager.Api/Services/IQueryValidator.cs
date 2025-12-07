namespace StreamManager.Api.Services;

/// <summary>
/// Interface for query validation specific to stream processing engines.
/// Each engine has different SQL syntax and capabilities.
/// </summary>
public interface IQueryValidator
{
    /// <summary>
    /// Gets the name of the engine this validator is for.
    /// </summary>
    string EngineName { get; }
    
    /// <summary>
    /// Validates an ad-hoc query (SELECT statement for immediate execution).
    /// </summary>
    /// <param name="query">The SQL query to validate</param>
    /// <returns>Validation result with errors and warnings</returns>
    ValidationResult ValidateAdHocQuery(string query);
    
    /// <summary>
    /// Validates a persistent query (long-running stream processing job).
    /// </summary>
    /// <param name="query">The SQL query to validate</param>
    /// <param name="streamName">The name for the persistent stream</param>
    /// <returns>Validation result with errors and warnings</returns>
    ValidationResult ValidatePersistentQuery(string query, string streamName);
    
    /// <summary>
    /// Generates engine-specific stream properties for query execution.
    /// </summary>
    /// <param name="isAdHoc">True for ad-hoc queries, false for persistent queries</param>
    /// <returns>Dictionary of engine-specific properties</returns>
    Dictionary<string, object> GenerateStreamProperties(bool isAdHoc);
}

/// <summary>
/// Result of query validation
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public List<string> Warnings { get; init; } = new();

    public static ValidationResult Success() => new() { IsValid = true };
    
    public static ValidationResult Fail(string errorMessage) => 
        new() { IsValid = false, ErrorMessage = errorMessage };
    
    public static ValidationResult SuccessWithWarnings(List<string> warnings) => 
        new() { IsValid = true, Warnings = warnings };
}
