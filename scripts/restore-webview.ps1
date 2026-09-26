$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$deps = Join-Path $projectRoot 'build\deps'
[IO.Directory]::CreateDirectory($deps) | Out-Null
$package = Join-Path $deps 'microsoft.web.webview2.1.0.3537.50.nupkg'
if (!(Test-Path -LiteralPath $package)) {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.3537.50/microsoft.web.webview2.1.0.3537.50.nupkg' -OutFile $package
}
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne '5EA526BBD728ADDA0DA4D31219267E96460494A427E4894C4E09D9F320F4B9AA') { throw 'WebView2 SDK SHA256 mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($entry in @('lib/net462/Microsoft.Web.WebView2.Core.dll','lib/net462/Microsoft.Web.WebView2.WinForms.dll','runtimes/win-x86/native/WebView2Loader.dll')) {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($entry), (Join-Path $projectRoot ('build\' + [IO.Path]::GetFileName($entry))), $true)
    }
} finally { $zip.Dispose() }
