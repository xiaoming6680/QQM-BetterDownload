param([switch]$Tests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Join-Path $projectRoot 'build'
[IO.Directory]::CreateDirectory($buildRoot) | Out-Null
& (Join-Path $PSScriptRoot 'restore-taglib.ps1')
& (Join-Path $PSScriptRoot 'restore-webview.ps1')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler is required.' }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$references = @('/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Core.dll')
$references += '/reference:' + (Join-Path $buildRoot 'TagLibSharp.dll')
foreach ($assembly in @('PresentationFramework','PresentationCore','WindowsBase','WindowsFormsIntegration','UIAutomationClient','UIAutomationTypes')) {
    $references += '/reference:' + (Join-Path $env:WINDIR ('Microsoft.NET\Framework\v4.0.30319\WPF\' + $assembly + '.dll'))
}
$references += '/reference:System.Xaml.dll'
foreach ($assembly in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll')) { $references += '/reference:' + (Join-Path $buildRoot $assembly) }
$references += '/resource:' + (Join-Path $projectRoot 'src\ui\settings.html') + ',settings.html'
$references += '/win32manifest:' + (Join-Path $projectRoot 'src\app.manifest')
if ($Tests) {
    $testSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' | ForEach-Object FullName)
    & $compiler /nologo /utf8output /optimize+ /platform:x86 /target:exe /main:QqmBetterDownload.Tests ("/out:" + (Join-Path $buildRoot 'tests.exe')) @references @sources @testSources
} else {
    & $compiler /nologo /utf8output /optimize+ /platform:x86 /target:winexe /main:QqmBetterDownload.Program ("/out:" + (Join-Path $buildRoot 'BetterDownload.exe')) @references @sources
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
    & $compiler /nologo /utf8output /optimize+ /platform:x86 /target:exe /main:QqmBetterDownload.Program ("/out:" + (Join-Path $buildRoot 'qqm-cli.exe')) @references @sources
}
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output 'Build succeeded.'
