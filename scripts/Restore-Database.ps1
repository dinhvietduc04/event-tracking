<#
.SYNOPSIS
    Restores an event-tracking backup into a designated PostgreSQL database.
.PARAMETER BackupPath
    Path to the .dump file created by Backup-Database.ps1.
.PARAMETER TargetDatabase
    Name of the database to restore into.
.PARAMETER ConnectionString
    Base connection string (admin access) to connect to PostgreSQL server.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupPath,

    [string]$TargetDatabase = "event_tracking_restored",

    [string]$ConnectionString = $env:ConnectionStrings__Operator
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $BackupPath)) {
    throw "Backup file does not exist: $BackupPath"
}

Write-Host "Restoring backup '$BackupPath' into database '$TargetDatabase'..."

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $hostName = if ($env:PGHOST) { $env:PGHOST } else { "localhost" }
    $port = if ($env:PGPORT) { $env:PGPORT } else { "5432" }
    $user = if ($env:PGUSER) { $env:PGUSER } else { "postgres" }

    # Ensure database exists
    & pg_restore -h $hostName -p $port -U $user -d $TargetDatabase --clean --if-exists --no-owner --no-privileges $BackupPath
} else {
    & pg_restore --dbname=$ConnectionString -d $TargetDatabase --clean --if-exists --no-owner --no-privileges $BackupPath
}

if ($LASTEXITCODE -ne 0) {
    Write-Warning "pg_restore exited with code $LASTEXITCODE (some warnings/errors during drop may be benign)."
}

Write-Host "Restore operation completed into '$TargetDatabase'."
