@echo off
rem Starts the server and starts it again if it stops unexpectedly (a crash).
rem A normal stop (!exit, or the exit command) ends the script instead.
rem Every start and stop is written to restarts.log, with the exit code Windows reported.
rem
rem Crash Watch: the server writes its crash and hang reports into the crashwatch folder. This script adds
rem what only it can see: the exit code (crashwatch\last-exit.txt) and the Windows event log entries about the
rem crash (crashwatch\last-events.txt). The next start of the server puts both into the crash report.
cd /d "%~dp0"
if not exist crashwatch\dumps mkdir crashwatch\dumps

rem when .NET itself dies (stack overflow, a crash in native code), write a memory dump of the moment.
rem The server keeps only the newest 3. Remove these four lines to turn it off.
set DOTNET_DbgEnableMiniDump=1
set DOTNET_DbgMiniDumpType=2
set DOTNET_DbgMiniDumpName=%~dp0crashwatch\dumps\crash-%%p-%%t.dmp
set DOTNET_CreateDumpDiagnostics=0

:start
echo [%date% %time%] Starting the server>> restarts.log
if exist Cove.exe (
    Cove.exe
) else (
    dotnet Cove.dll
)
set CODE=%errorlevel%
echo [%date% %time%] The server exited with code %CODE%>> restarts.log
echo [%date% %time%] The server exited with code %CODE%> crashwatch\last-exit.txt

if "%CODE%"=="0" goto stopped

rem what Windows recorded in the last 10 minutes (.NET Runtime errors, Application Error, Windows Error Reporting)
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-10)} -ErrorAction Stop | Where-Object { $_.ProviderName -in '.NET Runtime','Application Error','Windows Error Reporting' } | Select-Object -First 6 | Format-List TimeCreated,ProviderName,Message | Out-String -Width 200 } catch { '' }" > crashwatch\last-events.txt 2>nul

echo.
echo The server stopped unexpectedly (exit code %CODE%). Restarting in 10 seconds, close this window to cancel.
echo The crash report will be in the crashwatch folder once it starts again.
timeout /t 10
goto start

:stopped
echo.
echo The server was stopped normally.
pause
