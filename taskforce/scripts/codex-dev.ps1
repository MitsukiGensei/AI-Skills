#requires -Version 5.1
<#
.SYNOPSIS
    Run one non-interactive Codex CLI turn as a DEVELOPER subagent (may edit
    the repo). Used by the `taskforce` skill.

.DESCRIPTION
    Adapted from a companion review skill's codex helper (that one is locked read-only
    for reviewing). Differences:
        - access is per-packet and VERSION AWARE (-Access, see below)
        - output schema is optional; -OutFile receives the final message

    ACCESS MODEL (why it is not just `-s read-only`):
        codex >= 0.150 on Windows rejects EVERY child process under the
        read-only sandbox - not just writes. Verified 2026-08-31 on 0.150.1:
        a read-only turn asking for `dir` came back with
        `rejected: blocked by policy`. A reviewer in that mode cannot grep,
        cannot read the diff, and still exits 0 with a confident-looking empty
        report. That silent hollow review is worse than no review.

        So the sandbox is not the boundary any more; the prompt is:
          sandbox-ro : real read-only sandbox. Correct on codex < 0.150,
                       blind on >= 0.150 -> the script REFUSES it there.
          prompt-ro  : sandbox bypassed, a READ-ONLY CONTRACT is prepended to
                       the prompt, and the packet produces text only.
          write      : sandbox bypassed, WRITE CONTRACT prepended (only the
                       packet's exclusive file list may be touched).
          auto       : write if -AllowWrite, else sandbox-ro on < 0.150 and
                       prompt-ro on >= 0.150.
        Both contracts are injected by the wrapper itself, so a hand-written
        packet prompt cannot forget them. -NoContract disables the injection.
    Everything else is the same battle-tested setup: newest codex.exe resolved
    by version, strongest api-capable model from models_cache.json (priority 1
    = gpt-5.6-sol today), reasoning effort `ultra`, thinking stream on, fast
    tier (service_tier=priority), prompt piped via stdin, --ignore-user-config
    to drop MCP noise (auth still works, AGENTS.md still loads).

    NOTE on `ultra`: an older comment here claimed it must never be used under
    `exec`. Re-verified 2026-08-31 on codex-cli 0.144.2 + gpt-5.6-sol: a plain
    `exec` turn at effort=ultra completes normally (exit 0, output file
    written). Resolve-Effort still degrades to max/xhigh/... for any model
    whose models_cache entry does not list ultra.

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

    # `ultra` = deepest reasoning tier the picker offers; degraded per model
    # by Resolve-Effort when models_cache.json does not list it.
    [ValidateSet('low', 'medium', 'high', 'xhigh', 'max', 'ultra')][string]$Effort = 'ultra',

    # 'auto' resolves the strongest api-capable model from models_cache.json.
    [string]$Model = 'auto',

    # Dev packets can legitimately run long. Hard wall-clock cap.
    [int]$TimeoutSec = 3600,

    # Disable the fast speed tier (service_tier=priority) to save quota.
    [switch]$NoFast,

    # Turn OFF the reasoning stream. Default ON: detailed reasoning summaries
    # plus raw agent reasoning, so the live window and the stdout log show what
    # codex is actually thinking (that log is the PM's only way to audit a
    # write-enabled packet after the fact).
    [switch]$NoThinking,

    # Run codex headless with no visible window (the pre-2026-08 behavior).
    # Default is a visible Windows Terminal tab streaming codex's work live.
    [switch]$Hidden,

    # Grant codex REAL write access. On Windows codex has no write-capable
    # sandbox (`-s workspace-write` is silently downgraded to read-only and
    # file edits are refused), so writing requires
    # --dangerously-bypass-approvals-and-sandbox: NO sandbox, NO approvals.
    # The dispatcher decides per work packet: only pass this when the packet
    # genuinely needs codex to edit files, with a prompt spec that confines it
    # to its exclusive file list. Shorthand for -Access write.
    [switch]$AllowWrite,

    # See ACCESS MODEL in the header. 'auto' is version aware.
    [ValidateSet('auto', 'sandbox-ro', 'prompt-ro', 'write')][string]$Access = 'auto',

    # Do not prepend the read-only / write contract to the prompt. Only for
    # packets whose prompt already carries an equivalent contract.
    [switch]$NoContract
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

    foreach ($fallback in @('ultra', 'max', 'xhigh', 'high', 'medium', 'low')) {
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

# --- Access resolution ------------------------------------------------------
# 0.150.0 is where the read-only sandbox started rejecting child processes.
$roSandboxBroken = ($resolved.Version -and $resolved.Version -ge [version]'0.150.0')

$useAccess = $Access
if ($useAccess -eq 'auto') {
    if ($AllowWrite) { $useAccess = 'write' }
    elseif ($roSandboxBroken) { $useAccess = 'prompt-ro' }
    else { $useAccess = 'sandbox-ro' }
} elseif ($AllowWrite -and $useAccess -ne 'write') {
    Write-Status @{ ok = $false; error = "-AllowWrite conflicts with -Access $useAccess" } 2
}

if ($useAccess -eq 'sandbox-ro' -and $roSandboxBroken) {
    # Fail loudly instead of handing back a hollow review (see ACCESS MODEL).
    Write-Status @{
        ok    = $false
        error = "codex $($resolved.Version): the read-only sandbox blocks every child process, so a sandbox-ro packet cannot grep or read anything and would return an empty-but-confident result. Use -Access prompt-ro (bypass + read-only contract)."
    } 7
}

$outDir = Split-Path -Parent $OutFile
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
if (Test-Path $OutFile) { Remove-Item $OutFile -Force }

$stdoutFile = "$OutFile.stdout.log"
$stderrFile = "$OutFile.stderr.log"

# --- Prompt contract --------------------------------------------------------
# The wrapper owns the boundary, not the packet author: a forgotten line in a
# hand-written prompt must not turn a read-only packet into a writing one.
#
# The contract TEXT lives in ../contracts/*.md, NOT in this file. Reason:
# Windows PowerShell 5.1 decodes a BOM-less .ps1 as the ANSI codepage, so any
# CJK string literal here is mangled at parse time (observed 2026-08-31: the
# injected contract reached codex as GBK-read-as-UTF8 mojibake). Reading the
# text from a file with an explicit UTF-8 decode sidesteps that, and keeps this
# script pure ASCII.
function Get-Contract {
    param([string]$Name)
    $file = Join-Path (Split-Path -Parent $PSScriptRoot) "contracts\$Name.md"
    if (Test-Path $file) {
        return ([System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)).TrimEnd() + "`r`n`r`n"
    }
    # Fallback so the boundary never silently disappears if the file is missing.
    if ($Name -eq 'write') {
        return @'
<<WRITE CONTRACT - highest priority, overrides anything below>>
Sandbox and approvals are OFF, so the boundary is this contract:
- Only files listed under this packet's EXCLUSIVE FILE LIST may be created,
  modified or deleted. Anything else is forbidden, including "while I am here"
  fixes to neighbouring problems - report those instead of touching them.
- No p4 submit, no git commit/push, no dependency installs, no global config.
- Your report must list every file path you actually changed; the caller
  diffs it, and any file outside the list sends the whole packet back.
<<END CONTRACT>>

'@
    }
    return @'
<<READ-ONLY CONTRACT - highest priority, overrides anything below>>
The sandbox is open, but your grant is READ-ONLY:
- Do not create, modify, delete or move any file. No p4 edit/add/revert/submit,
  no git add/commit/checkout/clean/push, no installs, no config changes.
- Allowed: reading files, grep/search, side-effect-free analysis commands.
- Your only artifact is the final reply itself.
- If the task seems to ask you to edit files, do not: say in the reply that the
  packet was not granted write access.
<<END CONTRACT>>

'@
}

$stdinFile = $PromptFile
if (-not $NoContract -and $useAccess -ne 'sandbox-ro') {
    $header = Get-Contract $(if ($useAccess -eq 'write') { 'write' } else { 'read-only' })
    $body = [System.IO.File]::ReadAllText($PromptFile, [System.Text.Encoding]::UTF8)
    $stdinFile = "$OutFile.prompt.txt"
    [System.IO.File]::WriteAllText($stdinFile, ($header + $body), (New-Object System.Text.UTF8Encoding $false))
}

# --- Build args -------------------------------------------------------------
# Built by straight appends on purpose: the previous index-splicing version
# ($argList[0..($argList.Length - 4)] + ...) broke the moment a new flag was
# added, because every insert point was a hardcoded offset from the tail.
$argList = @('exec', '--ignore-user-config', '-m', $useModel)
$argList += @('-c', "model_reasoning_effort=`"$useEffort`"")
if (-not $NoFast) {
    # service_tier=priority is what the picker labels "Fast" (1.5x speed).
    $argList += @('-c', 'service_tier="priority"')
}
if (-not $NoThinking) {
    $argList += @('-c', 'model_reasoning_summary="detailed"')
    $argList += @('-c', 'show_raw_agent_reasoning=true')
}
if ($useAccess -eq 'sandbox-ro') {
    $argList += @('-c', 'approval_policy="never"', '-s', 'read-only')
} else {
    # The bypass flag replaces both the sandbox and the approval policy; it is
    # the only way codex can run ANY child process on codex >= 0.150 (and the
    # only way it can edit files at all on Windows). The boundary for
    # prompt-ro packets is the injected contract above.
    $argList += '--dangerously-bypass-approvals-and-sandbox'
}
if ($SchemaFile) { $argList += @('--output-schema', $SchemaFile) }
$argList += @(
    '--skip-git-repo-check',
    '-C', $RepoRoot,
    '-o', $OutFile,
    '-'
)

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
        -RedirectStandardInput $stdinFile `
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
            '-PromptFile', ('"' + $stdinFile + '"'),
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
    thinking   = (-not $NoThinking.IsPresent)
    access     = $useAccess
    contract   = $(if ($NoContract -or $useAccess -eq 'sandbox-ro') { 'none' } elseif ($useAccess -eq 'write') { 'write-contract' } else { 'read-only-contract' })
    sandbox    = $(if ($useAccess -eq 'sandbox-ro') { 'read-only' } else { 'bypass (full access, no approvals)' })
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
