using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using StreamManager.Api.Configuration;

namespace StreamManager.Api.Services.Engines;

/// <summary>
/// ksqlDB implementation of the stream query engine.
/// Wraps ksqlDB REST API for ad-hoc and persistent query execution.
/// </summary>
public class KsqlDbEngine(
    HttpClient httpClient,
    ILogger<KsqlDbEngine> logger,
    IQueryValidator validator,
    StreamEngineOptions engineOptions)
    : IStreamQueryEngine
{
    public string EngineName => "ksqlDB";

    public async IAsyncEnumerable<string> ExecuteAdHocQueryAsync(
        string query,
        Dictionary<string, object>? properties,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting ksqlDB query: {Query}", query);

        // Validate query
        var validation = validator.ValidateAdHocQuery(query);
        if (!validation.IsValid)
        {
            logger.LogWarning("Query validation failed: {Error}", validation.ErrorMessage);
            throw new InvalidOperationException($"Query validation failed: {validation.ErrorMessage}");
        }

        // Log warnings
        foreach (var warning in validation.Warnings)
        {
            logger.LogInformation("Query warning: {Warning}", warning);
        }

        // Ensure EMIT CHANGES is present for ksqlDB streaming queries
        if (!query.ToUpperInvariant().Contains("EMIT CHANGES"))
        {
            query = query.TrimEnd(';') + " EMIT CHANGES;";
            logger.LogInformation("Added EMIT CHANGES to query");
        }

        // Merge provided properties with validator-generated properties
        var streamProperties = validator.GenerateStreamProperties(isAdHoc: true);
        if (properties != null)
        {
            foreach (var prop in properties)
            {
                streamProperties[prop.Key] = prop.Value;
            }
        }

        var payload = new
        {
            ksql = query,
            streamsProperties = streamProperties
        };

        var jsonContent = JsonSerializer.Serialize(payload);
        var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        logger.LogInformation("Sending request to ksqlDB: {Url}", $"{engineOptions.KsqlDb.Url}/query");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{engineOptions.KsqlDb.Url}/query")
        {
            Content = httpContent
        };

        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        logger.LogInformation("Got HTTP response: {StatusCode}", response.StatusCode);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var reader = new StreamReader(stream);

        string? line;
        int lineCount = 0;

        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Query cancelled after {LineCount} lines", lineCount);
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                lineCount++;
                logger.LogDebug("Processing line {LineCount}", lineCount);

                // ksqlDB returns array-style JSON, need to extract individual objects
                var cleanLine = line.TrimEnd(',');
                if (cleanLine.StartsWith("["))
                {
                    cleanLine = cleanLine.TrimStart('[');
                }
                if (cleanLine.EndsWith("]"))
                {
                    cleanLine = cleanLine.TrimEnd(']');
                }

                if (!string.IsNullOrWhiteSpace(cleanLine))
                {
                    yield return cleanLine;
                }
            }
        }

        logger.LogInformation("Query stream completed after {LineCount} lines", lineCount);
    }

    public async Task<DeploymentResult> DeployPersistentQueryAsync(
        string name,
        string query,
        Dictionary<string, object>? properties,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var safeName = GenerateSafeName(name);
            var createStreamSql = $"CREATE STREAM {safeName} AS {query.TrimEnd(';')};";

            logger.LogInformation("Deploying persistent query with name: {SafeName}", safeName);

            var payload = new { ksql = createStreamSql };
            var jsonContent = JsonSerializer.Serialize(payload);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(
                $"{engineOptions.KsqlDb.Url}/ksql",
                httpContent,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Failed to deploy stream: {Error}", errorContent);
                return DeploymentResult.Failure($"Failed to deploy stream: {errorContent}");
            }

            // Query ksqlDB to get the actual created query ID and topic
            var showQueriesPayload = new { ksql = "SHOW QUERIES;" };
            var showQueriesJson = JsonSerializer.Serialize(showQueriesPayload);
            var showQueriesContent = new StringContent(showQueriesJson, Encoding.UTF8, "application/json");

            var queriesResponse = await httpClient.PostAsync(
                $"{engineOptions.KsqlDb.Url}/ksql",
                showQueriesContent,
                cancellationToken);

            string? actualQueryId = null;
            string? actualTopic = null;

            if (queriesResponse.IsSuccessStatusCode)
            {
                var queriesResult = await queriesResponse.Content.ReadAsStringAsync(cancellationToken);

                using var doc = JsonDocument.Parse(queriesResult);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var firstResult = doc.RootElement[0];
                    if (firstResult.TryGetProperty("queries", out var queries))
                    {
                        // Find the query with our stream name (look for most recent one)
                        foreach (var queryObj in queries.EnumerateArray())
                        {
                            if (queryObj.TryGetProperty("queryString", out var queryString) &&
                                queryString.GetString()?.Contains($"CREATE STREAM {safeName}", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                if (queryObj.TryGetProperty("id", out var idProp))
                                    actualQueryId = idProp.GetString();

                                if (queryObj.TryGetProperty("sinks", out var sinks) && sinks.GetArrayLength() > 0)
                                    actualTopic = sinks[0].GetString();
                            }
                        }
                    }
                }
            }

            // Fallback if we couldn't parse the response
            if (string.IsNullOrWhiteSpace(actualQueryId))
                actualQueryId = $"CSAS_{safeName.ToUpperInvariant()}_{Guid.NewGuid():N}";

            if (string.IsNullOrWhiteSpace(actualTopic))
                actualTopic = safeName.ToUpperInvariant();

            logger.LogInformation("Successfully deployed stream with query ID {QueryId} and topic {Topic}",
                actualQueryId, actualTopic);

            return DeploymentResult.Succeed(actualQueryId, actualTopic, safeName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deploying persistent query");
            return DeploymentResult.Failure($"Exception: {ex.Message}");
        }
    }

    public async Task StopPersistentQueryAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("Job ID cannot be null or empty", nameof(jobId));
        }

        try
        {
            var terminateSql = $"TERMINATE {jobId};";
            var payload = new { ksql = terminateSql };
            var jsonContent = JsonSerializer.Serialize(payload);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(
                $"{engineOptions.KsqlDb.Url}/ksql",
                httpContent,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Failed to stop query {JobId}: {Error}", jobId, errorContent);
                throw new InvalidOperationException($"Failed to stop query: {errorContent}");
            }

            logger.LogInformation("Successfully stopped query {JobId}", jobId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error stopping query {JobId}", jobId);
            throw;
        }
    }

    public async Task DeletePersistentQueryAsync(
        string jobId,
        string? streamName,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Step 1: Terminate the query if job ID provided
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            try
            {
                await StopPersistentQueryAsync(jobId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to terminate query {JobId}", jobId);
                errors.Add($"Terminate failed: {ex.Message}");
            }
        }

        // Step 2: Drop the stream and delete its topic
        if (!string.IsNullOrWhiteSpace(streamName))
        {
            try
            {
                var dropSql = $"DROP STREAM IF EXISTS {streamName} DELETE TOPIC;";
                var dropPayload = new { ksql = dropSql };
                var dropJsonContent = JsonSerializer.Serialize(dropPayload);
                var dropHttpContent = new StringContent(dropJsonContent, Encoding.UTF8, "application/json");

                var dropResponse = await httpClient.PostAsync(
                    $"{engineOptions.KsqlDb.Url}/ksql",
                    dropHttpContent,
                    cancellationToken);

                if (!dropResponse.IsSuccessStatusCode)
                {
                    var errorContent = await dropResponse.Content.ReadAsStringAsync(cancellationToken);
                    logger.LogWarning("Failed to drop stream {StreamName}: {Error}", streamName, errorContent);
                    errors.Add($"Drop stream failed: {errorContent}");
                }
                else
                {
                    logger.LogInformation("Successfully dropped stream {StreamName}", streamName);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Exception while dropping stream {StreamName}", streamName);
                errors.Add($"Drop stream exception: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Deletion completed with warnings: {string.Join("; ", errors)}");
        }
    }

    public async Task<IEnumerable<QueryInfo>> ListQueriesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var showQueriesPayload = new { ksql = "SHOW QUERIES;" };
            var jsonContent = JsonSerializer.Serialize(showQueriesPayload);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(
                $"{engineOptions.KsqlDb.Url}/ksql",
                httpContent,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Failed to list queries");
                return Enumerable.Empty<QueryInfo>();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var queries = new List<QueryInfo>();

            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var firstResult = doc.RootElement[0];
                if (firstResult.TryGetProperty("queries", out var queriesArray))
                {
                    foreach (var query in queriesArray.EnumerateArray())
                    {
                        var id = query.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        var queryString = query.TryGetProperty("queryString", out var qsProp) ? qsProp.GetString() : null;
                        var status = query.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : "UNKNOWN";

                        if (id != null && queryString != null)
                        {
                            queries.Add(new QueryInfo(id, null, status ?? "UNKNOWN", queryString));
                        }
                    }
                }
            }

            return queries;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error listing queries");
            return Enumerable.Empty<QueryInfo>();
        }
    }

    private static string GenerateSafeName(string name)
    {
        // Generate a safe name for ksqlDB (alphanumeric + underscore, starts with letter)
        var safeName = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (string.IsNullOrEmpty(safeName) || !char.IsLetter(safeName[0]))
            safeName = "STREAM_" + safeName;

        return $"VIEW_{safeName.ToUpperInvariant()}_{Guid.NewGuid():N}";
    }
}
