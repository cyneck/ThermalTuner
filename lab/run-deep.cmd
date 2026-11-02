@echo off
rem ============================================================
rem  ThermalPilot - 19.5 min deep run (double-click friendly)
rem  ASCII ONLY: cmd.exe mis-parses UTF-8 batch files.
rem  Real logic lives in run-deep.ps1 (also ASCII, no BOM needed).
rem ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-deep.ps1" %*
if errorlevel 1 (
    echo.
    echo [launcher] run-deep.ps1 exited with an error.
    pause
)
