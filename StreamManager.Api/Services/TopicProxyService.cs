using Confluent.Kafka;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StreamManager.Api.Data;
using StreamManager.Api.Hubs;

namespace StreamManager.Api.Services;

public class TopicProxyService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<KsqlHub> _hubContext;
    private readonly ILogger<TopicProxyService> _logger;
    private readonly Dictionary<string, IConsumer<string, string>> _consumers = new();
    private readonly string _kafkaBootstrapServers;

    public TopicProxyService(IServiceProvider serviceProvider, IHubContext<KsqlHub> hubContext, 
        ILogger<TopicProxyService> logger, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;
        _kafkaBootstrapServers = configuration.GetConnectionString("Kafka") ?? "localhost:9092";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAndConsumeActiveStreams(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TopicProxyService execution");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task ScanAndConsumeActiveStreams(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<StreamManagerDbContext>();

        var activeStreams = await context.StreamDefinitions
            .Where(s => s.IsActive && !string.IsNullOrEmpty(s.OutputTopic))
            .ToListAsync(cancellationToken);

        // Start consumers for new topics
        foreach (var stream in activeStreams)
        {
            if (!_consumers.ContainsKey(stream.OutputTopic!) && !string.IsNullOrWhiteSpace(stream.OutputTopic))
            {
                try
                {
                    var consumer = CreateConsumer(stream.OutputTopic);
                    _consumers[stream.OutputTopic] = consumer;
                    
                    // Start consuming in background task
                    _ = Task.Run(() => ConsumeTopicAsync(consumer, stream.OutputTopic, cancellationToken), cancellationToken);
                    
                    _logger.LogInformation("Started consuming topic {TopicName}", stream.OutputTopic);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start consumer for topic {TopicName}", stream.OutputTopic);
                }
            }
        }

        // Stop consumers for removed topics
        var activeTopics = activeStreams.Where(s => !string.IsNullOrEmpty(s.OutputTopic)).Select(s => s.OutputTopic!).ToHashSet();
        var consumersToRemove = _consumers.Keys.Where(topic => !activeTopics.Contains(topic)).ToList();

        foreach (var topic in consumersToRemove)
        {
            try
            {
                if (_consumers.TryGetValue(topic, out var consumer))
                {
                    try
                    {
                        consumer.Unsubscribe();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error unsubscribing consumer for topic {TopicName}", topic);
                    }
                    
                    try
                    {
                        consumer.Close();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error closing consumer for topic {TopicName}", topic);
                    }
                    
                    try
                    {
                        consumer.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error disposing consumer for topic {TopicName}", topic);
                    }
                    
                    _consumers.Remove(topic);
                    _logger.LogInformation("Stopped consuming topic {TopicName}", topic);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping consumer for topic {TopicName}", topic);
            }
        }
    }

    private async Task ConsumeTopicAsync(IConsumer<string, string> consumer, string topicName, CancellationToken cancellationToken)
    {
        try
        {
            int retryCount = 0;
            const int maxRetries = 10;
            
            while (retryCount < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // ksqlDB creates topics in uppercase, but our stored name might be lowercase
        var actualTopicName = topicName.ToUpperInvariant();
        _logger.LogInformation("Subscribing to topic {TopicName} (actual: {ActualTopicName})", topicName, actualTopicName);
        consumer.Subscribe(actualTopicName);

                    while (!cancellationToken.IsCancellationRequested && _consumers.ContainsKey(topicName))
                    {
                        try
                        {
                            var consumeResult = consumer.Consume(TimeSpan.FromSeconds(1));
                            if (consumeResult?.Message != null)
                            {
                                var messageValue = consumeResult.Message.Value ?? "";
                                var messageKey = consumeResult.Message.Key ?? "";
                                
                                _logger.LogInformation("Raw message from topic {TopicName}: Key='{Key}', Value='{Value}' (Length: {Length}), Offset={Offset}, Partition={Partition}", 
                                    topicName, messageKey, messageValue, messageValue.Length, consumeResult.Offset, consumeResult.Partition);

                                // Send the message to the SignalR group (use original topic name as group name)
                                await _hubContext.Clients.Group(topicName).SendAsync("ReceiveMessage", messageValue, cancellationToken);
                                
                                if (string.IsNullOrEmpty(messageValue))
                                {
                                    _logger.LogWarning("Received empty message value from topic {TopicName} - Headers: {Headers}", 
                                        topicName, string.Join(", ", consumeResult.Message.Headers?.Select(h => $"{h.Key}={System.Text.Encoding.UTF8.GetString(h.GetValueBytes())}") ?? new string[0]));
                                }
                            }
                            else if (consumeResult != null)
                            {
                                _logger.LogDebug("Received empty message from topic {TopicName}", topicName);
                            }
                        }
                        catch (ConsumeException ex) when (ex.Error.Code != ErrorCode.UnknownTopicOrPart)
                        {
                            _logger.LogError(ex, "Error consuming message from topic {TopicName}", topicName);
                        }
                    }
                    break; // If we get here, consumption was successful
                }
                catch (ConsumeException ex) when (ex.Error.Code == ErrorCode.UnknownTopicOrPart)
                {
                    retryCount++;
                    _logger.LogWarning("Topic {TopicName} not available yet (attempt {Attempt}/{MaxAttempts}), retrying in 5 seconds...", topicName, retryCount, maxRetries);
                    
                    if (retryCount >= maxRetries)
                    {
                        _logger.LogError(ex, "Topic {TopicName} still not available after {MaxRetries} attempts", topicName, maxRetries);
                        throw;
                    }
                    
                    await Task.Delay(5000, cancellationToken);
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in consumer for topic {TopicName}", topicName);
        }
        finally
        {
            try
            {
                consumer.Unsubscribe();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unsubscribing from topic {TopicName}", topicName);
            }
        }
    }

    private IConsumer<string, string> CreateConsumer(string topicName)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _kafkaBootstrapServers,
            GroupId = $"stream-manager-proxy-{Environment.MachineName}-{DateTime.UtcNow.Ticks}",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = true,
            StatisticsIntervalMs = 5000,
            SessionTimeoutMs = 6000,
            AutoCommitIntervalMs = 100,
            EnablePartitionEof = true
        };

        return new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) => _logger.LogError("Consumer error: {Reason}", e.Reason))
            .Build();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Clean up all consumers more defensively
        var consumersToCleanup = _consumers.ToList();
        foreach (var kvp in consumersToCleanup)
        {
            try
            {
                var consumer = kvp.Value;
                
                try
                {
                    consumer.Unsubscribe();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error unsubscribing consumer for topic {TopicName}", kvp.Key);
                }
                
                try
                {
                    consumer.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error closing consumer for topic {TopicName}", kvp.Key);
                }
                
                try
                {
                    consumer.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing consumer for topic {TopicName}", kvp.Key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up consumer for topic {TopicName}", kvp.Key);
            }
        }
        _consumers.Clear();

        await base.StopAsync(cancellationToken);
    }
}