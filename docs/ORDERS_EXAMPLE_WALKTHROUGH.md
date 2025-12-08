# Stream Manager Examples & Testing Guide

**Complete guide for testing and using Stream Manager with real examples for both ksqlDB and Flink.**

---

## Table of Contents

1. [Prerequisites & Setup](#prerequisites--setup)
2. [Quick Test Guide](#quick-test-guide)
3. [Ad-Hoc Query Examples](#ad-hoc-query-examples)
4. [Managed Stream Examples](#managed-stream-examples)
5. [Understanding Query Behavior](#understanding-query-behavior)
6. [Troubleshooting](#troubleshooting)

---

## Prerequisites & Setup

### Initial Setup

Before running any examples, create the Kafka topic and populate it with data.

#### Create the Kafka Topic

```bash
# Create the 'orders' topic in Kafka
docker exec -it broker kafka-topics --create \
  --topic orders \
  --bootstrap-server localhost:9092 \
  --partitions 3 \
  --replication-factor 1

# Verify topic was created
docker exec -it broker kafka-topics --list \
  --bootstrap-server localhost:9092
```

#### Generate Sample Data

The `generate_orders.sh` script inserts sample orders into the Kafka topic.

**Data Schema:**
```
ORDER_ID: INT (1011, 1012, 1013, ...)
CUSTOMER_ID: INT (500-599)
PRODUCT: STRING (Laptop, Mouse, Keyboard, etc.)
AMOUNT: DOUBLE (10.00 - 1500.00)
PURCHASE_DATE: STRING (ISO 8601 format: "2025-12-07T20:30:00")
```

**Start generating orders:**
```bash
cd scripts
./generate_orders.sh
```

Leave this running for 30-60 seconds to generate several orders, then press `Ctrl+C` to stop (or keep it running for continuous managed streams).

#### Verify Data in Kafka

```bash
docker exec broker kafka-console-consumer \
  --bootstrap-server localhost:9092 \
  --topic orders \
  --from-beginning \
  --max-messages 5 \
  --timeout-ms 3000
```

You should see JSON messages like:
```json
{"ORDER_ID":1011,"CUSTOMER_ID":573,"PRODUCT":"External SSD 1TB","AMOUNT":342.78,"PURCHASE_DATE":"2025-12-07T20:50:06"}
```

---

## Quick Test Guide

### Step 1: Start Infrastructure

**For ksqlDB:**
```bash
docker compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml up -d
```

**For Flink:**
```bash
docker compose -f docker-compose.base.yml -f docker-compose.flink.yml up -d
```

### Step 2: Generate Test Data

```bash
cd scripts
./generate_orders.sh
```

### Step 3: Run Your First Ad-Hoc Query

#### Via Web UI (Recommended)
1. Navigate to `https://localhost:7122` (accept SSL warning)
2. Go to "Ad-Hoc Query" page
3. Paste the SQL for your engine:

**ksqlDB:**
```sql
SELECT * FROM orders WHERE AMOUNT > 500 EMIT CHANGES LIMIT 5;
```

**Flink:**
```sql
CREATE TABLE orders (
    `ORDER_ID` INT,
    `CUSTOMER_ID` INT,
    `PRODUCT` STRING,
    `AMOUNT` DOUBLE,
    `PURCHASE_DATE` STRING
) WITH (
    'connector' = 'kafka',
    'topic' = 'orders',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json',
    'scan.startup.mode' = 'earliest-offset'
);

SELECT * FROM orders WHERE AMOUNT > 500 LIMIT 5;
```

4. Click "Execute Query"

#### Expected Results

You should see orders with AMOUNT > 500, for example:
```
ORDER_ID | CUSTOMER_ID | PRODUCT          | AMOUNT   | PURCHASE_DATE
---------|-------------|------------------|----------|---------------------------
1012     | 543         | Laptop           | 586.11   | 2025-12-07T20:52:19
1013     | 582         | USB Cable        | 1103.38  | 2025-12-07T20:50:13
1014     | 594         | Webcam HD        | 1324.25  | 2025-12-07T20:50:19
```

---

## Ad-Hoc Query Examples

**Purpose:** Test queries, explore data, prototype transformations

**Key Points (Flink):**
- Results from data that existed **before** the query started
- Query stops after returning results (not continuous)
- Tables are ephemeral (automatically cleaned up)

### Example 1: Simple Filter

**ksqlDB:**
```sql
SELECT * FROM orders WHERE AMOUNT > 500 EMIT CHANGES LIMIT 10;
```

**Flink:**
```sql
CREATE TABLE orders (
    `ORDER_ID` INT,
    `CUSTOMER_ID` INT,
    `PRODUCT` STRING,
    `AMOUNT` DOUBLE,
    `PURCHASE_DATE` STRING
) WITH (
    'connector' = 'kafka',
    'topic' = 'orders',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json',
    'scan.startup.mode' = 'earliest-offset'
);

SELECT * FROM orders WHERE AMOUNT > 500 LIMIT 10;
```

### Example 2: Product Search

**ksqlDB:**
```sql
SELECT * FROM orders WHERE PRODUCT LIKE '%Laptop%' EMIT CHANGES LIMIT 10;
```

**Flink:**
```sql
CREATE TABLE orders (
    `ORDER_ID` INT,
    `PRODUCT` STRING,
    `AMOUNT` DOUBLE
) WITH (
    'connector' = 'kafka',
    'topic' = 'orders',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json',
    'scan.startup.mode' = 'earliest-offset'
);

SELECT * FROM orders WHERE PRODUCT LIKE '%Laptop%' LIMIT 10;
```

### Example 3: Customer Analysis

**ksqlDB:**
```sql
SELECT 
    CUSTOMER_ID,
    COUNT(*) as ORDER_COUNT,
    SUM(AMOUNT) as TOTAL_SPENT
FROM orders
GROUP BY CUSTOMER_ID
EMIT CHANGES
LIMIT 10;
```

**Flink:**
```sql
CREATE TABLE orders (
    `ORDER_ID` INT,
    `CUSTOMER_ID` INT,
    `AMOUNT` DOUBLE
) WITH (
    'connector' = 'kafka',
    'topic' = 'orders',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json',
    'scan.startup.mode' = 'earliest-offset'
);

SELECT 
    CUSTOMER_ID,
    COUNT(*) as ORDER_COUNT,
    SUM(AMOUNT) as TOTAL_SPENT
FROM orders
GROUP BY CUSTOMER_ID
LIMIT 10;
```

---

## Managed Stream Examples

**Purpose:** Deploy persistent streaming jobs for production

**Key Points (Flink):**
- Runs continuously, processing new data as it arrives
- Use `INSERT INTO` to create persistent Flink jobs
- Omit `scan.bounded.mode` for unbounded streaming
- Job continues running after session closes

### Example 1: Filter High-Value Orders

**ksqlDB:**
```sql
CREATE STREAM high_value_orders AS
SELECT * FROM orders WHERE AMOUNT > 500
EMIT CHANGES;
```

**Flink:**
```sql
-- Define source table (unbounded)
CREATE TABLE orders (
    `ORDER_ID` INT,
    `CUSTOMER_ID` INT,
    `PRODUCT` STRING,
    `AMOUNT` DOUBLE,
    `PURCHASE_DATE` STRING
) WITH (
    'connector' = 'kafka',
    'topic' = 'orders',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json',
    'scan.startup.mode' = 'earliest-offset'
);

-- Define sink table
CREATE TABLE high_value_orders (
    `ORDER_ID` INT,
    `CUSTOMER_ID` INT,
    `PRODUCT` STRING,
    `AMOUNT` DOUBLE,
    `PURCHASE_DATE` STRING
) WITH (
    'connector' = 'kafka',
    'topic' = 'high_value_orders',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json'
);

-- Deploy continuous processing
INSERT INTO high_value_orders
SELECT * FROM orders WHERE AMOUNT > 500;
```

### Example 2: Tumbling Window Aggregation

**ksqlDB:**
```sql
CREATE TABLE product_sales_hourly AS
SELECT 
    PRODUCT,
    COUNT(*) AS UNITS_SOLD,
    SUM(AMOUNT) AS REVENUE
FROM orders
WINDOW TUMBLING (SIZE 1 HOUR)
GROUP BY PRODUCT
EMIT CHANGES;
```

**Flink:**
```sql
CREATE TABLE orders (
    `ORDER_ID` INT,
    `PRODUCT` STRING,
    `AMOUNT` DOUBLE,
    `PURCHASE_DATE` TIMESTAMP(3),
    WATERMARK FOR PURCHASE_DATE AS PURCHASE_DATE - INTERVAL '5' SECOND
) WITH (
    'connector' = 'kafka',
    'topic' = 'orders',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json',
    'scan.startup.mode' = 'earliest-offset'
);

CREATE TABLE product_sales_hourly (
    `PRODUCT` STRING,
    `WINDOW_START` TIMESTAMP(3),
    `WINDOW_END` TIMESTAMP(3),
    `UNITS_SOLD` BIGINT,
    `REVENUE` DOUBLE
) WITH (
    'connector' = 'kafka',
    'topic' = 'product_sales_hourly',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json'
);

INSERT INTO product_sales_hourly
SELECT 
    PRODUCT,
    TUMBLE_START(PURCHASE_DATE, INTERVAL '1' HOUR) as WINDOW_START,
    TUMBLE_END(PURCHASE_DATE, INTERVAL '1' HOUR) as WINDOW_END,
    COUNT(*) as UNITS_SOLD,
    SUM(AMOUNT) as REVENUE
FROM orders
GROUP BY 
    PRODUCT,
    TUMBLE(PURCHASE_DATE, INTERVAL '1' HOUR);
```

---

## Understanding Query Behavior

### Ad-Hoc Queries: Snapshots vs Streams

**Flink Behavior:**
- Ad-Hoc queries run in **Bounded Mode**.
- They read all data from the topic **up to the current time**, return results, and then **stop**.
- They do **not** continue to stream new data that arrives later.
- **Why?** To prevent resource leaks from open queries during testing.

**ksqlDB Behavior:**
- Ad-Hoc queries with `EMIT CHANGES` run continuously until stopped or LIMIT is reached.

### Correct Workflows for Testing

**Option A: Data First, Then Query**
1. Run `./scripts/generate_orders.sh` for 30s.
2. Run Ad-Hoc Query.
3. View results (historical data).

**Option B: Query, See Results, Query Again**
1. Run Ad-Hoc Query → See results → Query completes.
2. Generate more data.
3. Run Ad-Hoc Query again → See updated results.

---

## Troubleshooting

### No results returned

1. **Check data exists in Kafka:**
   ```bash
   docker exec broker kafka-console-consumer --bootstrap-server localhost:9092 --topic orders --from-beginning --max-messages 5
   ```

2. **Check Flink/ksqlDB logs:**
   ```bash
   docker logs flink-jobmanager
   # or
   docker logs ksqldb-server
   ```

### "Object 'orders' not found" (Flink)

You must include the `CREATE TABLE` statement in every Flink query script. Unlike ksqlDB, Flink tables in this system are ephemeral and session-scoped.

### "No resolvable bootstrap urls" error

**Flink:** Use `'properties.bootstrap.servers' = 'broker:29092'` (not `kafka:9092`).

### Flink Field Name Case Sensitivity

Kafka JSON often uses UPPERCASE field names (e.g., `ORDER_ID`).
Flink lowercases SQL identifiers by default.

**Solution:** Use backticks around field names in your `CREATE TABLE` statement:
```sql
CREATE TABLE orders (
    `ORDER_ID` INT,
    ...
)
```

---

**Happy Streaming!** 🚀

```