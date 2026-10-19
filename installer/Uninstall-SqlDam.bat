@echo off
setlocal

echo =======================================================
echo   SQL DAM - Enterprise Database Activity Monitor
echo   Uninstall Helper
echo =======================================================
echo.

set SCRIPT_DIR=%~dp0
set MSI_PATH=%SCRIPT_DIR%SqlDam-Setup-v1.0.0.msi

if not exist "%MSI_PATH%" (
    echo [ERROR] Could not find %MSI_PATH%
    pause
    exit /b 1
)

echo Uninstalling SQL DAM Service...
msiexec.exe /x "%MSI_PATH%" /qb /l*v "%SCRIPT_DIR%uninstall.log"

if %ERRORLEVEL% EQU 0 (
    echo.
    echo [SUCCESS] SQL DAM has been uninstalled.
) else (
    echo.
    echo [FAILED] Uninstall encountered code %ERRORLEVEL%.
    echo See log file for details: %SCRIPT_DIR%uninstall.log
)

echo.
pause
