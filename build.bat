@echo off
setlocal EnableDelayedExpansion

rem ============================================================
rem  BandwidthDesk build helper
rem
rem  Usage:
rem    build.bat                  -> Release build (bin/), default
rem    build.bat debug            -> Debug build (bin/)
rem    build.bat run              -> Release build + launch elevated
rem    build.bat clean            -> dotnet clean + remove bin/obj + build/
rem    build.bat publish          -> Self-contained x64 publish into build\publish\
rem    build.bat portable         -> publish + zip into build\BandwidthDesk-<ver>-portable-x64.zip
rem    build.bat installer        -> publish + run Inno Setup -> build\BandwidthDesk-<ver>-setup-x64.exe
rem    build.bat dist             -> publish + portable + installer (full distribution bundle)
rem ============================================================

set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
set "SOLUTION=%ROOT%\BandwidthDesk.slnx"
set "APPPROJ=%ROOT%\src\BandwidthDesk.App\BandwidthDesk.App.csproj"
set "NATIVE=%ROOT%\native\x64"
set "BUILDROOT=%ROOT%\build"
set "PUBDIR=%BUILDROOT%\publish"
set "INSTALLERDIR=%ROOT%\installer"
set "CONFIG=Release"
set "ACTION=build"
set "RID=win-x64"

if /I "%~1"=="debug"     set "CONFIG=Debug"
if /I "%~1"=="release"   set "CONFIG=Release"
if /I "%~1"=="run"       set "ACTION=run"
if /I "%~1"=="clean"     set "ACTION=clean"
if /I "%~1"=="publish"   set "ACTION=publish"
if /I "%~1"=="portable"  set "ACTION=portable"
if /I "%~1"=="installer" set "ACTION=installer"
if /I "%~1"=="dist"      set "ACTION=dist"

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

if /I "%ACTION%"=="publish"   goto :publish
if /I "%ACTION%"=="portable"  goto :portable
if /I "%ACTION%"=="installer" goto :installer
if /I "%ACTION%"=="dist"      goto :dist

rem ---- Default: plain build into bin\Release\... ------------------------------
echo [build] dotnet build "%SOLUTION%" -c %CONFIG%
dotnet build "%SOLUTION%" -c %CONFIG% -nologo
if errorlevel 1 goto :fail

set "OUTDIR=%ROOT%\src\BandwidthDesk.App\bin\%CONFIG%\net9.0-windows10.0.22000.0"
set "EXE=%OUTDIR%\BandwidthDesk.exe"

echo.
echo [build] OK
echo [build] Output: %EXE%

if not exist "%OUTDIR%\WinDivert.dll" (
  echo.
  echo [build] WARNING: WinDivert.dll is missing from the output directory.
  echo [build] Place WinDivert.dll in %NATIVE%\ and rebuild.
)
if not exist "%OUTDIR%\WinDivert64.sys" (
  echo.
  echo [build] WARNING: WinDivert64.sys is missing from the output directory.
  echo [build] Place WinDivert64.sys in %NATIVE%\ and rebuild.
)

if /I "%ACTION%"=="run" (
  echo [build] Launching elevated...
  powershell -NoProfile -Command "Start-Process -Verb RunAs '%EXE%'"
)

goto :done

rem ---- Publish (self-contained) ----------------------------------------------
:publish
call :read_version
call :do_publish
if errorlevel 1 goto :fail
goto :done

rem ---- Portable zip ----------------------------------------------------------
:portable
call :read_version
call :do_publish
if errorlevel 1 goto :fail
call :do_portable
if errorlevel 1 goto :fail
goto :done

rem ---- Installer (Inno Setup) ------------------------------------------------
:installer
call :read_version
call :do_publish
if errorlevel 1 goto :fail
call :do_installer
if errorlevel 1 goto :fail
goto :done

rem ---- Full distribution -----------------------------------------------------
:dist
call :read_version
call :do_publish
if errorlevel 1 goto :fail
call :do_portable
if errorlevel 1 goto :fail
call :do_installer
if errorlevel 1 (
  echo [build] Installer step failed or was skipped, but portable was produced.
)
goto :done

rem ============================================================================
rem  Helpers
rem ============================================================================

:read_version
set "APPVER=0.3.0"
set "VERFILE=%TEMP%\bandwidthdesk_ver.txt"
if exist "%VERFILE%" del /f /q "%VERFILE%" >nul 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "([xml](Get-Content -Raw '%ROOT%\Directory.Build.props')).Project.PropertyGroup.Version | Out-File -Encoding ascii -NoNewline '%VERFILE%'" >nul 2>nul
if exist "%VERFILE%" (
  set /p APPVER=<"%VERFILE%"
  del /f /q "%VERFILE%" >nul 2>nul
)
exit /b 0

:do_publish
echo [build] Publishing self-contained %RID% to "%PUBDIR%"...
if exist "%PUBDIR%" rd /s /q "%PUBDIR%"
mkdir "%PUBDIR%" >nul 2>nul

dotnet publish "%APPPROJ%" -c Release -r %RID% --self-contained true ^
  -p:PublishSingleFile=false ^
  -p:PublishReadyToRun=true ^
  -p:DebugType=none -p:DebugSymbols=false ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o "%PUBDIR%" -nologo
if errorlevel 1 exit /b 1

rem Ensure WinDivert native files are present in the published folder.
if exist "%NATIVE%\WinDivert.dll"   copy /Y "%NATIVE%\WinDivert.dll"   "%PUBDIR%\" >nul
if exist "%NATIVE%\WinDivert64.sys" copy /Y "%NATIVE%\WinDivert64.sys" "%PUBDIR%\" >nul

if not exist "%PUBDIR%\WinDivert.dll" (
  echo [build] WARNING: WinDivert.dll missing in publish output.
  echo [build]          Drop WinDivert.dll in "%NATIVE%\" and re-run.
)
if not exist "%PUBDIR%\WinDivert64.sys" (
  echo [build] WARNING: WinDivert64.sys missing in publish output.
  echo [build]          Drop WinDivert64.sys in "%NATIVE%\" and re-run.
)

rem Include LICENSE + README so the portable drop is self-explanatory.
if exist "%ROOT%\LICENSE"   copy /Y "%ROOT%\LICENSE"   "%PUBDIR%\LICENSE.txt" >nul
if exist "%ROOT%\README.md" copy /Y "%ROOT%\README.md" "%PUBDIR%\README.md"   >nul

echo [build] Publish OK: "%PUBDIR%"
exit /b 0

:do_portable
if not defined APPVER set "APPVER=0.3.0"
set "ZIPNAME=BandwidthDesk-%APPVER%-portable-x64.zip"
set "ZIPPATH=%BUILDROOT%\%ZIPNAME%"
if exist "%ZIPPATH%" del /F /Q "%ZIPPATH%"

echo [build] Packing portable zip: "%ZIPPATH%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%PUBDIR%\*' -DestinationPath '%ZIPPATH%' -CompressionLevel Optimal -Force"
if errorlevel 1 (
  echo [build] Compress-Archive failed.
  exit /b 1
)
echo [build] Portable OK: "%ZIPPATH%"
exit /b 0

:do_installer
if not defined APPVER set "APPVER=0.3.0"
call :find_iscc
if not defined ISCC (
  echo.
  echo [build] Inno Setup compiler ^(iscc.exe^) was not found.
  echo [build] Install Inno Setup 6 from https://jrsoftware.org/isdl.php
  echo [build] then re-run: build.bat installer
  exit /b 1
)

echo [build] Building installer with: "%ISCC%"
"%ISCC%" /Qp "/DAppVersion=%APPVER%" "/DSourceDir=%PUBDIR%" "/DOutputDir=%BUILDROOT%" "%INSTALLERDIR%\BandwidthDesk.iss"
if errorlevel 1 (
  echo [build] Inno Setup compilation failed.
  exit /b 1
)
echo [build] Installer OK: "%BUILDROOT%\BandwidthDesk-%APPVER%-setup-x64.exe"
exit /b 0

:find_iscc
set "ISCC="
where iscc.exe >nul 2>nul
if not errorlevel 1 (
  for /f "delims=" %%I in ('where iscc.exe') do set "ISCC=%%I"
)
if defined ISCC exit /b 0
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe"      set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 5\ISCC.exe"      set "ISCC=%ProgramFiles%\Inno Setup 5\ISCC.exe"
if exist "%ProgramFiles(x86)%\Inno Setup 5\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 5\ISCC.exe"
exit /b 0

:fail
echo.
echo [build] FAILED.
echo [build] Window closes in 5s...
timeout /t 5 /nobreak >nul 2>nul
exit /b 1

:done
echo.
echo [build] Done.
echo [build] Window closes in 5s...
timeout /t 5 /nobreak >nul 2>nul
exit /b 0

:clean
echo [build] Cleaning...
dotnet clean "%SOLUTION%" -nologo >nul 2>nul
for /d /r "%ROOT%\src" %%d in (bin obj) do (
  if exist "%%d" rd /s /q "%%d"
)
if exist "%BUILDROOT%" rd /s /q "%BUILDROOT%"
echo [build] Clean complete.
echo [build] Window closes in 5s...
timeout /t 5 /nobreak >nul 2>nul
exit /b 0
