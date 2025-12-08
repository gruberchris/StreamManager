# Stream Manager - Quick Start Guide

## Choosing Your Stream Processing Engine

Stream Manager supports two stream processing engines:
- **ksqlDB** (Confluent) - Default, simpler setup
- **Apache Flink** - More scalable, advanced features

The engine is configured once at deployment time via `appsettings.json`.

---

## Option 1: Run with ksqlDB (Default)

### 1. Start Infrastructure

```bash
# Start Kafka, Schema Registry, PostgreSQL, and ksqlDB
docker-compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml up -d
```

### 2. Configure Application

Edit `StreamManager.Api/appsettings.json`:

```json
{
  "StreamEngine": {
    "Provider": "KsqlDb",
    "KsqlDb": {
      "Url": "http://localhost:8088"
    }
  }
}
```

### 3. Run Database Migrations

```bash
cd StreamManager.Api
dotnet ef database update
```

### 4. Start API Server

```bash
cd StreamManager.Api
dotnet run
```

API available at: **https://localhost:7068**

### 5. Start Web Application

```bash
cd StreamManager.Web
dotnet run
```

Web UI available at: **https://localhost:7122**

### 6. Verify ksqlDB

Check ksqlDB is running:
```bash
curl http://localhost:8088/info
```

---

## Option 2: Run with Apache Flink

### 1. Start Infrastructure

```bash
# Start Kafka, Schema Registry, PostgreSQL, and Flink
docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml up -d
```

**Wait 30-60 seconds** for Flink to fully start up.

### 2. Configure Application

Edit `StreamManager.Api/appsettings.json`:

```json
{
  "StreamEngine": {
    "Provider": "Flink",
    "Flink": {
      "SqlGatewayUrl": "http://localhost:8082",
      "RestApiUrl": "http://localhost:8088"
    }
  }
}
```

### 3. Run Database Migrations

```bash
cd StreamManager.Api
dotnet ef database update
```

### 4. Start API Server

```bash
cd StreamManager.Api
dotnet run
```

API available at: **https://localhost:7068**

### 5. Start Web Application

```bash
cd StreamManager.Web
dotnet run
```

Web UI available at: **https://localhost:7122**

### 6. Verify Flink

Check Flink JobManager is running:
```bash
curl http://localhost:8088/overview
```

Check Flink SQL Gateway is running:
```bash
curl http://localhost:8082/v1/info
```

Access Flink Web UI: **http://localhost:8088**

---

## Setting Up the Orders Example

This guide shows you how to set up the `orders` example that all documentation refers to. **Start from scratch - no pre-existing objects are assumed.**

---

### Step 1: Create the Kafka Topic

**Both ksqlDB and Flink need the Kafka topic to exist first.** Create it:

```bash
# Create the 'orders' Kafka topic
docker exec -it broker kafka-topics --create \
  --topic orders \
  --bootstrap-server localhost:9092 \
  --partitions 3 \
  --replication-factor 1

# Verify it was created
docker exec -it broker kafka-topics --list \
  --bootstrap-server localhost:9092 | grep orders
```

Expected output: `orders`

---

### Step 2A: For ksqlDB - Create the Stream Definition

**ksqlDB requires a STREAM definition to map to the Kafka topic.**

Connect to ksqlDB CLI:
```bash
docker exec -it ksqldb-cli ksql http://ksqldb-server:8088
```

Create the orders stream:
```sql
CREATE STREAM orders (
    ORDER_ID INT,
    CUSTOMER_ID INT,
    PRODUCT VARCHAR,
    AMOUNT DOUBLE,
    PURCHASE_DATE VARCHAR
) WITH (
    KAFKA_TOPIC='orders',
    VALUE_FORMAT='JSON',
    PARTITIONS=3
);
```

Verify it was created:
```sql
SHOW STREAMS;
```

Exit ksqlDB CLI:
```sql
EXIT;
```

---

### Step 2B: For Flink - No Pre-Creation Needed!

**Flink does NOT require pre-creating table definitions in a central registry.** 

You'll include `CREATE TABLE` statements directly in your queries. This gives you full flexibility to define schemas on-the-fly.

**Ad-Hoc Query Example (Test & Explore):**
```sql
-- Define table with BOUNDED mode (stops reading after current data)
CREATE TABLE orders (
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
    'scan.startup.mode' = 'earliest-offset',
    'scan.bounded.mode' = 'latest-offset'  -- Important for ad-hoc tests!
);

-- Run query
SELECT * FROM orders WHERE AMOUNT > 500 LIMIT 10;
```

**Managed Stream Example (Continuous Processing):**
```sql
-- Define source (UNBOUNDED - continuous reading)
CREATE TABLE orders (
    ...
) WITH (
    'scan.startup.mode' = 'earliest-offset'
    -- No bounded mode here!
);

-- Define sink
CREATE TABLE high_value_orders (
    ...
);

-- Deploy
INSERT INTO high_value_orders SELECT ... FROM orders ...;
```

**You'll use this pattern in the Stream Manager UI.** No need to execute it now.

---

### Step 3: Generate Sample Data

Now that the Kafka topic exists (and ksqlDB stream if using ksqlDB), generate sample order data:

```bash
cd scripts
chmod +x generate_orders.sh
./generate_orders.sh
```

**What the script does:**
- Detects if you're running ksqlDB or Flink
- Inserts orders into the Kafka topic using the streaming engine
- Generates 1 order every 5 seconds

**Data Schema:**
- **ORDER_ID**: INT - Sequential starting at 1011 (1011, 1012, 1013, ...)
- **CUSTOMER_ID**: INT - Random between 500-599
- **PRODUCT**: STRING - Random from 20 products (Laptop, Wireless Mouse, Keyboard, etc.)
- **AMOUNT**: DOUBLE - Random between 10.00 - 1500.00
- **PURCHASE_DATE**: STRING - ISO 8601 format (e.g., "2025-12-07T20:30:15")

**Example Output:**
```
✓ Detected Flink SQL Gateway running on port 8083

Starting order generator - creating 1 order every 5 seconds
Engine: flink
Press Ctrl+C to stop

[2025-12-07T20:30:15] Inserting Order #1011: Laptop - $899.99 for Customer 542
  ✓ Successfully inserted (Flink)

[2025-12-07T20:30:20] Inserting Order #1012: Wireless Mouse - $29.99 for Customer 513
  ✓ Successfully inserted
```

Press `Ctrl+C` to stop the generator.

---

### Step 4: Verify Data is Flowing

Check that orders are being written to Kafka:

```bash
# View messages in the orders topic (Ctrl+C to exit)
docker exec -it broker kafka-console-consumer \
  --bootstrap-server localhost:9092 \
  --topic orders \
  --from-beginning \
  --max-messages 5
```

You should see JSON messages like:
```json
{"ORDER_ID":1011,"CUSTOMER_ID":542,"PRODUCT":"Laptop","AMOUNT":899.99,"PURCHASE_DATE":"2025-12-07T20:30:15"}
{"ORDER_ID":1012,"CUSTOMER_ID":513,"PRODUCT":"Wireless Mouse","AMOUNT":29.99,"PURCHASE_DATE":"2025-12-07T20:30:20"}
```

---

## Example Stream Definitions

**Now that you have data flowing, you can create stream definitions to process it.**

All examples below assume:
1. ✅ Kafka topic `orders` exists
2. ✅ `generate_orders.sh` is running (or has generated data)
3. ✅ For ksqlDB: STREAM definition exists
4. ✅ For Flink: No pre-setup needed (CREATE TABLE is in the query)

#### High-Value Orders (ksqlDB)

```sql
SELECT 
    ORDER_ID,
    CUSTOMER_ID,
    PRODUCT,
    AMOUNT,
    PURCHASE_DATE
FROM orders 
WHERE AMOUNT > 500
EMIT CHANGES;
```

#### High-Value Orders (Flink) - Complete Script

```sql
-- Define source table
CREATE TABLE orders (
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

-- Define sink table
CREATE TABLE high_value_orders (
    ORDER_ID INT,
    CUSTOMER_ID INT,
    PRODUCT STRING,
    AMOUNT DOUBLE,
    PURCHASE_DATE STRING
) WITH (
    'connector' = 'kafka',
    'topic' = 'high_value_orders',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json'
);

-- Deploy continuous processing
INSERT INTO high_value_orders
SELECT 
    ORDER_ID,
    CUSTOMER_ID,
    PRODUCT,
    AMOUNT,
    PURCHASE_DATE
FROM orders 
WHERE AMOUNT > 500;
```

#### Customer Order Summary (ksqlDB)

```sql
SELECT 
    CUSTOMER_ID,
    COUNT(*) AS ORDER_COUNT,
    SUM(AMOUNT) AS TOTAL_SPENT,
    AVG(AMOUNT) AS AVG_ORDER_VALUE
FROM orders
GROUP BY CUSTOMER_ID
EMIT CHANGES;
```

#### Customer Order Summary (Flink)

```sql
SELECT 
    CUSTOMER_ID,
    COUNT(*) AS ORDER_COUNT,
    SUM(AMOUNT) AS TOTAL_SPENT,
    AVG(AMOUNT) AS AVG_ORDER_VALUE
FROM orders
GROUP BY CUSTOMER_ID;
```

#### Product Sales Analytics (ksqlDB)

```sql
SELECT 
    PRODUCT,
    COUNT(*) AS UNITS_SOLD,
    SUM(AMOUNT) AS REVENUE,
    AVG(AMOUNT) AS AVG_PRICE
FROM orders
WINDOW TUMBLING (SIZE 1 HOUR)
GROUP BY PRODUCT
EMIT CHANGES;
```

#### Product Sales Analytics (Flink)

```sql
SELECT 
    PRODUCT,
    COUNT(*) AS UNITS_SOLD,
    SUM(AMOUNT) AS REVENUE,
    AVG(AMOUNT) AS AVG_PRICE,
    TUMBLE_START(TO_TIMESTAMP(PURCHASE_DATE), INTERVAL '1' HOUR) AS WINDOW_START
FROM orders
GROUP BY PRODUCT, TUMBLE(TO_TIMESTAMP(PURCHASE_DATE), INTERVAL '1' HOUR);
```

---

## SQL Syntax Differences

### ksqlDB Queries

```sql
-- Ad-hoc query (EMIT CHANGES is optional, added automatically)
SELECT * FROM orders WHERE amount > 100 EMIT CHANGES;

-- Persistent query (system creates stream)
SELECT customer_id, SUM(amount) as total
FROM orders
GROUP BY customer_id
EMIT CHANGES;
```

### Flink SQL Queries

```sql
-- Ad-hoc query (NO EMIT CHANGES)
SELECT * FROM orders WHERE amount > 100;

-- Persistent query (similar syntax)
SELECT customer_id, SUM(amount) as total
FROM orders
GROUP BY customer_id;
```

**Key Difference:** Remove `EMIT CHANGES` when using Flink!

---

## Ports Reference

| Service | Port | Purpose |
|---------|------|---------|
| **Kafka** | 9092 | Kafka broker (external) |
| **Schema Registry** | 8081 | Avro schema management |
| **PostgreSQL** | 5432 | Application database |
| **ksqlDB Server** | 8088 | ksqlDB REST API (ksqlDB only) |
| **Flink JobManager** | 8088 | Flink Web UI & REST API (Flink only) |
| **Flink SQL Gateway** | 8082 | SQL submission API (Flink only) |
| **API Server** | 7068 | Stream Manager API |
| **Web UI** | 7122 | Stream Manager Web Interface |

---

## Switching Engines

To switch from ksqlDB to Flink (or vice versa):

1. **Stop all services:**
   ```bash
   docker-compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml down
   # or
   docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml down
   ```

2. **Update `appsettings.json`** - Change `Provider` to `"KsqlDb"` or `"Flink"`

3. **Start new infrastructure:**
   ```bash
   docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml up -d
   ```

4. **Restart API and Web apps**

**Note:** Existing stream definitions in the database will need their SQL updated to match the new engine's syntax.

---

## Troubleshooting

### ksqlDB: "Connection refused" error
- Wait 15-30 seconds for ksqlDB to start
- Check logs: `docker logs ksqldb-server`

### Flink: "Session creation failed"
- Wait 30-60 seconds for Flink to fully initialize
- Verify all Flink containers are running: `docker ps | grep flink`
- Check SQL Gateway logs: `docker logs flink-sql-gateway`
- Check JobManager logs: `docker logs flink-jobmanager`

### Database: "Connection refused"
- Ensure PostgreSQL is running: `docker ps | grep postgres`
- Check connection string in `appsettings.json`

### "Unknown stream engine provider"
- Verify `StreamEngine.Provider` is either `"KsqlDb"` or `"Flink"` (case-sensitive)

---

## Next Steps

- See `docs/ENGINE_ABSTRACTION_PLAN.md` for architecture details
- See `docs/FLINK_MIGRATION.md` for SQL syntax migration guide (coming soon)
- See `docs/BEST_PRACTICES.md` for capacity planning

---

## Development vs Production

### Development (Current Setup)
- Single ksqlDB or Flink instance
- No authentication
- Lightweight resource limits

### Production Recommendations
- Multi-instance deployment (see `CAPACITY_PLANNING.md`)
- Add authentication/authorization
- Configure TLS/SSL
- Set up monitoring (Prometheus, Grafana)
- Implement backup strategy for PostgreSQL
