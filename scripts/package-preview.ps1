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
$dist = Join-Path $projectRoot 'dist'
[IO.Directory]::CreateDirectory($dist) | Out-Null
$entries = [ordered]@{}
foreach ($name in @('BetterDownload.exe','qqm-cli.exe','BetterDownload-Setup.exe','BetterDownloadBridge.dll','TagLibSharp.dll','Microsoft.Web.WebView2.Core.dll','WebView2Loader.dll')) {
    $entries[$name] = Join-Path $buildRoot $name
}
# Stream audited inputs directly into the archive, without expanded staging folders.
foreach ($file in $sourceFiles) {
    $entries['source/' + $file] = Join-Path $projectRoot $file
    if ($file -match '^(docs|licenses)/' -or $file -in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','CHANGELOG.md') -or $file -eq 'promo/cover.html') {
        $entries[$file] = Join-Path $projectRoot $file
    }
}
$version = [regex]::Match([IO.File]::ReadAllText((Join-Path $projectRoot 'src\Program.cs')), 'Version = "([0-9.]+)"').Groups[1].Value
$archive = Join-Path $dist ('QQM-BetterDownload-' + $version + '-preview.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem, System.IO.Compression
$manifest = [Collections.Generic.List[object]]::new()
$stream = [IO.File]::Open($archive, [IO.FileMode]::Create)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($name in $entries.Keys) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entries[$name], $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            $manifest.Add([ordered]@{ path = $name; sha256 = (Get-FileHash -LiteralPath $entries[$name] -Algorithm SHA256).Hash })
        }
        $preview = [Text.Encoding]::ASCII.GetBytes("@echo off`r`nstart `"`" `"%~dp0BetterDownload.exe`" --card-demo`r`n")
        $entry = $zip.CreateEntry('Preview-Cards.cmd').Open()
        try { $entry.Write($preview, 0, $preview.Length) } finally { $entry.Dispose() }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $manifest.Add([ordered]@{path='Preview-Cards.cmd'; sha256=[BitConverter]::ToString($sha.ComputeHash($preview)).Replace('-','')}) } finally { $sha.Dispose() }
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 3))
        $entry = $zip.CreateEntry('SHA256.json').Open()
        try { $entry.Write($bytes, 0, $bytes.Length) } finally { $entry.Dispose() }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
Copy-Item -LiteralPath (Join-Path $buildRoot 'BetterDownload-Setup.exe') -Destination (Join-Path $dist 'BetterDownload-Setup.exe') -Force
[ordered]@{ path = $archive; bytes = (Get-Item -LiteralPath $archive).Length; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash } | ConvertTo-Json