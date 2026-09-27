param([switch]$Tests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$zig = @(& (Join-Path $PSScriptRoot 'restore-zig.ps1'))[-1]
$buildRoot = Join-Path $projectRoot 'build'
[IO.Directory]::CreateDirectory($buildRoot) | Out-Null
& $zig cc -target x86-windows-gnu -shared -O2 -Wall -Wextra -Werror (Join-Path $projectRoot 'native\bridge.c') (Join-Path $projectRoot 'native\gf_ui.c') (Join-Path $projectRoot 'native\bridge.def') -o (Join-Path $buildRoot 'BetterDownloadBridge.dll') -lshell32 -luser32 -lcomctl32 -lgdi32 -loleaut32
if ($LASTEXITCODE -ne 0) { throw 'Native bridge compilation failed.' }
Write-Output 'Native x86 bridge built.'
if ($Tests) {
    & $zig cc -target x86-windows-gnu -shared -DBETTERDOWNLOAD_TEST -O2 -Wall -Wextra -Werror (Join-Path $projectRoot 'native\bridge.c') (Join-Path $projectRoot 'native\gf_ui.c') (Join-Path $projectRoot 'native\bridge.def') -o (Join-Path $buildRoot 'TestBridge.dll') -lshell32 -luser32 -lcomctl32 -lgdi32 -loleaut32
    if ($LASTEXITCODE -ne 0) { throw 'Test bridge compilation failed.' }
    & $zig cc -target x86-windows-gnu -municode '-Wl,--subsystem,windows' -O2 -Wall -Wextra -Werror (Join-Path $projectRoot 'tests\BridgeHost.c') -o (Join-Path $buildRoot 'BridgeHost.exe') -luser32 -lshell32
    if ($LASTEXITCODE -ne 0) { throw 'Test host compilation failed.' }
    # Unsigned stand-ins for QQ Music's key interface: valid, malformed, crashing, stalled.
    foreach ($variant in @(@('FakeKeys', ''), @('FakeKeysInvalid', '-DFAKE_INVALID'), @('FakeKeysCrash', '-DFAKE_CRASH'), @('FakeKeysHang', '-DFAKE_HANG'))) {
        $arguments = @('cc', '-target', 'x86-windows-gnu', '-shared', '-O2', '-Wall', '-Wextra', '-Werror')
        if ($variant[1]) { $arguments += $variant[1] }
        & $zig @arguments (Join-Path $projectRoot 'tests\FakeKeys.c') (Join-Path $projectRoot 'tests\FakeKeys.def') -o (Join-Path $buildRoot ($variant[0] + '.dll'))
        if ($LASTEXITCODE -ne 0) { throw ('Test key interface compilation failed: ' + $variant[0]) }
    }
}
