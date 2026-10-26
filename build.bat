@echo off
setlocal
cd /d "%~dp0"
title FrogLauncher build

if not exist "FrogLauncher.csproj" (
  echo [!] FrogLauncher.csproj not found next to build.bat.
  echo     Extract the WHOLE zip into a folder first. Do not run build.bat from inside the archive.
  goto end
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [!] .NET SDK 8 is not installed.
  echo     Download the SDK x64 here: https://dotnet.microsoft.com/download/dotnet/8.0
  echo     Then close this window and run build.bat again.
  goto end
)

if not exist "keys.props" (
  echo [i] keys.props not found. Modrinth works anyway, but CurseForge and Microsoft login
  echo     need keys. Copy keys.props.example to keys.props and fill it in. See README.
  echo.
)

echo [1/3] Building the launcher...
dotnet publish "FrogLauncher.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "build\launcher"
if errorlevel 1 goto fail

echo.
echo [2/3] Building the installer with the launcher inside...
if not exist "Setup\Payload" mkdir "Setup\Payload"
copy /y "build\launcher\FrogLauncher.exe" "Setup\Payload\FrogLauncher.exe" >nul
if errorlevel 1 goto fail
dotnet publish "Setup\FrogLauncherSetup.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "dist"
if errorlevel 1 goto fail

echo.
echo [3/3] Building NewsPublisher...
dotnet publish "tools\NewsPublisher\NewsPublisher.csproj" -c Release -o "dist"
if errorlevel 1 goto fail

echo.
echo ===== DONE =====
echo Folder dist:
echo   FrogLauncherSetup.exe  - the installer, give this to users
echo   NewsPublisher.exe       - console for launcher news, for you only
start "" "dist"
goto end

:fail
echo.
echo [!] BUILD FAILED. Scroll up, find the first line with "error", and send it to me.

:end
echo.
pause
endlocal
