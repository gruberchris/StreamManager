# Future Enhancements - Detailed Explanations

**Date:** December 7, 2025  
**Status:** 📋 Planning Document

---

## Overview

This document provides detailed explanations of proposed future enhancements for the StreamManager Flink engine. These are **not currently implemented** but represent valuable improvements for future versions.

---

## 1. Schema Inference

### **Current State**

Today, when you deploy a Flink stream, we create a simple table:

```sql
CREATE TABLE output_table (
    event_data STRING  -- Everything goes into one column as JSON string
) WITH (
    'connector' = 'kafka',
    'topic' = 'output_topic',
    'format' = 'json'
);
```

**Problem:**
- Everything is a string (no type safety)
- Downstream consumers must parse JSON manually
- No schema validation
- Can't use Flink's type system effectively

---

### **Proposed Enhancement: Automatic Schema Inference**

**Goal:** Automatically detect the output schema from the user's query and create a properly typed table.

---

#### **How It Would Work**

**User Input:**
```sql
SELECT 
    order_id, 
    customer_name, 
    total_amount, 
    order_date 
FROM orders 
WHERE total_amount > 100
```

**Current Behavior:**
```sql
CREATE TABLE output (
    event_data STRING  -- Just stores JSON as string
)
```

**Enhanced Behavior (Proposed):**
```sql
CREATE TABLE output (
    order_id BIGINT,
    customer_name STRING,
    total_amount DECIMAL(10,2),
    order_date TIMESTAMP(3),
    PRIMARY KEY (order_id) NOT ENFORCED
) WITH (
    'connector' = 'kafka',
    'topic' = 'output_topic',
    'format' = 'json'
);
```

---

#### **Implementation Approach**

**Step 1: Parse the Query**

```csharp
private async Task<TableSchema> InferSchemaFromQuery(
    string query, 
    string sessionHandle,
    CancellationToken cancellationToken)
{
    // Execute EXPLAIN or DESCRIBE on the query to get schema
    var explainQuery = $"EXPLAIN {query}";
    var result = await ExecuteStatementAsync(sessionHandle, explainQuery, cancellationToken);
    
    // Parse the schema from the explain result
    var schema = ParseSchemaFromExplain(result);
    
    return schema;
}
```

**Step 2: Generate Typed Table**

```csharp
private string GenerateCreateTableStatement(
    string tableName, 
    string topicName,
    TableSchema schema)  // ← New parameter
{
    var columns = string.Join(",\n    ", 
        schema.Columns.Select(c => $"{c.Name} {c.DataType}"));
    
    return $@"
CREATE TABLE IF NOT EXISTS {tableName} (
    {columns}
) WITH (
    'connector' = 'kafka',
    'topic' = '{topicName}',
    'properties.bootstrap.servers' = '{_kafkaBootstrap}',
    'format' = 'json',
    'json.fail-on-missing-field' = 'false',
    'json.ignore-parse-errors' = 'true'
)";
}
```

---

#### **Example Usage**

**Scenario: Analytics Dashboard**

```sql
-- User creates aggregation query
SELECT 
    customer_id,
    COUNT(*) as order_count,
    SUM(total) as total_spent,
    AVG(total) as avg_order_value,
    MAX(order_date) as last_order_date
FROM orders
GROUP BY customer_id
HAVING COUNT(*) > 5
```

**Generated Table (Auto-inferred):**
```sql
CREATE TABLE customer_metrics (
    customer_id BIGINT NOT NULL,
    order_count BIGINT NOT NULL,
    total_spent DECIMAL(10,2),
    avg_order_value DECIMAL(10,2),
    last_order_date TIMESTAMP(3),
    PRIMARY KEY (customer_id) NOT ENFORCED
) WITH (
    'connector' = 'kafka',
    'topic' = 'customer_metrics_output',
    'format' = 'json'
);
```

**Benefits:**
- ✅ Downstream consumers see typed data
- ✅ Better performance (no JSON parsing)
- ✅ Type safety and validation
- ✅ Easier to work with in BI tools

---

#### **Type Mapping**

| Flink SQL Type | Kafka JSON | Java/C# Type |
|----------------|------------|--------------|
| `BIGINT` | `number` | `long` / `Int64` |
| `INT` | `number` | `int` / `Int32` |
| `STRING` | `string` | `String` |
| `DECIMAL(p,s)` | `number` | `BigDecimal` / `decimal` |
| `TIMESTAMP(3)` | `string (ISO)` | `Instant` / `DateTime` |
| `BOOLEAN` | `boolean` | `boolean` / `bool` |
| `DOUBLE` | `number` | `double` |
| `ARRAY<T>` | `array` | `List<T>` |
| `ROW(...)` | `object` | Object / Class |

---

#### **Challenges**

1. **Complex Queries**
   - Nested subqueries
   - Multiple joins
   - Window functions
   - **Solution:** Fall back to `event_data STRING` for complex cases

2. **Dynamic Schemas**
   - Query output changes based on data
   - **Solution:** Use `ROW` type or allow schema evolution

3. **Flink Version Compatibility**
   - Different Flink versions support different types
   - **Solution:** Detect Flink version, use compatible types

---

#### **Implementation Phases**

**Phase 1: Basic Inference**
- Simple SELECT with column list
- Basic types (INT, STRING, BIGINT)
- No aggregations

**Phase 2: Aggregations**
- COUNT, SUM, AVG, MIN, MAX
- GROUP BY support
- HAVING clauses

**Phase 3: Advanced**
- Window functions
- Complex joins
- Nested structures (ROW, ARRAY)

---

#### **Configuration**

```json
{
  "Flink": {
    "SchemaInference": {
      "Enabled": true,
      "FallbackToString": true,  // Use STRING if inference fails
      "MaxComplexity": 5,         // Max query complexity for inference
      "IncludePrimaryKey": false  // Generate PRIMARY KEY constraints
    }
  }
}
```

---

## 2. Schema Registry Integration

### **Current State**

When using Avro format:

```json
"KafkaFormat": "avro"
```

**Problem:**
- Avro schemas must be managed manually
- No automatic schema registration
- No schema evolution support
- Harder to maintain consistency

---

### **Proposed Enhancement: Confluent Schema Registry Integration**

**Goal:** Automatically register Avro schemas with Confluent Schema Registry and support schema evolution.

---

#### **How It Would Work**

**Configuration:**
```json
{
  "Flink": {
    "KafkaFormat": "avro",
    "SchemaRegistry": {
      "Url": "http://localhost:8081",
      "AutoRegister": true,
      "CompatibilityMode": "BACKWARD"  // BACKWARD, FORWARD, FULL, NONE
    }
  }
}
```

**Deployment Flow:**

```
User: Deploy stream with Avro format
    ↓
Infer Schema from Query
    {
      "type": "record",
      "name": "CustomerMetrics",
      "fields": [
        {"name": "customer_id", "type": "long"},
        {"name": "order_count", "type": "long"},
        {"name": "total_spent", "type": "double"}
      ]
    }
    ↓
Register Schema with Schema Registry
    POST http://localhost:8081/subjects/customer_metrics-value/versions
    {
      "schema": "{...}"
    }
    ↓
Get Schema ID (e.g., 123)
    ↓
Create Flink Table with Schema Registry
    CREATE TABLE customer_metrics (...)
    WITH (
      'connector' = 'kafka',
      'format' = 'avro-confluent',
      'avro-confluent.url' = 'http://localhost:8081'
    )
    ↓
Insert data (Flink serializes with Schema ID)
```

---

#### **Benefits**

**1. Automatic Schema Management**
```csharp
// Before (Manual)
1. Create Avro schema file (.avsc)
2. Register schema manually: curl POST /subjects/.../versions
3. Configure Flink with schema ID
4. Keep schema and table in sync manually

// After (Automatic)
1. Deploy stream
   ✅ Schema auto-generated from query
   ✅ Schema auto-registered
   ✅ Table auto-created with schema reference
```

**2. Schema Evolution**
```csharp
// V1: Initial schema
{
  "customer_id": "long",
  "order_count": "long"
}

// V2: Add new field (backward compatible)
{
  "customer_id": "long",
  "order_count": "long",
  "total_spent": "double"  // ← New field with default
}

// Schema Registry validates compatibility ✅
```

**3. Multi-Consumer Support**
- All consumers use same schema
- Automatic deserialization
- Type safety across services

---

#### **Implementation**

```csharp
public interface ISchemaRegistryClient
{
    Task<int> RegisterSchemaAsync(string subject, string schema);
    Task<string> GetSchemaAsync(string subject, int version);
    Task<bool> CheckCompatibilityAsync(string subject, string schema);
}

public class ConfluentSchemaRegistryClient : ISchemaRegistryClient
{
    private readonly HttpClient _httpClient;
    private readonly string _registryUrl;
    
    public async Task<int> RegisterSchemaAsync(string subject, string schema)
    {
        var request = new { schema = schema };
        var response = await _httpClient.PostAsJsonAsync(
            $"{_registryUrl}/subjects/{subject}/versions",
            request
        );
        
        var result = await response.Content.ReadFromJsonAsync<SchemaIdResponse>();
        return result.Id;
    }
}
```

**Usage in FlinkEngine:**

```csharp
private async Task<string> GenerateCreateTableStatementWithSchemaRegistry(
    string tableName,
    string topicName,
    TableSchema schema,
    CancellationToken cancellationToken)
{
    if (_engineOptions.Flink.KafkaFormat == "avro" && 
        _engineOptions.Flink.SchemaRegistry?.Enabled == true)
    {
        // Convert TableSchema to Avro schema
        var avroSchema = ConvertToAvroSchema(schema, tableName);
        
        // Register with Schema Registry
        var subject = $"{topicName}-value";
        var schemaId = await _schemaRegistry.RegisterSchemaAsync(
            subject, 
            avroSchema
        );
        
        _logger.LogInformation(
            "Registered Avro schema for {Topic} with ID {SchemaId}", 
            topicName, 
            schemaId
        );
        
        // Create table with Schema Registry reference
        return $@"
CREATE TABLE IF NOT EXISTS {tableName} (
    {GenerateColumnList(schema)}
) WITH (
    'connector' = 'kafka',
    'topic' = '{topicName}',
    'properties.bootstrap.servers' = '{_kafkaBootstrap}',
    'format' = 'avro-confluent',
    'avro-confluent.url' = '{_schemaRegistryUrl}'
)";
    }
    
    // Fallback to plain JSON
    return GenerateCreateTableStatement(tableName, topicName);
}
```

---

#### **Schema Compatibility Modes**

| Mode | Description | Use Case |
|------|-------------|----------|
| **BACKWARD** | New schema can read old data | Adding optional fields |
| **FORWARD** | Old schema can read new data | Removing fields |
| **FULL** | Both backward and forward | Maximum flexibility |
| **NONE** | No validation | Development only |

**Example Configuration:**
```json
{
  "SchemaRegistry": {
    "CompatibilityMode": "BACKWARD"
  }
}
```

**What Happens:**
```csharp
// Schema Registry checks compatibility before allowing registration
if (!await _schemaRegistry.CheckCompatibilityAsync(subject, newSchema))
{
    throw new InvalidOperationException(
        "New schema is not backward compatible with existing schema"
    );
}
```

---

#### **Real-World Example**

**Scenario: E-commerce Order Stream**

**V1: Initial Deployment**
```sql
SELECT 
    order_id,
    customer_id,
    total_amount
FROM orders
```

**Generated Avro Schema V1:**
```json
{
  "type": "record",
  "name": "Order",
  "namespace": "com.streammanager.orders",
  "fields": [
    {"name": "order_id", "type": "long"},
    {"name": "customer_id", "type": "long"},
    {"name": "total_amount", "type": "double"}
  ]
}
```

**V2: Add Shipping Info (Backward Compatible)**
```sql
SELECT 
    order_id,
    customer_id,
    total_amount,
    shipping_address  -- New field
FROM orders
```

**Generated Avro Schema V2:**
```json
{
  "type": "record",
  "name": "Order",
  "namespace": "com.streammanager.orders",
  "fields": [
    {"name": "order_id", "type": "long"},
    {"name": "customer_id", "type": "long"},
    {"name": "total_amount", "type": "double"},
    {"name": "shipping_address", "type": ["null", "string"], "default": null}
  ]
}
```

**Schema Registry Response:**
```json
{
  "compatible": true,
  "message": "Schema is backward compatible"
}
```

**Old consumers:** Continue reading without issues ✅  
**New consumers:** Can access new field ✅  

---

## 3. Custom Table Options

### **Current State**

All tables use same configuration:

```sql
CREATE TABLE output (
    event_data STRING
) WITH (
    'connector' = 'kafka',
    'topic' = 'output_topic',
    'properties.bootstrap.servers' = 'localhost:9092',
    'format' = 'json'
    -- ↑ Fixed configuration for ALL streams
);
```

**Problem:**
- Can't customize partitions per stream
- Can't set retention per stream
- Can't configure compression per stream
- One-size-fits-all approach

---

### **Proposed Enhancement: Per-Stream Custom Options**

**Goal:** Allow users to specify custom Kafka and Flink options per stream.

---

#### **How It Would Work**

**API Request with Custom Options:**

```bash
curl -X POST http://localhost:7068/api/streams \
  -H "Content-Type: application/json" \
  -d '{
    "name": "High Throughput Orders",
    "sqlScript": "SELECT * FROM orders WHERE amount > 1000",
    "tableOptions": {
      "kafka.partitions": "12",
      "kafka.replication-factor": "3",
      "kafka.compression.type": "snappy",
      "kafka.retention.ms": "604800000",
      "flink.parallelism": "8",
      "scan.startup.mode": "earliest-offset"
    }
  }'
```

**Generated Table:**

```sql
CREATE TABLE high_throughput_orders_abc123 (
    event_data STRING
) WITH (
    'connector' = 'kafka',
    'topic' = 'high_throughput_orders_abc123_output',
    'properties.bootstrap.servers' = 'localhost:9092',
    'format' = 'json',
    
    -- Custom options from request ↓
    'topic.partitions' = '12',
    'topic.replication-factor' = '3',
    'properties.compression.type' = 'snappy',
    'properties.retention.ms' = '604800000',
    'scan.startup.mode' = 'earliest-offset'
);
```

---

#### **Supported Options Categories**

**1. Kafka Topic Configuration**

```json
{
  "tableOptions": {
    "kafka.partitions": "12",              // Topic partition count
    "kafka.replication-factor": "3",       // Replication factor
    "kafka.min.insync.replicas": "2",      // Minimum in-sync replicas
    "kafka.retention.ms": "604800000",     // 7 days retention
    "kafka.compression.type": "snappy",    // snappy, gzip, lz4, zstd
    "kafka.cleanup.policy": "delete"       // delete or compact
  }
}
```

**2. Flink Performance Tuning**

```json
{
  "tableOptions": {
    "flink.parallelism": "8",              // Task parallelism
    "flink.max-parallelism": "128",        // Max parallelism for rescaling
    "scan.startup.mode": "earliest-offset", // earliest, latest, timestamp
    "scan.bounded.mode": "unbounded",      // bounded or unbounded
    "sink.buffer-flush.max-rows": "1000",  // Batch size
    "sink.buffer-flush.interval": "5s"     // Flush interval
  }
}
```

**3. Data Format Options**

```json
{
  "tableOptions": {
    "format": "json",
    "json.fail-on-missing-field": "false",
    "json.ignore-parse-errors": "true",
    "json.timestamp-format.standard": "ISO-8601"
  }
}
```

**4. Schema and Serialization**

```json
{
  "tableOptions": {
    "format": "avro-confluent",
    "avro-confluent.url": "http://schema-registry:8081",
    "avro-confluent.schema-subject": "orders-value"
  }
}
```

---

#### **Implementation**

**1. Update StreamDefinition Model**

```csharp
public class StreamDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string SqlScript { get; set; }
    public string? JobId { get; set; }
    public string? StreamName { get; set; }
    public string? OutputTopic { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // ↓ NEW: Store custom options as JSON
    public Dictionary<string, string>? TableOptions { get; set; }
}
```

**2. Update Database Schema**

```sql
ALTER TABLE "StreamDefinitions"
ADD COLUMN "TableOptions" jsonb NULL;
```

**3. Update Controller**

```csharp
public record CreateStreamRequest(
    string Name, 
    string SqlScript,
    Dictionary<string, string>? TableOptions = null  // ← NEW
);

[HttpPost]
public async Task<IActionResult> CreateStream(CreateStreamRequest request)
{
    var stream = new StreamDefinition
    {
        Name = request.Name,
        SqlScript = request.SqlScript,
        TableOptions = request.TableOptions  // ← Store options
    };
    
    _context.StreamDefinitions.Add(stream);
    await _context.SaveChangesAsync();
    
    return Ok(stream);
}
```

**4. Update FlinkEngine**

```csharp
private string GenerateCreateTableStatement(
    string tableName, 
    string topicName,
    Dictionary<string, string>? customOptions = null)
{
    var withOptions = new Dictionary<string, string>
    {
        ["connector"] = "kafka",
        ["topic"] = topicName,
        ["properties.bootstrap.servers"] = _engineOptions.Flink.KafkaBootstrapServers,
        ["format"] = _engineOptions.Flink.KafkaFormat,
        ["scan.startup.mode"] = "latest-offset"
    };
    
    // Merge custom options
    if (customOptions != null)
    {
        foreach (var option in customOptions)
        {
            // Map user-friendly names to Flink option names
            var flinkOption = MapToFlinkOption(option.Key);
            withOptions[flinkOption] = option.Value;
        }
    }
    
    var withClause = string.Join(",\n    ", 
        withOptions.Select(kv => $"'{kv.Key}' = '{kv.Value}'"));
    
    return $@"
CREATE TABLE IF NOT EXISTS {tableName} (
    event_data STRING
) WITH (
    {withClause}
)";
}

private string MapToFlinkOption(string userOption)
{
    return userOption switch
    {
        "kafka.partitions" => "topic.partitions",
        "kafka.replication-factor" => "topic.replication-factor",
        "kafka.compression.type" => "properties.compression.type",
        "kafka.retention.ms" => "properties.retention.ms",
        "flink.parallelism" => "sink.parallelism",
        _ => userOption // Pass through unknown options
    };
}
```

---

#### **Use Cases**

**1. High-Volume Stream (Large Partitions)**

```json
{
  "name": "Payment Transactions",
  "sqlScript": "SELECT * FROM payments",
  "tableOptions": {
    "kafka.partitions": "24",
    "kafka.replication-factor": "3",
    "flink.parallelism": "12",
    "sink.buffer-flush.max-rows": "5000"
  }
}
```

**Why:** Handle millions of events per second

---

**2. Long-Term Storage (Extended Retention)**

```json
{
  "name": "Audit Logs",
  "sqlScript": "SELECT * FROM audit_events",
  "tableOptions": {
    "kafka.retention.ms": "31536000000",  // 1 year
    "kafka.cleanup.policy": "delete",
    "kafka.compression.type": "gzip"
  }
}
```

**Why:** Compliance requirements

---

**3. Low-Latency Stream (Minimal Buffering)**

```json
{
  "name": "Fraud Detection",
  "sqlScript": "SELECT * FROM transactions WHERE is_suspicious = true",
  "tableOptions": {
    "sink.buffer-flush.max-rows": "1",
    "sink.buffer-flush.interval": "0s",
    "kafka.partitions": "6",
    "flink.parallelism": "6"
  }
}
```

**Why:** Immediate fraud alerts

---

**4. Replay Historical Data**

```json
{
  "name": "Historical Analysis",
  "sqlScript": "SELECT * FROM orders WHERE order_date >= '2024-01-01'",
  "tableOptions": {
    "scan.startup.mode": "earliest-offset",
    "scan.bounded.mode": "bounded",
    "scan.bounded.end-offset": "latest"
  }
}
```

**Why:** One-time historical data processing

---

#### **Validation**

```csharp
public class TableOptionsValidator
{
    private static readonly HashSet<string> AllowedOptions = new()
    {
        "kafka.partitions",
        "kafka.replication-factor",
        "kafka.compression.type",
        "kafka.retention.ms",
        "flink.parallelism",
        "scan.startup.mode",
        // ... more allowed options
    };
    
    private static readonly Dictionary<string, (int Min, int Max)> NumericRanges = new()
    {
        ["kafka.partitions"] = (1, 1000),
        ["kafka.replication-factor"] = (1, 10),
        ["flink.parallelism"] = (1, 128)
    };
    
    public ValidationResult Validate(Dictionary<string, string> options)
    {
        foreach (var option in options)
        {
            // Check if option is allowed
            if (!AllowedOptions.Contains(option.Key))
            {
                return ValidationResult.Error(
                    $"Option '{option.Key}' is not allowed"
                );
            }
            
            // Validate numeric ranges
            if (NumericRanges.TryGetValue(option.Key, out var range))
            {
                if (!int.TryParse(option.Value, out var value) ||
                    value < range.Min || value > range.Max)
                {
                    return ValidationResult.Error(
                        $"Option '{option.Key}' must be between {range.Min} and {range.Max}"
                    );
                }
            }
        }
        
        return ValidationResult.Success();
    }
}
```

---

#### **Configuration Presets**

For easier use, provide preset configurations:

```json
{
  "Flink": {
    "TableOptionPresets": {
      "high-throughput": {
        "kafka.partitions": "12",
        "kafka.compression.type": "snappy",
        "flink.parallelism": "8",
        "sink.buffer-flush.max-rows": "5000"
      },
      "low-latency": {
        "kafka.partitions": "6",
        "flink.parallelism": "6",
        "sink.buffer-flush.max-rows": "1",
        "sink.buffer-flush.interval": "0s"
      },
      "long-retention": {
        "kafka.retention.ms": "31536000000",
        "kafka.compression.type": "gzip"
      }
    }
  }
}
```

**Usage:**
```json
{
  "name": "My Stream",
  "sqlScript": "SELECT ...",
  "preset": "high-throughput"  // ← Use preset
}
```

---

## 4. Multi-Cluster Support

### **Current State**

Single Kafka cluster configuration:

```json
{
  "Flink": {
    "KafkaBootstrapServers": "localhost:9092"
  }
}
```

**Problem:**
- Can only deploy to one Kafka cluster
- No disaster recovery
- No multi-region support
- Can't route different streams to different clusters

---

### **Proposed Enhancement: Multiple Kafka Cluster Support**

**Goal:** Deploy streams to different Kafka clusters based on requirements.

---

#### **How It Would Work**

**Configuration:**

```json
{
  "Flink": {
    "KafkaClusters": {
      "primary": {
        "BootstrapServers": "kafka1:9092,kafka2:9092,kafka3:9092",
        "SecurityProtocol": "SASL_SSL",
        "SaslMechanism": "PLAIN",
        "SaslUsername": "user1",
        "SaslPassword": "password1",
        "Region": "us-east-1"
      },
      "backup": {
        "BootstrapServers": "kafka-backup1:9092,kafka-backup2:9092",
        "SecurityProtocol": "SASL_SSL",
        "SaslMechanism": "PLAIN",
        "SaslUsername": "user2",
        "SaslPassword": "password2",
        "Region": "us-west-2"
      },
      "analytics": {
        "BootstrapServers": "analytics-broker:29092",
        "SecurityProtocol": "PLAINTEXT",
        "Region": "us-east-1"
      }
    },
    "DefaultCluster": "primary"
  }
}
```

---

#### **Usage**

**Deploy to Specific Cluster:**

```bash
curl -X POST http://localhost:7068/api/streams \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Critical Orders",
    "sqlScript": "SELECT * FROM orders WHERE priority = '\''HIGH'\''",
    "kafkaCluster": "primary"  // ← Specify cluster
  }'
```

**Deploy to Multiple Clusters (Replication):**

```bash
curl -X POST http://localhost:7068/api/streams \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Critical Orders",
    "sqlScript": "SELECT * FROM orders WHERE priority = '\''HIGH'\''",
    "kafkaClusters": ["primary", "backup"]  // ← Multi-cluster deployment
  }'
```

---

#### **Implementation**

**1. Update Model**

```csharp
public class StreamDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string SqlScript { get; set; }
    public string? JobId { get; set; }
    public string? StreamName { get; set; }
    public string? OutputTopic { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // ↓ NEW: Target Kafka cluster(s)
    public List<string> KafkaClusters { get; set; } = new() { "primary" };
    
    // Store cluster-specific job IDs
    public Dictionary<string, string>? ClusterJobIds { get; set; }
}
```

**2. Update FlinkEngine**

```csharp
public async Task<DeploymentResult> DeployPersistentQueryAsync(
    string name,
    string query,
    Dictionary<string, object>? properties,
    List<string>? targetClusters = null,  // ← NEW
    CancellationToken cancellationToken = default)
{
    var clusters = targetClusters ?? new List<string> { _defaultCluster };
    var deploymentResults = new Dictionary<string, DeploymentResult>();
    
    foreach (var clusterName in clusters)
    {
        var cluster = _engineOptions.Flink.KafkaClusters[clusterName];
        
        // Deploy to this cluster
        var result = await DeployToClusterAsync(
            name, 
            query, 
            cluster, 
            properties,
            cancellationToken
        );
        
        deploymentResults[clusterName] = result;
    }
    
    // Return combined result
    return CombineDeploymentResults(deploymentResults);
}

private async Task<DeploymentResult> DeployToClusterAsync(
    string name,
    string query,
    KafkaClusterConfig cluster,
    Dictionary<string, object>? properties,
    CancellationToken cancellationToken)
{
    var safeName = GenerateSafeName(name);
    var outputTopic = $"{safeName}_output";
    
    // Create table with cluster-specific settings
    var createTableStatement = $@"
CREATE TABLE IF NOT EXISTS {safeName} (
    event_data STRING
) WITH (
    'connector' = 'kafka',
    'topic' = '{outputTopic}',
    'properties.bootstrap.servers' = '{cluster.BootstrapServers}',
    'properties.security.protocol' = '{cluster.SecurityProtocol}',
    'properties.sasl.mechanism' = '{cluster.SaslMechanism}',
    'properties.sasl.jaas.config' = '{cluster.GetJaasConfig()}',
    'format' = 'json'
)";
    
    // Execute deployment...
    // (rest of deployment logic)
}
```

---

#### **Use Cases**

**1. Disaster Recovery**

```json
{
  "name": "Critical Transactions",
  "sqlScript": "SELECT * FROM transactions",
  "kafkaClusters": ["primary", "backup"]
}
```

**Result:**
- Same stream running in two regions
- If primary fails, backup continues
- Automatic failover capability

---

**2. Data Segregation**

```json
{
  "name": "Customer PII",
  "sqlScript": "SELECT customer_id, email, phone FROM customers",
  "kafkaCluster": "secure-cluster"  // Dedicated cluster for sensitive data
}
```

**Result:**
- PII data isolated
- Different security policies
- Compliance with data regulations

---

**3. Workload Separation**

```json
// Analytics workload
{
  "name": "Daily Metrics",
  "sqlScript": "SELECT DATE(order_date), COUNT(*) FROM orders GROUP BY DATE(order_date)",
  "kafkaCluster": "analytics"
}

// Real-time workload
{
  "name": "Fraud Alerts",
  "sqlScript": "SELECT * FROM transactions WHERE is_suspicious = true",
  "kafkaCluster": "primary"
}
```

**Result:**
- Analytics doesn't impact production
- Better resource management
- Independent scaling

---

**4. Multi-Region Deployment**

```json
{
  "name": "Global Orders",
  "sqlScript": "SELECT * FROM orders",
  "kafkaClusters": ["us-east", "eu-west", "ap-south"]
}
```

**Result:**
- Data replicated globally
- Reduced latency for consumers
- Geographic compliance

---

#### **Cluster Health Monitoring**

```csharp
public interface IKafkaClusterMonitor
{
    Task<ClusterHealth> CheckHealthAsync(string clusterName);
    Task<IEnumerable<ClusterMetrics>> GetMetricsAsync();
}

public class ClusterHealth
{
    public string ClusterName { get; set; }
    public bool IsHealthy { get; set; }
    public int AvailableBrokers { get; set; }
    public int TotalBrokers { get; set; }
    public double Latency { get; set; }
    public DateTime LastChecked { get; set; }
}

// Usage
var health = await _clusterMonitor.CheckHealthAsync("primary");
if (!health.IsHealthy)
{
    _logger.LogWarning(
        "Cluster {Cluster} is unhealthy: {AvailableBrokers}/{TotalBrokers} brokers available",
        health.ClusterName,
        health.AvailableBrokers,
        health.TotalBrokers
    );
}
```

---

#### **Automatic Failover**

```csharp
public async Task<DeploymentResult> DeployWithFailoverAsync(
    string name,
    string query,
    List<string> clusters,
    CancellationToken cancellationToken)
{
    foreach (var clusterName in clusters)
    {
        try
        {
            var cluster = _engineOptions.Flink.KafkaClusters[clusterName];
            
            // Check cluster health first
            var health = await _clusterMonitor.CheckHealthAsync(clusterName);
            if (!health.IsHealthy)
            {
                _logger.LogWarning(
                    "Skipping unhealthy cluster: {Cluster}",
                    clusterName
                );
                continue;
            }
            
            // Deploy to this cluster
            var result = await DeployToClusterAsync(
                name, query, cluster, null, cancellationToken
            );
            
            if (result.Success)
            {
                _logger.LogInformation(
                    "Successfully deployed to cluster: {Cluster}",
                    clusterName
                );
                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to deploy to cluster {Cluster}, trying next cluster",
                clusterName
            );
        }
    }
    
    return DeploymentResult.Failure(
        "Failed to deploy to any cluster"
    );
}
```

---

#### **Configuration Management**

**Secrets Management:**

```json
{
  "Flink": {
    "KafkaClusters": {
      "production": {
        "BootstrapServers": "${KAFKA_PROD_SERVERS}",
        "SaslUsername": "${KAFKA_PROD_USER}",
        "SaslPassword": "${KAFKA_PROD_PASSWORD}"
      }
    }
  }
}
```

**Use Azure Key Vault / AWS Secrets Manager:**

```csharp
public class KafkaClusterConfigProvider
{
    private readonly ISecretsManager _secretsManager;
    
    public async Task<KafkaClusterConfig> GetClusterConfigAsync(string clusterName)
    {
        var config = _baseConfig.KafkaClusters[clusterName];
        
        // Resolve secrets
        if (config.SaslPassword?.StartsWith("${") == true)
        {
            var secretName = config.SaslPassword.Trim("${}".ToCharArray());
            config.SaslPassword = await _secretsManager.GetSecretAsync(secretName);
        }
        
        return config;
    }
}
```

---

## Summary

| Enhancement | Complexity | Impact | Priority |
|-------------|------------|--------|----------|
| **Schema Inference** | High | High | P1 |
| **Schema Registry** | Medium | High | P1 |
| **Custom Table Options** | Low | Medium | P2 |
| **Multi-Cluster Support** | High | Medium | P2 |

---

### **Recommended Implementation Order**

**Phase 1 (v2.0):**
1. Schema Inference (basic types)
2. Custom Table Options

**Phase 2 (v2.1):**
1. Schema Registry Integration
2. Schema Inference (advanced types)

**Phase 3 (v2.2):**
1. Multi-Cluster Support
2. Automatic Failover

---

**All enhancements are designed to be:**
- ✅ Backward compatible
- ✅ Optional (with sensible defaults)
- ✅ Well-documented
- ✅ Production-ready

---

*Last Updated: December 7, 2025*
