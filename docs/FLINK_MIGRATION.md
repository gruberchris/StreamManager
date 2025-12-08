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

### Key Difference: Explicit Table Definitions

In ksqlDB, you define a stream once and it persists in the ksqlDB server.

In Flink (with Stream Manager), **you provide the complete SQL script** for every operation. This script includes:
1. **Source Table Definitions** (`CREATE TABLE ... WITH ...`)
2. **Sink Table Definitions** (for managed streams)
3. **Transformation Logic** (`SELECT` or `INSERT INTO`)

This gives you full control over the schema and connector properties for each query.

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

### New Pattern: Complete SQL Scripts

Instead of just writing the SELECT statement, you now write the **full SQL script**.

#### **ksqlDB Approach:**
```sql
-- One-time setup
CREATE STREAM orders (...) WITH (...);

-- Query
SELECT * FROM orders WHERE amount > 1000 EMIT CHANGES;
```

#### **Flink Approach (Stream Manager):**

**For Ad-Hoc Queries (Testing):**
```sql
-- Define table inline (ephemeral)
CREATE TABLE orders (
    order_id STRING,
    amount DECIMAL(10, 2)
) WITH (
    'connector' = 'kafka',
    'topic' = 'orders',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json',
    'scan.startup.mode' = 'earliest-offset',
    'scan.bounded.mode' = 'latest-offset'  -- Bounded for testing
);

-- Run query
SELECT * FROM orders WHERE amount > 1000 LIMIT 10;
```

**For Managed Streams (Production):**
```sql
-- Define source (unbounded)
CREATE TABLE orders (
    order_id STRING,
    amount DECIMAL(10, 2)
) WITH (
    'connector' = 'kafka',
    'topic' = 'orders',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json',
    'scan.startup.mode' = 'earliest-offset'
);

-- Define sink
CREATE TABLE high_value_orders (
    order_id STRING,
    amount DECIMAL(10, 2)
) WITH (
    'connector' = 'kafka',
    'topic' = 'high_value_orders',
    'properties.bootstrap.servers' = 'broker:29092',
    'format' = 'json'
);

-- Deploy
INSERT INTO high_value_orders
SELECT * FROM orders WHERE amount > 1000;
```

**Key Insight:** You explicitly define your inputs and outputs in every script.

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
-- Define table
CREATE TABLE orders (...) WITH (...);

-- Query
SELECT 
  order_id,
  customer_id,
  amount,
  product_name
FROM orders
WHERE amount > 500;
```

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
-- Define table with Watermark (required for windows)
CREATE TABLE orders (
  order_id INT,
  customer_id INT,
  amount DOUBLE,
  order_time TIMESTAMP(3),
  WATERMARK FOR order_time AS order_time - INTERVAL '5' SECOND
) WITH (...);

-- Query
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
1. Added WATERMARK definition to table
2. Removed `WINDOW TUMBLING`
3. Added `TUMBLE(...)` to GROUP BY

---

## Testing Queries

### Validation Checklist

✅ **Include `CREATE TABLE` for all sources**  
✅ **Remove all `EMIT CHANGES` keywords**  
✅ **Replace window clauses** with Flink window functions  
✅ **Update time functions** (UNIX_TIMESTAMP → CURRENT_TIMESTAMP)  
✅ **Check data types** (VARCHAR → STRING, add precision to TIMESTAMP)  
✅ **Use bounded mode** (`'scan.bounded.mode' = 'latest-offset'`) for testing  

---

## Summary

### Quick Migration Checklist

- [ ] Create full SQL script with `CREATE TABLE` statements
- [ ] Remove all `EMIT CHANGES`
- [ ] Replace `WINDOW X` with window functions in GROUP BY
- [ ] Update time functions (UNIX_TIMESTAMP → CURRENT_TIMESTAMP)
- [ ] Test query in "Ad-Hoc" mode first
- [ ] Deploy via "Create Stream" (using `INSERT INTO`)

### Key Advantages of Flink

✅ **Better window support** — More flexible time semantics  
✅ **Standard SQL** — Closer to ANSI SQL  
✅ **Time-bounded joins** — Better memory management  
✅ **Full Schema Control** — Define connectors exactly how you want

---

**Migration Status:** ✅ Complete — Ready for production use!