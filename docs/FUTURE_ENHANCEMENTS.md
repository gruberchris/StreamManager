# Future Enhancements - Detailed Explanations

**Date:** December 7, 2025  
**Status:** 📋 Planning Document

---

## Overview

This document provides detailed explanations of proposed future enhancements for the StreamManager Flink engine. These are **not currently implemented** but represent valuable improvements for future versions.

---

## 1. Schema Inference

### **Current State**

Users must explicitly define the table schema in their `CREATE TABLE` statements:

```sql
CREATE TABLE orders (
    ORDER_ID INT,
    CUSTOMER_ID INT,
    ...
) WITH (...)
```

**Problem:**
- Tedious to write manual schemas
- Prone to typos and mismatches
- Requires user to know the exact JSON structure

---

### **Proposed Enhancement: Automatic Schema Inference**

**Goal:** Automatically detect the output schema from the user's query or from sample data.

#### **How It Would Work**

**User Input:**
```sql
SELECT * FROM orders LIMIT 5
```

**System Action:**
1. System inspects the Kafka topic `orders`
2. Reads a few JSON messages
3. Infers types (INT, STRING, etc.)
4. Auto-generates the `CREATE TABLE` statement behind the scenes

**Benefits:**
- ✅ Much faster prototyping
- ✅ Reduced errors
- ✅ Lower barrier to entry for new users

---

## 2. Schema Registry Integration

### **Current State**

Currently focused on JSON format (`'format' = 'json'`).

### **Proposed Enhancement: Confluent Schema Registry Integration**

**Goal:** Automatically register Avro/Protobuf schemas with Confluent Schema Registry.

#### **How It Would Work**

**Configuration:**
```json
{
  "Flink": {
    "KafkaFormat": "avro",
    "SchemaRegistry": {
      "Url": "http://localhost:8081",
      "AutoRegister": true
    }
  }
}
```

**Deployment Flow:**
1. User deploys stream
2. System generates Avro schema from the SQL types
3. Registers schema with Registry
4. Creates Flink table using `avro-confluent` format

**Benefits:**
- ✅ Stronger type safety
- ✅ Schema evolution support
- ✅ Compatibility with broader Kafka ecosystem

---

## 3. Flink Catalog Support

### **Current State**
Every Flink query requires a `CREATE TABLE` statement that fully defines the connector properties (e.g., Kafka bootstrap servers, topic, format):

```sql
CREATE TABLE orders (
    ...
) WITH (
    'connector' = 'kafka',
    'topic' = 'orders',
    'properties.bootstrap.servers' = 'broker:29092',
    ...
);

SELECT * FROM orders;
```



**Problem:**
- Repetitive definition of tables across multiple queries.
- If a Kafka cluster or topic moves, all queries referencing it need to be updated.
- Hard to manage shared table definitions.

---

### **Proposed Enhancement: Flink Catalog Integration**

**Goal:** Allow users to register persistent table definitions in a Flink Catalog, eliminating the need to include `CREATE TABLE` statements in every query.

#### **How It Would Work**

**1. Register a Table Definition Once:**
User registers a `CREATE TABLE` statement (e.g., via a new UI page or API endpoint) and specifies which Flink Catalog to use (e.g., 'default_catalog').

```sql
-- Registered once in the Flink Catalog
CREATE TABLE default_catalog.default_database.orders (
    ORDER_ID INT,
    CUSTOMER_ID INT,
    PRODUCT STRING,
    AMOUNT DOUBLE,
    PURCHASE_DATE STRING
) WITH (
    'connector' = 'kafka',
    'topic' = 'orders',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json',
    'scan.startup.mode' = 'earliest-offset'
);
```

**2. Query by Name:**
Subsequent queries can simply refer to the table by its registered name:

```sql
SELECT * FROM default_catalog.default_database.orders;
```

**Benefits:**
- ✅ Centralized table management
- ✅ Simplified query authoring
- ✅ Easier updates to connector configurations

---



## 4. Custom Table Options

### **Current State**

Users can provide custom options via the SQL `WITH` clause, but there is no UI support for configuring these easily.

### **Proposed Enhancement: UI Configurator**

**Goal:** Allow users to set common properties via UI form fields instead of raw SQL.

**UI Options:**
- Partition Count
- Replication Factor
- Retention Period (e.g., "7 days")
- Compression Type (Snappy, Gzip)

---



## 5. Multi-Cluster Support

### **Current State**

Single Kafka cluster configuration (`broker:29092`).

### **Proposed Enhancement: Multiple Kafka Cluster Support**







**Configuration:**
```json
{
  "Flink": {
    "KafkaClusters": {
      "primary": { "BootstrapServers": "kafka1:9092" },
      "analytics": { "BootstrapServers": "analytics-broker:29092" }
    }
  }
}
```

**Benefits:**
- ✅ Disaster recovery
- ✅ Workload separation
- ✅ Multi-region support

---

## 6. Kafka Admin Client for Access Control

### **Current State**

The system does not verify user access to Kafka topics before allowing them to build and attempt to run queries. Users might try to query topics they lack permissions for, leading to runtime errors.

**Goal:** Proactively inform users if they lack read access to Kafka topics specified in their queries, enhancing user experience and preventing runtime failures.

#### **How It Would Work**

**1. UI-driven Topic Listing:**
When a user begins to write a query, the UI could display a list of accessible Kafka topics.

**2. Pre-Query Validation:**
Before submitting a Flink `CREATE TABLE` statement that references a Kafka topic, the system would use the Kafka Admin API to check if the authenticated user has `READ` access to that topic.

**3. Informative Feedback:**
If access is denied, the UI would inform the user immediately (e.g., "You do not have read access to topic 'sensitive_data'").

#### **Implementation Approach**

The `Confluent.Kafka` library provides an `AdminClient` that can be used to query Kafka metadata.

```csharp
using Confluent.Kafka;
using System;
using System.Linq; // For .Any()

public class KafkaAccessService
{
    private readonly AdminClientConfig _adminConfig;
    private readonly ILogger<KafkaAccessService> _logger;

    public KafkaAccessService(IConfiguration config, ILogger<KafkaAccessService> logger)
    {
        _adminConfig = new AdminClientConfig
        {
            BootstrapServers = config.GetConnectionString("Kafka") ?? "localhost:9092",
            // Potentially load user-specific SASL/SSL config for authorization
            // For example:
            // SaslMechanism = SaslMechanism.Plain,
            // SecurityProtocol = SecurityProtocol.SaslPlaintext,
            // SaslUsername = "user_from_auth",
            // SaslPassword = "password_from_auth"
        };
        _logger = logger;
    }

    public bool CanUserReadTopic(string topicName, AdminClientConfig userSpecificConfig = null)
    {
        var config = userSpecificConfig ?? _adminConfig;
        using var adminClient = new AdminClientBuilder(config).Build();
        
        try
        {
            var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(10));
            var topicMetadata = metadata.Topics.FirstOrDefault(t => t.Topic == topicName);

            if (topicMetadata == null)
            {
                _logger.LogWarning("Topic '{TopicName}' not found in metadata.", topicName);
                return false; // Topic doesn't exist or is not visible
            }

            if (topicMetadata.Error.IsError)
            {
                // Error indicates an issue, often authorization
                _logger.LogWarning("Error accessing topic '{TopicName}': {Error}", topicName, topicMetadata.Error.Reason);
                return false;
            }

            // A more granular check for READ access (requires more specific ACL APIs)
            // For simple AdminClient.GetMetadata, an inaccessible topic often just won't appear
            // or will show an error. The current approach assumes its presence/absence and error status
            // in GetMetadata is sufficient for a basic "can read" check.
            
            return true; // Topic found and no error reported
        }
        catch (KafkaException ex)
        {
            _logger.LogError(ex, "Kafka AdminClient error while checking topic access for '{TopicName}'.", topicName);
            // This could be an authorization exception if the user's base config is invalid
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error checking topic access for '{TopicName}'.", topicName);
            return false;
        }
    }

    public IEnumerable<string> GetAccessibleTopics(AdminClientConfig userSpecificConfig = null)
    {
        var config = userSpecificConfig ?? _adminConfig;
        using var adminClient = new AdminClientBuilder(config).Build();

        try
        {
            var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(10));
            return metadata.Topics
                           .Where(t => !t.Error.IsError)
                           .Select(t => t.Topic)
                           .ToList();
        }
        catch (KafkaException ex)
        {
            _logger.LogError(ex, "Kafka AdminClient error while listing accessible topics.");
            return Enumerable.Empty<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing accessible topics.");
            return Enumerable.Empty<string>();
        }
    }
}
```

#### **Benefits**

- ✅ **Improved User Experience:** Users are informed about access issues early.
- ✅ **Enhanced Security:** Prevents attempts to access unauthorized resources.
- ✅ **Reduced Errors:** Fewer query failures due to permission problems.
- ✅ **Clearer UI Feedback:** Direct feedback on topic accessibility.

---

## Summary

| Enhancement | Complexity | Impact | Priority |
|-------------|------------|--------|----------|
| **Schema Inference** | High | High | P1 |
| **Schema Registry** | Medium | High | P1 |
| **Flink Catalog Support** | Medium | High | P1 |
| **Custom Table Options** | Low | Medium | P2 |
| **Multi-Cluster Support** | High | Medium | P2 |
| **Kafka Admin Client for Access Control** | Medium | High | P1 |

---

*Last Updated: December 7, 2025*




