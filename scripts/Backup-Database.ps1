<#
.SYNOPSIS
    Performs a consistent compressed database backup using pg_dump.
.PARAMETER OutputDirectory
    Directory where timestamped backup files will be placed.
.PARAMETER ConnectionString
    PostgreSQL connection string (optional; defaults to ConnectionStrings__Tracking or localhost).
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../backups'),
    [string]$ConnectionString = $env:ConnectionStrings__Tracking
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $OutputDirectory)) {
    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
}

$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$fileName = "event_tracking_backup_$timestamp.dump"
$filePath = Join-Path $OutputDirectory $fileName

Write-Host "Initiating backup to: $filePath"

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $hostName = if ($env:PGHOST) { $env:PGHOST } else { "localhost" }
    $port = if ($env:PGPORT) { $env:PGPORT } else { "5432" }
    $user = if ($env:PGUSER) { $env:PGUSER } else { "postgres" }
    $db = if ($env:PGDATABASE) { $env:PGDATABASE } else { "event_tracking" }

    & pg_dump -h $hostName -p $port -U $user -d $db -Fc -f $filePath
} else {
    & pg_dump --dbname=$ConnectionString -Fc -f $filePath
}

if ($LASTEXITCODE -ne 0) {
    throw "pg_dump failed with exit code $LASTEXITCODE"
}

$fileItem = Get-Item $filePath
$hash = (Get-FileHash -Path $filePath -Algorithm SHA256).Hash

Write-Host "Backup completed successfully."
Write-Host "  File: $filePath"
Write-Host "  Size: $($fileItem.Length) bytes"
Write-Host "  SHA-256: $hash"

return [PSCustomObject]@{
    Path = $filePath
    Size = $fileItem.Length
    Hash = $hash
    Timestamp = $timestamp
}
