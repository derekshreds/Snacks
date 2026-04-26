@echo off
REM Snacks Deployment Script for Windows

echo ?? Snacks - Video Transcoding Service
echo =======================================

REM Create data directories
echo ?? Creating data directories...
if not exist "data" mkdir data
if not exist "data\output" mkdir data\output
if not exist "data\logs" mkdir data\logs

REM Create video library directory if it doesn't exist
if not exist "video-library" (
    mkdir video-library
    echo ?? Created video-library directory
    echo ?? Please copy your video files to the 'video-library' folder
    echo ?? Note: The container has READ-WRITE access for in-place transcoding
)

REM Build and start the container
echo ?? Building and starting Snacks container...
docker-compose down >nul 2>&1
docker-compose up -d --build

REM Wait for container to be ready
echo ? Waiting for container to start...
timeout /t 15 /nobreak >nul

REM Check container status
docker-compose ps | findstr "Up" >nul
if %errorlevel% == 0 (
    echo ? Snacks is running successfully!
    echo.
    echo ?? Web Interface: http://localhost:6767
    echo ?? Video Library: .\video-library (READ-WRITE)
    echo ?? Optional Output: .\data\output (for separate output)
    echo ?? Logs Directory: .\data\logs
    echo.
    echo ?? Health Check: http://localhost:6767/api/health
    echo.
    echo ?? Usage Instructions:
    echo   1. Place your video files in the 'video-library' folder
    echo   2. Open the web interface at http://localhost:6767
    echo   3. Use 'Browse Library' to select files for transcoding
    echo   4. Files are processed IN-PLACE unless you specify an output directory
    echo   5. Original files are backed up with '-OG' suffix during processing
    echo.
    echo To view logs: docker-compose logs -f snacks
    echo To stop: docker-compose down
    echo.
    echo Opening browser...
    start http://localhost:6767
) else (
    echo ? Failed to start Snacks. Check logs:
    docker-compose logs snacks
)

pause