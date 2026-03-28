#!/bin/bash

# Snacks Deployment Script

echo "?? Snacks - Video Transcoding Service"
echo "======================================="

# Create data directories
echo "?? Creating data directories..."
mkdir -p data/{output,logs}

# Create video library directory if it doesn't exist
if [ ! -d "video-library" ]; then
    mkdir -p video-library
    echo "?? Created video-library directory"
    echo "?? Please copy your video files to the 'video-library' folder"
    echo "?? Note: The container has READ-WRITE access for in-place transcoding"
fi

# Set permissions for Docker volumes
echo "?? Setting directory permissions..."
chmod 755 video-library/
chmod 755 data/
chmod 755 data/output data/logs

# Check for hardware acceleration support
echo "?? Checking for hardware acceleration support..."

if command -v vainfo >/dev/null 2>&1; then
    echo "? VAAPI support detected"
    vainfo
else
    echo "? VAAPI not found - Intel/AMD hardware acceleration may not work"
fi

if command -v nvidia-smi >/dev/null 2>&1; then
    echo "? NVIDIA GPU detected"
    nvidia-smi --query-gpu=name --format=csv,noheader
else
    echo "? NVIDIA GPU not found - NVIDIA NVENC will not be available"
fi

# Build and start the container
echo "?? Building and starting Snacks container..."
docker-compose down 2>/dev/null
docker-compose up -d --build

# Wait for container to be ready
echo "? Waiting for container to start..."
sleep 15

# Check container status
if docker-compose ps | grep -q "Up"; then
    echo "? Snacks is running successfully!"
    echo ""
    echo "?? Web Interface: http://localhost:8080"
    echo "?? Video Library: ./video-library (READ-WRITE)"
    echo "?? Optional Output: ./data/output (for separate output)"
    echo "?? Logs Directory: ./data/logs"
    echo ""
    echo "?? Health Check: http://localhost:8080/Home/Health"
    echo ""
    echo "?? Usage Instructions:"
    echo "  1. Place your video files in the 'video-library' folder"
    echo "  2. Open the web interface at http://localhost:8080"
    echo "  3. Use 'Browse Library' to select files for transcoding"
    echo "  4. Files are processed IN-PLACE unless you specify an output directory"
    echo "  5. Original files are backed up with '-OG' suffix during processing"
    echo ""
    echo "To view logs: docker-compose logs -f snacksweb"
    echo "To stop: docker-compose down"
else
    echo "? Failed to start Snacks. Check logs:"
    docker-compose logs snacksweb
fi