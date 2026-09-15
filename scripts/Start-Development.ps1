# Legacy milestone 1 prototype. For PostgreSQL use New-LocalEnvironment.ps1 and Docker Compose.
[CmdletBinding()]
param([switch]$ProvisionOnly)
$ErrorActionPreference = 'Stop'

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Storage__Profile = 'Volatile'
$index = 0
foreach ($project in @('a', 'b')) {
    foreach ($permission in @('ingest', 'read')) {
        $key = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key)))
        [Environment]::SetEnvironmentVariable("ProjectAccess__Keys__${index}__ProjectId", $project, 'Process')
        [Environment]::SetEnvironmentVariable("ProjectAccess__Keys__${index}__KeyHash", $hash, 'Process')
        [Environment]::SetEnvironmentVariable("ProjectAccess__Keys__${index}__Permissions__0", $permission, 'Process')
        [Environment]::SetEnvironmentVariable("ProjectAccess__Keys__${index}__Revoked", 'false', 'Process')
        [Environment]::SetEnvironmentVariable("DEMO_${project}_${permission}".ToUpperInvariant(), $key, 'Process')
        $index++
    }
}
Write-Host 'Provisioned projects a and b with separate random ingest/read keys (hashes in API configuration).'
Write-Host 'Keys are available as DEMO_A_INGEST, DEMO_A_READ, DEMO_B_INGEST, DEMO_B_READ in this terminal.'
if (!$ProvisionOnly) {
    $apiProject = Join-Path $PSScriptRoot '../EventTracking.Api/EventTracking.Api.csproj'
    dotnet run --project $apiProject
}
