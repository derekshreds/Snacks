@echo off
echo Building test runner...
dotnet run --project test-encoder -- %*
pause
