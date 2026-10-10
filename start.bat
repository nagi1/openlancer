@echo off
rem Starts the OpenLancer editor. Without an argument, it opens the "sample" profile in ..\GameData\profiles.
rem Build it first: dotnet build OpenLancer.slnx
set EDITOR=%~dp0src\OpenLancer.Editor\bin\Debug\net10.0\OpenLancer.Editor.exe
if not exist "%EDITOR%" (
  echo The editor is not built. Run: dotnet build "%~dp0OpenLancer.slnx"
  exit /b 1
)
start "" "%EDITOR%" %*
