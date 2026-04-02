@echo off
REM Snacks - Build Installer
REM Publishes the backend, bundles with Electron, and creates a signed Windows installer

echo.
echo  ====================================
echo   Snacks - Build Windows Installer
echo  ====================================
echo.

REM Step 1: Clean previous build
echo [1/6] Cleaning previous build...
if exist "electron-app\backend" rmdir /s /q "electron-app\backend"
if exist "electron-app\dist" rmdir /s /q "electron-app\dist"
echo Done.
echo.

REM Step 2: Set up code signing
echo [2/6] Checking code signing certificate...
if exist "signing\snacks-signing.pfx" (
    set CSC_LINK=%~dp0signing\snacks-signing.pfx
    if exist "signing\password.txt" (
        set /p CSC_KEY_PASSWORD=<signing\password.txt
    ) else (
        echo ERROR: signing\password.txt not found.
        echo        Create it with your certificate password.
        pause
        exit /b 1
    )
    echo Certificate found. Installer will be signed.
) else (
    echo WARNING: No certificate found in signing\snacks-signing.pfx
    echo          Installer will NOT be signed.
    echo          Run create-cert.bat to generate a self-signed certificate.
)
echo.

REM Step 3: Publish ASP.NET Core backend (self-contained, no .NET install needed)
echo [3/6] Publishing backend (self-contained)...
dotnet publish Snacks/Snacks.csproj -c Release -r win-x64 --self-contained
if %errorlevel% neq 0 (
    echo.
    echo ERROR: dotnet publish failed.
    pause
    exit /b 1
)
xcopy "Snacks\bin\Release\net10.0\win-x64\publish" "electron-app\backend\" /E /I /Q /Y >nul
if %errorlevel% neq 0 (
    echo.
    echo ERROR: Failed to copy publish output.
    pause
    exit /b 1
)
echo Backend published.
echo.

REM Step 4: Check for FFmpeg binaries
echo [4/6] Checking for FFmpeg...
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

REM Step 5: Install npm dependencies
echo [5/6] Installing npm dependencies...
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

REM Step 6: Build installer
echo [6/6] Building Windows installer...
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
echo    - Snacks desktop app (signed)
echo    - .NET runtime (self-contained)
echo    - FFmpeg binaries
echo    - Desktop shortcut with Snacks icon
echo    - Start menu shortcut
echo.

pause
