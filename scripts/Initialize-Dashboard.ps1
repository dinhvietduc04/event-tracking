# Provision a dashboard account on the local Compose PostgreSQL server only.
[CmdletBinding()]
param([string]$Username = 'demo', [string]$Projects = 'a,b', [ValidateSet('Distributed','Hosted')][string]$Profile = 'Distributed')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$local = Get-Content -Raw (Join-Path $root '.env') | ConvertFrom-StringData
$path = Join-Path $root 'dashboard-login.local.json'
if (Test-Path -LiteralPath $path) { throw 'dashboard-login.local.json already exists. Reuse that account instead of replacing it.' }
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
$names = @('ConnectionStrings__Tracking','Storage__Profile','Storage__MigrateOnStartup','Storage__AllowProfileTransition','Dashboard__Bootstrap__Username','Dashboard__Bootstrap__Password','Dashboard__Bootstrap__Projects')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:ConnectionStrings__Tracking = "Host=127.0.0.1;Port=$($local.POSTGRES_PORT);Database=event_tracking;Username=event_tracking;Password=$($local.POSTGRES_PASSWORD);GSS Encryption Mode=Disable"
    $env:Storage__Profile = $Profile
    $env:Storage__MigrateOnStartup = 'false'
    $env:Storage__AllowProfileTransition = 'false'
    $env:Dashboard__Bootstrap__Username = $Username
    $env:Dashboard__Bootstrap__Password = $password
    $env:Dashboard__Bootstrap__Projects = $Projects
    dotnet run --no-launch-profile --project (Join-Path $root 'EventTracking.Api') -- --migrate --dashboard-provision
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard provisioning failed.' }
    [IO.File]::WriteAllText($path, (@{ username = $Username; password = $password } | ConvertTo-Json))
    Write-Host 'Created dashboard-login.local.json. Use its credentials at /dashboard/.'
    Write-Host 'Keep this ignored file private. Bootstrap never resets an existing account.'
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
