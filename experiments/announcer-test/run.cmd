@echo off
rem AI announcer prototype - system .NET is v10, app targets v8: allow roll-forward
set DOTNET_ROLL_FORWARD=LatestMajor
cd /d "%~dp0"
"%~dp0bin\Debug\net8.0-windows\AnnouncerTest.exe"
pause
