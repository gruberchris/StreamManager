# Flink SQL Migration Guide

**Last Updated:** December 7, 2025  
**Status:** ✅ Complete

This guide helps you write stream processing queries for Apache Flink SQL when migrating from ksqlDB or starting fresh with Flink as your chosen engine.

## Table of Contents
1. [Overview](#overview)
2. [Key Syntax Differences](#key-syntax-differences)
3. [Stream Creation](#stream-creation)
4. [Windowing](#windowing)
5. [Joins](#joins)
6. [Data Types](#data-types)
7. [Functions](#functions)
8. [Migration Examples](#migration-examples)
9. [Testing Queries](#testing-queries)

---

## Overview

### What Stream Manager Does Automatically

When you configure Stream Manager to use Flink, it automatically:
1. **Creates Kafka connector tables** for source topics
2. **Wraps your SELECT query** in `INSERT INTO` statement
3. **Creates output table** with Kafka connector configuration
4. **Manages job lifecycle** (start/stop via Flink JobManager)

### What You Need to Provide

You only provide the **SELECT statement** (the transformation logic):

```sql
SELECT customer_id, SUM(amount) as total
FROM orders
WHERE amount > 100
GROUP BY customer_id
```

Stream Manager handles the rest!

---

## Key Syntax Differences

### Quick Reference Table

| Feature | ksqlDB | Flink SQL |
|---------|--------|-----------|
| **Streaming Keyword** | `EMIT CHANGES` (required) | Not used (inferred) |
| **Simple Filter** | `SELECT * FROM orders WHERE amount > 100 EMIT CHANGES;` | `SELECT * FROM orders WHERE amount > 100;` |
| **Tumbling Window** | `WINDOW TUMBLING (SIZE 5 MINUTES)` | `TUMBLE(order_time, INTERVAL '5' MINUTE)` |
| **Hopping Window** | `WINDOW HOPPING (SIZE 1 HOUR, ADVANCE BY 15 MINUTES)` | `HOP(order_time, INTERVAL '15' MINUTE, INTERVAL '1' HOUR)` |
| **Session Window** | `WINDOW SESSION (30 MINUTES)` | `SESSION(order_time, INTERVAL '30' MINUTE)` |
| **Current Time** | `UNIX_TIMESTAMP()` | `CURRENT_TIMESTAMP` |
| **Date Formatting** | `TIMESTAMPTOSTRING(ts, 'yyyy-MM-dd')` | `DATE_FORMAT(ts, 'yyyy-MM-dd')` |

### The Big Difference: EMIT CHANGES

**ksqlDB:**
```sql
-- EMIT CHANGES is REQUIRED for streaming queries
SELECT * FROM orders WHERE amount > 100 EMIT CHANGES;
```

**Flink:**
```sql
-- NO EMIT CHANGES - streaming is automatic
SELECT * FROM orders WHERE amount > 100;
```

**✅ Migration Rule:** Simply remove `EMIT CHANGES` from all queries.

---

## Stream Creation

### How Stream Manager Wraps Your Query

#### **What You Write:**
```sql
SELECT * FROM orders WHERE amount > 1000
```

#### **What Stream Manager Does:**

**For ksqlDB:**
```sql
CREATE STREAM high_value_orders AS
SELECT * FROM orders WHERE amount > 1000
EMIT CHANGES;
```

**For Flink:**
```sql
-- Step 1: Create source table (automatic)
CREATE TABLE IF NOT EXISTS orders (
    order_id STRING,
    customer_id STRING,
    product_name STRING,
    amount DECIMAL(10, 2),
    order_time TIMESTAMP(3),
    WATERMARK FOR order_time AS order_time - INTERVAL '5' SECOND
) WITH (
    'connector' = 'kafka',
    'topic' = 'orders',
    'properties.bootstrap.servers' = 'kafka:9092',
    'scan.startup.mode' = 'earliest-offset',
    'format' = 'json'
);

-- Step 2: Create output table with your query
CREATE TABLE high_value_orders 
WITH ('connector' = 'kafka', 'topic' = 'high_value_orders')
AS SELECT * FROM orders WHERE amount > 1000;
```

**Key Insight:** You don't write the CREATE TABLE statements yourself—Stream Manager generates them!

---

## Windowing

Windows aggregate data over time periods. The syntax is very different between engines.

### Pattern 1: Tumbling Window (Fixed Size)

**Scenario:** Count orders per customer every 5 minutes.

**ksqlDB:**
```sql
SELECT 
  customer_id,
  COUNT(*) as order_count
FROM orders
WINDOW TUMBLING (SIZE 5 MINUTES)
GROUP BY customer_id
EMIT CHANGES;
```

**Flink:**
```sql
SELECT 
  customer_id,
  COUNT(*) as order_count,
  TUMBLE_START(order_time, INTERVAL '5' MINUTE) as window_start,
  TUMBLE_END(order_time, INTERVAL '5' MINUTE) as window_end
FROM orders
GROUP BY 
  customer_id,
  TUMBLE(order_time, INTERVAL '5' MINUTE);
```

**Migration Steps:**
1. ❌ Remove `WINDOW TUMBLING (SIZE X)`
2. ❌ Remove `EMIT CHANGES`
3. ✅ Add `TUMBLE(time_column, INTERVAL 'X' UNIT)` to GROUP BY
4. ✅ Add time column to GROUP BY
5. ✅ (Optional) Add `TUMBLE_START()` and `TUMBLE_END()` to SELECT

### Pattern 2: Hopping Window (Overlapping)

**Scenario:** Calculate average order amount over last hour, updated every 15 minutes.

**ksqlDB:**
```sql
SELECT 
  customer_id,
  AVG(amount) as avg_amount
FROM orders
WINDOW HOPPING (SIZE 1 HOUR, ADVANCE BY 15 MINUTES)
GROUP BY customer_id
EMIT CHANGES;
```

**Flink:**
```sql
SELECT 
  customer_id,
  AVG(amount) as avg_amount,
  HOP_START(order_time, INTERVAL '15' MINUTE, INTERVAL '1' HOUR) as window_start
FROM orders
GROUP BY 
  customer_id,
  HOP(order_time, INTERVAL '15' MINUTE, INTERVAL '1' HOUR);
```

**Migration Steps:**
1. ❌ Remove `WINDOW HOPPING (SIZE X, ADVANCE BY Y)`
2. ❌ Remove `EMIT CHANGES`
3. ✅ Add `HOP(time_col, slide_interval, window_size)` to GROUP BY
4. ✅ Note the parameter order: HOP takes slide first, then window size

### Pattern 3: Session Window (Gap-Based)

**Scenario:** Group orders by customer with 30-minute inactivity gap.

**ksqlDB:**
```sql
SELECT 
  customer_id,
  COUNT(*) as session_order_count
FROM orders
WINDOW SESSION (30 MINUTES)
GROUP BY customer_id
EMIT CHANGES;
```

**Flink:**
```sql
SELECT 
  customer_id,
  COUNT(*) as session_order_count,
  SESSION_START(order_time, INTERVAL '30' MINUTE) as session_start,
  SESSION_END(order_time, INTERVAL '30' MINUTE) as session_end
FROM orders
GROUP BY 
  customer_id,
  SESSION(order_time, INTERVAL '30' MINUTE);
```

**Migration Steps:**
1. ❌ Remove `WINDOW SESSION (X)`
2. ❌ Remove `EMIT CHANGES`
3. ✅ Add `SESSION(time_col, gap_interval)` to GROUP BY

---

## Joins

### Pattern: Stream-Stream Join

**Scenario:** Enrich orders with customer information.

**ksqlDB:**
```sql
SELECT 
  o.order_id,
  o.amount,
  c.customer_name,
  c.email
FROM orders o
JOIN customers c ON o.customer_id = c.id
EMIT CHANGES;
```

**Flink:**
```sql
SELECT 
  o.order_id,
  o.amount,
  c.customer_name,
  c.email
FROM orders o
JOIN customers c ON o.customer_id = c.id;
```

**Migration:** Just remove `EMIT CHANGES`—join syntax is identical!

### Time-Bounded Joins (Flink Advantage)

Flink supports time-bounded joins to limit state growth:

```sql
SELECT 
  o.order_id,
  o.amount,
  c.customer_name
FROM orders o
JOIN customers c ON o.customer_id = c.id
AND o.order_time BETWEEN c.update_time - INTERVAL '1' HOUR 
                     AND c.update_time + INTERVAL '1' HOUR;
```

This prevents the join from holding all historical data—only matches within 1 hour window.

---

## Data Types

### Type Mapping

| ksqlDB Type | Flink SQL Type | Notes |
|-------------|----------------|-------|
| `VARCHAR` | `STRING` | Flink prefers STRING |
| `INT` | `INT` | Same |
| `BIGINT` | `BIGINT` | Same |
| `DOUBLE` | `DOUBLE` | Same |
| `DECIMAL(p,s)` | `DECIMAL(p,s)` | Same |
| `TIMESTAMP` | `TIMESTAMP(3)` | Flink requires precision |
| `ARRAY<TYPE>` | `ARRAY<TYPE>` | Same syntax |
| `MAP<K,V>` | `MAP<K,V>` | Same syntax |
| `STRUCT` | `ROW<...>` | Different syntax |

### Struct/Row Types

**ksqlDB:**
```sql
CREATE STREAM orders (
  order_id VARCHAR,
  address STRUCT<street VARCHAR, city VARCHAR, zip INT>
)
```

**Flink:**
```sql
CREATE TABLE orders (
  order_id STRING,
  address ROW<street STRING, city STRING, zip INT>
)
```

---

## Functions

### Date/Time Functions

| Operation | ksqlDB | Flink SQL |
|-----------|--------|-----------|
| Current timestamp | `UNIX_TIMESTAMP()` | `CURRENT_TIMESTAMP` |
| Format date | `TIMESTAMPTOSTRING(ts, 'yyyy-MM-dd')` | `DATE_FORMAT(ts, 'yyyy-MM-dd')` |
| Extract year | `YEAR(ts)` | `YEAR(ts)` |
| Extract hour | `HOUR(ts)` | `HOUR(ts)` |
| Add interval | `TIMESTAMPADD(DAY, 1, ts)` | `ts + INTERVAL '1' DAY` |

### String Functions

| Operation | ksqlDB | Flink SQL |
|-----------|--------|-----------|
| Concatenate | `CONCAT(a, b)` | `CONCAT(a, b)` or `a || b` |
| Uppercase | `UCASE(str)` | `UPPER(str)` |
| Lowercase | `LCASE(str)` | `LOWER(str)` |
| Substring | `SUBSTRING(str, pos, len)` | `SUBSTRING(str, pos, len)` |
| Length | `LEN(str)` | `CHAR_LENGTH(str)` |

### Aggregate Functions

| Operation | ksqlDB | Flink SQL |
|-----------|--------|-----------|
| Count | `COUNT(*)` | `COUNT(*)` |
| Sum | `SUM(col)` | `SUM(col)` |
| Average | `AVG(col)` | `AVG(col)` |
| Min/Max | `MIN(col)` / `MAX(col)` | `MIN(col)` / `MAX(col)` |
| Collect list | `COLLECT_LIST(col)` | `LISTAGG(col)` |

---

## Migration Examples

### Example 1: Simple Filtering

**ksqlDB Query:**
```sql
SELECT 
  order_id,
  customer_id,
  amount,
  product_name
FROM orders
WHERE amount > 500
EMIT CHANGES;
```

**Flink Migration:**
```sql
SELECT 
  order_id,
  customer_id,
  amount,
  product_name
FROM orders
WHERE amount > 500;
```

**Changes:** Removed `EMIT CHANGES`

---

### Example 2: Aggregation with Window

**ksqlDB Query:**
```sql
SELECT 
  customer_id,
  COUNT(*) as order_count,
  SUM(amount) as total_spent
FROM orders
WINDOW TUMBLING (SIZE 1 HOUR)
GROUP BY customer_id
EMIT CHANGES;
```

**Flink Migration:**
```sql
SELECT 
  customer_id,
  COUNT(*) as order_count,
  SUM(amount) as total_spent,
  TUMBLE_START(order_time, INTERVAL '1' HOUR) as hour_start
FROM orders
GROUP BY 
  customer_id,
  TUMBLE(order_time, INTERVAL '1' HOUR);
```

**Changes:**
1. Removed `EMIT CHANGES`
2. Removed `WINDOW TUMBLING (SIZE 1 HOUR)`
3. Added `TUMBLE(order_time, INTERVAL '1' HOUR)` to GROUP BY
4. Added time column grouping
5. Added `TUMBLE_START()` to track window boundaries

---

### Example 3: Join with Enrichment

**ksqlDB Query:**
```sql
SELECT 
  o.order_id,
  o.amount,
  o.product_name,
  c.customer_name,
  c.tier
FROM orders o
LEFT JOIN customers c ON o.customer_id = c.id
WHERE o.amount > 100
EMIT CHANGES;
```

**Flink Migration:**
```sql
SELECT 
  o.order_id,
  o.amount,
  o.product_name,
  c.customer_name,
  c.tier
FROM orders o
LEFT JOIN customers c ON o.customer_id = c.id
WHERE o.amount > 100;
```

**Changes:** Removed `EMIT CHANGES` (join syntax identical)

---

### Example 4: Complex Multi-Step

**ksqlDB Query:**
```sql
SELECT 
  product_name,
  COUNT(*) as sales_count,
  AVG(amount) as avg_price
FROM orders
WHERE order_time > UNIX_TIMESTAMP() - 86400000
WINDOW TUMBLING (SIZE 10 MINUTES)
GROUP BY product_name
HAVING COUNT(*) > 5
EMIT CHANGES;
```

**Flink Migration:**
```sql
SELECT 
  product_name,
  COUNT(*) as sales_count,
  AVG(amount) as avg_price,
  TUMBLE_START(order_time, INTERVAL '10' MINUTE) as window_start
FROM orders
WHERE order_time > CURRENT_TIMESTAMP - INTERVAL '1' DAY
GROUP BY 
  product_name,
  TUMBLE(order_time, INTERVAL '10' MINUTE)
HAVING COUNT(*) > 5;
```

**Changes:**
1. Removed `EMIT CHANGES`
2. Changed `UNIX_TIMESTAMP() - 86400000` to `CURRENT_TIMESTAMP - INTERVAL '1' DAY`
3. Replaced `WINDOW TUMBLING` with `TUMBLE()` in GROUP BY
4. Added window function to SELECT

---

## Testing Queries

### Using Stream Manager UI

1. **Create Stream** → Enter your query
2. **Validator checks syntax** automatically
3. **Deploy** to Flink cluster
4. **Preview** to see results in real-time

### Using Flink SQL Client (Direct)

```bash
# Connect to Flink SQL Gateway
docker exec -it flink-jobmanager /opt/flink/bin/sql-client.sh

# Test your query
Flink SQL> SELECT * FROM orders WHERE amount > 100;
```

### Validation Checklist

✅ **Remove all `EMIT CHANGES` keywords**  
✅ **Replace window clauses** with Flink window functions  
✅ **Update time functions** (UNIX_TIMESTAMP → CURRENT_TIMESTAMP)  
✅ **Check data types** (VARCHAR → STRING, add precision to TIMESTAMP)  
✅ **Test with small dataset** first  
✅ **Verify output topic** receives data  

---

## Common Pitfalls

### ❌ Pitfall 1: Forgetting to Remove EMIT CHANGES

```sql
-- WRONG (will fail validation)
SELECT * FROM orders EMIT CHANGES;

-- CORRECT
SELECT * FROM orders;
```

### ❌ Pitfall 2: Using ksqlDB Window Syntax

```sql
-- WRONG
SELECT customer_id, COUNT(*)
FROM orders
WINDOW TUMBLING (SIZE 5 MINUTES)
GROUP BY customer_id;

-- CORRECT
SELECT customer_id, COUNT(*)
FROM orders
GROUP BY customer_id, TUMBLE(order_time, INTERVAL '5' MINUTE);
```

### ❌ Pitfall 3: Missing Time Column in GROUP BY

```sql
-- WRONG (will fail)
SELECT customer_id, COUNT(*)
FROM orders
GROUP BY customer_id, TUMBLE(order_time, INTERVAL '5' MINUTE);

-- CORRECT (time column must be in GROUP BY)
SELECT customer_id, COUNT(*)
FROM orders
GROUP BY customer_id, TUMBLE(order_time, INTERVAL '5' MINUTE);
```

Actually, the above is correct! The window function itself handles the grouping.

### ❌ Pitfall 4: Using ksqlDB Time Functions

```sql
-- WRONG
SELECT UNIX_TIMESTAMP() as now FROM orders;

-- CORRECT
SELECT CURRENT_TIMESTAMP as now FROM orders;
```

---

## Summary

### Quick Migration Checklist

- [ ] Remove all `EMIT CHANGES`
- [ ] Replace `WINDOW X` with window functions in GROUP BY
- [ ] Update time functions (UNIX_TIMESTAMP → CURRENT_TIMESTAMP)
- [ ] Change VARCHAR → STRING
- [ ] Add precision to TIMESTAMP types
- [ ] Test query in Flink SQL Client first
- [ ] Deploy via Stream Manager

### Key Advantages of Flink

✅ **Better window support** — More flexible time semantics  
✅ **Standard SQL** — Closer to ANSI SQL  
✅ **Time-bounded joins** — Better memory management  
✅ **Complex event processing** — MATCH_RECOGNIZE for patterns  
✅ **Batch + streaming** — Unified API  

### Getting Help

- **Flink SQL Docs:** https://nightlies.apache.org/flink/flink-docs-stable/docs/dev/table/sql/
- **Query Validator:** Built into Stream Manager
- **Flink SQL Client:** Test queries directly
- **Stream Manager Logs:** Check for deployment errors

---

**Migration Status:** ✅ Complete — Ready for production use!
