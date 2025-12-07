using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using StreamManager.Api.Configuration;

namespace StreamManager.Api.Services.Validators;

/// <summary>
/// Query validator for Apache Flink SQL syntax.
/// Validates queries against Flink-specific syntax rules and resource limits.
/// </summary>
public class FlinkQueryValidator : IQueryValidator
{
    private readonly ILogger<FlinkQueryValidator> _logger;
    private readonly ResourceLimitsOptions _options;

    public string EngineName => "Apache Flink";

    public FlinkQueryValidator(
        ILogger<FlinkQueryValidator> logger, 
        IOptions<ResourceLimitsOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        
        _logger.LogInformation(
            "FlinkQueryValidator initialized - MaxLength: {MaxLength}, MaxJoins: {MaxJoins}, MaxWindows: {MaxWindows}, Blocked: {BlockedCount} keywords",
            _options.Query.MaxQueryLength,
            _options.Query.MaxJoins,
            _options.Query.MaxWindows,
            _options.Query.BlockedKeywords.Count);
    }

    public ValidationResult ValidateAdHocQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ValidationResult.Fail("Query cannot be empty");

        var normalizedQuery = query.Trim().ToUpperInvariant();

        // 1. Check query length
        if (query.Length > _options.Query.MaxQueryLength)
            return ValidationResult.Fail($"Query exceeds maximum length of {_options.Query.MaxQueryLength} characters");

        // 2. Must be a SELECT statement
        if (!normalizedQuery.StartsWith("SELECT"))
            return ValidationResult.Fail("Ad-hoc queries must be SELECT statements");

        // 3. Block dangerous operations
        foreach (var blocked in _options.Query.BlockedKeywords)
        {
            if (normalizedQuery.Contains(blocked.ToUpperInvariant()))
                return ValidationResult.Fail($"Operation '{blocked}' is not allowed in ad-hoc queries");
        }

        // 4. Flink-specific: EMIT CHANGES is INVALID syntax
        if (normalizedQuery.Contains("EMIT CHANGES"))
        {
            return ValidationResult.Fail("EMIT CHANGES is not valid Flink SQL syntax. Remove it from your query.");
        }

        // 5. Check for expensive operations
        var warnings = new List<string>();
        var joinCount = Regex.Matches(normalizedQuery, @"\bJOIN\b").Count;
        if (joinCount > _options.Query.MaxJoins)
            return ValidationResult.Fail($"Query contains {joinCount} JOINs. Maximum allowed: {_options.Query.MaxJoins}");
        else if (joinCount > 0)
            warnings.Add($"Query contains {joinCount} JOIN(s) which may consume significant resources");

        // Flink uses different window syntax: TUMBLE(), HOP(), SESSION()
        var tumbleCount = Regex.Matches(normalizedQuery, @"\bTUMBLE\s*\(").Count;
        var hopCount = Regex.Matches(normalizedQuery, @"\bHOP\s*\(").Count;
        var sessionCount = Regex.Matches(normalizedQuery, @"\bSESSION\s*\(").Count;
        var totalWindows = tumbleCount + hopCount + sessionCount;
        
        if (totalWindows > _options.Query.MaxWindows)
            return ValidationResult.Fail($"Query contains {totalWindows} window functions. Maximum allowed: {_options.Query.MaxWindows}");
        else if (totalWindows > 0)
            warnings.Add($"Query contains {totalWindows} window function(s) which require additional memory");

        // 6. Warn about unbounded queries
        if (!normalizedQuery.Contains("WHERE") && !normalizedQuery.Contains("LIMIT"))
            warnings.Add("Query has no WHERE clause or LIMIT - it will process all data indefinitely");

        return warnings.Count > 0 
            ? ValidationResult.SuccessWithWarnings(warnings) 
            : ValidationResult.Success();
    }

    public ValidationResult ValidatePersistentQuery(string query, string streamName)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ValidationResult.Fail("Query cannot be empty");

        if (string.IsNullOrWhiteSpace(streamName))
            return ValidationResult.Fail("Stream name cannot be empty");

        var normalizedQuery = query.Trim().ToUpperInvariant();

        // 1. Check query length
        if (query.Length > _options.Query.MaxQueryLength)
            return ValidationResult.Fail($"Query exceeds maximum length of {_options.Query.MaxQueryLength} characters");

        // 2. Must be a SELECT statement
        if (!normalizedQuery.StartsWith("SELECT"))
            return ValidationResult.Fail("Persistent queries must be SELECT statements");

        // 3. Block dangerous operations
        foreach (var blocked in _options.Query.BlockedKeywords)
        {
            if (normalizedQuery.Contains(blocked.ToUpperInvariant()))
                return ValidationResult.Fail($"Operation '{blocked}' is not allowed in persistent queries");
        }

        // 4. Flink: EMIT CHANGES is invalid
        if (normalizedQuery.Contains("EMIT CHANGES"))
        {
            return ValidationResult.Fail("EMIT CHANGES is not valid Flink SQL syntax. Remove it from your query.");
        }

        // 5. Flink persistent queries CAN have LIMIT (unlike ksqlDB)
        // This is allowed in Flink for bounded queries

        // 6. Check for expensive operations
        var warnings = new List<string>();
        var joinCount = Regex.Matches(normalizedQuery, @"\bJOIN\b").Count;
        if (joinCount > _options.Query.MaxJoins)
            return ValidationResult.Fail($"Query contains {joinCount} JOINs. Maximum allowed: {_options.Query.MaxJoins}");
        else if (joinCount > 1)
            warnings.Add($"Persistent query with {joinCount} JOINs will consume significant resources continuously");

        var tumbleCount = Regex.Matches(normalizedQuery, @"\bTUMBLE\s*\(").Count;
        var hopCount = Regex.Matches(normalizedQuery, @"\bHOP\s*\(").Count;
        var sessionCount = Regex.Matches(normalizedQuery, @"\bSESSION\s*\(").Count;
        var totalWindows = tumbleCount + hopCount + sessionCount;
        
        if (totalWindows > _options.Query.MaxWindows)
            return ValidationResult.Fail($"Query contains {totalWindows} window functions. Maximum allowed: {_options.Query.MaxWindows}");

        return warnings.Count > 0 
            ? ValidationResult.SuccessWithWarnings(warnings) 
            : ValidationResult.Success();
    }

    public Dictionary<string, object> GenerateStreamProperties(bool isAdHoc)
    {
        // Flink properties are different from ksqlDB
        // These would be passed to Flink SQL Gateway session config
        var properties = new Dictionary<string, object>();

        if (isAdHoc)
        {
            // Ad-hoc query properties
            properties["execution.checkpointing.interval"] = "2000";
            properties["pipeline.max-parallelism"] = "2";
            properties["table.exec.resource.default-parallelism"] = "1";
        }
        else
        {
            // Persistent query properties
            properties["execution.checkpointing.interval"] = "2000";
            properties["pipeline.max-parallelism"] = "4";
            properties["table.exec.resource.default-parallelism"] = "2";
        }

        return properties;
    }
}
