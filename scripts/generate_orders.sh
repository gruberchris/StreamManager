#!/bin/bash

# Detect which engine is running
ENGINE="unknown"
if curl -s http://localhost:8088/info > /dev/null 2>&1; then
    ENGINE="ksqldb"
    echo "✓ Detected ksqlDB running on port 8088"
elif curl -s http://localhost:8083/v1/info > /dev/null 2>&1; then
    ENGINE="flink"
    echo "✓ Detected Flink SQL Gateway running on port 8083"
    # Create Flink session
    SESSION_RESPONSE=$(curl -s -X POST http://localhost:8083/v1/sessions \
      -H "Content-Type: application/json" \
      -d '{"properties": {}}')
    SESSION_HANDLE=$(echo $SESSION_RESPONSE | grep -o '"sessionHandle":"[^"]*"' | cut -d'"' -f4)
    echo "✓ Created Flink session: $SESSION_HANDLE"
else
    echo "✗ Error: Could not detect ksqlDB or Flink"
    echo "  Make sure either ksqlDB (port 8088) or Flink SQL Gateway (port 8083) is running"
    exit 1
fi

# Array of product names
PRODUCTS=("Laptop" "Wireless Mouse" "Mechanical Keyboard" "USB-C Hub" "Monitor 27inch" "Webcam HD" "Desk Lamp LED" "Office Chair" "Headphones Wireless" "External SSD 1TB" "Gaming Mouse" "Laptop Stand" "USB Cable" "Power Bank" "Bluetooth Speaker" "Phone Case" "Screen Protector" "Wireless Charger" "Memory Card" "HDMI Cable")

# Counter for order IDs
ORDER_ID=1011

echo ""
echo "Starting order generator - creating 1 order every 30 seconds"
echo "Engine: $ENGINE"
echo "Press Ctrl+C to stop"
echo ""

while true; do
    # Generate random values
    CUSTOMER_ID=$((500 + RANDOM % 100))
    PRODUCT_INDEX=$((RANDOM % ${#PRODUCTS[@]}))
    PRODUCT="${PRODUCTS[$PRODUCT_INDEX]}"
    AMOUNT=$(awk -v min=10 -v max=1500 'BEGIN{srand(); printf "%.2f\n", min+rand()*(max-min)}')
    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%S")
    
    # Create INSERT statement
    INSERT_SQL="INSERT INTO orders (ORDER_ID, CUSTOMER_ID, PRODUCT, AMOUNT, PURCHASE_DATE) VALUES ($ORDER_ID, $CUSTOMER_ID, '$PRODUCT', $AMOUNT, '$TIMESTAMP');"
    
    echo "[$TIMESTAMP] Inserting Order #$ORDER_ID: $PRODUCT - \$$AMOUNT for Customer $CUSTOMER_ID"
    
    # Execute based on detected engine
    if [ "$ENGINE" = "ksqldb" ]; then
        # Execute via ksqlDB REST API
        curl -s -X POST http://localhost:8088/ksql \
          -H "Content-Type: application/vnd.ksql.v1+json; charset=utf-8" \
          -d "{\"ksql\":\"$INSERT_SQL\",\"streamsProperties\":{}}" > /dev/null
        
        if [ $? -eq 0 ]; then
            echo "  ✓ Successfully inserted (ksqlDB)"
        else
            echo "  ✗ Failed to insert (ksqlDB)"
        fi
    elif [ "$ENGINE" = "flink" ]; then
        # Execute via Flink SQL Gateway
        RESPONSE=$(curl -s -X POST http://localhost:8083/v1/sessions/$SESSION_HANDLE/statements \
          -H "Content-Type: application/json" \
          -d "{\"statement\":\"$INSERT_SQL\"}")
        
        if echo "$RESPONSE" | grep -q "operationHandle"; then
            echo "  ✓ Successfully inserted (Flink)"
        else
            echo "  ✗ Failed to insert (Flink)"
            echo "  Response: $RESPONSE"
        fi
    fi
    
    echo ""
    
    # Increment order ID
    ORDER_ID=$((ORDER_ID + 1))
    
    # Wait 5 seconds
    sleep 5
done