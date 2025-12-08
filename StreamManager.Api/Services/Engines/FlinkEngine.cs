using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamManager.Api.Configuration;

namespace StreamManager.Api.Services.Engines;

/// <summary>
/// Apache Flink implementation of the stream query engine.
/// Uses Flink SQL Gateway REST API for query submission and Flink REST API for job management.
/// </summary>
public class FlinkEngine(
    HttpClient httpClient,
    ILogger<FlinkEngine> logger,
    IQueryValidator validator,
    StreamEngineOptions engineOptions)
    : IStreamQueryEngine
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string EngineName => "Apache Flink";

    public async IAsyncEnumerable<string> ExecuteAdHocQueryAsync(
        string query,
        Dictionary<string, object>? properties,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting Flink ad-hoc query");

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

        string? sessionHandle = null;

        try
        {
            // Step 1: Create a session
            sessionHandle = await CreateSessionAsync(properties, cancellationToken);
            logger.LogInformation("Created Flink session: {SessionHandle}", sessionHandle);

            // Step 2: Parse the SQL script into individual statements
            // Users can provide complete scripts including CREATE TABLE statements
            logger.LogInformation("📝 FULL QUERY RECEIVED: {Query}", query.Length > 500 ? query.Substring(0, 500) + "..." : query);
            var statements = ParseSqlStatements(query);
            logger.LogInformation("📝 PARSED INTO {Count} STATEMENTS", statements.Count);
            
            // For ad-hoc queries, automatically add bounded mode to Kafka tables so they complete
            statements = statements.Select(EnsureBoundedModeForAdHocQuery).ToList();
            
            string? selectOperationHandle = null;

            // Step 3: Execute each statement in order
            foreach (var statement in statements)
            {
                var trimmedStmt = statement.Trim();
                if (string.IsNullOrEmpty(trimmedStmt)) continue;

                logger.LogInformation("⚡ EXECUTING STATEMENT {Index}/{Total}: {Statement}", 
                    statements.IndexOf(statement) + 1, statements.Count,
                    trimmedStmt.Length > 100 ? trimmedStmt.Substring(0, 100) + "..." : trimmedStmt);

                var operationHandle = await ExecuteStatementAsync(sessionHandle, trimmedStmt, cancellationToken);
                
                // Check if this is a SELECT query (will have results to stream)
                if (IsSelectQuery(trimmedStmt))
                {
                    selectOperationHandle = operationHandle;
                }
                else
                {
                    // Wait for DDL/DML statements to complete before proceeding
                    await WaitForOperationAsync(sessionHandle, operationHandle, cancellationToken);
                    logger.LogDebug("Statement completed successfully");
                }
            }

            // Step 4: If there was a SELECT query, fetch and stream its results
            if (selectOperationHandle != null)
            {
                await WaitForOperationAsync(sessionHandle, selectOperationHandle, cancellationToken);

                var resultCount = 0;
                await foreach (var result in FetchResultsAsync(sessionHandle, selectOperationHandle, cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        logger.LogDebug("Query cancelled after {ResultCount} results", resultCount);
                        yield break;
                    }

                    resultCount++;
                    yield return result;
                }

                logger.LogInformation("Ad-hoc query completed with {ResultCount} results", resultCount);
            }
            else
            {
                logger.LogInformation("Ad-hoc query completed (no SELECT statement)");
            }
        }
        finally
        {
            // Step 5: Cleanup - close session (all tables automatically dropped)
            if (sessionHandle != null)
            {
                await CloseSessionAsync(sessionHandle, CancellationToken.None);
            }
        }
    }

    public async Task<DeploymentResult> DeployPersistentQueryAsync(
        string name,
        string query,
        Dictionary<string, object>? properties,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation("Deploying persistent Flink stream: {Name}", name);

            string? sessionHandle = null;

            try
            {
                // Create a session for deployment
                sessionHandle = await CreateSessionAsync(properties, cancellationToken);
                logger.LogInformation("Created Flink session for deployment");

                // Parse the complete SQL script into individual statements
                // Users provide complete scripts including:
                // - CREATE TABLE for sources
                // - CREATE TABLE for sinks
                // - INSERT INTO statement
                var statements = ParseSqlStatements(query);
                
                if (statements.Count == 0)
                {
                    return DeploymentResult.Failure("No SQL statements found in query");
                }

                logger.LogInformation("Parsed {Count} SQL statements from script", statements.Count);

                // Execute each statement in order
                foreach (var statement in statements)
                {
                    var trimmedStmt = statement.Trim();
                    if (string.IsNullOrEmpty(trimmedStmt)) continue;

                    logger.LogDebug("Executing statement: {Statement}", 
                        trimmedStmt.Length > 100 ? trimmedStmt.Substring(0, 100) + "..." : trimmedStmt);

                    var operationHandle = await ExecuteStatementAsync(sessionHandle, trimmedStmt, cancellationToken);
                    
                    // Wait for each statement to complete
                    await WaitForOperationAsync(sessionHandle, operationHandle, cancellationToken);
                    
                    logger.LogDebug("Statement completed successfully");
                }

                // Wait for the Flink job to start
                logger.LogInformation("Waiting for Flink job to start...");
                await Task.Delay(2000, cancellationToken);

                // Find the job ID from Flink JobManager REST API
                var jobs = await ListRunningJobsAsync(cancellationToken);
                var latestJob = jobs.OrderByDescending(j => j.StartTime).FirstOrDefault();

                if (latestJob == null)
                {
                    return DeploymentResult.Failure(
                        "Failed to find created job in Flink. Job may not have started. " +
                        "Check that your script includes an INSERT INTO statement.");
                }

                var jobId = latestJob.Jid;

                // Extract output topic from the script
                var outputTopic = ExtractOutputTopicFromScript(query);

                logger.LogInformation(
                    "Successfully deployed Flink job. JobId: {JobId}, OutputTopic: {Topic}",
                    jobId, outputTopic);

                return DeploymentResult.Succeed(jobId, outputTopic, name);
            }
            finally
            {
                // Close session (job continues running independently)
                if (sessionHandle != null)
                {
                    await CloseSessionAsync(sessionHandle, CancellationToken.None);
                    logger.LogInformation("Closed deployment session");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deploying persistent Flink stream: {Name}", name);
            return DeploymentResult.Failure($"Deployment failed: {ex.Message}");
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
            logger.LogInformation("Stopping Flink job: {JobId}", jobId);

            // Use Flink REST API to stop the job
            // PATCH /jobs/{jobId} with "cancel" action (stop is deprecated/removed)
            var response = await httpClient.PatchAsync(
                $"{engineOptions.Flink.RestApiUrl}/jobs/{jobId}?mode=cancel",
                new StringContent("", Encoding.UTF8, "application/json"),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Failed to stop job {JobId}: {Error}", jobId, errorContent);
                throw new InvalidOperationException($"Failed to stop job: {errorContent}");
            }

            logger.LogInformation("Successfully stopped Flink job: {JobId}", jobId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error stopping Flink job {JobId}", jobId);
            throw;
        }
    }

    public async Task DeletePersistentQueryAsync(
        string jobId,
        string? streamName,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        logger.LogInformation("Deleting Flink job: {JobId}, stream: {StreamName}", jobId, streamName);

        // Step 1: Stop the job if it has an ID
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            try
            {
                await StopPersistentQueryAsync(jobId, cancellationToken);
                
                // Wait for job to fully stop
                await Task.Delay(2000, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stop job {JobId}", jobId);
                errors.Add($"Stop job failed: {ex.Message}");
            }
        }

        // Step 2: Drop the table (if stream name provided)
        if (!string.IsNullOrWhiteSpace(streamName))
        {
            string? sessionHandle = null;
            try
            {
                sessionHandle = await CreateSessionAsync(null, cancellationToken);
                var dropStatement = $"DROP TABLE IF EXISTS {streamName};";

                logger.LogInformation("Executing drop statement: {Statement}", dropStatement);

                await ExecuteStatementAsync(sessionHandle, dropStatement, cancellationToken);

                logger.LogInformation("Successfully dropped table {StreamName}", streamName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to drop table {StreamName}", streamName);
                errors.Add($"Drop table failed: {ex.Message}");
            }
            finally
            {
                if (sessionHandle != null)
                {
                    await CloseSessionAsync(sessionHandle, CancellationToken.None);
                }
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
            logger.LogInformation("Listing Flink jobs");

            var response = await httpClient.GetAsync(
                $"{engineOptions.Flink.RestApiUrl}/jobs",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Failed to list Flink jobs: {StatusCode}", response.StatusCode);
                return Enumerable.Empty<QueryInfo>();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var jobsResponse = JsonSerializer.Deserialize<FlinkJobsResponse>(content, _jsonOptions);

            if (jobsResponse?.Jobs == null)
                return Enumerable.Empty<QueryInfo>();

            var queries = new List<QueryInfo>();
            foreach (var job in jobsResponse.Jobs)
            {
                // Get job details to find the query string
                var details = await GetJobDetailsAsync(job.Id, cancellationToken);
                queries.Add(new QueryInfo(
                    job.Id,
                    details?.Name,
                    job.Status,
                    details?.Name ?? "N/A"
                ));
            }

            return queries;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error listing Flink jobs");
            return Enumerable.Empty<QueryInfo>();
        }
    }

    // ============================================
    // Private Helper Methods
    // ============================================

    private async Task<string> CreateSessionAsync(
        Dictionary<string, object>? properties,
        CancellationToken cancellationToken)
    {
        var sessionProps = new Dictionary<string, string>();
        
        // Add default properties
        var validatorProps = validator.GenerateStreamProperties(isAdHoc: true);
        foreach (var prop in validatorProps)
        {
            sessionProps[prop.Key] = prop.Value.ToString() ?? "";
        }

        // Override with user-provided properties
        if (properties != null)
        {
            foreach (var prop in properties)
            {
                sessionProps[prop.Key] = prop.Value.ToString() ?? "";
            }
        }

        var request = new CreateSessionRequest(
            SessionName: $"StreamManager_{Guid.NewGuid():N}",
            Properties: sessionProps.Count > 0 ? sessionProps : null
        );

        var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
        var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(
            $"{engineOptions.Flink.SqlGatewayUrl}/v1/sessions",
            httpContent,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var sessionResponse = JsonSerializer.Deserialize<CreateSessionResponse>(responseContent, _jsonOptions);

        if (sessionResponse == null || string.IsNullOrEmpty(sessionResponse.SessionHandle))
        {
            throw new InvalidOperationException("Failed to create Flink session");
        }

        return sessionResponse.SessionHandle;
    }

    private async Task<string> ExecuteStatementAsync(
        string sessionHandle,
        string statement,
        CancellationToken cancellationToken)
    {
        var request = new ExecuteStatementRequest(Statement: statement);
        var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
        var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(
            $"{engineOptions.Flink.SqlGatewayUrl}/v1/sessions/{sessionHandle}/statements",
            httpContent,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var statementResponse = JsonSerializer.Deserialize<ExecuteStatementResponse>(responseContent, _jsonOptions);

        if (statementResponse == null || string.IsNullOrEmpty(statementResponse.OperationHandle))
        {
            throw new InvalidOperationException("Failed to execute statement");
        }

        return statementResponse.OperationHandle;
    }

    private async Task WaitForOperationAsync(
        string sessionHandle,
        string operationHandle,
        CancellationToken cancellationToken)
    {
        var maxAttempts = 60; // 60 seconds max wait
        var attempt = 0;

        while (attempt < maxAttempts)
        {
            var response = await httpClient.GetAsync(
                $"{engineOptions.Flink.SqlGatewayUrl}/v1/sessions/{sessionHandle}/operations/{operationHandle}/status",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var status = JsonSerializer.Deserialize<OperationStatusResponse>(content, _jsonOptions);

                if (status?.Status == "FINISHED" || status?.Status == "RUNNING")
                {
                    logger.LogDebug("Operation ready with status: {Status}", status.Status);
                    return;
                }

                if (status?.Status == "ERROR" || status?.Status == "CANCELED")
                {
                    // Try to get error details from the result
                    try
                    {
                        var errorResponse = await httpClient.GetAsync(
                            $"{engineOptions.Flink.SqlGatewayUrl}/v1/sessions/{sessionHandle}/operations/{operationHandle}/result/0",
                            cancellationToken);
                        
                        if (errorResponse.IsSuccessStatusCode)
                        {
                            var errorContent = await errorResponse.Content.ReadAsStringAsync(cancellationToken);
                            logger.LogError("Operation failed with details: {ErrorDetails}", errorContent);
                        }
                    }
                    catch
                    {
                        // Ignore errors while trying to fetch error details
                    }
                    
                    throw new InvalidOperationException($"Operation failed with status: {status.Status}");
                }
            }

            attempt++;
            await Task.Delay(1000, cancellationToken);
        }

        throw new TimeoutException("Operation did not complete in time");
    }

    private async IAsyncEnumerable<string> FetchResultsAsync(
        string sessionHandle,
        string operationHandle,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long token = 0;
        var hasMore = true;
        var headerSent = false;

        while (hasMore && !cancellationToken.IsCancellationRequested)
        {
            var response = await httpClient.GetAsync(
                $"{engineOptions.Flink.SqlGatewayUrl}/v1/sessions/{sessionHandle}/operations/{operationHandle}/result/{token}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Read the error response body directly
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Failed to fetch results: {StatusCode}, Response: {ErrorBody}", response.StatusCode, errorBody);
                
                // Also try to get error from operation status
                var operationError = await GetOperationErrorAsync(sessionHandle, operationHandle, cancellationToken);
                logger.LogError("Operation error details: {OperationError}", operationError);
                
                // Yield error as a result instead of throwing to avoid crashing SignalR
                yield return JsonSerializer.Serialize(new
                {
                    error = true,
                    message = $"Query failed: {operationError}",
                    rawError = errorBody,
                    statusCode = (int)response.StatusCode
                }, _jsonOptions);
                
                yield break;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogInformation("📦 RAW FLINK RESPONSE (first 1500 chars): {Content}", 
                content.Length > 1500 ? content.Substring(0, 1500) + "..." : content);
            var result = JsonSerializer.Deserialize<FetchResultsResponse>(content, _jsonOptions);

            // Send header if not sent yet and columns are available
            if (!headerSent && result?.Results?.Columns != null)
            {
                var schemaString = string.Join(", ", result.Results.Columns.Select(c => $"`{c.Name}` {c.LogicalType.Type}"));
                var headerJson = JsonSerializer.Serialize(new
                {
                    header = new
                    {
                        queryId = operationHandle,
                        schema = schemaString
                    }
                }, _jsonOptions);
                
                logger.LogInformation("✅ YIELDING HEADER: {Header}", headerJson);
                yield return headerJson;
                headerSent = true;
            }

            if (result?.Results?.Data is { Count: > 0 })
            {
                logger.LogInformation("✅ FETCHED {Count} ROWS FROM FLINK", result.Results.Data.Count);
                foreach (var row in result.Results.Data)
                {
                    // Convert row to JSON format compatible with ksqlDB frontend
                    // Format: { "row": { "columns": [val1, val2, ...] } }
                    var rowWrapper = new
                    {
                        row = new
                        {
                            columns = row.Fields
                        }
                    };
                    
                    var rowJson = JsonSerializer.Serialize(rowWrapper, _jsonOptions);
                    // logger.LogInformation("✅ YIELDING ROW: {Row}", rowJson.Length > 100 ? rowJson.Substring(0, 100) + "..." : rowJson);
                    yield return rowJson;
                }
            }
            else
            {
                logger.LogDebug("⚠️ NO DATA IN FETCH RESPONSE. Results null: {ResultsNull}, Data null: {DataNull}, Data count: {Count}", 
                    result?.Results == null, result?.Results?.Data == null, result?.Results?.Data?.Count ?? 0);
            }

            // Check if there are more results and update token
            hasMore = !string.IsNullOrEmpty(result?.NextResultUri);

            if (hasMore && result?.NextResultUri != null)
            {
                // Extract the next token from the URI
                // Format: /v1/sessions/{handle}/operations/{handle}/result/{token}
                var parts = result.NextResultUri.Split('/');
                if (parts.Length > 0 && long.TryParse(parts.Last(), out var nextToken))
                {
                    if (nextToken == token)
                    {
                        // If token didn't change but hasMore is true, we might be in a wait state
                        // Just increment to be safe or rely on Flink to handle it?
                        // Actually, if Flink returns the same token, it means "try again with same token" (maybe waiting for results)
                        // But usually it advances. Let's just update token.
                        // However, Flink REST API documentation says we should use the NextResultUri.
                        // Let's trust the URI.
                    }
                    token = nextToken;
                }
                else
                {
                    logger.LogWarning("Failed to parse token from NextResultUri: {Uri}", result.NextResultUri);
                    // Fallback: simple increment if we can't parse? Or break?
                    // Safe fallback: increment token to avoid infinite loop on same page
                    token++;
                }
            }
            else
            {
                yield break;
            }

            // Small delay between fetches
            await Task.Delay(100, cancellationToken);
        }
    }

    private async Task CloseSessionAsync(string sessionHandle, CancellationToken cancellationToken)
    {
        try
        {
            await httpClient.DeleteAsync(
                $"{engineOptions.Flink.SqlGatewayUrl}/v1/sessions/{sessionHandle}",
                cancellationToken);

            logger.LogDebug("Closed Flink session: {SessionHandle}", sessionHandle);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to close Flink session: {SessionHandle}", sessionHandle);
        }
    }

    private async Task<string> GetOperationErrorAsync(
        string sessionHandle,
        string operationHandle,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetAsync(
                $"{engineOptions.Flink.SqlGatewayUrl}/v1/sessions/{sessionHandle}/operations/{operationHandle}/status",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return "Failed to retrieve error details";
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // Check for errors in the status
            if (root.TryGetProperty("status", out var status))
            {
                logger.LogDebug("Operation status: {Status}", status.GetString());
            }

            // Try to extract error from exceptionHistory
            if (root.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("errorMessage", out var errorMessage))
                {
                    return errorMessage.GetString() ?? "Unknown error";
                }
            }

            // Alternative: check for result with error
            var resultResponse = await httpClient.GetAsync(
                $"{engineOptions.Flink.SqlGatewayUrl}/v1/sessions/{sessionHandle}/operations/{operationHandle}/result/0",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!resultResponse.IsSuccessStatusCode)
            {
                var errorContent = await resultResponse.Content.ReadAsStringAsync(cancellationToken);
                // Try to parse Flink's error response
                try
                {
                    using var errorDoc = JsonDocument.Parse(errorContent);
                    if (errorDoc.RootElement.TryGetProperty("errors", out var errors) &&
                        errors.ValueKind == JsonValueKind.Array &&
                        errors.GetArrayLength() > 0)
                    {
                        var firstError = errors[0];
                        if (firstError.ValueKind == JsonValueKind.String)
                        {
                            var fullError = firstError.GetString() ?? "";
                            // Extract just the validation error message
                            if (fullError.Contains("ValidationException:"))
                            {
                                var parts = fullError.Split("ValidationException:");
                                if (parts.Length > 1)
                                {
                                    return "Validation error: " + parts[1].Trim();
                                }
                            }
                            return fullError;
                        }
                    }
                }
                catch
                {
                    // If parsing fails, return raw content
                    return errorContent.Length > 200 ? errorContent.Substring(0, 200) + "..." : errorContent;
                }
            }

            return "Query failed but no specific error message available";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to retrieve operation error details");
            return $"Error occurred: {ex.Message}";
        }
    }

    private async Task<FlinkJobDetailsResponse?> GetJobDetailsAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetAsync(
                $"{engineOptions.Flink.RestApiUrl}/jobs/{jobId}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<FlinkJobDetailsResponse>(content, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<FlinkJobDetailsResponse>> ListRunningJobsAsync(
        CancellationToken cancellationToken)
    {
        var jobs = new List<FlinkJobDetailsResponse>();

        try
        {
            var response = await httpClient.GetAsync(
                $"{engineOptions.Flink.RestApiUrl}/jobs",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return jobs;

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var jobsResponse = JsonSerializer.Deserialize<FlinkJobsResponse>(content, _jsonOptions);

            if (jobsResponse?.Jobs == null)
                return jobs;

            // Get details for each running job
            foreach (var job in jobsResponse.Jobs.Where(j => j.Status == "RUNNING"))
            {
                var details = await GetJobDetailsAsync(job.Id, cancellationToken);
                if (details != null)
                {
                    jobs.Add(details);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error listing running Flink jobs");
        }

        return jobs;
    }

    // ============================================
    // Helper Methods for SQL Script Parsing
    // ============================================

    /// <summary>
    /// Parses a SQL script into individual statements.
    /// Splits by semicolons and handles comments.
    /// </summary>
    private List<string> ParseSqlStatements(string sqlScript)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var lines = sqlScript.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Skip single-line comments that are on their own line
            if (trimmed.StartsWith("--"))
                continue;

            current.AppendLine(line);

            // Check if the statement ends with semicolon (ignoring trailing comments)
            // Regex to remove trailing comments: -- followed by anything to end of line
            var codeOnly = Regex.Replace(trimmed, @"--.*$", "").Trim();

            // Statement ends with semicolon
            if (codeOnly.EndsWith(";"))
            {
                var statement = current.ToString().Trim();
                if (!string.IsNullOrEmpty(statement))
                {
                    statements.Add(statement);
                }
                current.Clear();
            }
        }

        // Add any remaining content
        if (current.Length > 0)
        {
            var statement = current.ToString().Trim();
            if (!string.IsNullOrEmpty(statement))
            {
                statements.Add(statement);
            }
        }

        return statements;
    }

    /// <summary>
    /// Checks if a SQL statement is a SELECT query.
    /// </summary>
    private static bool IsSelectQuery(string statement)
    {
        var trimmed = statement.Trim();
        // Remove leading comments (/* ... */) if present (simple check)
        // This is a basic check - a full parser would be better
        if (trimmed.StartsWith("/*")) 
        {
             var endComment = trimmed.IndexOf("*/", StringComparison.Ordinal);
             if (endComment > -1)
             {
                 trimmed = trimmed.Substring(endComment + 2).Trim();
             }
        }
        
        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || 
               trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase); // Support CTEs
    }

    /// <summary>
    /// Extracts the output topic name from a SQL script.
    /// Looks for the INSERT INTO statement and finds the corresponding CREATE TABLE.
    /// </summary>
    private string ExtractOutputTopicFromScript(string sqlScript)
    {
        try
        {
            // Find the INSERT INTO statement
            var insertMatch = Regex.Match(
                sqlScript,
                @"INSERT\s+INTO\s+(\w+)",
                RegexOptions.IgnoreCase);

            if (!insertMatch.Success)
            {
                logger.LogWarning("Could not find INSERT INTO statement in script");
                return "unknown_topic";
            }

            var sinkTableName = insertMatch.Groups[1].Value;
            logger.LogDebug("Found sink table name: {TableName}", sinkTableName);

            // Find the CREATE TABLE statement for this sink
            var createMatch = Regex.Match(
                sqlScript,
                $@"CREATE\s+TABLE\s+{sinkTableName}\s*\([^\)]+\)\s*WITH\s*\([^\)]*'topic'\s*=\s*'([^']+)'",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (createMatch.Success)
            {
                var topic = createMatch.Groups[1].Value;
                logger.LogDebug("Extracted output topic: {Topic}", topic);
                return topic;
            }

            logger.LogWarning("Could not extract topic from CREATE TABLE for {TableName}", sinkTableName);
            return sinkTableName; // Return table name as fallback
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error extracting output topic from script");
            return "unknown_topic";
        }
    }

    /// <summary>
    /// Ensures that Kafka source tables in ad-hoc queries have bounded mode set.
    /// This prevents queries from waiting indefinitely for new data.
    /// Also ensures startup mode is 'earliest-offset' to capture existing data.
    /// </summary>
    private string EnsureBoundedModeForAdHocQuery(string statement)
    {
        var trimmed = statement.Trim();
        
        // Only modify CREATE TABLE statements for Kafka connectors
        if (!trimmed.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
        {
            return statement;
        }

        // Check if it's a Kafka connector
        if (!trimmed.Contains("'connector' = 'kafka'", StringComparison.OrdinalIgnoreCase))
        {
            return statement;
        }

        // Check if we need to modify anything
        var hasBoundedMode = trimmed.Contains("'scan.bounded.mode'", StringComparison.OrdinalIgnoreCase);
        var hasStartupMode = trimmed.Contains("'scan.startup.mode'", StringComparison.OrdinalIgnoreCase);

        if (hasBoundedMode && hasStartupMode)
        {
            return statement;
        }

        // Find the WITH clause
        var withMatch = Regex.Match(trimmed, @"\)\s*WITH\s*\((.*)\)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!withMatch.Success)
        {
            logger.LogWarning("Could not find WITH clause in CREATE TABLE statement");
            return statement;
        }

        // Find insertion point before the closing parenthesis of WITH clause
        var beforeClosing = trimmed.LastIndexOf(')');
        if (beforeClosing == -1)
        {
            return statement;
        }

        var propertiesToAdd = new List<string>();
        
        if (!hasBoundedMode)
        {
            propertiesToAdd.Add("'scan.bounded.mode' = 'latest-offset'");
            logger.LogInformation("✅ Auto-adding bounded mode to Kafka table for ad-hoc query");
        }
        
        if (!hasStartupMode)
        {
            propertiesToAdd.Add("'scan.startup.mode' = 'earliest-offset'");
            logger.LogInformation("✅ Auto-adding startup mode 'earliest-offset' to Kafka table for ad-hoc query");
        }

        if (propertiesToAdd.Count == 0) return statement;

        // Construct new statement
        var modifiedStatement = trimmed.Substring(0, beforeClosing).TrimEnd();
        
        // Ensure previous property has comma
        // Check strict ending, ignoring whitespace/newlines
        if (!modifiedStatement.TrimEnd().EndsWith(","))
        {
            modifiedStatement += ",";
        }
        
        modifiedStatement += "\n    " + string.Join(",\n    ", propertiesToAdd);
        modifiedStatement += "\n" + trimmed.Substring(beforeClosing);

        return modifiedStatement;
    }

}
