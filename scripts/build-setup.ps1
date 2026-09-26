param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1')
    & (Join-Path $PSScriptRoot 'build-native.ps1')
}
$buildRoot = Join-Path $projectRoot 'build'
$stage = Join-Path $buildRoot ('setup-payload-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
$names = @('BetterDownload.exe', 'BetterDownloadBridge.dll', 'TagLibSharp.dll', 'Microsoft.Web.WebView2.Core.dll', 'WebView2Loader.dll')
$notices = [ordered]@{
    'LICENSE.txt' = 'LICENSE'
    'THIRD-PARTY-NOTICES.md' = 'THIRD-PARTY-NOTICES.md'
    'LICENSE.TagLibSharp.txt' = 'licenses\TagLibSharp.txt'
    'LICENSE.WebView2.txt' = 'licenses\WebView2.txt'
    'LICENSE.QMUnlock.txt' = 'licenses\QMUnlock.txt'
    'LICENSE.MMKV.txt' = 'licenses\MMKV.txt'
}
$hashes = [ordered]@{}
foreach ($name in $names) {
    Copy-Item -LiteralPath (Join-Path $buildRoot $name) -Destination $stage
    $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash
}
foreach ($name in $notices.Keys) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $notices[$name]) -Destination (Join-Path $stage $name)
    $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash
}
$version = [regex]::Match([IO.File]::ReadAllText((Join-Path $projectRoot 'src\Program.cs')), 'Version = "([0-9.]+)"').Groups[1].Value
$manifest = Join-Path $buildRoot 'payload.json'
[ordered]@{ product = 'QQM-BetterDownload/v1'; version = $version; files = $hashes } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifest -Encoding utf8
$archive = Join-Path $buildRoot 'payload.zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'setup') -Filter '*.cs' | ForEach-Object FullName)
$refs = @('/reference:System.Core.dll','/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.IO.Compression.dll','/reference:Microsoft.CSharp.dll')
& $compiler /nologo /utf8output /optimize+ /platform:x86 /target:winexe ('/out:' + (Join-Path $buildRoot 'BetterDownload-Setup.exe')) ('/resource:' + $archive + ',payload.zip') ('/resource:' + $manifest + ',payload.json') @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Write-Output 'Installer built: build\BetterDownload-Setup.exe'
