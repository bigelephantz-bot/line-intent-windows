@echo off
cd /d "%~dp0"
if exist "dist\LineIntent-win-x64-0.7.0\LineIntent.exe" (
  start "" "dist\LineIntent-win-x64-0.7.0\LineIntent.exe"
) else (
  dotnet run --project "src\LineIntent\LineIntent.csproj" -c Release
  if errorlevel 1 pause
)
