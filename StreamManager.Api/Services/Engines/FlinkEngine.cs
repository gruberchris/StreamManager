using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
        logger.LogInformation("Starting Flink ad-hoc query: {Query}", query);

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
        string? operationHandle = null;

        try
        {
            // Step 1: Create a session
            sessionHandle = await CreateSessionAsync(properties, cancellationToken);
            logger.LogInformation("Created Flink session: {SessionHandle}", sessionHandle);

            // Step 2: Execute the statement
            operationHandle = await ExecuteStatementAsync(sessionHandle, query, cancellationToken);
            logger.LogInformation("Started operation: {OperationHandle}", operationHandle);

            // Step 3: Wait for operation to be ready
            await WaitForOperationAsync(sessionHandle, operationHandle, cancellationToken);

            // Step 4: Fetch and stream results
            var resultCount = 0;
            await foreach (var result in FetchResultsAsync(sessionHandle, operationHandle, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogDebug("Query cancelled after {ResultCount} results", resultCount);
                    yield break;
                }

                resultCount++;
                yield return result;
            }

            logger.LogInformation("Query completed with {ResultCount} results", resultCount);
        }
        finally
        {
            // Step 5: Cleanup - close session
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
            logger.LogInformation("Deploying persistent Flink query: {Name}", name);

            var safeName = GenerateSafeName(name);
            var outputTopic = $"{safeName}_output";
            string? sessionHandle = null;

            try
            {
                // Create a session for deployment
                sessionHandle = await CreateSessionAsync(properties, cancellationToken);

                // Step 1: Create output table with Kafka connector
                // This table will receive the results from the streaming query
                var createTableStatement = GenerateCreateTableStatement(safeName, outputTopic);
                
                logger.LogInformation("Creating output table: {TableName}", safeName);
                logger.LogDebug("Create table statement: {Statement}", createTableStatement);

                var createOperation = await ExecuteStatementAsync(sessionHandle, createTableStatement, cancellationToken);
                await WaitForOperationAsync(sessionHandle, createOperation, cancellationToken);

                // Step 2: Submit the INSERT INTO statement
                // This creates the persistent streaming job
                var insertStatement = $"INSERT INTO {safeName} {query.TrimEnd(';')};";

                logger.LogInformation("Submitting streaming job with INSERT INTO");
                logger.LogDebug("Insert statement: {Statement}", insertStatement);

                var insertOperation = await ExecuteStatementAsync(sessionHandle, insertStatement, cancellationToken);

                // Wait for the job to start
                await Task.Delay(3000, cancellationToken);

                // Step 3: Find the job ID from Flink REST API
                var jobs = await ListRunningJobsAsync(cancellationToken);
                var latestJob = jobs.OrderByDescending(j => j.StartTime).FirstOrDefault();

                if (latestJob == null)
                {
                    return DeploymentResult.Failure("Failed to find created job in Flink. Job may not have started.");
                }

                var jobId = latestJob.Jid;

                logger.LogInformation(
                    "Successfully deployed Flink job. JobId: {JobId}, Table: {TableName}, Topic: {Topic}",
                    jobId, safeName, outputTopic);

                return DeploymentResult.Succeed(jobId, outputTopic, safeName);
            }
            finally
            {
                if (sessionHandle != null)
                {
                    await CloseSessionAsync(sessionHandle, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deploying persistent Flink query: {Name}", name);
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
            // PATCH /jobs/{jobId} with "stop" action
            var response = await httpClient.PatchAsync(
                $"{engineOptions.Flink.RestApiUrl}/jobs/{jobId}?mode=stop",
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
            sessionProps[prop.Key] = prop.Value?.ToString() ?? "";
        }

        // Override with user-provided properties
        if (properties != null)
        {
            foreach (var prop in properties)
            {
                sessionProps[prop.Key] = prop.Value?.ToString() ?? "";
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

        while (hasMore && !cancellationToken.IsCancellationRequested)
        {
            var response = await httpClient.GetAsync(
                $"{engineOptions.Flink.SqlGatewayUrl}/v1/sessions/{sessionHandle}/operations/{operationHandle}/result/{token}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to fetch results: {StatusCode}", response.StatusCode);
                yield break;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<FetchResultsResponse>(content, _jsonOptions);

            if (result?.Results?.Data != null)
            {
                foreach (var row in result.Results.Data)
                {
                    // Convert row to JSON format similar to ksqlDB
                    var rowJson = JsonSerializer.Serialize(row.Fields);
                    yield return rowJson;
                }

                token += result.Results.Data.Count;
            }

            // Check if there are more results
            hasMore = !string.IsNullOrEmpty(result?.NextResultUri);

            if (!hasMore)
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

    private static string GenerateSafeName(string name)
    {
        // Generate a safe name for Flink (alphanumeric + underscore)
        var safeName = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (string.IsNullOrEmpty(safeName) || !char.IsLetter(safeName[0]))
            safeName = "STREAM_" + safeName;

        return $"{safeName}_{Guid.NewGuid():N}".ToLowerInvariant();
    }

    /// <summary>
    /// Generates a CREATE TABLE statement for Flink with Kafka connector.
    /// Uses a dynamic schema approach that works with JSON format.
    /// </summary>
    private string GenerateCreateTableStatement(string tableName, string topicName)
    {
        var kafkaBootstrapServers = engineOptions.Flink.KafkaBootstrapServers;
        var kafkaFormat = engineOptions.Flink.KafkaFormat;

        // Create a flexible table schema that can accept any JSON structure
        // Using RAW type allows Flink to accept dynamic JSON schemas
        var createTableSql = $@"
CREATE TABLE IF NOT EXISTS {tableName} (
    event_data STRING
) WITH (
    'connector' = 'kafka',
    'topic' = '{topicName}',
    'properties.bootstrap.servers' = '{kafkaBootstrapServers}',
    'format' = '{kafkaFormat}',
    'scan.startup.mode' = 'latest-offset'
)";

        return createTableSql;
    }
}
