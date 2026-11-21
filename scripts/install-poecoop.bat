@echo off
setlocal
set "POECOOP_SCRIPT=%~f0"
set "POECOOP_ROOT=%~dp0"
if not exist "%~dp0bepinex" for %%I in ("%~dp0..") do set "POECOOP_ROOT=%%~fI"
set "POECOOP_GAME=%~1"

if not defined POECOOP_GAME set "POECOOP_GAME=%GAME_DIR%"
if not defined POECOOP_GAME if exist "%ProgramFiles(x86)%\Steam\steamapps\common\Pillars of Eternity\PillarsOfEternity.exe" set "POECOOP_GAME=%ProgramFiles(x86)%\Steam\steamapps\common\Pillars of Eternity"
if not defined POECOOP_GAME if exist "%ProgramFiles%\Steam\steamapps\common\Pillars of Eternity\PillarsOfEternity.exe" set "POECOOP_GAME=%ProgramFiles%\Steam\steamapps\common\Pillars of Eternity"
if not defined POECOOP_GAME set /p "POECOOP_GAME=Pillars of Eternity game folder: "

if not exist "%POECOOP_GAME%\PillarsOfEternity.exe" (
    echo Could not find PillarsOfEternity.exe in "%POECOOP_GAME%".
    echo Drag the Pillars of Eternity game folder onto this file, or run:
    echo   %~nx0 "C:\path\to\Pillars of Eternity"
    pause
    exit /b 1
)
if not exist "%POECOOP_GAME%\PillarsOfEternity_Data\Managed\Assembly-CSharp.dll" (
    echo This does not look like a Pillars of Eternity installation.
    pause
    exit /b 1
)

where powershell.exe >nul 2>nul
if errorlevel 1 (
    echo Windows PowerShell is required to extract the official BepInEx package.
    pause
    exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$lines = Get-Content -LiteralPath $env:POECOOP_SCRIPT; $marker = [Array]::IndexOf($lines, '# POWERSHELL'); $code = $lines[($marker + 1)..($lines.Length - 1)] -join [Environment]::NewLine; & ([ScriptBlock]::Create($code))"
set "POECOOP_EXIT=%ERRORLEVEL%"
echo.
if "%POECOOP_EXIT%"=="0" (
    echo PoE Co-op is ready. Launch the game normally from Steam or your desktop.
) else (
    echo Installation failed. No existing configuration or saves were removed.
)
pause
exit /b %POECOOP_EXIT%

# POWERSHELL
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $env:POECOOP_GAME).Path
$root = (Resolve-Path -LiteralPath $env:POECOOP_ROOT).Path
$archive = Join-Path $root 'bepinex\BepInEx_win_x64_5.4.23.6.zip'
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    $archive = Join-Path $root 'tools\BepInEx_win_x64_5.4.23.6.zip'
}
$plugin = Join-Path $root 'plugin\PoECoop.dll'
if (-not (Test-Path -LiteralPath $plugin -PathType Leaf)) {
    $plugin = Join-Path $root 'src\PoECoop\bin\Release\PoECoop.dll'
}
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Missing official BepInEx archive: $archive" }
if (-not (Test-Path -LiteralPath $plugin -PathType Leaf)) { throw "Missing plugin\PoECoop.dll; build a release package first" }

$temp = Join-Path ([IO.Path]::GetTempPath()) ('PoECoop-' + [Guid]::NewGuid().ToString('N'))
$extract = Join-Path $temp 'bepinex'
$script:backupDir = $null

function Backup-File([string]$relative, [string]$source) {
    if (-not $script:backupDir) {
        $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID
        $script:backupDir = Join-Path $game ('.poecoop-backups\' + $stamp)
        New-Item -ItemType Directory -Path $script:backupDir -Force | Out-Null
    }
    $backup = Join-Path $script:backupDir $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $backup
}

function Install-File([string]$relative, [string]$source) {
    $destination = Join-Path $game $relative
    if ((Test-Path -LiteralPath $destination -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -eq
         (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash)) {
        return
    }
    if ((Test-Path -LiteralPath $destination) -and
        (-not (Test-Path -LiteralPath $destination -PathType Leaf))) {
        throw "Refusing to replace a non-file: $destination"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        Backup-File $relative $destination
    }
    Copy-Item -LiteralPath $source -Destination $destination -Force
    Write-Host "Installed $relative"
}

try {
    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force
    foreach ($file in Get-ChildItem -LiteralPath $extract -Recurse -File) {
        $relative = $file.FullName.Substring($extract.Length).TrimStart('\')
        if ($relative -eq 'changelog.txt') { continue }
        if ($relative -eq 'doorstop_config.ini' -and (Test-Path -LiteralPath (Join-Path $game $relative))) { continue }
        Install-File $relative $file.FullName
    }
    Install-File 'BepInEx\plugins\PoECoop\PoECoop.dll' $plugin

    $config = Join-Path $game 'BepInEx\config\poecoop.cfg'
    if (-not (Test-Path -LiteralPath $config)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $config) -Force | Out-Null
        Set-Content -LiteralPath $config -Encoding ASCII -Value @(
            '## PoE Co-op defaults. Existing settings are never replaced by the installer.',
            '[Connection]',
            'PreferredTransport = Steam'
        )
        Write-Host 'Installed Steam default configuration'
    }
    if ($script:backupDir) { Write-Host "Replaced files were backed up in: $script:backupDir" }
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
