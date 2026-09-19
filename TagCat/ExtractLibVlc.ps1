param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$AppDir
)

$extractPath = Join-Path (Split-Path $ZipPath -Parent) 'libvlc-extract'

if (Test-Path $extractPath) { Remove-Item $extractPath -Recurse -Force }
Expand-Archive -Path $ZipPath -DestinationPath $extractPath -Force

$dll = Get-ChildItem -Path $extractPath -Filter 'libvlc.dll' -Recurse |
    Where-Object { $_.DirectoryName -match 'x64' } | Select-Object -First 1
if (-not $dll) {
    $dll = Get-ChildItem -Path $extractPath -Filter 'libvlc.dll' -Recurse | Select-Object -First 1
}

if ($dll) {
    Copy-Item -Path (Join-Path $dll.DirectoryName '*') -Destination $AppDir -Recurse -Force
}

Remove-Item $extractPath -Recurse -Force -ErrorAction SilentlyContinue
