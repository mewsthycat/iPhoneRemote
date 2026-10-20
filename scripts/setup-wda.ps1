<#
.SYNOPSIS
    Mac-free WebDriverAgent setup for iPhoneRemote (Plan A).

.DESCRIPTION
    Stages (run in order, or -Stage all):
      tools    Download go-ios (Windows build) into tools\go-ios.
      package  Download Appium's prebuilt, unsigned WebDriverAgentRunner-Runner.zip and
               repackage it as tools\WDA.ipa.
      --- manual: sign + install tools\WDA.ipa with Sideloadly using your Apple ID ---
      run      Start the iOS 17+ tunnel, check Developer Mode, mount the developer image,
               find the sideloaded WDA and launch it. Leave this window open while you use
               `iremote wda` / `iremote tap` in another terminal.

.EXAMPLE
    .\scripts\setup-wda.ps1 -Stage tools
    .\scripts\setup-wda.ps1 -Stage package
    .\scripts\setup-wda.ps1 -Stage run
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'tools', 'package', 'run')]
    [string]$Stage = 'all',
    [string]$ToolsDir,
    # Override if auto-detection of the sideloaded WDA bundle id picks the wrong app.
    [string]$BundleId
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# $PSScriptRoot is empty in param() defaults on Windows PowerShell 5.1, so resolve here.
if (-not $ToolsDir) { $ToolsDir = Join-Path $PSScriptRoot '..\tools' }
$ToolsDir = [IO.Path]::GetFullPath($ToolsDir)
$GoIosDir = Join-Path $ToolsDir 'go-ios'
$IosExe = Join-Path $GoIosDir 'ios.exe'
$IpaPath = Join-Path $ToolsDir 'WDA.ipa'
$PairDir = Join-Path $ToolsDir 'tunnel-pairrecords'
$ImageDir = Join-Path $ToolsDir 'devimages'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Get-LatestAssetUrl([string]$repo, [string]$assetName) {
    $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'iPhoneRemote' }
    $asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
    if (-not $asset) { throw "Asset $assetName not found in $repo $($release.tag_name)" }
    Write-Host "    $repo $($release.tag_name): $assetName ($([math]::Round($asset.size / 1MB, 1)) MB)"
    return $asset.browser_download_url
}

function Invoke-Download([string]$url, [string]$dest) {
    $ProgressPreference = 'SilentlyContinue' # makes Invoke-WebRequest ~10x faster on PS 5.1
    Invoke-WebRequest $url -OutFile $dest -UseBasicParsing -Headers @{ 'User-Agent' = 'iPhoneRemote' }
}

function Invoke-Ios {
    # go-ios logs to stderr and prints results (often JSON) to stdout. On PS 5.1, redirected
    # native stderr becomes an error record, which 'Stop' would turn into a throw.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & $IosExe @args 2>$null }
    finally { $ErrorActionPreference = $prev }
    return @{ ExitCode = $LASTEXITCODE; Output = ($out -join "`n") }
}

# Zip a folder with forward-slash entry names. PowerShell 5.1's Compress-Archive writes
# backslashes, which iOS/Sideloadly reject as a malformed IPA.
function New-IpaFromFolder([string]$root, [string]$dest) {
    if (Test-Path $dest) { Remove-Item $dest -Force }
    $zip = [IO.Compression.ZipFile]::Open($dest, 'Create')
    try {
        $rootFull = (Resolve-Path $root).Path.TrimEnd('\') + '\'
        Get-ChildItem $root -Recurse -File | ForEach-Object {
            $entry = $_.FullName.Substring($rootFull.Length).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry, 'Optimal') | Out-Null
        }
    }
    finally { $zip.Dispose() }
}

function Install-Tools {
    Write-Step 'Downloading go-ios'
    New-Item -ItemType Directory -Force $GoIosDir | Out-Null
    $zip = Join-Path $ToolsDir 'go-ios-win.zip'
    Invoke-Download (Get-LatestAssetUrl 'danielpaulus/go-ios' 'go-ios-win.zip') $zip
    Expand-Archive $zip -DestinationPath $GoIosDir -Force
    Remove-Item $zip
    $exe = Get-ChildItem $GoIosDir -Recurse -Filter 'ios.exe' | Select-Object -First 1
    if (-not $exe) { throw 'ios.exe not found in go-ios-win.zip' }
    if ($exe.FullName -ne $IosExe) { Move-Item $exe.FullName $IosExe -Force }
    Write-Host "    $IosExe ($((Invoke-Ios --version).Output))"
}

function New-WdaIpa {
    Write-Step 'Downloading prebuilt WebDriverAgent (unsigned, real-device build)'
    New-Item -ItemType Directory -Force $ToolsDir | Out-Null
    # Short work path: nested .xctest/.dSYM entries exceed MAX_PATH (260) under deep project folders.
    $work = Join-Path $env:TEMP 'iprwda'
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory $work | Out-Null

    $zip = Join-Path $work 'WebDriverAgentRunner-Runner.zip'
    Invoke-Download (Get-LatestAssetUrl 'appium/WebDriverAgent' 'WebDriverAgentRunner-Runner.zip') $zip
    $extract = Join-Path $work 'extract'
    Expand-Archive $zip -DestinationPath $extract -Force

    $app = Get-ChildItem $extract -Recurse -Directory -Filter '*.app' |
        Where-Object { $_.Name -like 'WebDriverAgentRunner*' } |
        Sort-Object { $_.FullName.Length } | Select-Object -First 1
    if (-not $app) { throw "No WebDriverAgentRunner*.app inside $zip" }
    if (-not (Test-Path (Join-Path $app.FullName 'PlugIns\WebDriverAgentRunner.xctest'))) {
        Write-Warning 'PlugIns\WebDriverAgentRunner.xctest missing; this may not be a runnable WDA build.'
    }

    # Debug symbols aren't needed on device and only add signing surface.
    Get-ChildItem $app.FullName -Recurse -Directory -Filter '*.dSYM' | Remove-Item -Recurse -Force

    Write-Step "Packaging $($app.Name) as WDA.ipa"
    $payloadRoot = Join-Path $work 'ipa'
    $payload = Join-Path $payloadRoot 'Payload'
    New-Item -ItemType Directory -Force $payload | Out-Null
    Move-Item $app.FullName $payload
    New-IpaFromFolder $payloadRoot $IpaPath
    Remove-Item $work -Recurse -Force
    Write-Host "    $IpaPath"
    Write-Host ''
    Write-Host 'Next (manual): open Sideloadly, drag in tools\WDA.ipa, enter your Apple ID, click Start.' -ForegroundColor Yellow
    Write-Host 'Then on the iPhone: Settings > General > VPN & Device Management > trust your Apple ID.' -ForegroundColor Yellow
    Write-Host 'Then run: .\scripts\setup-wda.ps1 -Stage run' -ForegroundColor Yellow
}

function Start-Wda {
    if (-not (Test-Path $IosExe)) { throw "go-ios missing. Run: .\scripts\setup-wda.ps1 -Stage tools" }

    Write-Step 'Checking device'
    $list = Invoke-Ios list
    if ($list.Output -notmatch '[0-9a-fA-F-]{24,}') { throw 'No iPhone found. Plug it in, unlock it, tap Trust.' }
    Write-Host "    $($list.Output)"

    Write-Step 'Ensuring iOS 17+ tunnel is running'
    if ((Invoke-Ios tunnel ls).Output -notmatch 'address') {
        New-Item -ItemType Directory -Force $PairDir | Out-Null
        # --userspace runs the tunnel as a TCP stack inside go-ios: no admin rights, no wintun.dll.
        Start-Process $IosExe -ArgumentList 'tunnel', 'start', '--userspace', "--pair-record-path=$PairDir" `
            -WindowStyle Minimized
        $deadline = (Get-Date).AddSeconds(30)
        while ((Invoke-Ios tunnel ls).Output -notmatch 'address') {
            if ((Get-Date) -gt $deadline) {
                throw 'Tunnel did not come up in 30 s. Check the minimized "ios tunnel" window. If the phone shows a Trust prompt, accept it.'
            }
            Start-Sleep -Seconds 1
        }
    }
    Write-Host '    tunnel up'

    Write-Step 'Checking Developer Mode'
    $dev = (Invoke-Ios devmode get).Output
    if ($dev -match 'false|disabled') {
        Invoke-Ios devmode reveal | Out-Null
        throw 'Developer Mode is off. On the iPhone: Settings > Privacy & Security > Developer Mode > On, restart, confirm. Then re-run -Stage run.'
    }

    Write-Step 'Mounting developer disk image'
    New-Item -ItemType Directory -Force $ImageDir | Out-Null
    & $IosExe image auto "--basedir=$ImageDir"
    if ($LASTEXITCODE -ne 0) {
        # go-ios requests the image signature from https://gs.apple.com, whose cert chains to
        # "Apple Root CA" -- not in the Windows trust store -- so the mount fails with
        # "x509: certificate signed by unknown authority". pymobiledevice3 talks to the same
        # Apple service over plain HTTP; the phone verifies Apple's signature on the ticket itself.
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        & py -c 'import pymobiledevice3' 2>$null
        $hasPmd3 = $LASTEXITCODE -eq 0
        $ErrorActionPreference = $prev
        if (-not $hasPmd3) {
            throw 'ios image auto failed (see above). If the error is "certificate signed by unknown authority", run: py -m pip install pymobiledevice3   then re-run -Stage run.'
        }
        Write-Host '    go-ios mount failed; retrying with pymobiledevice3' -ForegroundColor Yellow
        & py -m pymobiledevice3 mounter auto-mount
        if ($LASTEXITCODE -ne 0) { throw 'pymobiledevice3 mounter auto-mount failed (see output above).' }
    }

    Write-Host '    Reminder: Settings > Developer > Enable UI Automation must be ON, or taps/screenshots are refused.' -ForegroundColor Yellow

    Write-Step 'Locating sideloaded WebDriverAgent'
    if (-not $BundleId) {
        $apps = (Invoke-Ios apps).Output | ConvertFrom-Json
        $wda = $apps | Where-Object {
            $_.CFBundleIdentifier -like '*WebDriverAgent*' -or $_.CFBundleName -like 'WebDriverAgent*'
        } | Select-Object -First 1
        if (-not $wda) { throw 'WDA is not installed. Sideload tools\WDA.ipa with Sideloadly first (or pass -BundleId).' }
        $BundleId = $wda.CFBundleIdentifier
    }
    Write-Host "    $BundleId"

    Write-Step 'Launching WDA (Ctrl+C to stop). In another terminal: dotnet run --project src/IPhoneRemote.Cli -- wda'
    & $IosExe runwda "--bundleid=$BundleId" "--testrunnerbundleid=$BundleId" '--xctestconfig=WebDriverAgentRunner.xctest'
}

switch ($Stage) {
    'tools'   { Install-Tools }
    'package' { New-WdaIpa }
    'run'     { Start-Wda }
    'all'     {
        if (-not (Test-Path $IosExe)) { Install-Tools }
        if (-not (Test-Path $IpaPath)) { New-WdaIpa; return }  # stop here for the manual Sideloadly step
        Start-Wda
    }
}
