@echo off
setlocal

rem ============================================================
rem  BandwidthDesk build helper
rem  Usage:
rem    build.bat             -> Release build, default
rem    build.bat debug       -> Debug build
rem    build.bat run         -> Release build + launch elevated
rem    build.bat clean       -> dotnet clean + remove bin/obj
rem ============================================================

set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
set "SOLUTION=%ROOT%\BandwidthDesk.slnx"
set "CONFIG=Release"
set "ACTION=build"

if /I "%~1"=="debug"   set "CONFIG=Debug"
if /I "%~1"=="release" set "CONFIG=Release"
if /I "%~1"=="run"     set "ACTION=run"
if /I "%~1"=="clean"   set "ACTION=clean"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [build] .NET SDK not found in PATH. Install .NET 9 SDK from https://dotnet.microsoft.com/download
  exit /b 1
)

if /I "%ACTION%"=="clean" goto :clean

rem Kill any running BandwidthDesk so the DLLs aren't locked.
tasklist /FI "IMAGENAME eq BandwidthDesk.exe" /NH 2>nul | find /I "BandwidthDesk.exe" >nul
if not errorlevel 1 (
  echo [build] Closing running BandwidthDesk.exe so the build can overwrite its DLLs...
  taskkill /F /IM BandwidthDesk.exe >nul 2>nul
)

echo [build] dotnet build "%SOLUTION%" -c %CONFIG%
dotnet build "%SOLUTION%" -c %CONFIG% -nologo
if errorlevel 1 (
  echo.
  echo [build] FAILED.
  echo [build] Window closes in 5s...
  timeout /t 5 /nobreak >nul
  exit /b 1
)

set "OUTDIR=%ROOT%\src\BandwidthDesk.App\bin\%CONFIG%\net9.0-windows10.0.22000.0"
set "EXE=%OUTDIR%\BandwidthDesk.exe"

echo.
echo [build] OK
echo [build] Output: %EXE%

if not exist "%OUTDIR%\WinDivert.dll" (
  echo.
  echo [build] WARNING: WinDivert.dll is missing from the output directory.
  echo [build] Place WinDivert.dll and WinDivert64.sys in %ROOT%\native\x64\ and rebuild.
)

if /I "%ACTION%"=="run" (
  echo [build] Launching elevated...
  powershell -NoProfile -Command "Start-Process -Verb RunAs '%EXE%'"
)

echo [build] Window closes in 5s...
timeout /t 5 /nobreak >nul
exit /b 0

:clean
echo [build] Cleaning...
dotnet clean "%SOLUTION%" -nologo >nul 2>nul
for /d /r "%ROOT%\src" %%d in (bin obj) do (
  if exist "%%d" rd /s /q "%%d"
)
echo [build] Clean complete.
echo [build] Window closes in 5s...
timeout /t 5 /nobreak >nul
exit /b 0
