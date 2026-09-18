# Apply schema and initialize the selected profile; never seed accounts/keys or transition profiles.
# Requires PowerShell 7 and .NET 10. No connection string/password arguments are accepted.
[CmdletBinding()]
param(
    [ValidateSet('Local', 'Configured')][string]$Target = 'Local',
    [ValidateSet('Distributed', 'Hosted')][string]$Profile = 'Distributed',
    [string]$Database = 'event_tracking',
    [string]$ExpectedHost,
    [string]$ExpectedDatabase
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$names = @('ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT', 'ConnectionStrings__Operator',
    'Storage__Profile', 'Storage__MigrateOnStartup', 'Storage__AllowProfileTransition',
    'Administration__ExpectedDatabase', 'Administration__ExpectedHost')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    if ($Target -eq 'Local') {
        if ([string]::IsNullOrWhiteSpace($Database)) { throw 'Database cannot be empty.' }
        $local = Get-Content -Raw (Join-Path $root '.env') | ConvertFrom-StringData
        if (!$local.POSTGRES_PASSWORD) { throw 'Local POSTGRES_PASSWORD is missing from .env.' }
        $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
        $connection['Host'] = '127.0.0.1'
        $connection['Port'] = if ($local.POSTGRES_PORT) { [string]$local.POSTGRES_PORT } else { '15433' }
        $connection['Database'] = [string]$Database
        $connection['Username'] = 'event_tracking'
        $connection['Password'] = [string]$local.POSTGRES_PASSWORD
        $connection['GSS Encryption Mode'] = 'Disable'
        $env:ConnectionStrings__Operator = $connection.ConnectionString
        # Testing disables production-only checks, without development bootstrap side effects.
        $env:ASPNETCORE_ENVIRONMENT = 'Testing'
        $env:DOTNET_ENVIRONMENT = 'Testing'
        $ExpectedHost = '127.0.0.1'
        $ExpectedDatabase = $Database
    } else {
        if ([string]::IsNullOrWhiteSpace($env:ConnectionStrings__Operator)) { throw 'Set ConnectionStrings__Operator privately before using -Target Configured.' }
        if ([string]::IsNullOrWhiteSpace($ExpectedHost) -or [string]::IsNullOrWhiteSpace($ExpectedDatabase)) {
            throw 'Configured migrations require -ExpectedHost and -ExpectedDatabase to identify the intended target.'
        }
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        $env:DOTNET_ENVIRONMENT = 'Production'
    }
    $env:Storage__Profile = $Profile
    $env:Storage__MigrateOnStartup = 'false'
    $env:Storage__AllowProfileTransition = 'false'
    $env:Administration__ExpectedHost = $ExpectedHost
    $env:Administration__ExpectedDatabase = $ExpectedDatabase
    Write-Host "Migrating $Target database '$ExpectedDatabase' on '$ExpectedHost' ($Profile)."
    dotnet run --configuration Release --no-launch-profile --project (Join-Path $root 'EventTracking.Api') -- --migrate
    if ($LASTEXITCODE -ne 0) { throw 'Database migration failed. Runtime deployment must not proceed.' }
    Write-Host 'Database migration completed. Reapply runtime grants after schema changes.'
} finally {
    foreach ($name in $names) {
        if ($null -eq $previous[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    }
}
