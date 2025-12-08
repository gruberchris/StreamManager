#!/bin/bash

# ============================================================================
# Order Data Generator for Stream Manager
# ============================================================================
# This script generates sample order data and writes it directly to the
# Kafka topic 'orders' using kafka-console-producer.
#
# IMPORTANT: The Kafka topic 'orders' must exist before running this script.
# 
# Create the topic with:
#   docker exec -it broker kafka-topics --create --topic orders \
#     --bootstrap-server localhost:9092 --partitions 3 --replication-factor 1
# ============================================================================

# Check if broker container is running
if ! docker ps | grep -q broker; then
    echo "✗ Error: Kafka broker container is not running"
    echo "  Start it with: docker compose up -d"
    exit 1
fi

# Check if orders topic exists
if ! docker exec broker kafka-topics --list --bootstrap-server localhost:9092 2>/dev/null | grep -q "^orders$"; then
    echo "✗ Error: Kafka topic 'orders' does not exist"
    echo ""
    echo "Create it with:"
    echo "  docker exec broker kafka-topics --create --topic orders \\"
    echo "    --bootstrap-server localhost:9092 --partitions 3 --replication-factor 1"
    exit 1
fi

echo "✓ Kafka broker is running"
echo "✓ Topic 'orders' exists"

# Array of product names
PRODUCTS=("Laptop" "Wireless Mouse" "Mechanical Keyboard" "USB-C Hub" "Monitor 27inch" "Webcam HD" "Desk Lamp LED" "Office Chair" "Headphones Wireless" "External SSD 1TB" "Gaming Mouse" "Laptop Stand" "USB Cable" "Power Bank" "Bluetooth Speaker" "Phone Case" "Screen Protector" "Wireless Charger" "Memory Card" "HDMI Cable")

# Counter for order IDs
ORDER_ID=1011

echo ""
echo "Starting order generator - creating 1 order every 5 seconds"
echo "Writing directly to Kafka topic 'orders'"
echo "Press Ctrl+C to stop"
echo ""

while true; do
    # Generate random values
    CUSTOMER_ID=$((500 + RANDOM % 100))
    PRODUCT_INDEX=$((RANDOM % ${#PRODUCTS[@]}))
    PRODUCT="${PRODUCTS[$PRODUCT_INDEX]}"
    AMOUNT=$(awk -v min=10 -v max=1500 'BEGIN{srand(); printf "%.2f\n", min+rand()*(max-min)}')
    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%S")
    
    # Create JSON message
    JSON_MESSAGE=$(cat <<EOF
{"ORDER_ID":$ORDER_ID,"CUSTOMER_ID":$CUSTOMER_ID,"PRODUCT":"$PRODUCT","AMOUNT":$AMOUNT,"PURCHASE_DATE":"$TIMESTAMP"}
EOF
)
    
    echo "[$TIMESTAMP] Order #$ORDER_ID: $PRODUCT - \$$AMOUNT for Customer $CUSTOMER_ID"
    
    # Write to Kafka using kafka-console-producer
    echo "$JSON_MESSAGE" | docker exec -i broker kafka-console-producer \
      --bootstrap-server localhost:9092 \
      --topic orders 2>/dev/null
    
    if [ $? -eq 0 ]; then
        echo "  ✓ Written to Kafka"
    else
        echo "  ✗ Failed to write to Kafka"
    fi
    
    echo ""
    
    # Increment order ID
    ORDER_ID=$((ORDER_ID + 1))
    
    # Wait 5 seconds
    sleep 5
done