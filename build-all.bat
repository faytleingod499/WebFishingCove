@echo off
rem Builds the server and the four plugins into the "dist" folder.
rem   dist\            the server (run Cove.exe from here, or "dotnet Cove.dll")
rem   dist\plugins\    the plugin dlls
rem Copy the files you want over your running server, keeping your own .cfg and .txt files.

dotnet publish Cove\Cove.csproj -c Release -o dist
if errorlevel 1 goto failed

for %%P in (AutoMessages ChalkObserver ChalkPersist DiscordBridge) do (
    dotnet build Cove.%%P -c Release -o dist\plugins
    if errorlevel 1 goto failed
)

echo.
echo Done. Everything is in the dist folder.
goto end

:failed
echo.
echo The build failed, see the messages above.

:end
pause
