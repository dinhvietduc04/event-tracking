# Run only against a configured private Hosted preview. Does not create or deploy hosting resources.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaseUrl,
    [string]$IngestKey = $env:SPIKE_INGEST_KEY,
    [string]$ReadKey = $env:SPIKE_READ_KEY,
    [ValidateRange(0, 900)][int]$IdleSeconds = 45,
    [string]$ReportPath = (Join-Path $PSScriptRoot '../hosted-spike.local.json')
)
$ErrorActionPreference = 'Stop'
if (!$IngestKey -or !$ReadKey) { throw 'Set SPIKE_INGEST_KEY and SPIKE_READ_KEY using private preview project credentials.' }
$BaseUrl = $BaseUrl.TrimEnd('/')
$live = Invoke-RestMethod "$BaseUrl/health/live"
if ($live.profile -ne 'Hosted') { throw 'The spike must run against the Hosted profile.' }
$eventId = [Guid]::NewGuid().ToString()
$userId = "spike-$eventId"
$payload = @{ eventId = $eventId; eventType = 'spike'; schemaVersion = 1; userId = $userId; occurredAt = [DateTimeOffset]::UtcNow.ToString('O'); properties = @{ fixture = 'hosted-spike' } } | ConvertTo-Json -Compress
$client = [Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $IngestKey)
try {
    $tasks = @(1..12 | ForEach-Object { $client.PostAsync("$BaseUrl/v1/events", [Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, 'application/json')) })
    $receipts = @()
    foreach ($task in $tasks) {
        $response = $task.GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 200) { throw "Hosted acceptance returned $($response.StatusCode)." }
        $receipts += $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        $response.Dispose()
    }
    if (@($receipts | Where-Object { !$_.alreadyAccepted }).Count -ne 1) { throw 'Concurrent retries did not produce exactly one new event.' }
    $readHeaders = @{ Authorization = "Bearer $ReadKey" }
    $before = Invoke-RestMethod "$BaseUrl/v1/analytics/users/$userId" -Headers $readHeaders
    if (@($before).Count -ne 1 -or $before[0].count -ne 1) { throw 'Hosted event was not immediately queryable exactly once.' }
    Write-Host "Idling for $IdleSeconds seconds. Confirm actual instance scale-down separately in Vercel logs."
    for ($elapsed = 0; $elapsed -lt $IdleSeconds; $elapsed += 15) { Start-Sleep -Seconds ([Math]::Min(15, $IdleSeconds - $elapsed)) }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $after = Invoke-RestMethod "$BaseUrl/v1/analytics/users/$userId" -Headers $readHeaders
    $timer.Stop()
    if (@($after).Count -ne 1 -or $after[0].count -ne 1) { throw 'Persisted event missing after idle interval.' }
    $report = @{ recordedAt = [DateTimeOffset]::UtcNow.ToString('O'); baseUrl = $BaseUrl; eventId = $eventId; profile = 'Hosted'; concurrentRequests = 12; uniqueEvents = 1; idleSeconds = $IdleSeconds; firstReadAfterIdleMs = $timer.ElapsedMilliseconds; checks = 'passed'; actualScaleDownVerified = $false; tlsDatabaseVerified = $false; providerQuotaReviewed = $false }
    [IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json))
    $report | ConvertTo-Json
} finally { $client.Dispose() }
