#requires -Version 5.1
<#
.SYNOPSIS
    Run one non-interactive Codex CLI turn as a DEVELOPER subagent (may edit
    the repo). Used by the `taskforce` skill.

.DESCRIPTION
    Adapted from a companion review-skill codex helper (that one is locked read-only
    for reviewing). Differences:
        - write access is per-packet: read-only by default, -AllowWrite grants
          real file editing (on Windows this means the sandbox bypass flag,
          because `-s workspace-write` is silently downgraded to read-only)
        - output schema is optional; -OutFile receives the final message
    Everything else is the same battle-tested setup: newest codex.exe resolved
    by version, strongest api-capable model from models_cache.json, reasoning
    effort `max`, fast tier (service_tier=priority), prompt piped via stdin,
    --ignore-user-config to drop MCP noise (auth still works, AGENTS.md still
    loads).

    By default the turn runs in a VISIBLE terminal window (Windows Terminal
    tab when available) so the user can watch codex work live; logs and the
    output contract are unchanged. Pass -Hidden for the headless behavior.

    Emits ONE line of JSON status on stdout. Exit code 0 = codex finished and
    wrote -OutFile; non-zero = failure (status JSON still printed with .error).

.EXAMPLE
    .\codex-dev.ps1 -PromptFile wp-03.md -OutFile wp-03.out.md -RepoRoot H:\repo
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PromptFile,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [Parameter(Mandatory = $true)][string]$RepoRoot,

    # Optional JSON schema to force structured final output.
    [string]$SchemaFile,

    # `max` = deepest single-agent reasoning. `ultra` self-delegates and is
    # unpredictable under `exec`; never use it here.
    [ValidateSet('low', 'medium', 'high', 'xhigh', 'max')][string]$Effort = 'max',

    # 'auto' resolves the strongest api-capable model from models_cache.json.
    [string]$Model = 'auto',

    # Dev packets can legitimately run long. Hard wall-clock cap.
    [int]$TimeoutSec = 3600,

    # Disable the fast speed tier (service_tier=priority) to save quota.
    [switch]$NoFast,

    # Run codex headless with no visible window (the pre-2026-08 behavior).
    # Default is a visible Windows Terminal tab streaming codex's work live.
    [switch]$Hidden,

    # Grant codex REAL write access. On Windows codex 0.147 has no
    # write-capable sandbox (`-s workspace-write` is silently downgraded to
    # read-only and file edits are refused), so writing requires
    # --dangerously-bypass-approvals-and-sandbox: NO sandbox, NO approvals.
    # The dispatcher decides per work packet: only pass this when the packet
    # genuinely needs codex to edit files, with a prompt spec that confines it
    # to its exclusive file list. Default is read-only.
    [switch]$AllowWrite
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Off

$FallbackModel = 'gpt-5.6-sol'

function Write-Status {
    param([hashtable]$Data, [int]$ExitCode)
    ($Data | ConvertTo-Json -Depth 5 -Compress)
    exit $ExitCode
}

# PS 5.1 wraps a native exe's stderr in ErrorRecords, which under
# `$ErrorActionPreference = 'Stop'` makes a harmless diagnostic line terminate
# the script. `codex --version` on a stale CLI prints exactly such a line.
function Invoke-Cli {
    param([string]$Exe, [string[]]$CliArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $raw = & $Exe @CliArgs 2>&1
        $out = @($raw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { [string]$_ })
        return @{ Code = $LASTEXITCODE; Out = $out }
    } catch {
        return @{ Code = -1; Out = @() }
    } finally {
        $ErrorActionPreference = $prev
    }
}

function Get-CodexHome {
    if ($env:CODEX_HOME -and (Test-Path $env:CODEX_HOME)) { return $env:CODEX_HOME }
    return (Join-Path $env:USERPROFILE '.codex')
}

# The desktop app ships a hashed-path binary that is usually NEWER than the one
# on PATH, and the server rejects frontier models from stale CLIs. Always pick
# by reported version, never by PATH order.
function Resolve-CodexExe {
    $candidates = New-Object System.Collections.Generic.List[string]

    $onPath = Get-Command codex -ErrorAction SilentlyContinue
    if ($onPath) { $candidates.Add($onPath.Source) }

    foreach ($root in @(
            (Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'),
            (Join-Path $env:LOCALAPPDATA 'Programs\OpenAI\Codex\bin')
        )) {
        if (Test-Path $root) {
            Get-ChildItem -Path $root -Recurse -Filter 'codex.exe' -ErrorAction SilentlyContinue |
                ForEach-Object { $candidates.Add($_.FullName) }
        }
    }

    $best = $null; $bestVer = $null
    foreach ($c in ($candidates | Sort-Object -Unique)) {
        $r = Invoke-Cli $c @('--version')
        if ($r.Code -ne 0 -or $r.Out.Count -eq 0) { continue }
        $m = [regex]::Match(($r.Out -join ' '), '(\d+)\.(\d+)\.(\d+)')
        if (-not $m.Success) { continue }
        $v = [version]::new([int]$m.Groups[1].Value, [int]$m.Groups[2].Value, [int]$m.Groups[3].Value)
        if (-not $bestVer -or $v -gt $bestVer) { $bestVer = $v; $best = $c }
    }
    return @{ Path = $best; Version = $bestVer }
}

# models_cache.json ranks models by `priority` (1 = strongest). Only consider
# ones the API actually serves and that the picker lists.
function Resolve-Model {
    param([string]$Requested)
    if ($Requested -and $Requested -ne 'auto') { return $Requested }

    $cache = Join-Path (Get-CodexHome) 'models_cache.json'
    if (-not (Test-Path $cache)) { return $FallbackModel }

    $json = $null
    try { $json = Get-Content $cache -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $FallbackModel }
    if (-not $json.models) { return $FallbackModel }

    $pick = $json.models |
        Where-Object { $_.supported_in_api -eq $true -and $_.visibility -eq 'list' -and $_.priority } |
        Sort-Object { [int]$_.priority } |
        Select-Object -First 1

    if ($pick -and $pick.slug) { return $pick.slug }
    return $FallbackModel
}

function Resolve-Effort {
    param([string]$Model, [string]$Wanted)

    $cache = Join-Path (Get-CodexHome) 'models_cache.json'
    if (-not (Test-Path $cache)) { return $Wanted }

    $json = $null
    try { $json = Get-Content $cache -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $Wanted }

    $entry = $json.models | Where-Object { $_.slug -eq $Model } | Select-Object -First 1
    if (-not $entry -or -not $entry.supported_reasoning_levels) { return $Wanted }

    $supported = @($entry.supported_reasoning_levels.effort)
    if ($supported -contains $Wanted) { return $Wanted }

    foreach ($fallback in @('max', 'xhigh', 'high', 'medium', 'low')) {
        if ($supported -contains $fallback) { return $fallback }
    }
    return $Wanted
}

# --- Preflight --------------------------------------------------------------
if (-not (Test-Path $PromptFile)) {
    Write-Status @{ ok = $false; error = "PromptFile not found: $PromptFile" } 2
}
if (-not (Test-Path $RepoRoot)) {
    Write-Status @{ ok = $false; error = "RepoRoot not found: $RepoRoot" } 2
}
if ($SchemaFile) {
    if (-not (Test-Path $SchemaFile)) {
        Write-Status @{ ok = $false; error = "SchemaFile not found: $SchemaFile" } 2
    }
    # codex rejects a schema file that starts with a UTF-8 BOM ("expected value
    # at line 1 column 1"). Strip it rather than failing an hour into a run.
    $schemaBytes = [System.IO.File]::ReadAllBytes($SchemaFile)
    if ($schemaBytes.Length -ge 3 -and $schemaBytes[0] -eq 0xEF -and $schemaBytes[1] -eq 0xBB -and $schemaBytes[2] -eq 0xBF) {
        [System.IO.File]::WriteAllBytes($SchemaFile, $schemaBytes[3..($schemaBytes.Length - 1)])
    }
}

# The visible-window runner executes with a different CWD, so every path that
# crosses that boundary must be absolute.
$PromptFile = (Resolve-Path -LiteralPath $PromptFile).Path
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
if ($SchemaFile) { $SchemaFile = (Resolve-Path -LiteralPath $SchemaFile).Path }
if (-not [System.IO.Path]::IsPathRooted($OutFile)) { $OutFile = Join-Path (Get-Location).Path $OutFile }

$resolved = Resolve-CodexExe
if (-not $resolved.Path) {
    Write-Status @{ ok = $false; error = 'codex.exe not found (checked PATH, %LOCALAPPDATA%\OpenAI\Codex\bin, %LOCALAPPDATA%\Programs\OpenAI\Codex\bin)' } 3
}

$useModel = Resolve-Model -Requested $Model
$useEffort = Resolve-Effort -Model $useModel -Wanted $Effort

$outDir = Split-Path -Parent $OutFile
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
if (Test-Path $OutFile) { Remove-Item $OutFile -Force }

$stdoutFile = "$OutFile.stdout.log"
$stderrFile = "$OutFile.stderr.log"

# --- Build args -------------------------------------------------------------
$argList = @(
    'exec',
    '--ignore-user-config',
    '-m', $useModel,
    '-c', "model_reasoning_effort=`"$useEffort`""
)
if ($AllowWrite) {
    # The bypass flag replaces both the sandbox and the approval policy; it is
    # the only way codex can edit files on Windows (see -AllowWrite doc).
    $argList += '--dangerously-bypass-approvals-and-sandbox'
} else {
    $argList += @('-c', 'approval_policy="never"', '-s', 'read-only')
}
$argList += @(
    '--skip-git-repo-check',
    '-C', $RepoRoot,
    '-o', $OutFile,
    '-'
)
if ($SchemaFile) {
    $argList = $argList[0..($argList.Length - 4)] + @('--output-schema', $SchemaFile) + $argList[($argList.Length - 3)..($argList.Length - 1)]
}
if (-not $NoFast) {
    # service_tier=priority is what the picker labels "Fast" (1.5x speed).
    $argList = $argList[0..1] + @('-c', 'service_tier="priority"') + $argList[2..($argList.Length - 1)]
}

# Start-Process joins ArgumentList with plain spaces, so any argument holding a
# space would be split into two. Quote those before handing them over.
$quoted = $argList | ForEach-Object {
    if ($_ -match '\s' -and -not $_.StartsWith('"')) { '"' + $_ + '"' } else { $_ }
}

# --- Run --------------------------------------------------------------------
# Default: the codex turn runs inside a VISIBLE terminal window (Windows
# Terminal tab when available, plain PowerShell window otherwise) so the user
# can watch the agent work live. codex-window-runner.ps1 streams stdout/stderr
# to that console while mirroring them into the log files, then reports the
# exit code through a done-file. -Hidden restores the headless behavior; the
# wrapper also falls back to headless if the window fails to come up.
$sw = [Diagnostics.Stopwatch]::StartNew()

function Invoke-CodexHidden {
    $p = Start-Process -FilePath $resolved.Path -ArgumentList $quoted `
        -RedirectStandardInput $PromptFile `
        -RedirectStandardOutput $stdoutFile `
        -RedirectStandardError $stderrFile `
        -NoNewWindow -PassThru

    # Touching .Handle while the process is alive makes .NET cache the native
    # handle; without it ExitCode stays null after exit.
    $null = $p.Handle

    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        try { $p.Kill() } catch { }
        try { $p.WaitForExit(10000) | Out-Null } catch { }
        return @{ Exit = $null; TimedOut = $true }
    }
    # The timed overload returns before async streams drain and leaves ExitCode
    # unpopulated; the argument-less call flushes both.
    try { $p.WaitForExit() } catch { }
    return @{ Exit = $p.ExitCode; TimedOut = $false }
}

$run = $null
if (-not $Hidden) {
    $doneFile = "$OutFile.done"
    $pidFile = "$OutFile.pid"
    foreach ($f in @($doneFile, $pidFile)) { if (Test-Path $f) { Remove-Item $f -Force } }

    $runner = Join-Path $PSScriptRoot 'codex-window-runner.ps1'
    $argsB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @($argList) -Compress)))
    $title = 'Codex ' + [System.IO.Path]::GetFileNameWithoutExtension($OutFile)
    $runnerArgs = (@(
            '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-File', ('"' + $runner + '"'),
            '-CodexExe', ('"' + $resolved.Path + '"'),
            '-ArgsB64', $argsB64,
            '-PromptFile', ('"' + $PromptFile + '"'),
            '-StdoutFile', ('"' + $stdoutFile + '"'),
            '-StderrFile', ('"' + $stderrFile + '"'),
            '-DoneFile', ('"' + $doneFile + '"'),
            '-PidFile', ('"' + $pidFile + '"'),
            '-Title', ('"' + $title + '"')
        ) -join ' ')

    $launched = $false
    try {
        $wt = Get-Command wt.exe -ErrorAction SilentlyContinue
        if ($wt) {
            # -w 0 groups parallel codex turns as tabs of one terminal window.
            Start-Process -FilePath $wt.Source -ArgumentList ('-w 0 new-tab --title "' + $title + '" powershell ' + $runnerArgs)
        } else {
            Start-Process -FilePath 'powershell.exe' -ArgumentList $runnerArgs
        }
        $launched = $true
    } catch { }

    if ($launched) {
        # The runner writes its PID first thing; no PID file within 60s means
        # the window never came up - fall back to the headless path below.
        $probe = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path $pidFile) -and $probe.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 500 }
        if (Test-Path $pidFile) {
            while (-not (Test-Path $doneFile) -and $sw.Elapsed.TotalSeconds -lt $TimeoutSec) { Start-Sleep -Milliseconds 500 }
            if (Test-Path $doneFile) {
                Start-Sleep -Milliseconds 300 # let the runner finish flushing the logs
                $code = -1
                try { $code = [int]((Get-Content $doneFile -ErrorAction Stop | Select-Object -First 1)) } catch { }
                $run = @{ Exit = $code; TimedOut = $false }
            } else {
                $runnerPid = $null
                try { $runnerPid = [int]((Get-Content $pidFile -ErrorAction Stop | Select-Object -First 1)) } catch { }
                if ($runnerPid) { $null = Invoke-Cli 'taskkill' @('/T', '/F', '/PID', "$runnerPid") }
                $run = @{ Exit = $null; TimedOut = $true }
            }
        }
    }
}
if (-not $run) { $run = Invoke-CodexHidden }
$sw.Stop()

$status = @{
    model      = $useModel
    effort     = $useEffort
    fast       = (-not $NoFast.IsPresent)
    sandbox    = $(if ($AllowWrite) { 'bypass (full access, no approvals)' } else { 'read-only' })
    codex_exe  = $resolved.Path
    codex_ver  = "$($resolved.Version)"
    elapsed_s  = [int]$sw.Elapsed.TotalSeconds
    out        = $OutFile
    stdout_log = $stdoutFile
    stderr_log = $stderrFile
}

if ($run.TimedOut) {
    $status.ok = $false
    $status.error = "timed out after ${TimeoutSec}s"
    Write-Status $status 4
}

if (-not (Test-Path $OutFile)) {
    $tail = ''
    if (Test-Path $stderrFile) { $tail = (Get-Content $stderrFile -Tail 15 -ErrorAction SilentlyContinue) -join ' | ' }
    $status.ok = $false
    $status.error = "codex produced no output file (exit $($run.Exit)). stderr tail: $tail"
    Write-Status $status 5
}

if ($SchemaFile) {
    # -Encoding UTF8 matters: output may contain CJK, and PS 5.1's default
    # codepage read would mangle it into invalid JSON.
    $payload = Get-Content $OutFile -Raw -Encoding UTF8
    try {
        $null = $payload | ConvertFrom-Json
    } catch {
        $status.ok = $false
        $status.error = "output file is not valid JSON: $($_.Exception.Message)"
        Write-Status $status 6
    }
}

$status.ok = $true
$status.exit_code = $run.Exit
Write-Status $status 0
