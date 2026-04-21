@echo off
REM ============================================================================
REM Downloads ~50k-word subtitle-derived frequency lists from hermitdave's
REM FrequencyWords repo (MIT licensed, derived from OpenSubtitles corpora) for
REM every language Snacks' OCR pipeline can target. Each file is stripped of
REM the frequency column and saved as {tessLang}.txt so SubtitleSpellChecker
REM loads it automatically.
REM
REM Source: https://github.com/hermitdave/FrequencyWords
REM Size:   ~7-10 MB total for all 27 languages.
REM ============================================================================
setlocal

cd /d "%~dp0"

set BASE=https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018

REM  Tesseract 3-letter code  <->  FrequencyWords 2-letter code
call :get eng     en
call :get spa     es
call :get fra     fr
call :get deu     de
call :get ita     it
call :get por     pt
call :get rus     ru
call :get jpn     ja
call :get kor     ko
call :get chi_sim zh_cn
call :get ara     ar
call :get hin     hi
call :get nld     nl
call :get swe     sv
call :get nor     no
call :get dan     da
call :get fin     fi
call :get pol     pl
call :get tur     tr
call :get ces     cs
call :get hun     hu
call :get ell     el
call :get heb     he
call :get tha     th
call :get vie     vi
call :get ind     id
call :get ukr     uk

echo.
echo Done. Delete any {lang}.txt file and re-run to refresh it.
exit /b 0


:get
REM  %1 = Tesseract lang code (file name we write)
REM  %2 = FrequencyWords lang code (URL segment)
set TESS=%1
set HD=%2
set OUT=%TESS%.txt

if exist "%OUT%" (
    echo [skip] %OUT% already present
    exit /b 0
)

echo [get ] %OUT%
curl -fsSL --retry 3 --retry-delay 2 "%BASE%/%HD%/%HD%_50k.txt" -o "%OUT%.raw"
if errorlevel 1 (
    echo        FAILED
    if exist "%OUT%.raw" del /q "%OUT%.raw"
    exit /b 0
)

REM  Strip the "<word> <frequency>" format down to just the word. Keep
REM  single-char entries too (English "I" and "a" matter for correction).
powershell -NoProfile -Command ^
    "Get-Content '%OUT%.raw' -Encoding UTF8 |" ^
    "ForEach-Object { ($_ -split ' ')[0] } |" ^
    "Where-Object { $_.Length -ge 1 } |" ^
    "Set-Content '%OUT%' -Encoding UTF8"

del /q "%OUT%.raw"
exit /b 0
