@echo off
REM Snacks - Build and Push DEV tag only
REM Builds the Docker image and pushes ONLY to derekshreds/snacks-docker:dev
REM Does NOT touch :latest or snacksweb tags

echo Snacks - Build and Push (DEV)
echo ================================
echo.

REM Build the image with dev tag
echo [1/2] Building Docker image (dev)...
docker buildx build --tag derekshreds/snacks-docker:dev --load --provenance=false --sbom=false -f Snacks/Dockerfile .
if %errorlevel% neq 0 (
    echo ERROR: Docker build failed.
    pause
    exit /b 1
)
echo Build complete.
echo.

REM Push dev tag only
echo [2/2] Pushing derekshreds/snacks-docker:dev...
docker push derekshreds/snacks-docker:dev
if %errorlevel% neq 0 (
    echo ERROR: Docker push failed. Make sure you are logged in:
    echo   docker login
    pause
    exit /b 1
)
echo Push complete.
echo.

echo Done! To test on your NAS, update your compose to use:
echo   image: derekshreds/snacks-docker:dev
echo.

pause
