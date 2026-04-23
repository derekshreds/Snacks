@echo off
REM ============================================================================
REM Downloads the recommended tessdata_best language pack set into this folder
REM so it ships with the build (csproj CopyToOutputDirectory picks up *.traineddata).
REM
REM Source: https://github.com/tesseract-ocr/tessdata_best (Apache 2.0)
REM Size:   ~106 MB total for the 10-language set.
REM ============================================================================
setlocal EnableDelayedExpansion

cd /d "%~dp0"

set BASE_URL=https://github.com/tesseract-ocr/tessdata_best/raw/main
set LANGS=eng spa fra deu ita por rus jpn chi_sim osd

set /a TOTAL=0
set /a OK=0
set /a SKIP=0
set /a FAIL=0

for %%L in (%LANGS%) do (
    set /a TOTAL+=1
    if exist "%%L.traineddata" (
        echo [skip] %%L.traineddata already present
        set /a SKIP+=1
    ) else (
        echo [get ] %%L.traineddata
        curl -fL --retry 3 --retry-delay 2 -o "%%L.traineddata.tmp" "%BASE_URL%/%%L.traineddata"
        if !errorlevel! neq 0 (
            echo        FAILED
            if exist "%%L.traineddata.tmp" del /q "%%L.traineddata.tmp"
            set /a FAIL+=1
        ) else (
            ren "%%L.traineddata.tmp" "%%L.traineddata"
            set /a OK+=1
        )
    )
)

echo.
echo Done. !OK! downloaded, !SKIP! already present, !FAIL! failed (of !TOTAL! total).
if !FAIL! neq 0 exit /b 1
exit /b 0
