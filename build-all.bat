@echo off
rem Builds the server and the plugins into the "dist" folder.
rem   dist\            the server (run Cove.exe from here, or "dotnet Cove.dll")
rem   dist\plugins\    the plugin dlls
rem Copy the files you want over your running server, keeping your own .cfg and .txt files.

dotnet publish Cove\Cove.csproj -c Release -o dist
if errorlevel 1 goto failed

rem Each plugin is built into its own folder under "build" and only the plugin's own dll is copied to dist\plugins.
rem (Building straight into dist\plugins also put a second copy of the server and its libraries there.)
if not exist dist\plugins mkdir dist\plugins
for %%P in (AutoMessages ChalkObserver ChalkPersist ChalkSaves ChatTriggers Feed Greetings ClearChalk DiscordBridge) do (
    dotnet build Cove.%%P -c Release -o build\%%P
    if errorlevel 1 goto failed
    copy /y build\%%P\Cove.%%P.dll dist\plugins\ >nul
    if errorlevel 1 goto failed
)

copy /y run-server.bat dist\ >nul

echo.
echo Done. Everything is in the dist folder.
goto end

:failed
echo.
echo The build failed, see the messages above.

:end
pause
