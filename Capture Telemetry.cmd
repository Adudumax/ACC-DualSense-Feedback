@echo off
cd /d "%~dp0"
echo ACC DualSense diagnostic capture is enabled.
echo Record ONE requested driving condition, then press Ctrl+C to stop.
echo The ACC-telemetry-*.csv file will be saved in this folder.
echo Rename it for that condition before starting the next capture.
echo.
start /wait "" ACCDualSenseFeedback.exe --capture-telemetry
echo.
pause
