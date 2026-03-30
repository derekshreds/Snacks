@echo off
REM Snacks - Build and Push to Docker Hub
REM Builds the Docker image and pushes to derekshreds/snacks-docker

echo Snacks - Build and Push
echo ==========================
echo.

REM Build the image
echo [1/2] Building Docker image...
docker buildx build --tag derekshreds/snacks-docker:latest --load --provenance=false --sbom=false -f Snacks/Dockerfile .
if %errorlevel% neq 0 (
    echo ERROR: Docker build failed.
    pause
    exit /b 1
)
echo Build complete.
echo.

REM Push to Docker Hub (both image names)
echo [2/3] Pushing derekshreds/snacks-docker:latest...
docker push derekshreds/snacks-docker:latest
if %errorlevel% neq 0 (
    echo ERROR: Docker push failed. Make sure you are logged in:
    echo   docker login
    pause
    exit /b 1
)
echo Push complete.
echo.

echo [3/3] Pushing derekshreds/snacksweb:latest...
docker tag derekshreds/snacks-docker:latest derekshreds/snacksweb:latest
docker push derekshreds/snacksweb:latest
if %errorlevel% neq 0 (
    echo ERROR: Push of snacksweb tag failed.
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
