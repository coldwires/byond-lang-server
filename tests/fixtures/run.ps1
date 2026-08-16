# Runs the fixture suite. Three questions, and they are different ones:
#
#   1. does DM do what we think?          dm.exe compiles ok/ clean, and it RUNS
#                                          with every self-check passing
#   2. do we agree about diagnostics?      dmc diagdiff over every fixture:
#                                          zero invented, everywhere
#   3. does dm.exe reject what we think?   errors/ produces exactly the
#                                          diagnostics recorded beside it
#
# Degrades on purpose: with no dm.exe the compiler-side checks SKIP and our own
# side still runs. A skip is reported as a skip - never as a pass.
#
# CI does have a compiler: the byond-fixtures job fetches the standalone zip and
# passes -Byond, since BYOND is Windows-only and no runner ships it. Every step
# here that shells out to dm.exe forwards that path rather than letting a child
# process fall back to the install location - see the --dm notes below.

[CmdletBinding()]
param(
    [string]$Byond = "${env:ProgramFiles(x86)}\BYOND\bin",
    [switch]$OursOnly,
    # The mined probe corpus: 255 compiles plus 255 diagdiffs, so a few minutes.
    # Off by default to keep the core suite quick.
    [switch]$Probes,
    [switch]$UpdateBaseline,
    # Downgrade a runtime-world failure to a SKIP. For hosts that cannot run DreamDaemon at all -
    # a hosted CI runner kills it with STATUS_DLL_NOT_FOUND in 5 seconds - where failing every
    # build would put a permanent red on a gate nobody can fix from there. Everything else in the
    # suite still gates. Never pass this on a machine that CAN run the daemon: the runtime world
    # is the only tier that catches a BYOND release changing what DM MEANS.
    [switch]$RuntimeOptional
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
#
# A line `total N errors, M warnings` pins the compiler's own summary as well,
# which is how a fixture asserts SILENCE: "these lines are reported" cannot say
# "and nothing else is", and a case whose whole point is that dm.exe stays
# quiet under a live `#pragma warn` needs the count beside its control.

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
        if ($line -match '^total\s+(.+)$') {
            if ($out -notmatch [regex]::Escape($Matches[1].Trim())) { $missing += $line }
            continue
        }

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
        #
        # The failure is DIAGNOSED rather than just reported: "no log produced" reads the same
        # whether the daemon hung, exited instantly, or ran and wrote somewhere else, and those
        # want different fixes. A hosted CI runner has no interactive desktop session, which is a
        # plausible reason a GUI-hosted daemon never starts - so the exit code, the elapsed time
        # and anything it printed are all captured and shown.
        $ddOut = Join-Path $root 'ok\dd-stdout.txt'
        $ddErr = Join-Path $root 'ok\dd-stderr.txt'
        $started = Get-Date

        # The daemon is launched FROM the BYOND bin directory, with that directory on PATH. On a
        # workstation neither matters - the loader searches an exe's own folder - but a hosted CI
        # runner produced 0xC0000135 (STATUS_DLL_NOT_FOUND) in 5 seconds, and BYOND's shells load
        # byondcore and friends by name, so giving the search path the directory explicitly is the
        # cheap thing to rule out first.
        $previousPath = $env:PATH
        $env:PATH = "$Byond;$env:PATH"

        try {
            $p = Start-Process -FilePath $dd -ArgumentList "`"$dmb`" -safe -invisible -once -logself" `
                 -PassThru -WindowStyle Hidden -WorkingDirectory $Byond `
                 -RedirectStandardOutput $ddOut -RedirectStandardError $ddErr
        }
        finally {
            $env:PATH = $previousPath
        }

        # POLL FOR THE RESULT rather than waiting blind for the process. `-once` does not actually
        # make the daemon exit here - it writes the log, keeps running, and was killed at the
        # timeout on EVERY run, so the suite paid the full wait every time even when the world had
        # finished in a second. Watching for the log's own completion marker ends it as soon as
        # there is an answer, which also lets the ceiling be generous for a slow hosted runner
        # without costing a fast machine anything.
        $deadline = (Get-Date).AddSeconds(180)
        $done = $false

        while ((Get-Date) -lt $deadline) {
            if ((Test-Path $log) -and ((Get-Content $log -Raw -ErrorAction SilentlyContinue) -match 'RESULT (OK|FAIL)')) {
                $done = $true
                break
            }

            if ($p.HasExited -and (Get-Date) -gt $started.AddSeconds(5)) { break }
            Start-Sleep -Milliseconds 500
        }

        $elapsed = [int]((Get-Date) - $started).TotalSeconds

        if (-not $p.HasExited) { $p.Kill() }

        if ($done) {
            Write-Host "        world finished in ${elapsed}s" -ForegroundColor DarkGray
        } elseif ($p.HasExited) {
            # Decode the codes that mean something specific, since a bare -1073741515 sends a
            # reader to a search engine and the answer changes what you do next.
            $why = switch ($p.ExitCode) {
                -1073741515 { ' (0xC0000135 STATUS_DLL_NOT_FOUND - a DLL the daemon needs is not on the search path)' }
                -1073741701 { ' (0xC000007B STATUS_INVALID_IMAGE_FORMAT - a 32/64-bit mismatch)' }
                -1073740791 { ' (0xC0000409 stack buffer overrun)' }
                default { '' }
            }

            Write-Host "        daemon exited $($p.ExitCode)$why after ${elapsed}s with no result" -ForegroundColor DarkGray
        } else {
            Write-Host "        no result within 180s (daemon killed)" -ForegroundColor DarkGray
        }

        if (-not (Test-Path $log)) {
            # STATUS_DLL_NOT_FOUND names no name, which leaves the one useful fact out. Read the
            # daemon's import table and report which of them the loader cannot resolve - that turns
            # "a DLL is missing" into something actionable, such as a redistributable to install.
            if ($p.HasExited -and $p.ExitCode -eq -1073741515) {
                $dumpbin = Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe' -ErrorAction SilentlyContinue |
                           Select-Object -First 1

                if ($dumpbin) {
                    $imports = & $dumpbin.FullName /dependents $dd 2>$null |
                               Select-String -Pattern '^\s{4}(\S+\.dll)$' |
                               ForEach-Object { $_.Matches[0].Groups[1].Value }

                    $missing = @($imports | Where-Object {
                        -not (Test-Path (Join-Path $Byond $_)) -and
                        -not (Get-Command $_ -ErrorAction SilentlyContinue) -and
                        -not (Test-Path (Join-Path $env:SystemRoot "System32\$_")) -and
                        -not (Test-Path (Join-Path $env:SystemRoot "SysWOW64\$_"))
                    })

                    if ($missing.Count -gt 0) {
                        Write-Host "        UNRESOLVED IMPORTS: $($missing -join ', ')" -ForegroundColor DarkGray
                    } else {
                        Write-Host "        every direct import resolves - the gap is a TRANSITIVE dependency" -ForegroundColor DarkGray
                    }
                }
            }

            foreach ($capture in @($ddOut, $ddErr)) {
                if ((Test-Path $capture) -and (Get-Item $capture).Length -gt 0) {
                    Write-Host "        $(Split-Path $capture -Leaf): $((Get-Content $capture -Raw).Trim())" -ForegroundColor DarkGray
                }
            }

            # A log written under a different name is a different problem from none at all.
            $strays = Get-ChildItem (Split-Path $dmb -Parent) -Filter '*.log' -ErrorAction SilentlyContinue
            if ($strays) {
                Write-Host "        .log files present: $(($strays | ForEach-Object { $_.Name }) -join ', ')" -ForegroundColor DarkGray
            }
        }

        if (Test-Path $log) {
            $text = Get-Content $log -Raw

            if ($text -match 'RESULT OK') {
                $count = if ($text -match 'checks (\d+) failed') { $Matches[1] } else { '?' }
                Pass "ok/ runtime, $count checks"
            } else {
                $bad = ($text -split "`n" | Where-Object { $_ -match '^FAIL ' }) -join '; '
                Fail 'ok/ runtime' $bad
            }
        } elseif ($RuntimeOptional) {
            Skip 'ok/ runtime (no log - this host cannot run DreamDaemon)'
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
    # --dm is forwarded rather than left to the default, or this step diffs against whatever
    # dm.exe is installed while step [1] compiled with $Byond. A run against a standalone build
    # then measures two different compilers and reads as agreement; on a machine with no install
    # it fails for every fixture.
    $out = & dotnet run -c Release --project src\Dm.Cli -- diagdiff $dme.FullName --dm $dm 2>&1 | Out-String
    Pop-Location

    if ($out -match 'invented\s+(\d+)') {
        if ([int]$Matches[1] -eq 0) { Pass $name } else { Fail $name "$($Matches[1]) invented" }
    } else {
        Fail $name 'could not read the diagdiff summary'
    }
}

# -- 4. the mined probe corpus, against a ratchet ----------------------------
#
# 255 single-message probes mined from the diagnostic lab. We implement a
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
            # --dm for the same reason as step [3]: the ratchet is a comparison against a specific
            # compiler, so it has to be the one this run was pointed at.
            $out = & dotnet $dll diagdiff $probe.FullName --dm $dm 2>&1 | Out-String

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
