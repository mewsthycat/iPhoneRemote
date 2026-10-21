<#
.SYNOPSIS
    Starts iPhone Remote (builds it first if the source changed). Used by iPhoneRemote.cmd.

.EXAMPLE
    .\scripts\launch.ps1                  # start the viewer
    .\scripts\launch.ps1 -CreateShortcut  # add an "iPhone Remote" shortcut to the Desktop
    .\scripts\launch.ps1 -Rebuild         # force a fresh build
#>
param([switch]$CreateShortcut, [switch]$Rebuild)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\IPhoneRemote.Viewer\IPhoneRemote.Viewer.csproj'

function Fail([string]$message) {
    Add-Type -AssemblyName PresentationFramework # loaded only when needed: it costs ~0.7 s
    [System.Windows.MessageBox]::Show($message, 'iPhone Remote', 'OK', 'Error') | Out-Null
    exit 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail '.NET SDK not found. Install the .NET 10 SDK from https://dot.net, then try again.'
}

if ($CreateShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'iPhone Remote.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = (Get-Command powershell).Source
    $s.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`""
    $s.WorkingDirectory = $root
    $s.WindowStyle = 7 # minimized: no console flash
    $s.IconLocation = "$env:SystemRoot\System32\shell32.dll,43"
    $s.Description = 'View and control your iPhone'
    $s.Save()
    Write-Host "Created $lnk"
    exit 0
}

# Already open? Bring it to the front instead of starting a second copy (they'd fight over the USB ports).
$running = Get-Process iPhoneRemote -ErrorAction SilentlyContinue | Select-Object -First 1
if ($running) {
    (New-Object -ComObject WScript.Shell).AppActivate($running.Id) | Out-Null
    exit 0
}

# Where the Release exe lives (bin may be redirected to %LOCALAPPDATA% by Directory.Build.props).
# Asking MSBuild takes ~2 s, so cache the answer per checkout folder; a moved checkout re-asks.
$cacheFile = Join-Path $env:LOCALAPPDATA 'iPhoneRemote\launch-cache.txt'
$exe = $null
if (Test-Path $cacheFile) {
    $cached = Get-Content $cacheFile -TotalCount 2
    if ($cached.Count -eq 2 -and $cached[0] -eq $root) { $exe = $cached[1] }
}
if (-not $exe) {
    $targetPath = (& dotnet msbuild $proj -nologo '-getProperty:TargetPath' '-p:Configuration=Release' | Select-Object -Last 1).Trim()
    $exe = [IO.Path]::ChangeExtension($targetPath, '.exe')
    New-Item -ItemType Directory -Force (Split-Path $cacheFile) | Out-Null
    Set-Content $cacheFile -Value $root, $exe -Encoding UTF8
}

# Rebuild only when a source file is newer than the exe, so normal launches are instant.
$newest = Get-ChildItem (Join-Path $root 'src'), (Join-Path $root 'Directory.Build.props') -Recurse -File -Include *.cs, *.xaml, *.csproj, *.props |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Measure-Object LastWriteTime -Maximum | Select-Object -ExpandProperty Maximum
if ($Rebuild -or -not (Test-Path $exe) -or (Get-Item $exe).LastWriteTime -lt $newest) {
    $log = Join-Path $env:TEMP 'iphoneremote-build.log'
    & dotnet build $proj -c Release -nologo -v q *> $log
    if ($LASTEXITCODE -ne 0) { Fail "The build failed. Details are in:`n$log" }
}

Start-Process $exe -WorkingDirectory $root
