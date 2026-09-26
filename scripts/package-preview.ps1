param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceFiles = @(& (Join-Path $PSScriptRoot 'check-repo.ps1') -ListFiles)
if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1')
    & (Join-Path $PSScriptRoot 'build-native.ps1')
    & (Join-Path $PSScriptRoot 'build-setup.ps1') -SkipBuild
}
$buildRoot = Join-Path $projectRoot 'build'
$stage = Join-Path $buildRoot ('package-preview-' + [Guid]::NewGuid().ToString('N'))
$dist = Join-Path $projectRoot 'dist'
[IO.Directory]::CreateDirectory($stage) | Out-Null
[IO.Directory]::CreateDirectory($dist) | Out-Null
foreach ($name in @('BetterDownload.exe','qqm-cli.exe','BetterDownload-Setup.exe','BetterDownloadBridge.dll','TagLibSharp.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll')) {
    Copy-Item -LiteralPath (Join-Path $buildRoot $name) -Destination (Join-Path $stage $name)
}
# Copy only audited Git candidates, including corresponding source and licenses.
foreach ($file in $sourceFiles) {
    $target = Join-Path (Join-Path $stage 'source') $file
    [IO.Directory]::CreateDirectory((Split-Path $target)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $target
    if ($file -match '^(docs|licenses)/' -or $file -in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','CHANGELOG.md') -or $file -eq 'promo/cover.html') {
        $target = Join-Path $stage $file
        [IO.Directory]::CreateDirectory((Split-Path $target)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $target
    }
}
[IO.File]::WriteAllText((Join-Path $stage 'Preview-Cards.cmd'), "@echo off`r`nstart `"`" `"%~dp0BetterDownload.exe`" --card-demo`r`n", [Text.Encoding]::ASCII)
$manifest = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $stage 'SHA256.json') -Encoding utf8
$archive = Join-Path $dist 'QQM-BetterDownload-0.1.0-preview.zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
[ordered]@{ path = $archive; bytes = (Get-Item -LiteralPath $archive).Length; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash } | ConvertTo-Json
