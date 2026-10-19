<#
.SYNOPSIS
  Скачивает wintun.dll с проверкой SHA-256 и кладёт в папку openconnect.
  Скрипт настройки сети больше не нужен: его заменил OpenToConnect.NetConfig.exe.
#>
param(
    [Parameter(Mandatory)] [string] $OutDir
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Wintun 0.14.1 — драйвер виртуального адаптера (wintun.net), подписан Microsoft.
$WintunUrl = 'https://www.wintun.net/builds/wintun-0.14.1.zip'
$WintunSha256 = '07C256185D6EE3652E09FA55C0B673E2624B565E02C4B9091C79CA7D2F24EF51'

function Get-Verified([string] $Url, [string] $Path, [string] $Sha256) {
    Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing
    $actual = (Get-FileHash -Algorithm SHA256 $Path).Hash
    if ($actual -ne $Sha256) {
        Remove-Item $Path
        throw "Хэш не совпал для $Url`: ожидался $Sha256, получен $actual"
    }
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'wintun.zip'
    Get-Verified $WintunUrl $zip $WintunSha256
    Expand-Archive $zip -DestinationPath $tmp
    Copy-Item (Join-Path $tmp 'wintun\bin\amd64\wintun.dll') $OutDir -Force
    Copy-Item (Join-Path $tmp 'wintun\LICENSE.txt') (Join-Path $OutDir 'wintun-LICENSE.txt') -Force
}
finally {
    Remove-Item -Recurse -Force $tmp
}

Write-Host "wintun.dll сохранён в $OutDir"
