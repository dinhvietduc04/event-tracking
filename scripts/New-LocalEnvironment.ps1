# Generates new local-only credentials. Existing files are never overwritten.
[CmdletBinding()]
param([string]$Directory = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$envPath = Join-Path $Directory '.env'
$keyPath = Join-Path $Directory 'demo-keys.local.json'
if ((Test-Path -LiteralPath $envPath) -or (Test-Path -LiteralPath $keyPath)) { throw 'Local configuration already exists; reuse it instead of silently changing credentials.' }
$databasePassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$lines = @("POSTGRES_PASSWORD=$databasePassword", 'POSTGRES_PORT=15433', 'API_PORT=5191', 'STORAGE_PROFILE=Distributed')
$keys = @{}
foreach ($project in @('A', 'B')) {
    foreach ($permission in @('INGEST', 'READ')) {
        $key = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key)))
        $lines += "PROJECT_${project}_${permission}_HASH=$hash"
        $keys["DEMO_${project}_${permission}"] = $key
    }
}
[IO.File]::WriteAllLines($envPath, $lines)
[IO.File]::WriteAllText($keyPath, ($keys | ConvertTo-Json))
Write-Host 'Created ignored .env and demo-keys.local.json with random local credentials.'
Write-Host 'Run docker compose up --build -d, then scripts/Send-DemoEvents.ps1.'
