@echo off
REM Snacks - Build Installer
REM Publishes the backend, bundles with Electron, and creates a Windows installer

echo.
echo  ====================================
echo   Snacks - Build Windows Installer
echo  ====================================
echo.

REM Step 1: Clean previous build
echo [1/5] Cleaning previous build...
if exist "electron-app\backend" rmdir /s /q "electron-app\backend"
if exist "electron-app\dist" rmdir /s /q "electron-app\dist"
echo Done.
echo.

REM Step 2: Publish ASP.NET Core backend (self-contained, no .NET install needed)
echo [2/5] Publishing backend (self-contained)...
dotnet publish Snacks/Snacks.csproj -c Release -r win-x64 --self-contained
if %errorlevel% neq 0 (
    echo.
    echo ERROR: dotnet publish failed.
    pause
    exit /b 1
)
xcopy "Snacks\bin\Release\net8.0\win-x64\publish" "electron-app\backend\" /E /I /Q /Y >nul
if %errorlevel% neq 0 (
    echo.
    echo ERROR: Failed to copy publish output.
    pause
    exit /b 1
)
echo Backend published.
echo.

REM Step 3: Check for FFmpeg binaries
echo [3/5] Checking for FFmpeg...
if not exist "electron-app\ffmpeg\ffmpeg.exe" (
    echo.
    echo  FFmpeg not found in electron-app\ffmpeg\
    echo.
    echo  Download ffmpeg-release-full from:
    echo    https://www.gyan.dev/ffmpeg/builds/
    echo.
    echo  Extract and place ffmpeg.exe and ffprobe.exe in:
    echo    electron-app\ffmpeg\
    echo.
    pause
    exit /b 1
)
if not exist "electron-app\ffmpeg\ffprobe.exe" (
    echo.
    echo  ffprobe.exe not found in electron-app\ffmpeg\
    echo  Make sure both ffmpeg.exe and ffprobe.exe are present.
    echo.
    pause
    exit /b 1
)
echo FFmpeg found.
echo.

REM Step 4: Install npm dependencies
echo [4/5] Installing npm dependencies...
pushd electron-app
call npm install
if %errorlevel% neq 0 (
    popd
    echo.
    echo ERROR: npm install failed.
    pause
    exit /b 1
)
popd
echo Dependencies installed.
echo.

REM Step 5: Build installer
echo [5/5] Building Windows installer...
pushd electron-app
call npx electron-builder --win --x64
if %errorlevel% neq 0 (
    popd
    echo.
    echo ERROR: Electron builder failed.
    pause
    exit /b 1
)
popd
echo.

echo  ====================================
echo   Build complete!
echo  ====================================
echo.
echo  Installer location:
echo    electron-app\dist\
echo.
echo  The installer includes:
echo    - Snacks desktop app
echo    - .NET runtime (self-contained)
echo    - FFmpeg binaries
echo    - Desktop shortcut with Snacks icon
echo    - Start menu shortcut
echo.

pause
