@echo off
setlocal

echo =======================================================
echo   SQL DAM - Enterprise Database Activity Monitor
echo   Installation Setup
echo =======================================================
echo.

set SCRIPT_DIR=%~dp0
set MSI_PATH=%SCRIPT_DIR%SqlDam-Setup-v1.0.0.msi

if not exist "%MSI_PATH%" (
    echo [ERROR] Could not find %MSI_PATH%
    pause
    exit /b 1
)

echo Starting SQL DAM Setup Wizard...
echo.

:: Launch the MSI with full interactive wizard and completion dialog
msiexec.exe /i "%MSI_PATH%" /l*v "%SCRIPT_DIR%install.log"

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ========================================================
    echo  [SUCCESS] SQL DAM installation completed successfully!
    echo ========================================================
    echo  - Windows Service : SqlDam ("SQL Database Activity Monitor")
    echo  - Desktop App     : "SQL DAM Desktop" (Desktop and Start Menu)
    echo  - Web Dashboard   : http://127.0.0.1:5055/
    echo  - Setup Log       : %SCRIPT_DIR%install.log
    echo ========================================================
) else if %ERRORLEVEL% EQU 1602 (
    echo.
    echo [INFO] Setup was cancelled by the user.
) else (
    echo.
    echo [FAILED] Installation encountered code %ERRORLEVEL%.
    echo See log file for details: %SCRIPT_DIR%install.log
)

echo.
pause
