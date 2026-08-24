#requires -Version 5.1
<#
.SYNOPSIS
    Runs one `codex exec` turn inside a VISIBLE terminal window, streaming
    output to the console live while mirroring it into log files.

.DESCRIPTION
    Launched by codex-agent.ps1 / codex-dev.ps1 in a new Windows Terminal tab
    (or plain PowerShell window). Not meant to be invoked by hand.

    Contract with the wrapper:
      - writes its own PID to -PidFile immediately (timeout kill handle)
      - streams codex stdout -> console + -StdoutFile (UTF-8, incremental,
        tail-able while running), stderr -> console + -StderrFile
      - on codex exit, writes the exit code to -DoneFile (completion signal)
      - success: window auto-closes after 5s; failure: window stays open
        until Enter so the user can inspect what went wrong
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CodexExe,
    # Base64(UTF8(JSON array)) of the codex CLI arguments. Base64 sidesteps
    # every layer of wt/powershell command-line quote mangling.
    [Parameter(Mandatory = $true)][string]$ArgsB64,
    [Parameter(Mandatory = $true)][string]$PromptFile,
    [Parameter(Mandatory = $true)][string]$StdoutFile,
    [Parameter(Mandatory = $true)][string]$StderrFile,
    [Parameter(Mandatory = $true)][string]$DoneFile,
    [Parameter(Mandatory = $true)][string]$PidFile,
    [string]$Title = 'Codex'
)

$ErrorActionPreference = 'Continue'
try { $host.UI.RawUI.WindowTitle = $Title } catch { }

# Advertise our PID first thing: the wrapper treats its presence as "window is
# alive" and uses it for a process-tree kill on timeout.
Set-Content -LiteralPath $PidFile -Value $PID

# UTF-8 both directions: the prompt piped INTO codex ($OutputEncoding) and the
# codex output decoded back ([Console]::OutputEncoding). Prompts and findings
# contain CJK; PS 5.1 defaults would mangle them.
$OutputEncoding = New-Object System.Text.UTF8Encoding $false
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$cliArgs = @()
try {
    $decoded = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ArgsB64))
    $cliArgs = @((ConvertFrom-Json $decoded) | ForEach-Object { [string]$_ })
} catch {
    Set-Content -LiteralPath $DoneFile -Value -1
    Write-Host "[runner] failed to decode ArgsB64: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host | Out-Null
    exit 1
}

$enc = New-Object System.Text.UTF8Encoding $false
$outW = New-Object System.IO.StreamWriter($StdoutFile, $false, $enc)
$errW = New-Object System.IO.StreamWriter($StderrFile, $false, $enc)
$outW.AutoFlush = $true
$errW.AutoFlush = $true

$exitCode = -1
try {
    Write-Host ('[runner] ' + $CodexExe) -ForegroundColor Cyan
    Write-Host ('[runner] args: ' + ($cliArgs -join ' ')) -ForegroundColor Cyan
    Write-Host ''

    # 2>&1 merges codex stderr into the pipeline as ErrorRecords; we split the
    # two streams back apart for the log files while echoing both live.
    Get-Content -LiteralPath $PromptFile -Raw -Encoding UTF8 |
        & $CodexExe @cliArgs 2>&1 |
        ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) {
                # .Exception.Message holds the raw stderr line; ToString() on a
                # blank line degenerates to the exception type name.
                $line = $_.Exception.Message
                if ($null -eq $line) { $line = $_.ToString() }
                $errW.WriteLine($line)
                Write-Host $line -ForegroundColor DarkYellow
            } else {
                $line = [string]$_
                $outW.WriteLine($line)
                Write-Host $line
            }
        }
    $exitCode = $LASTEXITCODE
} catch {
    $errW.WriteLine('[runner] fatal: ' + $_.Exception.Message)
    $exitCode = -1
} finally {
    $outW.Close()
    $errW.Close()
    Set-Content -LiteralPath $DoneFile -Value $exitCode
}

Write-Host ''
if ($exitCode -eq 0) {
    Write-Host '[runner] codex finished (exit 0); window closes in 5s...' -ForegroundColor Green
    Start-Sleep -Seconds 5
} else {
    Write-Host ('[runner] codex exited with ' + $exitCode + ' - window kept open; press Enter to close.') -ForegroundColor Red
    Read-Host | Out-Null
}
exit $exitCode
