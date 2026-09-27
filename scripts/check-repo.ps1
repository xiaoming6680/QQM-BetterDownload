param([switch]$ListFiles)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$files = @(& git -C $projectRoot -c core.quotepath=false ls-files --cached --others --exclude-standard | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect Git file list.' }
$files = @($files | Where-Object { Test-Path -LiteralPath (Join-Path $projectRoot $_) -PathType Leaf })
$problems = [Collections.Generic.List[string]]::new()
$rootFiles = @('.gitignore','.gitattributes','README.md','CHANGELOG.md','LICENSE','THIRD-PARTY-NOTICES.md')
foreach ($file in $files) {
    $full = Join-Path $projectRoot $file
    if (!(Test-Path -LiteralPath $full -PathType Leaf)) { continue }
    $allowed = $file -in $rootFiles -or $file -match '^(src|setup|tests)/[^/]+\.cs$' -or
        $file -in @('src/app.manifest','src/BetterDownload.ico','src/ui/settings.html','src/ui/entry.svg','native/bridge.c','native/bridge.def','native/gf_ui.c','native/gf_ui.h','tests/BridgeHost.c','tests/fixtures/tone.flac','tests/fixtures/README.md','promo/cover.html') -or
        $file -match '^scripts/[^/]+\.(ps1|cjs)$' -or $file -match '^licenses/[^/]+\.txt$' -or
        $file -match '^docs/[A-Z-]+\.md$' -or $file -match '^docs/(images/)?[a-z-]+\.(png|jpg)$' -or
        $file -match '^\.github/workflows/[^/]+\.ya?ml$'
    if (!$allowed) { $problems.Add("Unexpected repository file: $file"); continue }
    if ((Get-Item -LiteralPath $full).Length -gt 3MB) { $problems.Add("Oversized source/document: $file") }
    if ($file -match '\.(cs|c|ps1|cjs|html|md|ya?ml)$') {
        $content = [IO.File]::ReadAllText($full)
        if ($content -match '(?i)[A-Z]:[\\/]Users[\\/]\d{5,}[\\/]|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|-----BEGIN (RSA |EC )?PRIVATE KEY-----') {
            $problems.Add("Possible private data in: $file")
        }
        if ($file -match '\.md$') {
            foreach ($match in [regex]::Matches($content, '(?:src="|\]\()([^"\s)]+)')) {
                $target = $match.Groups[1].Value
                if ($target -match '^(https?://|#)') { continue }
                $target = $target.Split('#')[0]
                if ($target -and !(Test-Path -LiteralPath (Join-Path (Split-Path $full) $target))) { $problems.Add("Missing document link in ${file}: $target") }
            }
        }
    }
}
$fixture = Join-Path $projectRoot 'tests/fixtures/tone.flac'
if ((Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash -ne '7F98DD78D4BC1172D22A83FE0161A81A42568FE19E246471125E620B03E88947') { $problems.Add('Unexpected audio fixture.') }
foreach ($sample in @('.research/private.txt','.tools/tool.exe','build/app.exe','src/settings.json','docs/song.mflac','tests/private.flac','src/detected-paths.json','docs/mmkv.default','progress.md','unrelated.txt')) {
    & git -C $projectRoot check-ignore --no-index --quiet -- $sample
    if ($LASTEXITCODE -ne 0) { $problems.Add("Ignore rule missing for: $sample") }
}
if ($problems.Count) { throw ($problems -join "`n") }
if ($ListFiles) { $files } else { Write-Output ("Repository check passed: {0} source/document files." -f $files.Count) }
