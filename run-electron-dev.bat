@echo off
REM Snacks Desktop - Dev Mode
REM Publishes backend and launches Electron in development mode

echo Snacks Desktop - Dev Mode
echo ==============================
echo.

REM Step 1: Publish ASP.NET Core backend
echo [1/3] Publishing ASP.NET Core backend...
if exist "electron-app\backend" rmdir /s /q "electron-app\backend"
dotnet publish Snacks/Snacks.csproj -c Release -r win-x64 --self-contained
if %errorlevel% neq 0 (
    echo ERROR: dotnet publish failed.
    pause
    exit /b 1
)
xcopy "Snacks\bin\Release\net10.0\win-x64\publish" "electron-app\backend\" /E /I /Q /Y >nul
if %errorlevel% neq 0 (
    echo ERROR: dotnet publish failed.
    pause
    exit /b 1
)
echo Backend published.
echo.

REM Step 2: Check for FFmpeg binaries
echo [2/3] Checking for FFmpeg...
if not exist "electron-app\ffmpeg\ffmpeg.exe" (
    echo FFmpeg not found in electron-app/ffmpeg/
    echo Download ffmpeg-release-full from https://www.gyan.dev/ffmpeg/builds/
    echo Place ffmpeg.exe and ffprobe.exe in electron-app/ffmpeg/
    pause
    exit /b 1
)
echo FFmpeg found.
echo.

REM Step 3: Launch Electron in dev mode
echo [3/3] Launching Electron...
pushd electron-app
call npx electron .
popd

pause
