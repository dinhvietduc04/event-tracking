# Reproducible local-only production security smoke. Creates/removes its own PostgreSQL container.
# Requires Docker Linux containers, PowerShell 7 and .NET 10. Never reads hosted credentials.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runId = [Guid]::NewGuid().ToString('N')
$container = 'event-tracking-security-' + $runId
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('event-tracking-security-' + $runId)
$names = @('ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT','ConnectionStrings__Operator','ConnectionStrings__Tracking',
    'Storage__Profile','Storage__MigrateOnStartup','Storage__AllowProfileTransition','Dashboard__Enabled',
    'Dashboard__Bootstrap__Username','Dashboard__Bootstrap__Password','Dashboard__Bootstrap__Projects',
    'Administration__RoleName','Administration__RoleKind','Administration__Action','Administration__Actor',
    'Administration__Username','Administration__ProjectId','Administration__Reason','Administration__ExpectedHost','Administration__ExpectedDatabase',
    'DataProtection__Certificate__Path','DataProtection__Certificate__Base64','DataProtection__Certificate__Password',
    'AllowedHosts','ReverseProxy__TrustForwardedProto','ReverseProxy__ExclusiveIngress','Logging__LogLevel__Default','PORT','POSTGRES_PASSWORD')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$process = $null
$created = $false
$rsa = [Security.Cryptography.RSA]::Create(2048)
$rootRsa = [Security.Cryptography.RSA]::Create(2048)
$certificate = $null
$authority = $null
function Run-Operator([string[]]$Arguments) {
    $output = & dotnet (Join-Path $root 'EventTracking.Api/bin/Release/net10.0/EventTracking.Api.dll') @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Operator command failed: $($Arguments -join ' '). $($output | Select-Object -Last 6)" }
}
try {
    [IO.Directory]::CreateDirectory($temporary) | Out-Null
    $rootRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=EventTracking synthetic test CA', $rootRsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $rootRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
    $authority = $rootRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(2))
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('localhost') # Intentionally exclude the IP to test hostname verification.
    $request.CertificateExtensions.Add($san.Build())
    $publicCertificate = $request.Create($authority, [DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddDays(1), [Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
    $certificate = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($publicCertificate, $rsa)
    $publicCertificate.Dispose()
    $certificateFile = Join-Path $temporary 'server.crt'
    $privateKeyFile = Join-Path $temporary 'server.key'
    $caFile = Join-Path $temporary 'ca.crt'
    $pfxFile = Join-Path $temporary 'protection.pfx'
    [IO.File]::WriteAllText($certificateFile, $certificate.ExportCertificatePem())
    [IO.File]::WriteAllText($privateKeyFile, $rsa.ExportPkcs8PrivateKeyPem())
    [IO.File]::WriteAllText($caFile, $authority.ExportCertificatePem())
    $pfxPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    [IO.File]::WriteAllBytes($pfxFile, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $pfxPassword))
    $env:POSTGRES_PASSWORD = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $apiPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    docker create --name $container -e POSTGRES_PASSWORD -e POSTGRES_USER=event_tracking -e POSTGRES_DB=event_tracking -p '127.0.0.1::5432' --entrypoint sh postgres:17-alpine -c 'chown postgres:postgres /tmp/server.key && chmod 600 /tmp/server.key && exec docker-entrypoint.sh postgres -c ssl=on -c ssl_cert_file=/tmp/server.crt -c ssl_key_file=/tmp/server.key' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the disposable PostgreSQL container.' }
    $created = $true
    docker cp $certificateFile "${container}:/tmp/server.crt"
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy the synthetic certificate.' }
    docker cp $privateKeyFile "${container}:/tmp/server.key"
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy the synthetic private key.' }
    docker start $container | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the disposable PostgreSQL container.' }
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        docker exec $container pg_isready -U event_tracking -d event_tracking 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (!$ready) { throw 'Disposable PostgreSQL did not become ready.' }
    $binding = docker port $container 5432/tcp
    $port = [int]($binding.Split(':')[-1])
    $connection = [Data.Common.DbConnectionStringBuilder]::new()
    $connection['Host'] = 'localhost'; $connection['Port'] = $port; $connection['Database'] = 'event_tracking'
    $connection['Username'] = 'event_tracking'; $connection['Password'] = $env:POSTGRES_PASSWORD
    $connection['SSL Mode'] = 'VerifyFull'; $connection['GSS Encryption Mode'] = 'Disable'; $connection['Root Certificate'] = [string]$caFile
    $env:ConnectionStrings__Operator = $connection.ConnectionString
    $env:ConnectionStrings__Tracking = $null
    $env:ASPNETCORE_ENVIRONMENT = 'Production'; $env:DOTNET_ENVIRONMENT = 'Production'
    $env:Storage__Profile = 'Hosted'; $env:Storage__MigrateOnStartup = 'false'; $env:Storage__AllowProfileTransition = 'false'
    $env:Logging__LogLevel__Default = 'Warning'
    & (Join-Path $root 'scripts/Migrate-Database.ps1') -Target Configured -Profile Hosted -ExpectedHost localhost -ExpectedDatabase event_tracking
    "CREATE ROLE security_api LOGIN PASSWORD '$apiPassword' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;" |
        docker exec -i $container psql -U event_tracking -d event_tracking -v ON_ERROR_STOP=1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the synthetic runtime login.' }
    $env:Administration__RoleName = 'security_api'; $env:Administration__RoleKind = 'Api'
    Run-Operator @('--grant-runtime')
    $env:Dashboard__Bootstrap__Username = 'production-smoke'; $env:Dashboard__Bootstrap__Password = 'synthetic-smoke-login-password'
    $env:Dashboard__Bootstrap__Projects = 'smoke'
    Run-Operator @('--dashboard-provision')
    $env:Administration__Action = 'grant-admin'; $env:Administration__Actor = 'smoke'
    $env:Administration__Username = 'production-smoke'; $env:Administration__ProjectId = 'smoke'; $env:Administration__Reason = 'Synthetic smoke'
    Run-Operator @('--dashboard-admin')
    # Verify TLS against the client's trust chain and hostname, not the pooler's backend report.
    $connection['Host'] = '127.0.0.1'
    $env:ConnectionStrings__Operator = $connection.ConnectionString
    & dotnet (Join-Path $root 'EventTracking.Api/bin/Release/net10.0/EventTracking.Api.dll') --storage-info *> $null
    if ($LASTEXITCODE -eq 0) { throw 'TLS unexpectedly accepted the wrong hostname.' }
    $connection['Host'] = 'localhost'; $connection.Remove('Root Certificate') | Out-Null
    $env:ConnectionStrings__Operator = $connection.ConnectionString
    & dotnet (Join-Path $root 'EventTracking.Api/bin/Release/net10.0/EventTracking.Api.dll') --storage-info *> $null
    if ($LASTEXITCODE -eq 0) { throw 'TLS unexpectedly accepted an untrusted certificate.' }
    $connection['Root Certificate'] = [string]$caFile; $connection['Username'] = 'security_api'; $connection['Password'] = [string]$apiPassword
    $env:ConnectionStrings__Tracking = $connection.ConnectionString
    foreach ($name in [Environment]::GetEnvironmentVariables('Process').Keys | Where-Object { $_ -like 'Administration__*' -or $_ -like 'Dashboard__Bootstrap__*' }) {
        # Keep restoration complete even when a caller supplied additional operator settings.
        if (!$previous.ContainsKey($name)) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process'); $names += $name }
        Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
    }
    $env:ConnectionStrings__Operator = $null
    $env:Dashboard__Enabled = 'true'; $env:AllowedHosts = 'localhost'
    $env:DataProtection__Certificate__Base64 = $null; $env:DataProtection__Certificate__Path = $pfxFile; $env:DataProtection__Certificate__Password = $pfxPassword
    $env:ReverseProxy__TrustForwardedProto = 'true'; $env:ReverseProxy__ExclusiveIngress = 'true'; $env:PORT = ''
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $httpPort = $listener.LocalEndpoint.Port; $listener.Stop()
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.WorkingDirectory = $root
    $start.ArgumentList.Add((Join-Path $root 'EventTracking.Api/bin/Release/net10.0/EventTracking.Api.dll'))
    $start.ArgumentList.Add('--urls'); $start.ArgumentList.Add("http://127.0.0.1:$httpPort")
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $baseUrl = "http://127.0.0.1:$httpPort"
    $hostHeaders = @{ Host = 'localhost' }
    $ready = $false
    $lastHealthError = 'No health response'
    for ($attempt = 0; $attempt -lt 45; $attempt++) {
        if ($process.HasExited) { throw "Production API exited: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())" }
        try { $health = Invoke-RestMethod "$baseUrl/health/ready" -Headers $hostHeaders -TimeoutSec 5; $ready = $health.status -eq 'ready'; if ($ready) { break } } catch { $lastHealthError = $_.Exception.Message }
        Start-Sleep -Seconds 1
    }
    if (!$ready) {
        $process.Kill($true); $process.WaitForExit()
        throw "Production API did not become ready: $lastHealthError. $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"
    }
    $insecure = Invoke-WebRequest "$baseUrl/dashboard-api/auth/token" -Headers $hostHeaders -SkipHttpErrorCheck
    if ($insecure.StatusCode -ne 400) { throw 'Production unexpectedly accepted unencrypted HTTP.' }
    $headers = @{ Host = 'localhost'; 'X-Forwarded-Proto' = 'https' }
    $token = Invoke-WebRequest "$baseUrl/dashboard-api/auth/token" -Headers $headers
    $csrfCookie = @($token.Headers['Set-Cookie'])[0].Split(';')[0]
    if (@($token.Headers['Set-Cookie'])[0] -notmatch '(?i); secure') { throw 'Missing Secure cookie attribute.' }
    $headers['Cookie'] = $csrfCookie; $headers['X-CSRF-Token'] = ($token.Content | ConvertFrom-Json).token
    $login = Invoke-WebRequest "$baseUrl/dashboard-api/auth/login" -Method Post -Headers $headers -ContentType application/json -Body '{"username":"production-smoke","password":"synthetic-smoke-login-password"}'
    $sessionCookie = @($login.Headers['Set-Cookie'])[0].Split(';')[0]
    $headers['Cookie'] = "$csrfCookie; $sessionCookie"
    $token = Invoke-RestMethod "$baseUrl/dashboard-api/auth/token" -Headers $headers
    $headers['X-CSRF-Token'] = $token.token
    $key = Invoke-RestMethod "$baseUrl/dashboard-api/projects/smoke/admin/keys" -Method Post -Headers $headers -ContentType application/json -Body '{"permissions":["ingest","read"]}'
    $producerHeaders = @{ Host = 'localhost'; Authorization = "Bearer $($key.key)"; 'X-Forwarded-Proto' = 'https' }
    $event = @{ eventId = [Guid]::NewGuid().ToString(); eventType = 'production_smoke'; schemaVersion = 1; occurredAt = [DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json -Compress
    $receipt = Invoke-RestMethod "$baseUrl/v1/events" -Method Post -Headers $producerHeaders -ContentType application/json -Body $event
    $analytics = Invoke-RestMethod "$baseUrl/v1/analytics/events" -Headers $producerHeaders
    if ($receipt.status -ne 'persisted' -or $analytics[0].count -ne 1) { throw 'Production ingestion/analytics reconciliation failed.' }
    $plaintext = docker exec $container psql -U event_tracking -d event_tracking -Atc "SELECT count(*) FROM data_protection_keys WHERE xml LIKE '%<value>%';"
    if ($plaintext.Trim() -ne '0') { throw 'A production protection key was stored unencrypted.' }
    Write-Output 'Production profile smoke passed: configured migration, restricted runtime role, trusted TLS, wrong-host/untrusted-CA rejection, HTTPS enforcement, Secure login, encrypted cookie keys, admin key issuance and durable analytics.'
} finally {
    if ($process) { if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() }
    if ($created) { docker rm -f -v $container | Out-Null }
    # Validate the absolute temp path before recursively deleting only this run's artifacts.
    $resolved = [IO.Path]::GetFullPath($temporary)
    $expected = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('event-tracking-security-' + $runId)))
    if ($resolved -ne $expected -or !(Split-Path $resolved -Leaf).StartsWith('event-tracking-security-')) { throw 'Unexpected temporary cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    if ($certificate) { $certificate.Dispose() }; if ($authority) { $authority.Dispose() }
    $rsa.Dispose(); $rootRsa.Dispose()
    foreach ($name in $names) {
        if ($null -eq $previous[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    }
}
