@echo off
chcp 65001 >nul
title FrogLauncher - сборка
where dotnet >nul 2>nul
if errorlevel 1 (
  echo [!] Не найден .NET SDK 8. Скачайте: https://dotnet.microsoft.com/download/dotnet/8.0
  pause & exit /b 1
)

if not exist keys.props (
  echo [i] keys.props не найден: Modrinth работает и так, но для CurseForge и входа Microsoft
  echo     без настройки нужны ключи. Скопируйте keys.props.example в keys.props и заполните.
  echo.
)

echo [1/3] Сборка лаунчера...
dotnet publish FrogLauncher.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o build\launcher
if errorlevel 1 ( echo [!] Не удалось собрать лаунчер. & pause & exit /b 1 )

echo [2/3] Сборка установщика (лаунчер вшивается внутрь)...
if not exist Setup\Payload mkdir Setup\Payload
copy /y build\launcher\FrogLauncher.exe Setup\Payload\FrogLauncher.exe >nul
dotnet publish Setup\FrogLauncherSetup.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
if errorlevel 1 ( echo [!] Не удалось собрать установщик. & pause & exit /b 1 )

echo [3/3] Сборка NewsPublisher (консоль новостей)...
dotnet publish tools\NewsPublisher\NewsPublisher.csproj -c Release -o dist
if errorlevel 1 ( echo [!] Не удалось собрать NewsPublisher. & pause & exit /b 1 )

echo.
echo ГОТОВО. В папке dist:
echo   FrogLauncher-Setup.exe  - установщик (его и раздавайте пользователям)
echo   NewsPublisher.exe       - консоль для новостей лаунчера (только для вас)
pause
