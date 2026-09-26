$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $projectRoot '.tools'
$version = '0.15.2'
$folder = Join-Path $toolRoot "zig-x86_64-windows-$version"
$archive = Join-Path $toolRoot "zig-x86_64-windows-$version.zip"
[IO.Directory]::CreateDirectory($toolRoot) | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -UseBasicParsing -Uri "https://ziglang.org/download/$version/zig-x86_64-windows-$version.zip" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '3A0ED1E8799A2F8CE2A6E6290A9FF22E6906F8227865911FB7DDEDC3CC14CB0C') { throw 'Zig SHA256 mismatch.' }
if (!(Test-Path -LiteralPath (Join-Path $folder 'zig.exe'))) { Expand-Archive -LiteralPath $archive -DestinationPath $toolRoot }
Write-Output (Join-Path $folder 'zig.exe')
