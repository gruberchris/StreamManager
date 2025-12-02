# Stream Manager

A self-service ksqlDB platform that decouples stream processing management from execution, allowing users to define, manage, and visualize ksqlDB queries via a web interface.

## Architecture

- **Frontend:** ASP.NET Core MVC (Razor Views) - UI dashboard
- **Backend:** ASP.NET Core Web API - Control plane and data plane
- **Database:** PostgreSQL - Stores metadata about queries and status
- **Engine:** ksqlDB (Interactive Mode) - Stream processing engine
- **Communication:** REST APIs + SignalR for real-time streaming

## Projects

### StreamManager.Api
The backend Web API that provides:
- **Ad-hoc Query Service:** Streams KSQL query results via SignalR
- **Stream Management:** CRUD operations for persistent stream definitions
- **Topic Proxy Service:** Background service that consumes Kafka topics and broadcasts to SignalR groups
- **SignalR Hub:** Real-time communication for query streaming and topic previews

### StreamManager.Web
The frontend MVC web application that provides:
- **Query Tool:** Execute ad-hoc KSQL queries with real-time streaming results
- **Stream Management:** Create, deploy, stop, and delete persistent streams
- **Live Preview:** View real-time data from deployed streams

## Features

### Ad-Hoc Queries (/Query)
- CodeMirror-based SQL editor with syntax highlighting
- Real-time streaming of query results via SignalR
- Dynamic table generation based on query schema
- Start/stop query execution with cancellation support

### Stream Management (/Streams)
- Create persistent stream definitions
- Deploy streams to ksqlDB (CREATE STREAM AS SELECT)
- Stop running streams (TERMINATE query)
- Delete stream definitions
- View stream status and metadata

### Live Preview (/Stream/{id})
- Real-time preview of data from deployed streams
- SignalR-based consumption of Kafka topics
- Connection status monitoring
- Message history with scrollable view

## Database Schema

**StreamDefinitions Table:**
- `Id` (Guid) - Primary key
- `Name` (string) - Friendly stream name
- `KsqlScript` (text) - The SELECT statement
- `KsqlQueryId` (string) - ksqlDB query identifier
- `OutputTopic` (string) - Kafka output topic name
- `IsActive` (bool) - Stream deployment status
- `CreatedAt` (DateTime) - Creation timestamp

## Getting Started

1. **Start Infrastructure:**
   ```bash
   docker-compose up -d
   ```

2. **Run Database Migrations:**
   ```bash
   cd StreamManager.Api
   dotnet ef database update
   ```

3. **Start API Server:**
   ```bash
   cd StreamManager.Api
   dotnet run
   ```
   API will be available at: https://localhost:7068

4. **Start Web Application:**
   ```bash
   cd StreamManager.Web  
   dotnet run
   ```
   Web UI will be available at: https://localhost:7122

## Infrastructure Requirements

The application expects the following services to be running:
- **ksqlDB Server:** http://localhost:8088 (Interactive Mode)
- **Kafka:** localhost:9092
- **PostgreSQL:** localhost:5432
  - Database: `stream_manager_db`
  - User: `admin`
  - Password: `password`

## API Endpoints

### Stream Management
- `GET /api/stream` - List all streams
- `POST /api/stream` - Create new stream
- `GET /api/stream/{id}` - Get stream details
- `POST /api/stream/{id}/deploy` - Deploy stream to ksqlDB
- `POST /api/stream/{id}/stop` - Stop running stream
- `DELETE /api/stream/{id}` - Delete stream

### SignalR Hub (/hub/ksql)
- `ExecuteAdHoc(string ksql)` - Stream ad-hoc query results
- `JoinStreamGroup(string topicName)` - Subscribe to topic updates
- `LeaveStreamGroup(string topicName)` - Unsubscribe from topic

## Technology Stack

- **.NET 10** - Application framework
- **ASP.NET Core MVC** - Web framework
- **SignalR** - Real-time communication
- **Entity Framework Core** - ORM with PostgreSQL
- **Confluent.Kafka** - Kafka client library
- **Bootstrap 5** - UI framework
- **CodeMirror** - Code editor component
- **ksqlDB** - Stream processing engine

## Next Steps

The current implementation provides a solid foundation. Consider adding:
- Authentication and authorization
- Stream query validation and testing
- Performance metrics and monitoring
- Advanced query editing features
- Stream topology visualization
- Error handling and retry policies