<#
.SYNOPSIS
  Сборка релиза RibbixLauncher: лаунчер (self-contained, один exe) → payload.zip → RibbixLauncherSetup.exe, плюс publish API.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\build.ps1
  powershell -ExecutionPolicy Bypass -File build\build.ps1 -CutAssets -SkipApi
#>
param(
    [string] $Configuration = "Release",
    # Номер релиза (без «v»): 2.1.0, 2.2.0-beta.1. По умолчанию — из Directory.Build.props.
    [string] $Version,
    [switch] $CutAssets,
    [switch] $SkipApi
)

$ErrorActionPreference = "Stop"
$versionArgs = @()
if ($Version) {
    $numeric = ($Version -split '-')[0]
    $versionArgs = @("-p:Version=$Version", "-p:AssemblyVersion=$numeric.0", "-p:FileVersion=$numeric.0", "-p:InformationalVersion=$Version")
}
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts"
$launcherOut = Join-Path $out "launcher"
$payloadDir = Join-Path $root "src\RibbixLauncher.Installer\Payload"
$payload = Join-Path $payloadDir "payload.zip"

function Step($text) { Write-Host "`n🐸 $text" -ForegroundColor Green }
function Exec([scriptblock] $cmd) { & $cmd; if ($LASTEXITCODE -ne 0) { throw "Команда завершилась с кодом $LASTEXITCODE" } }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out, $payloadDir | Out-Null

if ($CutAssets) {
    Step "Нарезка ассетов из атласов"
    Exec { dotnet run --project "$root\tools\AssetCutter" -c Release -- "$root\assets\assets.map.json" "$root\assets\atlas" "$root\assets\out" }
}

Step "Publish лаунчера (win-x64, self-contained, single-file)"
Exec { dotnet publish "$root\src\RibbixLauncher\RibbixLauncher.csproj" -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none @versionArgs -o $launcherOut }

Step "Упаковка payload.zip"
if (Test-Path $payload) { Remove-Item $payload -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($launcherOut, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Step "Сборка деинсталлятора (без payload)"
$installerProj = "$root\src\RibbixLauncher.Installer\RibbixLauncher.Installer.csproj"
$uninstTmp = Join-Path $out "uninstaller"
Remove-Item (Join-Path $payloadDir "uninstall.exe") -Force -ErrorAction SilentlyContinue
# --no-incremental: иначе MSBuild возьмёт из obj прошлую сборку с payload внутри (деинсталлятор раздуется до 70+ МБ).
Exec { dotnet build $installerProj -c $Configuration -p:DebugType=none -p:EmbedPayload=false --no-incremental -o $uninstTmp }
Copy-Item (Join-Path $uninstTmp "RibbixLauncherSetup.exe") (Join-Path $payloadDir "uninstall.exe") -Force
Remove-Item $uninstTmp -Recurse -Force

Step "Сборка установщика"
Exec { dotnet build $installerProj -c $Configuration -p:DebugType=none --no-incremental @versionArgs }
$setup = Join-Path $root "src\RibbixLauncher.Installer\bin\$Configuration\net48\RibbixLauncherSetup.exe"
Copy-Item $setup (Join-Path $out "RibbixLauncherSetup.exe")
# Копия под старым именем: установленные FrogLauncher 2.x ищут в релизе именно FrogLauncherSetup.exe.
Copy-Item $setup (Join-Path $out "FrogLauncherSetup.exe")

Step "Publish RibbixNews (RibbixNews.exe + RibbixNews.settings.json)"
Exec { dotnet publish "$root\samples\RibbixNews.DevHost\RibbixNews.DevHost.csproj" -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -o (Join-Path $out "RibbixNews") }

if (-not $SkipApi) {
    Step "Publish API"
    Exec { dotnet publish "$root\src\Ribbix.Api\Ribbix.Api.csproj" -c $Configuration -o (Join-Path $out "api") -p:DebugType=none }
}

Step "Готово"
Get-ChildItem $out -File | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    "{0,-24} {1,8:N1} МБ  sha256 {2}" -f $_.Name, ($_.Length / 1MB), $hash
}
Write-Host "`nУстановщик: $out\RibbixLauncherSetup.exe" -ForegroundColor Cyan
Write-Host "Релиз для автообновления: GitHub → Releases → Draft a new release → тег v$(if ($Version) { $Version } else { '<версия>' }) → прикрепите RibbixLauncherSetup.exe (имя файла — ровно такое)." -ForegroundColor Cyan
Write-Host "Прикрепите к тому же релизу и FrogLauncherSetup.exe — по нему обновятся игроки со старым FrogLauncher." -ForegroundColor Cyan
