@echo off
rem Starts the OpenLancer editor. The optional argument is a profile file (.json). Without it, the editor opens the
rem newest recent profile.
rem The script builds the editor first, so the editor is never older than its sources (decision D28). The editor of
rem this folder locks its files while it runs, so then the build does not run and a second editor starts. The second
rem editor takes no edits for a mod that the first editor has open. The game of Play (OpenLancer.Play.exe) of this
rem folder locks the same files, so it stops the build too. An editor or a game of another folder, for example a
rem scratch build, does not stop the build.
rem The build runs in this folder, so that global.json selects the .NET SDK, also when Nexus starts the script from
rem another folder. The editor starts in the folder of the caller, so that a relative profile path works.
rem When the build fails, the build output goes to %TEMP%\openlancer-editor-build.log, a message box names that file,
rem and no editor starts. The script does not pause, so a start from Nexus does not wait.
rem The script does not build the native libraries. See docs\UPSTREAM.md for that step.
setlocal
set "PROJECT=%~dp0src\OpenLancer.Editor\OpenLancer.Editor.csproj"
set "BIN=%~dp0src\OpenLancer.Editor\bin\Debug\net10.0"
set "EDITOR=%BIN%\OpenLancer.Editor.exe"
set "PLAY=%BIN%\OpenLancer.Play.exe"
set "BUILDLOG=%TEMP%\openlancer-editor-build.log"
set "BUILDFAILED="

rem Exit code 1 when the editor or the game of this folder runs. A process whose path the check cannot read counts as
rem one of them.
powershell -NoProfile -Command "if (Get-Process OpenLancer.Editor, OpenLancer.Play -ErrorAction SilentlyContinue | Where-Object { -not $_.Path -or $_.Path -in $env:EDITOR, $env:PLAY }) { exit 1 }"
rem The build failure test is "||", not "if errorlevel 1". The .NET host fails with a negative exit code, for example
rem when the SDK of global.json is missing, and "if errorlevel 1" does not see a negative code.
if not errorlevel 1 (
  pushd "%~dp0"
  dotnet build "%PROJECT%" -c Debug -nologo -v q >"%BUILDLOG%" 2>&1 || set "BUILDFAILED=1"
  popd
)
if defined BUILDFAILED (
  start "" powershell -NoProfile -WindowStyle Hidden -Command "Add-Type -AssemblyName System.Windows.Forms; [void][System.Windows.Forms.MessageBox]::Show('The editor did not build, so it did not start. The build output is in ' + $env:BUILDLOG + '.', 'OpenLancer Editor', 'OK', 'Error')"
  exit /b 1
)

if not exist "%EDITOR%" (
  echo The editor is not built: %EDITOR%
  exit /b 1
)
start "" "%EDITOR%" %*
