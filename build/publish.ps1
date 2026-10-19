<#
.SYNOPSIS
  Собирает установщик artifacts\OpenToConnect-<версия>.msi.

  Окно и служба публикуются в одну папку (artifacts\publish), чтобы делить одну копию .NET:
    OpenToConnect.exe           — окно
    OpenToConnect.Service.exe   — служба
    OpenToConnect.NetConfig.exe — настройка сети туннеля (её запускает openconnect)
    openconnect\                — openconnect.exe, DLL, wintun.dll
.PARAMETER OpenConnectDir
  Папка с результатом build-openconnect.sh. Обязательна для MSI; без неё (-NoInstaller)
  собирается только папка publish — для локальной проверки .NET-части.
#>
param(
    [string] $OpenConnectDir,
    [string] $Configuration = 'Release',
    [switch] $NoInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version

if (Test-Path $artifacts) { Remove-Item -Recurse -Force $artifacts }

foreach ($project in 'src\O2C.App\O2C.App.csproj', 'src\O2C.Service\O2C.Service.csproj', 'src\O2C.NetConfig\O2C.NetConfig.csproj') {
    dotnet publish (Join-Path $root $project) -c $Configuration -o $publish
    if ($LASTEXITCODE) { throw "publish $project failed" }
}
# Отладочные символы в установщик не кладём.
Get-ChildItem $publish -Filter *.pdb | Remove-Item

if ($NoInstaller) {
    Write-Host "Папка publish: $publish (установщик не собирался)"
    return
}
if (-not $OpenConnectDir) { throw 'Укажите -OpenConnectDir (результат build-openconnect.sh) или -NoInstaller' }

$ocOut = Join-Path $publish 'openconnect'
New-Item -ItemType Directory -Force $ocOut | Out-Null
Copy-Item (Join-Path $OpenConnectDir '*') $ocOut -Recurse -Force
& (Join-Path $PSScriptRoot 'fetch-runtime.ps1') -OutDir $ocOut

# Лицензии сторонних компонентов — в папку licenses рядом с программой (попадают в MSI).
# Лицензии DLL openconnect лежат в openconnect\licenses (их собирает build-openconnect.sh).
$licenses = Join-Path $publish 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $licenses 'OpenToConnect-LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $licenses
Copy-Item (Join-Path $root 'licenses\*') $licenses -Recurse -Force
$dotnetRoot = Split-Path (Get-Command dotnet).Source
foreach ($name in 'LICENSE.txt', 'ThirdPartyNotices.txt') {
    $src = Join-Path $dotnetRoot $name
    if (-not (Test-Path $src)) { throw "Не найден $src — лицензия .NET обязательна в установщике" }
    Copy-Item $src (Join-Path $licenses "dotnet-$name")
}
foreach ($required in 'openconnect\openconnect-COPYING.LGPL', 'openconnect\wintun-LICENSE.txt', 'openconnect\PACKAGES.txt') {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "Нет обязательного файла лицензии: $required" }
}

dotnet build (Join-Path $root 'installer\O2C.Installer.wixproj') -c Release `
    "-p:PublishDir=$publish\" "-p:ProductVersion=$version" -o $artifacts
if ($LASTEXITCODE) { throw 'MSI build failed' }

$msi = Join-Path $artifacts "OpenToConnect-$version.msi"
if (-not (Test-Path $msi)) { throw "Не найден $msi" }
Write-Host "Установщик: $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) МБ)"
