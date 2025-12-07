using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using StreamManager.Api.Configuration;

namespace StreamManager.Api.Services.Validators;

/// <summary>
/// Query validator for ksqlDB SQL syntax.
/// Validates queries against ksqlDB-specific syntax rules and resource limits.
/// </summary>
public class KsqlDbQueryValidator : IQueryValidator
{
    private readonly ILogger<KsqlDbQueryValidator> _logger;
    private readonly ResourceLimitsOptions _options;

    public string EngineName => "ksqlDB";

    public KsqlDbQueryValidator(
        ILogger<KsqlDbQueryValidator> logger, 
        IOptions<ResourceLimitsOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        
        _logger.LogInformation(
            "KsqlDbQueryValidator initialized - MaxLength: {MaxLength}, MaxJoins: {MaxJoins}, MaxWindows: {MaxWindows}, Blocked: {BlockedCount} keywords",
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

        // 4. ksqlDB-specific: EMIT CHANGES is optional (will be added by engine if missing)
        if (!normalizedQuery.Contains("EMIT CHANGES"))
        {
            _logger.LogDebug("EMIT CHANGES will be added by ksqlDB engine");
        }

        // 5. Check for expensive operations
        var warnings = new List<string>();
        var joinCount = Regex.Matches(normalizedQuery, @"\bJOIN\b").Count;
        if (joinCount > _options.Query.MaxJoins)
            return ValidationResult.Fail($"Query contains {joinCount} JOINs. Maximum allowed: {_options.Query.MaxJoins}");
        else if (joinCount > 0)
            warnings.Add($"Query contains {joinCount} JOIN(s) which may consume significant resources");

        var windowCount = Regex.Matches(normalizedQuery, @"\bWINDOW\b").Count;
        if (windowCount > _options.Query.MaxWindows)
            return ValidationResult.Fail($"Query contains {windowCount} WINDOWs. Maximum allowed: {_options.Query.MaxWindows}");
        else if (windowCount > 0)
            warnings.Add($"Query contains {windowCount} WINDOW(s) which require additional memory");

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

        // 4. ksqlDB persistent queries cannot have LIMIT
        if (normalizedQuery.Contains("LIMIT"))
            return ValidationResult.Fail("Persistent queries cannot have LIMIT clause - they run continuously");

        // 5. Check for expensive operations (same as ad-hoc)
        var warnings = new List<string>();
        var joinCount = Regex.Matches(normalizedQuery, @"\bJOIN\b").Count;
        if (joinCount > _options.Query.MaxJoins)
            return ValidationResult.Fail($"Query contains {joinCount} JOINs. Maximum allowed: {_options.Query.MaxJoins}");
        else if (joinCount > 1)
            warnings.Add($"Persistent query with {joinCount} JOINs will consume significant resources continuously");

        var windowCount = Regex.Matches(normalizedQuery, @"\bWINDOW\b").Count;
        if (windowCount > _options.Query.MaxWindows)
            return ValidationResult.Fail($"Query contains {windowCount} WINDOWs. Maximum allowed: {_options.Query.MaxWindows}");

        return warnings.Count > 0 
            ? ValidationResult.SuccessWithWarnings(warnings) 
            : ValidationResult.Success();
    }

    public Dictionary<string, object> GenerateStreamProperties(bool isAdHoc)
    {
        var properties = isAdHoc 
            ? _options.StreamProperties.AdHoc 
            : _options.StreamProperties.Persistent;

        return new Dictionary<string, object>(properties);
    }
}
