@echo off
rem Download the latest PoE Co-op release and install it over the current one.
setlocal
set "POECOOP_SCRIPT=%~f0"
set "POECOOP_HERE=%~dp0"
set "POECOOP_GAME=%~1"
if not defined POECOOP_GAME set "POECOOP_GAME=%GAME_DIR%"
if not defined POECOOP_GAME if exist "%~dp0PillarsOfEternity.exe" set "POECOOP_GAME=%~dp0."

where powershell.exe >nul 2>nul
if errorlevel 1 (
    echo Windows PowerShell is required to download the release.
    pause
    exit /b 1
)

rem The PowerShell call and the exit share one line: see the note after it.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$lines = Get-Content -LiteralPath $env:POECOOP_SCRIPT; $marker = [Array]::IndexOf($lines, '# POWERSHELL'); $code = $lines[($marker + 1)..($lines.Length - 1)] -join [Environment]::NewLine; & ([ScriptBlock]::Create($code))" && exit /b 0
rem Only reached on failure. A successful update may have replaced this file, so it exits on the line above.
echo.
echo Update failed.
pause
exit /b 1

# POWERSHELL
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = 'ULilBagel/poe-coop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$temp = Join-Path ([IO.Path]::GetTempPath()) ('PoECoop-update-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$code = 1
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'poecoop-updater' }
    $zip = $release.assets | Where-Object { $_.name -match '^PoECoop-[0-9][0-9A-Za-z.+-]*\.zip$' } | Select-Object -First 1
    if (-not $zip) { throw 'The release has no PoECoop zip.' }
    $prefix = "https://github.com/$repo/releases/download/"
    if (-not $zip.browser_download_url.StartsWith($prefix)) { throw "Unexpected download address: $($zip.browser_download_url)" }
    $sum = $release.assets | Where-Object { $_.name -eq ($zip.name + '.sha256') } | Select-Object -First 1

    $archive = Join-Path $temp $zip.name
    Write-Host "Downloading $($zip.name)..."
    Invoke-WebRequest -Uri $zip.browser_download_url -OutFile $archive -UseBasicParsing
    if ($sum) {
        if (-not $sum.browser_download_url.StartsWith($prefix)) { throw "Unexpected download address: $($sum.browser_download_url)" }
        $sumFile = Join-Path $temp ($zip.name + '.sha256')
        Invoke-WebRequest -Uri $sum.browser_download_url -OutFile $sumFile -UseBasicParsing
        $expected = ((Get-Content -LiteralPath $sumFile -TotalCount 1) -split '\s+')[0]
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        if ($expected -ne $actual) { throw "$($zip.name) does not match its published SHA-256; nothing was installed." }
        Write-Host 'Checksum matches.'
    } else {
        Write-Host 'No published checksum to compare for this release; continuing.'
    }

    $package = Join-Path $temp 'package'
    Expand-Archive -LiteralPath $archive -DestinationPath $package -Force
    $installer = Join-Path $package 'PoECoop\install-poecoop.bat'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'The release has no installer.' }

    # The installer finds the game itself, or asks, when no folder is known here.
    if ($env:POECOOP_GAME) {
        $game = (Resolve-Path -LiteralPath $env:POECOOP_GAME).Path
        & cmd.exe /c "`"$installer`" `"$game`""
    } else {
        & cmd.exe /c "`"$installer`""
    }
    $code = $LASTEXITCODE
    if ($code -eq 0) {
        Write-Host ''
        Write-Host "Updated to $($zip.name). Every player in a session needs this same release."
    }
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
exit $code
