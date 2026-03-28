@echo off
REM Snacks - Build and Push to Docker Hub
REM Builds the Docker image and pushes to derekshreds/snacksweb

echo Snacks - Build and Push
echo ==========================
echo.

REM Build the image
echo [1/2] Building Docker image...
docker buildx build --tag derekshreds/snacksweb:latest --load --provenance=false --sbom=false -f Snacks/Dockerfile .
if %errorlevel% neq 0 (
    echo ERROR: Docker build failed.
    pause
    exit /b 1
)
echo Build complete.
echo.

REM Push to Docker Hub
echo [2/2] Pushing to Docker Hub...
docker push derekshreds/snacksweb:latest
if %errorlevel% neq 0 (
    echo ERROR: Docker push failed. Make sure you are logged in:
    echo   docker login
    pause
    exit /b 1
)
echo Push complete.
echo.

echo Done! Now on your NAS:
echo.
echo   1. In Container Station:
echo      - Applications ^> Create
echo      - Paste contents of deploy-compose.yml
echo      - Update the video library path to your NAS media folder
echo      - Click Create
echo.
echo   2. Access Snacks at http://YOUR-NAS-IP:6767
echo.

pause
