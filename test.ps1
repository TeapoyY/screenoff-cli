[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$exe = (& (Join-Path $PSScriptRoot 'build.ps1')).FullName
$dist = Split-Path -Parent $exe
$unitTests = Join-Path $dist 'session-tests.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
& $compiler /nologo /codepage:65001 "/out:$unitTests" (Join-Path $PSScriptRoot 'source\ScreenSession.cs') (Join-Path $PSScriptRoot 'tests\SessionTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Could not compile the state-machine tests.' }
& $unitTests
if ($LASTEXITCODE -ne 0) { throw 'The state-machine tests failed.' }

function Check-Command([int]$ExpectedExit, [string[]]$Command, [string]$ExpectedText) {
    $ErrorActionPreference = 'Continue'
    $out = (& $exe @Command 2>&1 | Out-String).Trim()
    $actualExit = $LASTEXITCODE
    if ($actualExit -ne $ExpectedExit -or $out -notmatch $ExpectedText) {
        throw "screenoff $Command returned $actualExit with output '$out'."
    }
    Write-Host "PASS screenoff $Command => $actualExit"
}

Check-Command 0 @('help') 'screenoff off'
Check-Command 2 @('off', '--delay', '0') 'expects whole seconds'
Check-Command 2 @('off', '--for', '86401') 'expects whole seconds'
Check-Command 2 @('off', '--dry-run', '--dry-run') 'Duplicate option'
Check-Command 2 @('wake', '--unexpected') 'takes no options'
Check-Command 0 @('status') '^idle$'
Check-Command 0 @('--wake') '^idle$'
Check-Command 0 @('off', '--dry-run', '--delay', '1') '(?s)countdown 1.*dry-run-complete'

$stdout = Join-Path $dist 'dry-run.stdout.txt'
$stderr = Join-Path $dist 'dry-run.stderr.txt'
$testMetadata = @{
    FilePath = $exe
    ArgumentList = @('--off', '--dry-run', '--delay', '25')
    WindowStyle = 'Hidden'
    RedirectStandardOutput = $stdout
    RedirectStandardError = $stderr
    PassThru = $true
}
$child = Start-Process @testMetadata
try {
    $null = $child.Handle # Retain the exit code on Windows PowerShell 5.1.
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($child.HasExited) { throw "Dry-run controller exited early with code $($child.ExitCode)." }
        $state = (& $exe status | Out-String).Trim()
        if ($state -eq 'countdown') { break }
        Start-Sleep -Milliseconds 75
    } while ($deadline.Elapsed.TotalSeconds -lt 5)
    if ($state -ne 'countdown') { throw "Dry-run status did not reach countdown: $state" }
    Write-Host 'PASS another process can query the countdown'
    Check-Command 3 @('off', '--dry-run') 'Another ScreenOff session'
    Check-Command 0 @('wake') '^idle$'
    if (-not $child.WaitForExit(1000) -or $child.ExitCode -ne 0) {
        throw "Cancellation did not end the controller cleanly. Exit code: '$($child.ExitCode)'."
    }
    $transcript = Get-Content -Raw -LiteralPath $stdout
    if ($transcript -notmatch 'cancelled' -or $transcript -match 'sending-hardware-off|dry-run-complete') {
        throw "Unexpected dry-run transcript: $transcript"
    }
    Write-Host 'PASS wake cancels a running countdown and waits for its exit'
}
finally {
    if (-not $child.HasExited) {
        & $exe wake | Out-Null
        if (-not $child.WaitForExit(1000)) { $child.Kill() } # This test owns only a dry-run process.
    }
    $child.Dispose()
}
Check-Command 0 @('status') '^idle$'
