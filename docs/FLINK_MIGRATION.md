# Flink SQL Migration Guide

**Status:** 🚧 To be completed in Phase 4

This guide will help you migrate queries from ksqlDB syntax to Flink SQL syntax.

---

## Quick Reference

| Feature | ksqlDB | Flink SQL |
|---------|--------|-----------|
| **Streaming SELECT** | `SELECT * FROM orders EMIT CHANGES;` | `SELECT * FROM orders;` |
| **Create Stream** | `CREATE STREAM AS SELECT...` | `CREATE TABLE AS SELECT...` |
| **Tumbling Window** | `WINDOW TUMBLING (SIZE 5 MINUTES)` | `TUMBLE(time_col, INTERVAL '5' MINUTE)` |
| **Hopping Window** | `WINDOW HOPPING (SIZE 1 HOUR, ADVANCE BY 15 MINUTES)` | `HOP(time_col, INTERVAL '15' MINUTE, INTERVAL '1' HOUR)` |

---

## Common Patterns

### Pattern 1: Simple Filter

**ksqlDB:**
```sql
SELECT * FROM orders 
WHERE amount > 100 
EMIT CHANGES;
```

**Flink:**
```sql
SELECT * FROM orders 
WHERE amount > 100;
```

**Migration:** Remove `EMIT CHANGES`

---

### Pattern 2: Aggregation

**ksqlDB:**
```sql
SELECT 
  customer_id,
  COUNT(*) as order_count,
  SUM(amount) as total_amount
FROM orders
GROUP BY customer_id
EMIT CHANGES;
```

**Flink:**
```sql
SELECT 
  customer_id,
  COUNT(*) as order_count,
  SUM(amount) as total_amount
FROM orders
GROUP BY customer_id;
```

**Migration:** Remove `EMIT CHANGES`

---

### Pattern 3: Window Aggregation

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
  TUMBLE_START(order_time, INTERVAL '5' MINUTE) as window_start
FROM orders
GROUP BY 
  customer_id,
  TUMBLE(order_time, INTERVAL '5' MINUTE);
```

**Migration:** 
1. Remove `EMIT CHANGES`
2. Replace `WINDOW TUMBLING (SIZE X)` with `TUMBLE(time_column, INTERVAL 'X' UNIT)`
3. Add TUMBLE grouping clause
4. Requires time attribute column

---

### Pattern 4: JOIN

**ksqlDB:**
```sql
SELECT 
  o.order_id,
  o.amount,
  c.customer_name
FROM orders o
JOIN customers c ON o.customer_id = c.id
EMIT CHANGES;
```

**Flink:**
```sql
SELECT 
  o.order_id,
  o.amount,
  c.customer_name
FROM orders o
JOIN customers c ON o.customer_id = c.id;
```

**Migration:** Remove `EMIT CHANGES` (JOIN syntax is similar)

---

## Detailed Differences

### 1. EMIT CHANGES
- **ksqlDB:** Required for streaming queries (or added automatically)
- **Flink:** Not used; streaming is inferred from table type

### 2. CREATE Statements
- **ksqlDB:** `CREATE STREAM view_name AS SELECT...`
- **Flink:** `CREATE TABLE view_name AS SELECT...` or separate `INSERT INTO`

### 3. Window Functions
- **ksqlDB:** `WINDOW TUMBLING/HOPPING/SESSION`
- **Flink:** `TUMBLE()`, `HOP()`, `SESSION()` functions in GROUP BY clause

### 4. Time Attributes
- **ksqlDB:** Implicit event time handling
- **Flink:** Explicit time attributes required (`event_time TIMESTAMP(3) WATERMARK FOR event_time AS...`)

---

## Step-by-Step Migration Process

1. **Identify all ad-hoc queries**
   - Simple: Remove `EMIT CHANGES`
   - With windows: Rewrite window syntax

2. **Identify all persistent queries**
   - Update CREATE syntax
   - Add explicit time attributes if using windows

3. **Test each query individually**
   - Compare results with ksqlDB version
   - Verify performance

4. **Update rate limits if needed**
   - Flink may have different resource profiles

---

## Tools & Resources

### Syntax Validators
- **ksqlDB:** https://docs.ksqldb.io/en/latest/
- **Flink SQL:** https://nightlies.apache.org/flink/flink-docs-stable/docs/dev/table/sql/queries/

### Testing Approach
1. Deploy with Flink
2. Run queries in Flink SQL Client first
3. Once verified, deploy via Stream Manager

---

## Known Limitations

### ksqlDB Features Not in Flink
- **EMIT CHANGES** - Not valid in Flink SQL (streaming is inferred)
- **CREATE STREAM** - Flink uses CREATE TABLE instead
- **ROWTIME** - Flink uses watermarks and time attributes differently
- **STRUCT** - Different complex type syntax

### Flink Features Not in ksqlDB
- **Bounded queries** - LIMIT works in Flink persistent queries
- **Complex event processing** - MATCH_RECOGNIZE for patterns
- **Temporal tables** - Time-versioned lookups
- **Catalogs** - Multi-catalog support

---

## Getting Help

If you encounter migration issues:
1. Check Flink SQL documentation
2. Test query in Flink SQL Client
3. Review Flink logs for detailed error messages
4. Consult `ENGINE_ABSTRACTION_PLAN.md` for architecture details

---

---

## Implementation Status

✅ **FlinkEngine.cs:** Fully implemented  
✅ **FlinkQueryValidator.cs:** Complete with syntax validation  
✅ **Docker Compose:** Flink services configured  
✅ **Documentation:** Phase 4 complete

---

## Quick Testing Guide

### Test 1: Verify Flink is Running

```bash
# Check Flink Web UI
curl http://localhost:8081/overview

# Check SQL Gateway
curl http://localhost:8083/v1/info
```

### Test 2: Run Ad-Hoc Query (via API)

```bash
curl -X POST http://localhost:7068/api/test/query \
  -H "Content-Type: application/json" \
  -d '{
    "query": "SELECT customer_id, order_total FROM orders WHERE order_total > 100 LIMIT 5"
  }'
```

### Test 3: Check Validation (Should Fail)

```bash
curl -X POST http://localhost:7068/api/test/query \
  -H "Content-Type: application/json" \
  -d '{
    "query": "SELECT * FROM orders EMIT CHANGES"
  }'

# Expected: Validation error about EMIT CHANGES
```

---

## Additional Resources

- [Flink SQL Documentation](https://nightlies.apache.org/flink/flink-docs-stable/docs/dev/table/sql/queries/overview/)
- [Flink SQL Gateway REST API](https://nightlies.apache.org/flink/flink-docs-stable/docs/dev/table/sql-gateway/rest/)
- [Phase 4 Implementation Details](PHASE4_IMPLEMENTATION.md)

---

**Status:** ✅ Complete - Ready for production use!
