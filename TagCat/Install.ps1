# TagCat - first-run dependency setup.
#
# Checks for the .NET 9 Desktop Runtime and for VLC/LibVLC, fetching whichever is missing.
# Never touches anything outside this folder except installing the .NET Runtime itself,
# which is a normal, supported, machine-wide install - the same thing you'd get running
# Microsoft's own installer by hand.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$appDir = Join-Path $root 'app'

function Write-Step($message) {
    Write-Host ""
    Write-Host "  $message" -ForegroundColor Cyan
}

# ---- .NET 9 Desktop Runtime ----------------------------------------------

Write-Step "Checking for the .NET 9 Desktop Runtime..."

$hasDotNet9Desktop = $false
try {
    $runtimes = & dotnet --list-runtimes 2>$null
    if ($runtimes -match 'Microsoft\.WindowsDesktop\.App 9\.') {
        $hasDotNet9Desktop = $true
    }
} catch {
    # dotnet isn't on PATH at all - treated the same as "not installed".
}

if ($hasDotNet9Desktop) {
    Write-Host "  Found." -ForegroundColor Green
} else {
    Write-Step "Not found. Downloading the .NET 9 Desktop Runtime installer..."

    $dotnetInstaller = Join-Path $env:TEMP 'windowsdesktop-runtime-9-installer.exe'
    $dotnetUrl = 'https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe'

    try {
        Invoke-WebRequest -Uri $dotnetUrl -OutFile $dotnetInstaller -UseBasicParsing
    } catch {
        Write-Host ""
        Write-Host "  Could not download the .NET Runtime installer automatically." -ForegroundColor Red
        Write-Host "  Get it yourself from: https://dotnet.microsoft.com/download/dotnet/9.0" -ForegroundColor Red
        Write-Host "  Choose 'Desktop Runtime' for x64, then run Install.bat again." -ForegroundColor Red
        exit 1
    }

    Write-Step "Installing (Windows will ask for administrator approval)..."
    $proc = Start-Process -FilePath $dotnetInstaller -ArgumentList '/install', '/quiet', '/norestart' `
        -Wait -PassThru -Verb RunAs
    Remove-Item $dotnetInstaller -ErrorAction SilentlyContinue

    if ($proc.ExitCode -ne 0) {
        Write-Host ""
        Write-Host "  The .NET Runtime installer reported a problem (code $($proc.ExitCode))." -ForegroundColor Red
        Write-Host "  Try installing it yourself from: https://dotnet.microsoft.com/download/dotnet/9.0" -ForegroundColor Red
        exit 1
    }

    Write-Host "  Installed." -ForegroundColor Green
}

# ---- VLC / LibVLC ---------------------------------------------------------

Write-Step "Checking for VLC..."

$vlcCandidates = @(
    (Join-Path $appDir 'libvlc.dll'),
    'C:\Program Files\VideoLAN\VLC\libvlc.dll',
    'C:\Program Files (x86)\VideoLAN\VLC\libvlc.dll'
)

$foundVlc = $vlcCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($foundVlc) {
    Write-Host "  Found at $(Split-Path $foundVlc -Parent)." -ForegroundColor Green
} else {
    Write-Step "Not found. Downloading LibVLC - the media engine, not the VLC app itself (a few hundred MB)..."

    $nupkgUrl = 'https://api.nuget.org/v3-flatcontainer/videolan.libvlc.windows/3.0.23.1/videolan.libvlc.windows.3.0.23.1.nupkg'
    $nupkgPath = Join-Path $env:TEMP 'libvlc-windows.nupkg.zip'
    $extractPath = Join-Path $env:TEMP 'libvlc-windows-extract'

    $downloadFailed = $false
    try {
        Invoke-WebRequest -Uri $nupkgUrl -OutFile $nupkgPath -UseBasicParsing
    } catch {
        $downloadFailed = $true
    }

    if ($downloadFailed) {
        Write-Host ""
        Write-Host "  Could not download LibVLC automatically." -ForegroundColor Red
        Write-Host "  TagCat will still start, but video and audio preview will not work" -ForegroundColor Red
        Write-Host "  until VLC is installed - get it from: https://www.videolan.org/vlc/" -ForegroundColor Red
        Write-Host "  Run Install.bat again afterwards and it will be picked up." -ForegroundColor Red
    } else {
        if (Test-Path $extractPath) { Remove-Item $extractPath -Recurse -Force }
        Expand-Archive -Path $nupkgPath -DestinationPath $extractPath -Force
        Remove-Item $nupkgPath -ErrorAction SilentlyContinue

        # Searched for rather than assumed at one fixed path - the exact internal folder
        # layout of a NuGet package is an implementation detail, not something safe to
        # hard-code confidently.
        $libvlcDll = Get-ChildItem -Path $extractPath -Filter 'libvlc.dll' -Recurse |
            Where-Object { $_.DirectoryName -match 'x64' } | Select-Object -First 1
        if (-not $libvlcDll) {
            $libvlcDll = Get-ChildItem -Path $extractPath -Filter 'libvlc.dll' -Recurse | Select-Object -First 1
        }

        if (-not $libvlcDll) {
            Write-Host ""
            Write-Host "  Downloaded LibVLC but could not find libvlc.dll inside it." -ForegroundColor Red
            Write-Host "  TagCat will still start, but video and audio preview will not work." -ForegroundColor Red
            Write-Host "  Installing VLC yourself (videolan.org) and restarting TagCat will fix this." -ForegroundColor Red
        } else {
            Copy-Item -Path (Join-Path $libvlcDll.DirectoryName '*') -Destination $appDir -Recurse -Force
            Write-Host "  Installed into the app folder." -ForegroundColor Green
        }

        Remove-Item $extractPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "  Setup complete." -ForegroundColor Green
