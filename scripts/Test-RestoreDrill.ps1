<#
.SYNOPSIS
    Executes a disaster recovery restore drill with analytics reconciliation.
.DESCRIPTION
    Real mode (Docker PostgreSQL container available):
      1. Records baseline counts (events, event_identity, stored bytes, inbox)
         from the source database.
      2. pg_dump (custom format) to a timestamped backup file.
      3. Creates a fresh target database and pg_restores into it.
      4. Reconciles counts between source and target; fails on any mismatch.
    Simulation mode (no Docker): validates the backup/restore scripts exist
    and reports ProcedureReady without live counts.
.PARAMETER SourceDatabase
    Source database to back up (default "event_tracking").
.PARAMETER TargetDatabase
    Fresh database to restore into (dropped first if it exists).
.PARAMETER Container
    Docker PostgreSQL container name.
.PARAMETER DbUser
    PostgreSQL username inside the container.
#>
[CmdletBinding()]
param(
    [string]$SourceDatabase = "event_tracking",
    [string]$TargetDatabase = "",
    [string]$Container = "event-tracking-m2-postgres-1",
    [string]$DbUser = "event_tracking"
)

$ErrorActionPreference = 'Stop'

function Invoke-Db([string]$Database, [string]$Sql) {
    $out = docker exec $Container psql -U $DbUser -d $Database -tA -c $Sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $out" }
    return ($out | Select-Object -Last 1).Trim()
}

$dockerOk = $false
try { docker inspect $Container --format "{{.State.Status}}" 2>$null | Out-Null; $dockerOk = ($LASTEXITCODE -eq 0) } catch { }

if (-not $dockerOk) {
    Write-Warning "Container '$Container' unavailable - running in procedure-validation mode."
    foreach ($script in @('Backup-Database.ps1', 'Restore-Database.ps1')) {
        if (-not (Test-Path (Join-Path $PSScriptRoot $script))) { throw "Missing script: $script" }
    }
    return [PSCustomObject]@{ Status = "ProcedureReady"; Mode = "simulation"; Timestamp = [DateTimeOffset]::UtcNow.ToString("O") }
}

if ([string]::IsNullOrWhiteSpace($TargetDatabase)) {
    $TargetDatabase = "${SourceDatabase}_restored_$(Get-Date -Format 'yyyyMMddTHHmmss')"
}
$backupFile = "/tmp/restore_drill_$([Guid]::NewGuid().ToString('N').Substring(0, 8)).dump"

Write-Host "=== Disaster Recovery & Analytics Reconciliation Drill ==="
Write-Host "Source: $SourceDatabase -> Target: $TargetDatabase (container $Container)"

# 1. Baseline.
$base = @{
    Events = [long](Invoke-Db $SourceDatabase "SELECT count(*) FROM events;")
    Identities = [long](Invoke-Db $SourceDatabase "SELECT count(*) FROM event_identity;")
    Bytes = [long](Invoke-Db $SourceDatabase "SELECT coalesce(sum(stored_bytes),0) FROM event_identity;")
    Inbox = [long](Invoke-Db $SourceDatabase "SELECT count(*) FROM inbox;")
}
Write-Host "Baseline: events=$($base.Events) identities=$($base.Identities) bytes=$($base.Bytes) inbox=$($base.Inbox)"

# 2. Backup.
docker exec $Container pg_dump -U $DbUser -d $SourceDatabase -Fc -f $backupFile 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "pg_dump failed." }
$size = docker exec $Container stat -c %s $backupFile 2>&1 | Select-Object -Last 1
Write-Host "Backup complete: $backupFile ($size bytes)"

# 3. Restore into a fresh database.
docker exec $Container dropdb -U $DbUser --if-exists $TargetDatabase 2>&1 | Out-Null
docker exec $Container createdb -U $DbUser $TargetDatabase 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "createdb failed." }
docker exec $Container pg_restore -U $DbUser -d $TargetDatabase $backupFile 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "pg_restore failed." }

# 4. Reconcile.
$restored = @{
    Events = [long](Invoke-Db $TargetDatabase "SELECT count(*) FROM events;")
    Identities = [long](Invoke-Db $TargetDatabase "SELECT count(*) FROM event_identity;")
    Bytes = [long](Invoke-Db $TargetDatabase "SELECT coalesce(sum(stored_bytes),0) FROM event_identity;")
    Inbox = [long](Invoke-Db $TargetDatabase "SELECT count(*) FROM inbox;")
}
docker exec $Container rm -f $backupFile 2>&1 | Out-Null

$mismatches = @()
foreach ($k in $base.Keys) { if ($base[$k] -ne $restored[$k]) { $mismatches += "${k}: $($base[$k]) vs $($restored[$k])" } }
if ($mismatches.Count -gt 0) { throw "RECONCILIATION FAILED: $($mismatches -join '; ')" }

Write-Host "Reconciliation: 100% parity (events=$($restored.Events) identities=$($restored.Identities) bytes=$($restored.Bytes) inbox=$($restored.Inbox))"
return [PSCustomObject]@{
    Status = "Passed"; Mode = "measured"; SourceDatabase = $SourceDatabase; TargetDatabase = $TargetDatabase
    Events = $restored.Events; Identities = $restored.Identities; Bytes = $restored.Bytes; Inbox = $restored.Inbox
    Parity = "100%"; Timestamp = [DateTimeOffset]::UtcNow.ToString("O")
}
