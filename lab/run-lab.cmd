@echo off
rem ============================================================
rem  ThermalPilot lab launcher (ASCII ONLY - do not add Chinese
rem  text here: cmd.exe mis-parses UTF-8 batch files).
rem  Real logic lives in run-lab.ps1 (UTF-8 with BOM).
rem  Set THERMALPILOT_NOPAUSE=1 for unattended runs.
rem ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-lab.ps1" %*
if errorlevel 1 (
    echo.
    echo [launcher] run-lab.ps1 exited with an error.
    if not defined THERMALPILOT_NOPAUSE pause
)
