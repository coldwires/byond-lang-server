# Runs the fixture suite. Three questions, and they are different ones:
#
#   1. does DM do what we think?          dm.exe compiles ok/ clean, and it RUNS
#                                          with every self-check passing
#   2. do we agree about diagnostics?      dmc diagdiff over every fixture:
#                                          zero invented, everywhere
#   3. does dm.exe reject what we think?   errors/ produces exactly the
#                                          diagnostics recorded beside it
#
# Degrades on purpose: BYOND is Windows-only and is not on CI runners, so with
# no dm.exe the compiler-side checks SKIP and our own side still runs. A skip is
# reported as a skip - never as a pass.

[CmdletBinding()]
param(
    [string]$Byond = "${env:ProgramFiles(x86)}\BYOND\bin",
    [switch]$OursOnly,
    # The mined probe corpus: 252 compiles plus 252 diagdiffs, so a few minutes.
    # Off by default to keep the core suite quick.
    [switch]$Probes,
    [switch]$UpdateBaseline
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $root '..\..')

$dm = Join-Path $Byond 'dm.exe'
$dd = Join-Path $Byond 'dreamdaemon.exe'
$haveByond = (-not $OursOnly) -and (Test-Path $dm)

$script:failures = @()
$script:passes = 0
$script:skips = 0

function Pass($what) { $script:passes++; Write-Host "  ok    $what" -ForegroundColor Green }
function Skip($what) { $script:skips++;  Write-Host "  skip  $what" -ForegroundColor DarkGray }
function Fail($what, $detail) {
    $script:failures += "$what : $detail"
    Write-Host "  FAIL  $what" -ForegroundColor Red
    if ($detail) { Write-Host "        $detail" -ForegroundColor Red }
}

function Compile($dme) {
    $out = & $dm $dme 2>&1 | Out-String
    return $out
}

# -- 1+2. every fixture compiles exactly as recorded -------------------------
#
# One convention rather than two: EVERY fixture .dme has a sibling .expected.
# Empty means "0 errors, 0 warnings"; otherwise it lists `line severity text`
# for each diagnostic dm.exe must produce. That covers must-compile-clean,
# must-fail, and the third shape - a case that needs its own compilation unit
# yet still compiles clean, like the numeric-pragma one.

Write-Host "`n[1] every fixture compiles as recorded" -ForegroundColor Cyan

foreach ($expected in Get-ChildItem $root -Recurse -Filter '*.expected' |
         Where-Object { $_.Directory.Name -ne 'probes' } | Sort-Object FullName) {
    $dme = [IO.Path]::ChangeExtension($expected.FullName, '.dme')
    $name = "$($expected.Directory.Name)/$([IO.Path]::GetFileNameWithoutExtension($dme))"

    if (-not $haveByond) { Skip "$name (no dm.exe)"; continue }
    if (-not (Test-Path $dme)) { Fail $name 'no .dme beside the .expected'; continue }

    $out = Compile $dme
    $wanted = @(Get-Content $expected.FullName | Where-Object { $_.Trim() -and $_ -notmatch '^\s*#' })

    if ($wanted.Count -eq 0) {
        if ($out -match '(\d+) errors?, (\d+) warnings?' -and [int]$Matches[1] -eq 0 -and [int]$Matches[2] -eq 0) {
            Pass "$name compiles clean"
        } else {
            $lines = ($out -split "`n" | Where-Object { $_ -match ':(error|warning)' }) -join ' | '
            Fail "$name compiles clean" $lines
        }

        continue
    }

    $missing = @()

    foreach ($line in $wanted) {
        $parts = $line -split '\s+', 3
        $where = ":$($parts[0]):$($parts[1])"

        if ($out -notmatch [regex]::Escape($where) -or $out -notmatch [regex]::Escape($parts[2])) {
            $missing += $line
        }
    }

    if ($missing.Count -eq 0) { Pass $name } else { Fail $name ($missing -join ' | ') }
}

# -- the one fixture that is RUN, not just compiled --------------------------

Write-Host "`n[2] ok/ runs, every check passing" -ForegroundColor Cyan

if (-not $haveByond) {
    Skip 'ok/ runtime (no dm.exe)'
} else {
    $log = Join-Path $root 'ok\ok.log'
    Remove-Item $log -ErrorAction SilentlyContinue
    $dmb = Join-Path $root 'ok\ok.dmb'

    if (-not (Test-Path $dmb)) {
        Fail 'ok/ runtime' 'no .dmb - the compile above must pass first'
    } else {
        # -safe, not -trusted: nothing in ok/ needs trusted mode, and a -trusted world
        # waits on a GUI approval prompt when no interactive approval exists, so a
        # headless run hangs to the timeout and reports "no log produced".
        $p = Start-Process -FilePath $dd -ArgumentList "`"$dmb`" -safe -invisible -once -logself" -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(60000)) { $p.Kill() }

        if (Test-Path $log) {
            $text = Get-Content $log -Raw

            if ($text -match 'RESULT OK') {
                $count = if ($text -match 'checks (\d+) failed') { $Matches[1] } else { '?' }
                Pass "ok/ runtime, $count checks"
            } else {
                $bad = ($text -split "`n" | Where-Object { $_ -match '^FAIL ' }) -join '; '
                Fail 'ok/ runtime' $bad
            }
        } else {
            Fail 'ok/ runtime' 'no log produced'
        }
    }
}

# -- 3. we invent nothing, anywhere ------------------------------------------

Write-Host "`n[3] diagdiff: zero invented" -ForegroundColor Cyan

foreach ($dme in Get-ChildItem $root -Recurse -Filter '*.dme' |
         Where-Object { $_.Directory.Name -ne 'probes' }) {
    $name = "diagdiff $($dme.Directory.Name)/$($dme.Name)"

    if (-not $haveByond) { Skip "$name (no dm.exe)"; continue }

    Push-Location $repo
    $out = & dotnet run -c Release --project src\Dm.Cli -- diagdiff $dme.FullName 2>&1 | Out-String
    Pop-Location

    if ($out -match 'invented\s+(\d+)') {
        if ([int]$Matches[1] -eq 0) { Pass $name } else { Fail $name "$($Matches[1]) invented" }
    } else {
        Fail $name 'could not read the diagdiff summary'
    }
}

# -- 4. the mined probe corpus, against a ratchet ----------------------------
#
# 252 single-message probes mined from the diagnostic lab. We implement a
# fraction of what dm.exe reports, so this is a floor that must not drop rather
# than a target to hit. See errors/probes/BASELINE.txt for why `invented` is not
# the metric here.

if ($Probes) {
    Write-Host "`n[4] probe corpus: agreement must not regress" -ForegroundColor Cyan

    $baselineFile = Join-Path $root 'errors\probes\BASELINE.txt'
    $baseline = 0

    foreach ($line in Get-Content $baselineFile) {
        if ($line -match '^agreed\s+(\d+)') { $baseline = [int]$Matches[1] }
    }

    if (-not $haveByond) {
        Skip "probe corpus (no dm.exe)"
    } else {
        Push-Location $repo
        & dotnet build -c Release src\Dm.Cli\Dm.Cli.csproj | Out-Null
        $dll = Join-Path $repo 'src\Dm.Cli\bin\Release\net9.0\dmc.dll'

        $agreeing = @()

        foreach ($probe in Get-ChildItem (Join-Path $root 'errors\probes') -Filter '*.dme') {
            $out = & dotnet $dll diagdiff $probe.FullName 2>&1 | Out-String

            if ($out -match 'agreed\s+(\d+)' -and [int]$Matches[1] -gt 0) {
                $agreeing += $probe.BaseName
            }
        }

        Pop-Location

        $now = $agreeing.Count
        $total = (Get-ChildItem (Join-Path $root 'errors\probes') -Filter '*.dme').Count

        if ($UpdateBaseline) {
            $header = Get-Content $baselineFile | Where-Object { $_ -match '^#' -or -not $_.Trim() }
            $body = @("agreed $now", "total $total") + ($agreeing | Sort-Object | ForEach-Object { "probe $_" })
            Set-Content $baselineFile (($header + $body) -join "`n") -Encoding utf8
            Pass "probe corpus baseline updated: $now / $total"
        } elseif ($now -lt $baseline) {
            Fail 'probe corpus' "agreement dropped: $now, baseline $baseline"
        } else {
            $note = if ($now -gt $baseline) { " (up from $baseline - run -UpdateBaseline)" } else { "" }
            Pass "probe corpus: $now / $total agree$note"
        }
    }
}

# -- summary -----------------------------------------------------------------

Write-Host ''
Write-Host "passed $script:passes   failed $($script:failures.Count)   skipped $script:skips"

if ($script:failures.Count -gt 0) {
    Write-Host ''
    $script:failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

exit 0
