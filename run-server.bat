@echo off
rem Starts the server and starts it again if it stops unexpectedly (a crash).
rem A normal stop (!exit, or the exit command) ends the script instead.
rem Every start and stop is written to restarts.log, with the exit code Windows reported.
cd /d "%~dp0"

:start
echo [%date% %time%] Starting the server>> restarts.log
if exist Cove.exe (
    Cove.exe
) else (
    dotnet Cove.dll
)
set CODE=%errorlevel%
echo [%date% %time%] The server exited with code %CODE%>> restarts.log

if "%CODE%"=="0" goto stopped

echo.
echo The server stopped unexpectedly (exit code %CODE%). Restarting in 10 seconds, close this window to cancel.
timeout /t 10
goto start

:stopped
echo.
echo The server was stopped normally.
pause
