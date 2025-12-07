#!/bin/bash

# Cleanup Flink jobs, sessions, and Kafka topics
# Usage: ./cleanup-flink.sh [--all] [--jobs] [--sessions] [--topics]

echo "🧹 Cleaning up Flink jobs, sessions, and topics..."
echo ""

# Parse arguments
CLEANUP_JOBS=false
CLEANUP_SESSIONS=false
CLEANUP_TOPICS=false

if [ "$1" == "--all" ] || [ $# -eq 0 ]; then
    CLEANUP_JOBS=true
    CLEANUP_SESSIONS=true
    CLEANUP_TOPICS=true
elif [ "$1" == "--jobs" ]; then
    CLEANUP_JOBS=true
elif [ "$1" == "--sessions" ]; then
    CLEANUP_SESSIONS=true
elif [ "$1" == "--topics" ]; then
    CLEANUP_TOPICS=true
else
    echo "Usage: $0 [--all|--jobs|--sessions|--topics]"
    echo "  --all       : Clean up everything (default)"
    echo "  --jobs      : Cancel all running Flink jobs"
    echo "  --sessions  : Close all SQL Gateway sessions"
    echo "  --topics    : Delete all Kafka topics (except internal)"
    exit 1
fi

FLINK_REST_API="http://localhost:8081"
FLINK_SQL_GATEWAY="http://localhost:8083"

# Check if Flink is running
if ! curl -s "$FLINK_REST_API/overview" > /dev/null 2>&1; then
    echo "❌ Error: Flink JobManager is not running on $FLINK_REST_API"
    echo "   Make sure Flink is started with docker-compose"
    exit 1
fi

if ! curl -s "$FLINK_SQL_GATEWAY/v1/info" > /dev/null 2>&1; then
    echo "⚠️  Warning: Flink SQL Gateway is not running on $FLINK_SQL_GATEWAY"
    echo "   Session cleanup will be skipped"
    CLEANUP_SESSIONS=false
fi

echo "✅ Flink services detected"
echo ""

# Show current state
echo "📊 Current State:"
echo "----------------"

# Show running jobs
JOBS_RESPONSE=$(curl -s "$FLINK_REST_API/jobs/overview")
RUNNING_JOBS=$(echo "$JOBS_RESPONSE" | grep -o '"state":"RUNNING"' | wc -l | tr -d ' ')
echo "Running Jobs: $RUNNING_JOBS"

if [ "$RUNNING_JOBS" -gt 0 ]; then
    echo ""
    echo "🔍 Job Details:"
    JOB_IDS=$(curl -s "$FLINK_REST_API/jobs" | grep -o '"id":"[^"]*"' | cut -d'"' -f4)
    for JOB_ID in $JOB_IDS; do
        JOB_INFO=$(curl -s "$FLINK_REST_API/jobs/$JOB_ID")
        JOB_NAME=$(echo "$JOB_INFO" | grep -o '"name":"[^"]*"' | cut -d'"' -f4 | head -1)
        JOB_STATE=$(echo "$JOB_INFO" | grep -o '"state":"[^"]*"' | cut -d'"' -f4 | head -1)
        echo "  - $JOB_ID ($JOB_STATE): $JOB_NAME"
    done
fi

echo ""

# Show topics
TOPICS=$(docker exec kafka kafka-topics --bootstrap-server localhost:9092 --list 2>/dev/null | grep -v "^_" | sort)
TOPIC_COUNT=$(echo "$TOPICS" | grep -c '^' 2>/dev/null || echo "0")
echo "Kafka Topics: $TOPIC_COUNT (excluding internal)"

if [ "$TOPIC_COUNT" -gt 0 ]; then
    echo "$TOPICS" | while read -r topic; do
        [ -n "$topic" ] && echo "  - $topic"
    done
fi

echo ""
echo "----------------"
echo ""

# Cleanup Jobs
if [ "$CLEANUP_JOBS" = true ] && [ "$RUNNING_JOBS" -gt 0 ]; then
    echo "🗑️  Cancelling all running Flink jobs..."
    
    JOB_IDS=$(curl -s "$FLINK_REST_API/jobs" | grep -o '"id":"[^"]*"' | cut -d'"' -f4)
    
    for JOB_ID in $JOB_IDS; do
        JOB_STATE=$(curl -s "$FLINK_REST_API/jobs/$JOB_ID" | grep -o '"state":"[^"]*"' | cut -d'"' -f4 | head -1)
        
        if [ "$JOB_STATE" = "RUNNING" ] || [ "$JOB_STATE" = "CREATED" ]; then
            echo "  Cancelling job: $JOB_ID"
            curl -s -X PATCH "$FLINK_REST_API/jobs/$JOB_ID?mode=cancel" > /dev/null
        fi
    done
    
    echo "  ⏳ Waiting for jobs to cancel..."
    sleep 3
    
    # Verify cancellation
    REMAINING_JOBS=$(curl -s "$FLINK_REST_API/jobs/overview" | grep -o '"state":"RUNNING"' | wc -l | tr -d ' ')
    
    if [ "$REMAINING_JOBS" -eq 0 ]; then
        echo "  ✅ All jobs cancelled successfully"
    else
        echo "  ⚠️  Warning: $REMAINING_JOBS job(s) still running"
    fi
    
    echo ""
fi

# Cleanup Sessions
if [ "$CLEANUP_SESSIONS" = true ]; then
    echo "🗑️  Closing all Flink SQL Gateway sessions..."
    
    # Get all sessions
    SESSIONS_RESPONSE=$(curl -s "$FLINK_SQL_GATEWAY/v1/sessions")
    SESSION_HANDLES=$(echo "$SESSIONS_RESPONSE" | grep -o '"sessionHandle":"[^"]*"' | cut -d'"' -f4)
    
    if [ -z "$SESSION_HANDLES" ]; then
        echo "  ℹ️  No active sessions found"
    else
        for SESSION_HANDLE in $SESSION_HANDLES; do
            echo "  Closing session: $SESSION_HANDLE"
            curl -s -X DELETE "$FLINK_SQL_GATEWAY/v1/sessions/$SESSION_HANDLE" > /dev/null
        done
        
        echo "  ✅ All sessions closed"
    fi
    
    echo ""
fi

# Cleanup Topics
if [ "$CLEANUP_TOPICS" = true ] && [ "$TOPIC_COUNT" -gt 0 ]; then
    echo "🗑️  Deleting Kafka topics..."
    
    echo "$TOPICS" | while read -r topic; do
        if [ -n "$topic" ]; then
            echo "  Deleting topic: $topic"
            docker exec kafka kafka-topics --bootstrap-server localhost:9092 --delete --topic "$topic" 2>/dev/null
        fi
    done
    
    echo "  ⏳ Waiting for topic deletion..."
    sleep 2
    
    # Verify deletion
    REMAINING_TOPICS=$(docker exec kafka kafka-topics --bootstrap-server localhost:9092 --list 2>/dev/null | grep -v "^_" | wc -l)
    
    if [ "$REMAINING_TOPICS" -eq 0 ]; then
        echo "  ✅ All topics deleted successfully"
    else
        echo "  ⚠️  Warning: $REMAINING_TOPICS topic(s) still exist"
    fi
    
    echo ""
fi

# Final state
echo "✅ Cleanup complete!"
echo ""
echo "📊 Final State:"
echo "----------------"

FINAL_JOBS=$(curl -s "$FLINK_REST_API/jobs/overview" | grep -o '"state":"RUNNING"' | wc -l | tr -d ' ')
echo "Running Jobs: $FINAL_JOBS"

FINAL_TOPICS=$(docker exec kafka kafka-topics --bootstrap-server localhost:9092 --list 2>/dev/null | grep -v "^_" | wc -l)
echo "Kafka Topics: $FINAL_TOPICS (excluding internal)"

echo "----------------"
echo ""
echo "💡 Tip: You can also use:"
echo "   $0 --jobs      # Cancel only Flink jobs"
echo "   $0 --sessions  # Close only SQL Gateway sessions"
echo "   $0 --topics    # Delete only Kafka topics"
echo "   $0 --all       # Clean up everything (default)"
