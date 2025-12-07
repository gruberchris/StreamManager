# Stream Manager Scripts

Utility scripts for managing and testing Stream Manager.

---

## 📜 Available Scripts

### 🧮 `calculate_resources.sh`

Calculate resource requirements for ksqlDB or Flink deployments.

**Usage:**
```bash
./calculate_resources.sh
```

**What it does:**
- Calculates memory, CPU, and disk requirements
- Provides recommendations for scaling
- Estimates costs based on workload

---

### 🧹 `cleanup-ksqldb.sh`

Clean up orphaned ksqlDB streams and Kafka topics.

**Usage:**
```bash
./cleanup-ksqldb.sh
```

**What it does:**
- Shows current queries, streams, and topics
- Drops orphaned streams (no running query)
- Deletes associated Kafka topics
- Displays final state after cleanup

**When to use:**
- After stopping stream definitions via the API
- When orphaned streams exist in ksqlDB
- To clean up test/development environments
- Before switching to a different stream engine

**Example Output:**
```
🧹 Cleaning up ksqlDB streams and topics...

📊 Current Queries:
 Query ID                          | Status  | Query
----------------------------------+---------+------------------------
 CSAS_VIEW_ORDERS_123             | RUNNING | CREATE STREAM VIEW...

📊 Current Streams:
 Stream Name                      | Kafka Topic           | Format
----------------------------------+-----------------------+--------
 VIEW_CUSTOMER525ORDERS_E0E2A...  | VIEW_CUSTOMER525...   | JSON

🗑️  Dropping VIEW_CUSTOMER525ORDERS_E0E2AE6D7AA4448ABB50341E39E2E369...

✅ Cleanup complete!
```

---

### 🧹 `cleanup-flink.sh`

Clean up Flink jobs, SQL Gateway sessions, and Kafka topics.

**Usage:**
```bash
./cleanup-flink.sh [options]
```

**Options:**
- `--all` - Clean up everything (default if no option provided)
- `--jobs` - Cancel only running Flink jobs
- `--sessions` - Close only SQL Gateway sessions
- `--topics` - Delete only Kafka topics (excluding internal topics)

**What it does:**
- Detects running Flink services (JobManager & SQL Gateway)
- Shows current state (jobs, topics)
- Cancels running Flink jobs
- Closes SQL Gateway sessions
- Deletes Kafka topics (excludes `_internal` topics)
- Displays final state after cleanup

**When to use:**
- After stopping stream definitions via the API
- When Flink jobs are stuck or need to be restarted
- To clean up test/development environments
- Before switching to a different stream engine
- When SQL Gateway sessions accumulate

**Examples:**

```bash
# Clean up everything
./cleanup-flink.sh
# or
./cleanup-flink.sh --all

# Cancel only running jobs
./cleanup-flink.sh --jobs

# Close only SQL Gateway sessions
./cleanup-flink.sh --sessions

# Delete only Kafka topics
./cleanup-flink.sh --topics
```

**Example Output:**
```
🧹 Cleaning up Flink jobs, sessions, and topics...

✅ Flink services detected

📊 Current State:
----------------
Running Jobs: 2

🔍 Job Details:
  - abc123def456 (RUNNING): High Value Orders
  - xyz789ghi012 (RUNNING): Customer Order Summary

Kafka Topics: 3 (excluding internal)
  - orders
  - high-value-orders
  - customer-summary

----------------

🗑️  Cancelling all running Flink jobs...
  Cancelling job: abc123def456
  Cancelling job: xyz789ghi012
  ⏳ Waiting for jobs to cancel...
  ✅ All jobs cancelled successfully

🗑️  Closing all Flink SQL Gateway sessions...
  Closing session: session-abc-123
  Closing session: session-def-456
  ✅ All sessions closed

🗑️  Deleting Kafka topics...
  Deleting topic: orders
  Deleting topic: high-value-orders
  Deleting topic: customer-summary
  ⏳ Waiting for topic deletion...
  ✅ All topics deleted successfully

✅ Cleanup complete!

📊 Final State:
----------------
Running Jobs: 0
Kafka Topics: 0 (excluding internal)
----------------

💡 Tip: You can also use:
   ./cleanup-flink.sh --jobs      # Cancel only Flink jobs
   ./cleanup-flink.sh --sessions  # Close only SQL Gateway sessions
   ./cleanup-flink.sh --topics    # Delete only Kafka topics
   ./cleanup-flink.sh --all       # Clean up everything (default)
```

---

### 🛒 `generate_orders.sh`

Generate continuous sample orders for testing and development.

**Usage:**
```bash
./generate_orders.sh
```

**What it does:**
- Automatically detects which engine is running (ksqlDB or Flink)
- Creates a new SQL session (Flink only)
- Generates one order every 30 seconds
- Inserts orders into the `orders` stream/table
- Uses realistic e-commerce data

**Requirements:**
- The `orders` stream/table must exist (see Quick Start documentation)
- Either ksqlDB or Flink SQL Gateway must be running

**Generated Data:**

| Field | Type | Example | Description |
|-------|------|---------|-------------|
| **ORDER_ID** | INT | 1011 | Sequential, starting at 1011 |
| **CUSTOMER_ID** | INT | 542 | Random between 500-599 |
| **PRODUCT** | VARCHAR/STRING | "Laptop" | Random from 20 products |
| **AMOUNT** | DOUBLE | 899.99 | Random $10-$1500 |
| **PURCHASE_DATE** | VARCHAR/STRING | "2025-12-07T01:45:32" | ISO 8601 timestamp |

**Products Available:**
- Electronics: Laptop, Monitor 27inch, Webcam HD, External SSD 1TB, Phone Case
- Peripherals: Wireless Mouse, Mechanical Keyboard, Gaming Mouse, Laptop Stand
- Accessories: USB-C Hub, USB Cable, HDMI Cable, Memory Card, Screen Protector
- Audio: Headphones Wireless, Bluetooth Speaker
- Power: Power Bank, Wireless Charger
- Furniture: Desk Lamp LED, Office Chair

**Example Output:**

```bash
# ksqlDB Mode
✓ Detected ksqlDB running on port 8088

Starting order generator - creating 1 order every 30 seconds
Engine: ksqldb
Press Ctrl+C to stop

[2025-12-07T01:45:32] Inserting Order #1011: Laptop - $899.99 for Customer 542
  ✓ Successfully inserted (ksqlDB)

[2025-12-07T01:46:02] Inserting Order #1012: Wireless Mouse - $29.99 for Customer 513
  ✓ Successfully inserted (ksqlDB)
```

```bash
# Flink Mode
✓ Detected Flink SQL Gateway running on port 8083
✓ Created Flink session: 01234567-89ab-cdef-0123-456789abcdef

Starting order generator - creating 1 order every 30 seconds
Engine: flink
Press Ctrl+C to stop

[2025-12-07T01:45:32] Inserting Order #1011: Laptop - $899.99 for Customer 542
  ✓ Successfully inserted (Flink)

[2025-12-07T01:46:02] Inserting Order #1012: Wireless Mouse - $29.99 for Customer 513
  ✓ Successfully inserted (Flink)
```

**Stopping:**
Press `Ctrl+C` to stop the generator.

**Use Cases:**
- Testing stream processing queries
- Demonstrating real-time analytics
- Validating aggregation and windowing
- Load testing with continuous data
- Development and debugging

---

## 🚀 Quick Start Workflow

### 1. Start Infrastructure

**For ksqlDB:**
```bash
docker-compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml up -d
```

**For Flink:**
```bash
docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml up -d
```

### 2. Create Orders Stream/Table

See `/docs/QUICK_START.md` for detailed instructions.

### 3. Generate Sample Data

```bash
cd scripts
./generate_orders.sh
```

### 4. Create Stream Definitions

Use the API or Web UI to create stream definitions that process the orders.

### 5. Clean Up (When Done)

**For ksqlDB:**
```bash
./cleanup-ksqldb.sh
```

**For Flink:**
```bash
./cleanup-flink.sh --all
```

---

## 🔧 Troubleshooting

### `generate_orders.sh` - "Could not detect ksqlDB or Flink"

**Cause:** Neither ksqlDB nor Flink SQL Gateway is running.

**Solution:**
1. Check Docker containers: `docker ps`
2. Ensure infrastructure is started:
   ```bash
   # For ksqlDB
   docker-compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml up -d
   
   # For Flink
   docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml up -d
   ```
3. Wait 30-60 seconds for services to start
4. Run the script again

---

### `cleanup-flink.sh` - "Flink JobManager is not running"

**Cause:** Flink is not started or not accessible on `localhost:8081`.

**Solution:**
1. Check Flink containers: `docker ps | grep flink`
2. Start Flink: `docker-compose -f docker-compose.base.yml -f docker-compose.flink.yml up -d`
3. Wait 30-60 seconds for initialization
4. Verify: `curl http://localhost:8081/overview`

---

### `cleanup-ksqldb.sh` - No output or errors

**Cause:** ksqlDB is not running or not accessible.

**Solution:**
1. Check ksqlDB container: `docker ps | grep ksqldb`
2. Start ksqlDB: `docker-compose -f docker-compose.base.yml -f docker-compose.ksqldb.yml up -d`
3. Wait 15-30 seconds for initialization
4. Verify: `curl http://localhost:8088/info`

---

### `generate_orders.sh` - "Failed to insert"

**Cause:** The `orders` stream/table does not exist.

**Solution:**
1. Create the orders stream/table (see `/docs/QUICK_START.md`)
2. For ksqlDB: Use `docker exec -it ksqldb-cli ksql ...`
3. For Flink: Use the API or SQL Gateway to create the table
4. Run the script again

---

## 📚 Additional Documentation

- **Quick Start Guide:** `/docs/QUICK_START.md`
- **Engine Abstraction:** `/docs/ENGINE_ABSTRACTION_PLAN.md`
- **Best Practices:** `/docs/BEST_PRACTICES.md`
- **Capacity Planning:** `/docs/CAPACITY_PLANNING.md`
- **Future Enhancements:** `/docs/FUTURE_ENHANCEMENTS.md`

---

## 💡 Tips

- Always run cleanup scripts before switching engines
- Use `--jobs` or `--sessions` for targeted cleanup
- Generate orders continuously for realistic testing
- Check Docker logs if scripts fail: `docker logs <container_name>`
- Scripts are idempotent - safe to run multiple times

---

## 🤝 Contributing

When adding new scripts:
1. Make them executable: `chmod +x script-name.sh`
2. Add usage instructions at the top
3. Include error handling and validation
4. Support both ksqlDB and Flink (when applicable)
5. Update this README with documentation
