@echo off
echo Scanning for image-based subtitles...
dotnet run --project test-subs-finder -- %*
pause
