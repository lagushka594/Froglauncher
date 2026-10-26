<#
.SYNOPSIS
  Сборка релиза FrogLauncher: лаунчер (self-contained, один exe) → payload.zip → FrogLauncherSetup.exe, плюс publish API.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\build.ps1
  powershell -ExecutionPolicy Bypass -File build\build.ps1 -CutAssets -SkipApi
#>
param(
    [string] $Configuration = "Release",
    [switch] $CutAssets,
    [switch] $SkipApi
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts"
$launcherOut = Join-Path $out "launcher"
$payloadDir = Join-Path $root "src\FrogLauncher.Installer\Payload"
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
Exec { dotnet publish "$root\src\FrogLauncher\FrogLauncher.csproj" -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -o $launcherOut }

Step "Упаковка payload.zip"
if (Test-Path $payload) { Remove-Item $payload -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($launcherOut, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Step "Сборка деинсталлятора (без payload)"
$installerProj = "$root\src\FrogLauncher.Installer\FrogLauncher.Installer.csproj"
$uninstTmp = Join-Path $out "uninstaller"
Remove-Item (Join-Path $payloadDir "uninstall.exe") -Force -ErrorAction SilentlyContinue
# --no-incremental: иначе MSBuild возьмёт из obj прошлую сборку с payload внутри (деинсталлятор раздуется до 70+ МБ).
Exec { dotnet build $installerProj -c $Configuration -p:DebugType=none -p:EmbedPayload=false --no-incremental -o $uninstTmp }
Copy-Item (Join-Path $uninstTmp "FrogLauncherSetup.exe") (Join-Path $payloadDir "uninstall.exe") -Force
Remove-Item $uninstTmp -Recurse -Force

Step "Сборка установщика"
Exec { dotnet build $installerProj -c $Configuration -p:DebugType=none --no-incremental }
$setup = Join-Path $root "src\FrogLauncher.Installer\bin\$Configuration\net48\FrogLauncherSetup.exe"
Copy-Item $setup (Join-Path $out "FrogLauncherSetup.exe")

Step "Publish FrogNews (FrogNews.exe + FrogNews.settings.json)"
Exec { dotnet publish "$root\samples\FrogNews.DevHost\FrogNews.DevHost.csproj" -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -o (Join-Path $out "FrogNews") }

if (-not $SkipApi) {
    Step "Publish API"
    Exec { dotnet publish "$root\src\Frog.Api\Frog.Api.csproj" -c $Configuration -o (Join-Path $out "api") -p:DebugType=none }
}

Step "Готово"
Get-ChildItem $out -File | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    "{0,-24} {1,8:N1} МБ  sha256 {2}" -f $_.Name, ($_.Length / 1MB), $hash
}
Write-Host "`nУстановщик: $out\FrogLauncherSetup.exe" -ForegroundColor Cyan
