@echo off
REM Snacks Desktop - Build
REM Builds the Electron desktop app with bundled backend

echo Snacks Desktop - Build
echo ==========================
echo.

REM Step 1: Publish ASP.NET Core backend
echo [1/4] Publishing ASP.NET Core backend...
if exist "electron-app\backend" rmdir /s /q "electron-app\backend"
dotnet publish Snacks/Snacks.csproj -c Release -r win-x64 --self-contained
if %errorlevel% neq 0 (
    echo ERROR: dotnet publish failed.
    pause
    exit /b 1
)
xcopy "Snacks\bin\Release\net10.0\win-x64\publish" "electron-app\backend\" /E /I /Q /Y >nul
if %errorlevel% neq 0 (
    echo ERROR: Failed to copy publish output.
    pause
    exit /b 1
)
echo Backend published.
echo.

REM Step 2: Check for FFmpeg binaries
echo [2/4] Checking for FFmpeg...
if not exist "electron-app\ffmpeg\ffmpeg.exe" (
    echo FFmpeg not found in electron-app/ffmpeg/
    echo Download ffmpeg-8.1.x-full_build.zip ^(8.1.2 or newer^) from https://github.com/GyanD/codexffmpeg/releases
    echo Place ffmpeg.exe and ffprobe.exe in electron-app/ffmpeg/
    pause
    exit /b 1
)
node scripts\validate-ffmpeg-version.mjs electron-app\ffmpeg\ffmpeg.exe
if %errorlevel% neq 0 (
    echo ERROR: FFmpeg is unpatched for PixelSmash ^(CVE-2026-8461^) or lacks zscale / libvorbis.
    pause
    exit /b 1
)
echo.

REM Step 3: Install npm dependencies
echo [3/4] Installing npm dependencies...
pushd electron-app
call npm install
if %errorlevel% neq 0 (
    popd
    echo ERROR: npm install failed.
    pause
    exit /b 1
)
popd
echo Dependencies installed.
echo.

REM Step 4: Build Electron app
echo [4/4] Building Electron app...
pushd electron-app
call npx electron-builder --win --x64
if %errorlevel% neq 0 (
    popd
    echo ERROR: Electron build failed.
    pause
    exit /b 1
)
popd
echo Electron build complete.
echo.

echo Done! Installer is in electron-app/dist/
echo.

pause
