@echo off
REM Snacks - Build and Push to Docker Hub
REM Builds the Docker image and pushes to derekshreds/snacks-docker

set VERSION=2.8.2

echo Snacks - Build and Push (v%VERSION%)
echo ==========================
echo.

REM Build the image with both :latest and pinned version tags
echo [1/2] Building Docker image (tags: latest, %VERSION%)...
docker buildx build --tag derekshreds/snacks-docker:latest --tag derekshreds/snacks-docker:%VERSION% --load --provenance=false --sbom=false -f Snacks/Dockerfile .
if %errorlevel% neq 0 (
    echo ERROR: Docker build failed.
    pause
    exit /b 1
)
echo Build complete.
echo.

REM Push to Docker Hub (both image names, both tags)
echo [2/3] Pushing derekshreds/snacks-docker (latest, %VERSION%)...
docker push derekshreds/snacks-docker:latest
if %errorlevel% neq 0 (
    echo ERROR: Docker push failed. Make sure you are logged in:
    echo   docker login
    pause
    exit /b 1
)
docker push derekshreds/snacks-docker:%VERSION%
if %errorlevel% neq 0 (
    echo ERROR: Push of snacks-docker:%VERSION% failed.
    pause
    exit /b 1
)
echo Push complete.
echo.

echo [3/3] Pushing derekshreds/snacksweb (latest, %VERSION%)...
docker tag derekshreds/snacks-docker:latest derekshreds/snacksweb:latest
docker tag derekshreds/snacks-docker:%VERSION% derekshreds/snacksweb:%VERSION%
docker push derekshreds/snacksweb:latest
if %errorlevel% neq 0 (
    echo ERROR: Push of snacksweb:latest failed.
    pause
    exit /b 1
)
docker push derekshreds/snacksweb:%VERSION%
if %errorlevel% neq 0 (
    echo ERROR: Push of snacksweb:%VERSION% failed.
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
