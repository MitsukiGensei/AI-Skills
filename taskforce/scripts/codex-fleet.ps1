#requires -Version 5.1
<#
.SYNOPSIS
    Launch and supervise a FLEET of parallel Codex CLI agents for the
    `taskforce` skill. One call = N codex agents working at the same time.

.DESCRIPTION
    codex-dev.ps1 runs exactly ONE codex turn and blocks until it finishes.
    Driving several of those by hand meant, in practice, that only one codex
    ever ran at a time. codex-fleet.ps1 is the dispatcher on top of it:

        - reads a JSON manifest describing K work packets
        - starts them concurrently up to -MaxParallel (hard cap 6)
        - gives every packet its OWN output directory (no packet can read
          another's result by accident; review packets are sealed away from
          the task directory by default)
        - keeps a live fleet index file so a second process (the PM session)
          can poll progress without touching the running children
        - -Collect moves the finished outputs into the task directory in one
          shot, AFTER everybody is done (the "physically out of reach until
          both are finished" rule from SKILL.md 5.1)

    Every child inherits codex-dev.ps1's defaults: strongest api-capable model
    (gpt-5.6-sol), reasoning effort `ultra`, thinking stream on, fast tier on.

    THIS SCRIPT BLOCKS while supervising. The PM must call it with the Bash
    tool's run_in_background:true and poll with -Status.

.PARAMETER Manifest
    JSON file:

    {
      "repoRoot":   "<repo root>",
      "workDir":    "<repo root>\\.taskforce\\<slug>\\codex",
      "maxParallel": 3,
      "packets": [
        { "id": "wp-03", "role": "dev",    "promptFile": "...\\wp-03.md",
          "allowWrite": true,  "timeoutSec": 3600 },
        { "id": "rv-01", "role": "review", "promptFile": "...\\rv-01.md",
          "schemaFile": "...\\schemas\\review-findings.schema.json" }
      ]
    }

    Packet fields: id (required, [A-Za-z0-9._-]), promptFile (required),
    role (dev|review|design|probe, default dev), allowWrite (default false),
    access (auto|sandbox-ro|prompt-ro|write, default auto - see codex-dev.ps1's
    ACCESS MODEL; on codex >= 0.150 a non-writing packet resolves to prompt-ro
    because the read-only sandbox blocks every child process there),
    schemaFile, timeoutSec (default 3600), outDir (default: per-role, below),
    repoRoot (default: manifest repoRoot).

    repoRoot MUST be the absolute path of the checkout the PM session is
    running in. With 池化 worktree / CoW worktrees several checkouts of the same
    stream coexist on one machine (each its own P4 client, resolved via the
    .p4config under that root), so never copy repoRoot from another task's
    manifest - codex would edit files in a different client.

.EXAMPLE
    # launch (from Bash with run_in_background:true)
    .\codex-fleet.ps1 -Manifest .taskforce\slug\codex\fleet-wave1.json

.EXAMPLE
    # poll from the PM session
    .\codex-fleet.ps1 -Status -FleetFile .taskforce\slug\codex\fleet-<id>.json

.EXAMPLE
    # after everybody finished, publish the sealed review outputs
    .\codex-fleet.ps1 -Collect -FleetFile ... -Into .taskforce\slug\xreview
#>
[CmdletBinding(DefaultParameterSetName = 'Launch')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Launch')][string]$Manifest,

    # Concurrent codex turns. Counts against the wave-wide agent budget in
    # SKILL.md 4.4 (<= 6 agents in flight, codex included).
    [Parameter(ParameterSetName = 'Launch')][ValidateRange(1, 6)][int]$MaxParallel = 0,

    # Where sealed (review/design) outputs live until -Collect. Default is a
    # per-fleet folder under TEMP, i.e. outside the task directory entirely.
    [Parameter(ParameterSetName = 'Launch')][string]$SealedRoot,

    # Pass through to codex-dev.ps1: no visible terminal window per agent.
    [Parameter(ParameterSetName = 'Launch')][switch]$Hidden,

    # Disable the fast (priority) service tier for every packet.
    [Parameter(ParameterSetName = 'Launch')][switch]$NoFast,

    [Parameter(ParameterSetName = 'Launch')][int]$PollMs = 2000,

    [Parameter(Mandatory = $true, ParameterSetName = 'Status')][switch]$Status,
    [Parameter(Mandatory = $true, ParameterSetName = 'Collect')][switch]$Collect,

    [Parameter(Mandatory = $true, ParameterSetName = 'Status')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Collect')][string]$FleetFile,

    [Parameter(Mandatory = $true, ParameterSetName = 'Collect')][string]$Into
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Off

$Utf8NoBom = New-Object System.Text.UTF8Encoding $false

function Write-Json {
    param($Data, [int]$ExitCode = 0)
    ($Data | ConvertTo-Json -Depth 8 -Compress)
    exit $ExitCode
}

function Save-Fleet {
    param($Fleet, [string]$Path)
    $tmp = "$Path.tmp"
    [System.IO.File]::WriteAllText($tmp, ($Fleet | ConvertTo-Json -Depth 8), $Utf8NoBom)
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

function Read-Fleet {
    param([string]$Path)
    if (-not (Test-Path $Path)) { Write-Json @{ ok = $false; error = "fleet file not found: $Path" } 2 }
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Test-Alive {
    param($ProcId)
    if (-not $ProcId) { return $false }
    return [bool](Get-Process -Id ([int]$ProcId) -ErrorAction SilentlyContinue)
}

# codex-dev.ps1 prints one line of JSON on stdout; we capture it per packet.
function Read-ChildStatus {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    $raw = (Get-Content -LiteralPath $Path -Raw -Encoding UTF8)
    if (-not $raw) { return $null }
    $line = ($raw -split "`n" | Where-Object { $_.Trim().StartsWith('{') } | Select-Object -Last 1)
    if (-not $line) { return $null }
    try { return ($line | ConvertFrom-Json) } catch { return $null }
}

function Get-Summary {
    param($Fleet)
    $packets = @()
    foreach ($p in $Fleet.packets) {
        $packets += [ordered]@{
            id         = $p.id
            role       = $p.role
            access     = $p.access
            state      = $p.state
            exit       = $p.exit
            ok         = $p.ok
            error      = $p.error
            elapsed_s  = $p.elapsed_s
            out        = $p.outFile
            status     = $p.statusFile
            stdout_log = "$($p.outFile).stdout.log"
            stderr_log = "$($p.outFile).stderr.log"
        }
    }
    return [ordered]@{
        ok        = @($Fleet.packets | Where-Object { $_.state -eq 'failed' }).Count -eq 0
        fleet_id  = $Fleet.fleetId
        fleetFile = $Fleet.fleetFile
        total     = @($Fleet.packets).Count
        done      = @($Fleet.packets | Where-Object { $_.state -eq 'done' }).Count
        failed    = @($Fleet.packets | Where-Object { $_.state -eq 'failed' }).Count
        running   = @($Fleet.packets | Where-Object { $_.state -eq 'running' }).Count
        pending   = @($Fleet.packets | Where-Object { $_.state -eq 'pending' }).Count
        packets   = $packets
    }
}

# --- -Status ----------------------------------------------------------------
if ($Status) {
    $fleet = Read-Fleet $FleetFile
    # Derive live state without mutating the file the supervisor owns.
    foreach ($p in $fleet.packets) {
        if ($p.state -eq 'running' -and -not (Test-Alive $p.procId)) {
            $st = Read-ChildStatus $p.statusFile
            if ($st) {
                $p.state = $(if ($st.ok) { 'done' } else { 'failed' })
                $p.ok = $st.ok; $p.error = $st.error; $p.elapsed_s = $st.elapsed_s
            } else {
                $p.state = 'failed'; $p.error = 'child exited without status JSON'
            }
        }
    }
    Write-Json (Get-Summary $fleet) 0
}

# --- -Collect ---------------------------------------------------------------
if ($Collect) {
    $fleet = Read-Fleet $FleetFile
    $unfinished = @($fleet.packets | Where-Object { $_.state -notin @('done', 'failed') })
    if ($unfinished.Count -gt 0) {
        Write-Json @{ ok = $false; error = "fleet still running: $(($unfinished | ForEach-Object { $_.id }) -join ',')" } 3
    }
    if (-not (Test-Path $Into)) { New-Item -ItemType Directory -Force -Path $Into | Out-Null }
    $logDir = Join-Path $Into 'logs'
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }

    $moved = @()
    foreach ($p in $fleet.packets) {
        $dest = Join-Path $Into ("$($p.id).md")
        if (Test-Path $p.outFile) { Copy-Item -LiteralPath $p.outFile -Destination $dest -Force }
        foreach ($suffix in @('.stdout.log', '.stderr.log')) {
            $src = "$($p.outFile)$suffix"
            if (Test-Path $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $logDir "$($p.id)$suffix") -Force }
        }
        if (Test-Path $p.statusFile) { Copy-Item -LiteralPath $p.statusFile -Destination (Join-Path $logDir "$($p.id).status.json") -Force }
        $moved += [ordered]@{ id = $p.id; state = $p.state; file = $dest; exists = (Test-Path $dest) }
    }
    Write-Json @{ ok = $true; into = $Into; collected = $moved } 0
}

# --- -Launch ----------------------------------------------------------------
if (-not (Test-Path $Manifest)) { Write-Json @{ ok = $false; error = "manifest not found: $Manifest" } 2 }

$mf = $null
try { $mf = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { Write-Json @{ ok = $false; error = "manifest is not valid JSON: $($_.Exception.Message)" } 2 }

if (-not $mf.packets -or @($mf.packets).Count -eq 0) {
    Write-Json @{ ok = $false; error = 'manifest has no packets' } 2
}

$repoRootDefault = $mf.repoRoot
if (-not $repoRootDefault) { Write-Json @{ ok = $false; error = 'manifest.repoRoot is required' } 2 }
if (-not (Test-Path $repoRootDefault)) { Write-Json @{ ok = $false; error = "repoRoot not found: $repoRootDefault" } 2 }
$repoRootDefault = (Resolve-Path -LiteralPath $repoRootDefault).Path

$workDir = $mf.workDir
if (-not $workDir) { $workDir = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $Manifest).Path) 'fleet' }
if (-not (Test-Path $workDir)) { New-Item -ItemType Directory -Force -Path $workDir | Out-Null }
$workDir = (Resolve-Path -LiteralPath $workDir).Path

if ($MaxParallel -le 0) {
    $MaxParallel = $(if ($mf.maxParallel) { [int]$mf.maxParallel } else { 3 })
}
if ($MaxParallel -gt 6) { $MaxParallel = 6 }   # SKILL.md 4.4 wave budget

if (-not $SealedRoot) { $SealedRoot = Join-Path $env:TEMP 'taskforce-sealed' }

$fleetId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 4))
$fleetFilePath = Join-Path $workDir "fleet-$fleetId.json"
$devScript = Join-Path $PSScriptRoot 'codex-dev.ps1'
if (-not (Test-Path $devScript)) { Write-Json @{ ok = $false; error = "codex-dev.ps1 not found next to codex-fleet.ps1" } 3 }

$packets = @()
$seenIds = @{}
$seenDirs = @{}
foreach ($raw in $mf.packets) {
    $id = [string]$raw.id
    if (-not $id -or $id -notmatch '^[A-Za-z0-9._-]{1,64}$') {
        Write-Json @{ ok = $false; error = "bad packet id: '$id' (allowed: A-Za-z0-9._-)" } 2
    }
    if ($seenIds.ContainsKey($id)) { Write-Json @{ ok = $false; error = "duplicate packet id: $id" } 2 }
    $seenIds[$id] = $true

    $role = $(if ($raw.role) { [string]$raw.role } else { 'dev' })
    $prompt = [string]$raw.promptFile
    if (-not $prompt -or -not (Test-Path $prompt)) {
        Write-Json @{ ok = $false; error = "packet ${id}: promptFile not found: $prompt" } 2
    }
    $prompt = (Resolve-Path -LiteralPath $prompt).Path

    $schema = $null
    if ($raw.schemaFile) {
        if (-not (Test-Path $raw.schemaFile)) { Write-Json @{ ok = $false; error = "packet ${id}: schemaFile not found: $($raw.schemaFile)" } 2 }
        $schema = (Resolve-Path -LiteralPath $raw.schemaFile).Path
    }

    $pRepo = $(if ($raw.repoRoot) { (Resolve-Path -LiteralPath $raw.repoRoot).Path } else { $repoRootDefault })

    # Sealed-by-default for anything whose value depends on NOT having seen the
    # other agents' conclusions (SKILL.md 5.1 / UCL 2026: reachable paths get
    # reached - 80% of sealed runs opened a decoy they were never told about).
    $outDir = [string]$raw.outDir
    if (-not $outDir) {
        if ($role -in @('review', 'design')) { $outDir = Join-Path (Join-Path $SealedRoot $fleetId) $id }
        else { $outDir = Join-Path $workDir $id }
    }
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    $outDir = (Resolve-Path -LiteralPath $outDir).Path
    $dirKey = $outDir.ToLowerInvariant()
    if ($seenDirs.ContainsKey($dirKey)) { Write-Json @{ ok = $false; error = "packets $($seenDirs[$dirKey]) and $id share outDir $outDir - isolation requires one directory per packet" } 2 }
    $seenDirs[$dirKey] = $id

    $access = $(if ($raw.access) { [string]$raw.access } else { 'auto' })
    if ($access -notin @('auto', 'sandbox-ro', 'prompt-ro', 'write')) {
        Write-Json @{ ok = $false; error = "packet ${id}: bad access '$access'" } 2
    }
    if ([bool]$raw.allowWrite -and $access -notin @('auto', 'write')) {
        Write-Json @{ ok = $false; error = "packet ${id}: allowWrite conflicts with access '$access'" } 2
    }

    $packets += [ordered]@{
        id         = $id
        role       = $role
        promptFile = $prompt
        schemaFile = $schema
        repoRoot   = $pRepo
        allowWrite = [bool]$raw.allowWrite
        access     = $access
        timeoutSec = $(if ($raw.timeoutSec) { [int]$raw.timeoutSec } else { 3600 })
        outDir     = $outDir
        outFile    = (Join-Path $outDir 'out.md')
        statusFile = (Join-Path $outDir 'status.json')
        childErr   = (Join-Path $outDir 'child.err.log')
        state      = 'pending'
        procId     = $null
        exit       = $null
        ok         = $null
        error      = $null
        elapsed_s  = $null
        startedAt  = $null
        endedAt    = $null
    }
}

$fleet = [ordered]@{
    fleetId     = $fleetId
    fleetFile   = $fleetFilePath
    manifest    = (Resolve-Path -LiteralPath $Manifest).Path
    workDir     = $workDir
    sealedRoot  = $SealedRoot
    maxParallel = $MaxParallel
    startedAt   = (Get-Date).ToString('o')
    packets     = $packets
}
Save-Fleet $fleet $fleetFilePath

# --- supervise --------------------------------------------------------------
$sw = [Diagnostics.Stopwatch]::StartNew()

function Start-Packet {
    param($P)
    $childArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', ('"' + $devScript + '"'),
        '-PromptFile', ('"' + $P.promptFile + '"'),
        '-OutFile', ('"' + $P.outFile + '"'),
        '-RepoRoot', ('"' + $P.repoRoot + '"'),
        '-TimeoutSec', $P.timeoutSec
    )
    if ($P.schemaFile) { $childArgs += @('-SchemaFile', ('"' + $P.schemaFile + '"')) }
    if ($P.access -and $P.access -ne 'auto') { $childArgs += @('-Access', $P.access) }
    elseif ($P.allowWrite) { $childArgs += '-AllowWrite' }
    if ($Hidden) { $childArgs += '-Hidden' }
    if ($NoFast) { $childArgs += '-NoFast' }

    $proc = Start-Process -FilePath 'powershell.exe' -ArgumentList $childArgs `
        -RedirectStandardOutput $P.statusFile `
        -RedirectStandardError $P.childErr `
        -WindowStyle Hidden -PassThru
    $P.procId = $proc.Id
    $P.state = 'running'
    $P.startedAt = (Get-Date).ToString('o')
}

function Complete-Packet {
    param($P)
    $st = Read-ChildStatus $P.statusFile
    $P.endedAt = (Get-Date).ToString('o')
    if ($st) {
        $P.ok = [bool]$st.ok
        $P.exit = $st.exit_code
        $P.error = $st.error
        $P.elapsed_s = $st.elapsed_s
        $P.state = $(if ($st.ok) { 'done' } else { 'failed' })
    } else {
        $tail = ''
        if (Test-Path $P.childErr) { $tail = ((Get-Content $P.childErr -Tail 10 -ErrorAction SilentlyContinue) -join ' | ') }
        $P.ok = $false
        $P.state = 'failed'
        $P.error = "child produced no status JSON. stderr tail: $tail"
    }
}

while ($true) {
    $changed = $false

    foreach ($p in $fleet.packets) {
        if ($p.state -eq 'running' -and -not (Test-Alive $p.procId)) {
            Complete-Packet $p
            $changed = $true
        }
    }

    $running = @($fleet.packets | Where-Object { $_.state -eq 'running' }).Count
    foreach ($p in $fleet.packets) {
        if ($running -ge $MaxParallel) { break }
        if ($p.state -eq 'pending') {
            Start-Packet $p
            $running++
            $changed = $true
        }
    }

    if ($changed) { Save-Fleet $fleet $fleetFilePath }

    $left = @($fleet.packets | Where-Object { $_.state -in @('pending', 'running') }).Count
    if ($left -eq 0) { break }
    Start-Sleep -Milliseconds $PollMs
}

$sw.Stop()
$fleet.endedAt = (Get-Date).ToString('o')
$fleet.elapsed_s = [int]$sw.Elapsed.TotalSeconds
Save-Fleet $fleet $fleetFilePath

$summary = Get-Summary $fleet
$summary.elapsed_s = [int]$sw.Elapsed.TotalSeconds
Write-Json $summary $(if ($summary.failed -gt 0) { 1 } else { 0 })
