namespace StreamManager.Api.Configuration;

/// <summary>
/// Configuration for stream processing engine selection and settings
/// </summary>
public class StreamEngineOptions
{
    public const string SectionName = "StreamEngine";
    
    /// <summary>
    /// Which stream processing engine to use (KsqlDb or Flink)
    /// </summary>
    public StreamEngineProvider Provider { get; set; } = StreamEngineProvider.KsqlDb;
    
    /// <summary>
    /// ksqlDB-specific configuration
    /// </summary>
    public KsqlDbOptions KsqlDb { get; set; } = new();
    
    /// <summary>
    /// Apache Flink-specific configuration
    /// </summary>
    public FlinkOptions Flink { get; set; } = new();
}

/// <summary>
/// Available stream processing engine providers
/// </summary>
public enum StreamEngineProvider
{
    /// <summary>
    /// Confluent ksqlDB - SQL stream processing on Kafka
    /// </summary>
    KsqlDb = 0,
    
    /// <summary>
    /// Apache Flink - Distributed stream processing framework
    /// </summary>
    Flink = 1
}

/// <summary>
/// Configuration options for ksqlDB engine
/// </summary>
public class KsqlDbOptions
{
    /// <summary>
    /// ksqlDB server URL (e.g., http://localhost:8088)
    /// </summary>
    public string Url { get; set; } = "http://localhost:8088";
}

/// <summary>
/// Configuration options for Apache Flink engine
/// </summary>
public class FlinkOptions
{
    /// <summary>
    /// Flink SQL Gateway URL for query submission (e.g., http://localhost:8083)
    /// </summary>
    public string SqlGatewayUrl { get; set; } = "http://localhost:8083";
    
    /// <summary>
    /// Flink REST API URL for job management (e.g., http://localhost:8081)
    /// </summary>
    public string RestApiUrl { get; set; } = "http://localhost:8081";
    
    /// <summary>
    /// Kafka bootstrap servers for Flink Kafka connector (e.g., localhost:9092)
    /// </summary>
    public string KafkaBootstrapServers { get; set; } = "localhost:9092";
    
    /// <summary>
    /// Default Kafka connector format (json, avro, csv)
    /// </summary>
    public string KafkaFormat { get; set; } = "json";
}
