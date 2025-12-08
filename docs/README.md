# Stream Manager Documentation

**Last Updated:** December 7, 2025

This directory contains comprehensive documentation for the Stream Manager project.

## 📚 Documentation Index

### Getting Started
- **[QUICK_START.md](QUICK_START.md)** — Installation and first steps
  - Choosing between ksqlDB and Flink
  - Running with Docker Compose
  - Setting up the Orders example (data generation)

- **[ORDERS_EXAMPLE_WALKTHROUGH.md](ORDERS_EXAMPLE_WALKTHROUGH.md)** — **Primary Tutorial**
  - Step-by-step guide for running Ad-Hoc Queries
  - Creating Managed Streams
  - Detailed SQL examples for both ksqlDB and Flink
  - Troubleshooting common issues

- **[QUICK_REFERENCE.md](QUICK_REFERENCE.md)** — Command reference card
  - Docker commands
  - API endpoints
  - Common SQL snippets
  - Troubleshooting cheat sheet

### Architecture & Implementation
- **[ARCHITECTURE.md](ARCHITECTURE.md)** — System design
  - Engine abstraction layer (Strategy Pattern)
  - Flink implementation details (ephemeral sessions)
  - Database schema & API structure
  - SignalR real-time streaming design

### Operations & Best Practices
- **[OPERATIONS.md](OPERATIONS.md)** — Production guide
  - Capacity planning & scaling
  - Resource limits (application, engine, infrastructure)
  - Monitoring & health checks
  - Ad-hoc query cost analysis

### Migration & SQL Syntax
- **[FLINK_MIGRATION.md](FLINK_MIGRATION.md)** — ksqlDB to Flink guide
  - Key syntax differences (removing `EMIT CHANGES`)
  - Architecture shift (persistent vs. ephemeral tables)
  - Window functions & joins
  - Real-world migration examples

### Future Planning
- **[FUTURE_ENHANCEMENTS.md](FUTURE_ENHANCEMENTS.md)** — Roadmap
  - Schema inference ideas
  - Planned integrations

---

## 📖 Recommended Reading Order

### For New Users
1. **[QUICK_START.md](QUICK_START.md)** - Get the containers running.
2. **[ORDERS_EXAMPLE_WALKTHROUGH.md](ORDERS_EXAMPLE_WALKTHROUGH.md)** - Run your first queries.
3. **[FLINK_MIGRATION.md](FLINK_MIGRATION.md)** - Understand how to write Flink SQL correctly.

### For Developers
1. **[ARCHITECTURE.md](ARCHITECTURE.md)** - Understand the code structure.
2. **[OPERATIONS.md](OPERATIONS.md)** - Learn about resource management.

---

## 🔄 Recent Changes (December 7, 2025)

### Major Documentation Refactor
- **Consolidated** multiple fragmented guides into `ORDERS_EXAMPLE_WALKTHROUGH.md` and `FLINK_MIGRATION.md`.
- **Updated** Flink architecture docs to reflect the "Session Per Request" model.
- **Clarified** the distinction between Ad-Hoc (Bounded) and Managed (Unbounded) streams.
- **Removed** obsolete files to reduce confusion.

### Key Implementation Updates Reflected in Docs
- **Flink Ad-Hoc Queries:** Now use `scan.bounded.mode = 'latest-offset'` for safe testing.
- **Flink Managed Streams:** Now require explicit `CREATE TABLE` and `INSERT INTO` statements.
- **Topic Case Sensitivity:** Fixed issues with topic name handling in the Proxy Service.

---

## 📞 Getting Help

If you're stuck:
1. Check the **[ORDERS_EXAMPLE_WALKTHROUGH.md](ORDERS_EXAMPLE_WALKTHROUGH.md)** troubleshooting section.
2. Review **[QUICK_REFERENCE.md](QUICK_REFERENCE.md)** for common commands.
3. Search **[FLINK_MIGRATION.md](FLINK_MIGRATION.md)** for SQL syntax help.