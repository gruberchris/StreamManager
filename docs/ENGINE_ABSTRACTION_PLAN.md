# Stream Engine Abstraction Implementation Plan

**Date:** December 7, 2025  
**Objective:** Support both ksqlDB and Apache Flink as interchangeable stream processing engines via configuration

---

## Overview

This document outlines the plan to refactor Stream Manager to support multiple stream processing engines (ksqlDB and Apache Flink) through a common abstraction layer. The engine choice is made at **deployment time via configuration**, not at runtime.

### Key Principle
> **One deployment = One engine**  
> The application will use either ksqlDB OR Flink, configured via `appsettings.json`. No runtime switching or mixed queries.

---

## Architecture

```
┌─────────────────────────────────────────────┐
│         appsettings.json                     │
│  "StreamEngine": {                          │
│    "Provider": "KsqlDb"  // or "Flink"     │
│  }                                           │
└─────────────────────────────────────────────┘
                    ↓
┌─────────────────────────────────────────────┐
│         Program.cs (DI Registration)        │
│  if (provider == "KsqlDb")                  │
│    services.AddScoped<IStreamQueryEngine,   │
│                       KsqlDbEngine>();      │
│  else                                       │
│    services.AddScoped<IStreamQueryEngine,   │
│                       FlinkEngine>();       │
└─────────────────────────────────────────────┘
                    ↓
┌─────────────────────────────────────────────┐
│     Controllers & Services                  │
│  Use IStreamQueryEngine interface           │
│  (No knowledge of which engine is active)   │
└─────────────────────────────────────────────┘
```

### Pattern: Strategy Pattern
- **Interface:** `IStreamQueryEngine`
- **Implementations:** `KsqlDbEngine`, `FlinkEngine`
- **Selection:** Dependency Injection based on configuration

---

## Why This Approach?

### Advantages
✅ **Clean separation** - Controllers don't know which engine is running  
✅ **Easy testing** - Can mock `IStreamQueryEngine` for unit tests  
✅ **Future-proof** - Easy to add more engines (Spark SQL, etc.)  
✅ **Simple deployment** - Just change one config value and docker-compose file  
✅ **No database changes** - Existing schema works for both engines  

### Design Decisions

#### ❓ Why not store SQL dialect in the database?
**Answer:** Queries are deployment-specific. If you deploy with Flink, all queries must be Flink SQL. If you switch engines, you'd rewrite queries anyway. Storing dialect adds complexity without benefit for the use case of "one deployment, one engine."

#### ❓ Why not auto-detect SQL dialect?
**Answer:** Fragile and error-prone. Better to be explicit via configuration and let validation guide users to write correct syntax for the deployed engine.

#### ❓ Why keep existing field names like `KsqlScript`?
**Answer:** Backward compatibility. No need to migrate database schema. The field stores SQL regardless of engine. Future refactoring could rename if desired.

---

## Implementation Plan

### Phase 1: Abstraction Layer (Day 1-2)

#### Files to Create

**1. `Services/IStreamQueryEngine.cs`**
```csharp
public interface IStreamQueryEngine
{
    string EngineName { get; }
    
    IAsyncEnumerable<string> ExecuteAdHocQueryAsync(
        string query, 
        Dictionary<string, object>? properties,
        CancellationToken cancellationToken);
    
    Task<DeploymentResult> DeployPersistentQueryAsync(
        string name, 
        string query,
        Dictionary<string, object>? properties,
        CancellationToken cancellationToken);
    
    Task StopPersistentQueryAsync(
        string jobId,
        CancellationToken cancellationToken);
    
    Task DeletePersistentQueryAsync(
        string jobId,
        string? streamName,
        CancellationToken cancellationToken);
    
    Task<IEnumerable<QueryInfo>> ListQueriesAsync(
        CancellationToken cancellationToken);
}
```

**2. `Configuration/StreamEngineOptions.cs`**
```csharp
public class StreamEngineOptions
{
    public const string SectionName = "StreamEngine";
    
    public StreamEngineProvider Provider { get; set; } = StreamEngineProvider.KsqlDb;
    public KsqlDbOptions KsqlDb { get; set; } = new();
    public FlinkOptions Flink { get; set; } = new();
}

public enum StreamEngineProvider
{
    KsqlDb,
    Flink
}

public class KsqlDbOptions
{
    public string Url { get; set; } = "http://localhost:8088";
}

public class FlinkOptions
{
    public string SqlGatewayUrl { get; set; } = "http://localhost:8083";
    public string RestApiUrl { get; set; } = "http://localhost:8081";
}
```

**3. Supporting Models**
```csharp
public record DeploymentResult(
    bool Success,
    string? JobId,
    string? OutputTopic,
    string? StreamName,
    string? ErrorMessage = null);

public record QueryInfo(
    string Id,
    string? Name,
    string Status,
    string QueryString);
```

---

### Phase 2: KsqlDB Engine (Day 2-3)

#### File to Create

**`Services/Engines/KsqlDbEngine.cs`**

**Purpose:** Wrap existing ksqlDB logic from `AdHocKsqlService` and `StreamController`

**Implementation Notes:**
- Copy HTTP streaming logic from `AdHocKsqlService.ExecuteQueryStreamAsync`
- Copy deployment logic from `StreamController.DeployStream`
- Copy stop/delete logic from `StreamController.StopStream` and `DeleteStream`
- Add `ListQueriesAsync` method using `SHOW QUERIES`
- Automatically add `EMIT CHANGES` if missing for ad-hoc queries
- Use `CREATE STREAM {name} AS {query}` for persistent queries

**Key Methods:**
1. `ExecuteAdHocQueryAsync` - POST to `/query`, stream results line-by-line
2. `DeployPersistentQueryAsync` - Create stream, query for ID and topic
3. `StopPersistentQueryAsync` - TERMINATE query
4. `DeletePersistentQueryAsync` - TERMINATE + DROP STREAM
5. `ListQueriesAsync` - SHOW QUERIES, parse response

---

### Phase 3: Update Dependencies (Day 3-4)

#### Files to Modify

**1. `Program.cs`**
- Remove `AddHttpClient<AdHocKsqlService>`
- Add conditional registration based on `StreamEngineOptions.Provider`
- Bind `StreamEngineOptions` from configuration

**Changes:**
```csharp
// OLD:
builder.Services.AddHttpClient<AdHocKsqlService>(...);
builder.Services.AddScoped<AdHocKsqlService>();

// NEW:
var engineOptions = builder.Configuration
    .GetSection(StreamEngineOptions.SectionName)
    .Get<StreamEngineOptions>() ?? new();

builder.Services.AddSingleton(engineOptions);

if (engineOptions.Provider == StreamEngineProvider.KsqlDb)
{
    builder.Services.AddHttpClient<IStreamQueryEngine, KsqlDbEngine>(...);
}
else if (engineOptions.Provider == StreamEngineProvider.Flink)
{
    builder.Services.AddHttpClient<IStreamQueryEngine, FlinkEngine>(...);
}
```

**2. `Controllers/StreamController.cs`**
- Remove `HttpClient` injection
- Remove `_ksqlDbUrl` field
- Inject `IStreamQueryEngine` instead
- Replace all direct HTTP calls with engine interface calls

**Changes:**
```csharp
// OLD:
private readonly HttpClient _httpClient;
private readonly string _ksqlDbUrl;

public StreamController(HttpClient httpClient, ...) { }

[HttpPost("{id}/deploy")]
public async Task<ActionResult> DeployStream(Guid id)
{
    // Direct HTTP calls to ksqlDB
    var response = await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", ...);
}

// NEW:
private readonly IStreamQueryEngine _engine;

public StreamController(IStreamQueryEngine engine, ...) { }

[HttpPost("{id}/deploy")]
public async Task<ActionResult> DeployStream(Guid id)
{
    var result = await _engine.DeployPersistentQueryAsync(...);
    if (result.Success) { ... }
}
```

**3. `Hubs/KsqlHub.cs`**
- Remove `AdHocKsqlService` injection
- Inject `IStreamQueryEngine` instead
- Replace service call with engine interface call

**Changes:**
```csharp
// OLD:
private readonly AdHocKsqlService _ksqlService;

public async IAsyncEnumerable<string> ExecuteAdHoc(...)
{
    await foreach (var line in _ksqlService.ExecuteQueryStreamAsync(...)) { }
}

// NEW:
private readonly IStreamQueryEngine _engine;

public async IAsyncEnumerable<string> ExecuteAdHoc(...)
{
    await foreach (var line in _engine.ExecuteAdHocQueryAsync(...)) { }
}
```

**4. `Services/KsqlQueryValidator.cs`**
- Inject `StreamEngineOptions`
- Make validation logic engine-aware
- ksqlDB: Allow `EMIT CHANGES`, block `CREATE TABLE`
- Flink: Block `EMIT CHANGES`, allow `CREATE TABLE`

**Changes:**
```csharp
public QueryValidationResult ValidateAdHocQuery(string query)
{
    if (_engineOptions.Provider == StreamEngineProvider.KsqlDb)
    {
        // ksqlDB-specific validation
        // EMIT CHANGES is optional (will be added automatically)
    }
    else if (_engineOptions.Provider == StreamEngineProvider.Flink)
    {
        // Flink-specific validation
        // EMIT CHANGES is invalid syntax
        if (query.Contains("EMIT CHANGES", StringComparison.OrdinalIgnoreCase))
        {
            return new QueryValidationResult(false, 
                "EMIT CHANGES is not valid Flink SQL syntax");
        }
    }
    
    // Common validations (length, JOINs, WINDOWs, blocked keywords)
}
```

**5. `appsettings.json`**
- Add `StreamEngine` section
- Keep existing sections for backward compatibility

**Changes:**
```json
{
  "StreamEngine": {
    "Provider": "KsqlDb",
    "KsqlDb": {
      "Url": "http://localhost:8088"
    },
    "Flink": {
      "SqlGatewayUrl": "http://localhost:8083",
      "RestApiUrl": "http://localhost:8081"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "...",
    "Kafka": "localhost:9092"
  }
}
```

#### File to Delete

**`Services/AdHocKsqlService.cs`**
- Logic moved into `KsqlDbEngine.cs`
- No longer needed as a separate service

---

### Phase 4: Flink Engine (Day 4-7)

#### Docker Infrastructure

**1. Create `docker-compose.base.yml`**
- Contains shared services: Kafka, Schema Registry, PostgreSQL
- Used by both ksqlDB and Flink deployments

**2. Rename `docker-compose.yml` → `docker-compose.ksqldb.yml`**
- Contains only ksqlDB-specific service
- Extends base services

**3. Create `docker-compose.flink.yml`**
- Contains Flink services:
  - **JobManager** (port 8081) - Cluster coordinator
  - **TaskManager** (workers) - Executes jobs, 2 cores, 4GB RAM
  - **SQL Gateway** (port 8083) - REST API for SQL submission

**Flink Configuration:**
```yaml
flink-jobmanager:
  image: flink:1.18-scala_2.12-java11
  ports:
    - "8081:8081"
  environment:
    - FLINK_PROPERTIES=
      jobmanager.rpc.address: flink-jobmanager
      state.backend: rocksdb
      execution.checkpointing.interval: 2000

flink-taskmanager:
  image: flink:1.18-scala_2.12-java11
  command: taskmanager
  deploy:
    resources:
      limits:
        cpus: '2.0'
        memory: 4G
  environment:
    - FLINK_PROPERTIES=
      jobmanager.rpc.address: flink-jobmanager
      taskmanager.numberOfTaskSlots: 4

flink-sql-gateway:
  image: flink:1.18-scala_2.12-java11
  ports:
    - "8083:8083"
  command: /opt/flink/bin/sql-gateway.sh start-foreground
```

**Usage:**
```bash
# Run with ksqlDB
docker-compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml up

# Run with Flink
docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml up
```

#### File to Create

**`Services/Engines/FlinkEngine.cs`**

**Implementation Strategy:**

**Ad-Hoc Queries (Flink SQL Gateway REST API):**
1. Create session: `POST /v1/sessions`
2. Submit statement: `POST /v1/sessions/{sessionId}/statements`
3. Poll for results: `GET /v1/sessions/{sessionId}/statements/{statementHandle}/result/{token}`
4. Stream results to caller
5. Close session on completion/cancellation

**Persistent Queries:**
1. Option A: `CREATE TABLE {name} AS SELECT...` (single DDL)
2. Option B: `CREATE TABLE` + `INSERT INTO` (two statements)
3. Query Flink REST API for job ID: `GET /jobs`
4. Store job ID in database

**Stop Queries:**
1. Use Flink REST API: `PATCH /jobs/{jobId}` with status `STOP`
2. Or via SQL Gateway: `STOP JOB '{jobId}'`

**Delete Queries:**
1. Stop job first (if running)
2. Drop table: `DROP TABLE {name}`
3. Delete job from Flink: `DELETE /jobs/{jobId}` (cleanup)

**List Queries:**
1. Query Flink REST API: `GET /jobs`
2. Map to `QueryInfo` objects

**Key Differences from ksqlDB:**
- **Session management** - Must create/maintain sessions
- **Polling** - May need to poll for results vs pure streaming
- **No EMIT CHANGES** - Streaming is inferred from source
- **Different DDL** - CREATE TABLE vs CREATE STREAM
- **Job lifecycle** - More states (CREATED, RUNNING, FINISHED, FAILED, CANCELED)

---

### Phase 5: Documentation & Testing (Day 7-8)

#### Files to Create/Modify

**1. `README.md` (Update)**
- Add "Choosing Your Stream Engine" section
- Document how to run with ksqlDB
- Document how to run with Flink
- Update configuration examples

**2. `docs/FLINK_MIGRATION.md` (Create)**
- SQL syntax comparison table
- Migration examples for common queries
- Window function differences
- Troubleshooting guide

**3. `docs/SQL_SYNTAX_GUIDE.md` (Create)**
- Side-by-side syntax comparison
- When to use which engine
- Performance characteristics
- Best practices per engine

---

## SQL Syntax Differences

### Key Differences

| Feature | ksqlDB Syntax | Flink SQL Syntax |
|---------|---------------|------------------|
| **Streaming SELECT** | `SELECT * FROM orders EMIT CHANGES;` | `SELECT * FROM orders;` |
| **Persistent Query** | `CREATE STREAM view AS SELECT...` | `CREATE TABLE view AS SELECT...` or `INSERT INTO view SELECT...` |
| **Terminate Query** | `TERMINATE {queryId};` | `STOP JOB '{jobId}';` |
| **Window Tumbling** | `WINDOW TUMBLING (SIZE 5 MINUTES)` | `TUMBLE(time_col, INTERVAL '5' MINUTE)` |
| **Window Hopping** | `WINDOW HOPPING (SIZE 1 HOUR, ADVANCE BY 15 MINUTES)` | `HOP(time_col, INTERVAL '15' MINUTE, INTERVAL '1' HOUR)` |
| **Drop Stream** | `DROP STREAM name;` | `DROP TABLE name;` |
| **Show Queries** | `SHOW QUERIES;` | REST API: `GET /jobs` |

### Compatible Syntax (No Changes Needed)

✅ Basic SELECT: `SELECT id, name, amount FROM orders`  
✅ WHERE clauses: `WHERE amount > 100`  
✅ Basic JOINs: `FROM a JOIN b ON a.id = b.id`  
✅ Aggregations: `COUNT(*), SUM(amount), AVG(price)`  
✅ GROUP BY: `GROUP BY customer_id`

### Migration Examples

**Example 1: Simple Filter**
```sql
-- ksqlDB
SELECT * FROM orders WHERE amount > 100 EMIT CHANGES;

-- Flink (just remove EMIT CHANGES)
SELECT * FROM orders WHERE amount > 100;
```

**Example 2: Tumbling Window Aggregation**
```sql
-- ksqlDB
SELECT customer_id, COUNT(*) as order_count
FROM orders
WINDOW TUMBLING (SIZE 5 MINUTES)
GROUP BY customer_id
EMIT CHANGES;

-- Flink
SELECT 
  customer_id, 
  COUNT(*) as order_count,
  TUMBLE_START(order_time, INTERVAL '5' MINUTE) as window_start
FROM orders
GROUP BY 
  customer_id,
  TUMBLE(order_time, INTERVAL '5' MINUTE);
```

**Example 3: Create Persistent Stream**
```sql
-- ksqlDB
CREATE STREAM high_value_orders AS
  SELECT * FROM orders WHERE amount > 1000;

-- Flink (Option 1: Single DDL)
CREATE TABLE high_value_orders AS
  SELECT * FROM orders WHERE amount > 1000;

-- Flink (Option 2: Separate statements)
CREATE TABLE high_value_orders (
  order_id STRING,
  customer_id STRING,
  amount DECIMAL(10,2),
  order_time TIMESTAMP(3)
) WITH (
  'connector' = 'kafka',
  'topic' = 'high_value_orders',
  'properties.bootstrap.servers' = 'localhost:9092'
);

INSERT INTO high_value_orders
  SELECT * FROM orders WHERE amount > 1000;
```

---

## File Change Summary

### Files to Create (8 new files)
1. ✅ `Services/IStreamQueryEngine.cs` - Core interface
2. ✅ `Services/Engines/KsqlDbEngine.cs` - ksqlDB implementation
3. ✅ `Services/Engines/FlinkEngine.cs` - Flink implementation
4. ✅ `Configuration/StreamEngineOptions.cs` - Configuration model
5. ✅ `docker-compose.base.yml` - Shared infrastructure
6. ✅ `docker-compose.flink.yml` - Flink services
7. ✅ `docs/FLINK_MIGRATION.md` - Migration guide
8. ✅ `docs/SQL_SYNTAX_GUIDE.md` - Syntax reference

### Files to Modify (6 files)
1. ✅ `Program.cs` - Conditional DI registration
2. ✅ `Controllers/StreamController.cs` - Use interface
3. ✅ `Hubs/KsqlHub.cs` - Use interface
4. ✅ `Services/KsqlQueryValidator.cs` - Engine-aware validation
5. ✅ `appsettings.json` - Add engine configuration
6. ✅ `README.md` - Document both engines

### Files to Delete (1 file)
1. ✅ `Services/AdHocKsqlService.cs` - Logic moved to KsqlDbEngine

### Files to Rename (1 file)
1. ✅ `docker-compose.yml` → `docker-compose.ksqldb.yml`

### Files Unchanged (No modifications needed)
- ✅ `Services/TopicProxyService.cs` - Already engine-agnostic
- ✅ `Services/QueryRateLimiter.cs` - No changes needed
- ✅ `Models/StreamDefinition.cs` - No schema changes
- ✅ `Data/StreamManagerDbContext.cs` - No schema changes
- ✅ All migration files - No database changes

---

## Risk Assessment

### Low Risk
✅ **TopicProxyService** - Consumes Kafka topics, independent of query engine  
✅ **Database Schema** - No changes required  
✅ **Rate Limiting** - Engine-agnostic  
✅ **SignalR Communication** - Just transports strings  

### Medium Risk
⚠️ **Query Validation** - Must handle two SQL dialects correctly  
⚠️ **Error Handling** - Different error formats from each engine  
⚠️ **Configuration** - Must validate config at startup  

### High Risk
🔴 **Flink Session Management** - New complexity vs stateless ksqlDB  
🔴 **Result Streaming** - Flink may use polling vs push streaming  
🔴 **Resource Configuration** - Different tuning parameters  

---

## Testing Strategy

### Phase 1: Unit Tests
- Mock `IStreamQueryEngine` interface
- Test controllers with mock engine
- Validate configuration binding

### Phase 2: Integration Tests - ksqlDB
- Deploy with ksqlDB configuration
- Test ad-hoc queries
- Test persistent query lifecycle
- Verify existing functionality preserved

### Phase 3: Integration Tests - Flink
- Deploy with Flink configuration
- Test ad-hoc queries
- Test persistent query lifecycle
- Compare results with ksqlDB

### Phase 4: Load Testing
- 20 concurrent ad-hoc queries (both engines)
- 50 persistent queries (both engines)
- Measure resource usage
- Validate rate limiting works

---

## Success Criteria

✅ Can switch between engines by changing one config value  
✅ Existing ksqlDB functionality works unchanged  
✅ Flink implementation provides equivalent features  
✅ No database schema changes required  
✅ Documentation covers both engines  
✅ Tests pass for both engines  
✅ Performance meets requirements (see CAPACITY_PLANNING.md)  

---

## Timeline

**Total Estimated Time:** 7-8 days

| Phase | Duration | Deliverable |
|-------|----------|-------------|
| Phase 1 | 1-2 days | Abstraction layer complete |
| Phase 2 | 1 day | ksqlDB engine working |
| Phase 3 | 1-2 days | All dependencies updated |
| Phase 4 | 3-4 days | Flink engine working |
| Phase 5 | 1 day | Documentation & testing |

---

## Rollout Strategy

### Development Environment
1. Start with ksqlDB configuration (default)
2. Verify existing features work
3. Switch to Flink configuration
4. Test Flink features
5. Document any issues

### Staging Environment
1. Deploy with ksqlDB first (proven technology)
2. Run smoke tests
3. Deploy with Flink
4. Run comparative tests
5. Choose engine for production

### Production Deployment
1. Choose engine based on requirements:
   - **ksqlDB** if: Simpler ops, proven stability, smaller scale
   - **Flink** if: Need scale, advanced SQL features, existing Flink expertise
2. Update `appsettings.json` with chosen provider
3. Deploy appropriate docker-compose file
4. Monitor resource usage (see BEST_PRACTICES.md)

---

## Future Enhancements

### Potential Future Work (Out of Scope for Initial Implementation)
- 🔮 Support for Apache Spark SQL
- 🔮 Support for Kafka Streams DSL
- 🔮 Query translation layer (auto-convert between dialects)
- 🔮 A/B testing framework (run same query on both engines)
- 🔮 Cost estimation before execution
- 🔮 Query optimization suggestions

---

## Questions & Decisions

### Decision Log

| Question | Decision | Rationale |
|----------|----------|-----------|
| Store SQL dialect in DB? | ❌ No | One deployment = one engine; no mixed queries |
| Auto-detect SQL dialect? | ❌ No | Fragile; explicit config is clearer |
| Rename `KsqlScript` field? | ❌ No | Backward compatibility; works for both |
| Support both engines simultaneously? | ❌ No | Adds complexity without clear benefit |
| Modify database schema? | ❌ No | Current schema works for both engines |

---

## References

- [ksqlDB REST API Documentation](https://docs.ksqldb.io/en/latest/developer-guide/ksqldb-rest-api/)
- [Flink SQL Gateway REST API](https://nightlies.apache.org/flink/flink-docs-stable/docs/dev/table/sql-gateway/rest/)
- [Flink SQL Syntax](https://nightlies.apache.org/flink/flink-docs-stable/docs/dev/table/sql/queries/overview/)
- Project Docs: `BEST_PRACTICES.md`, `CAPACITY_PLANNING.md`, `RESOURCE_LIMITS.md`

---

**Document Status:** ✅ Ready for Implementation  
**Next Step:** Begin Phase 1 - Create abstraction layer files
