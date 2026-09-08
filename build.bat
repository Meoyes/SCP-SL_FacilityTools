@echo off
chcp 437 >nul
setlocal enabledelayedexpansion

echo ============================================
echo  FacilityTools build (Windows)
echo  EXILED 8.9.11 / net48
echo  Commands: probe, restorefacility
echo ============================================

cd /d "%~dp0"

if exist "Directory.Build.props" del /f /q "Directory.Build.props" >nul 2>nul
if exist "NuGet.Config" del /f /q "NuGet.Config" >nul 2>nul
if exist "nuget.config" del /f /q "nuget.config" >nul 2>nul
echo [OK] Cleaned conflicting props/config.

if exist "obj" rmdir /s /q "obj" >nul 2>nul
if exist "bin" rmdir /s /q "bin" >nul 2>nul
echo [OK] Cleaned obj/bin.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet SDK not found.
    pause & exit /b 1
)

dotnet --version

echo [INFO] Restoring (generates project.assets.json)...
dotnet restore "FacilityTools.csproj"
if errorlevel 1 (
    echo [ERROR] dotnet restore failed.
    pause & exit /b 1
)

echo [INFO] Building...
dotnet build "FacilityTools.csproj" -c Release -f net48
if errorlevel 1 (
    echo [ERROR] dotnet build failed.
    pause & exit /b 1
)

if exist "bin\Release\net48\FacilityTools.dll" (
    echo.
    echo ============ SUCCESS ============
    echo  bin\Release\net48\FacilityTools.dll
    echo  Deploy to: %%AppData%%\EXILED\Plugins\
    echo  Commands : probe / probe open / probe lift
    echo             restorefacility
    echo ============================
) else (
    echo [ERROR] DLL not produced.
)
pause
