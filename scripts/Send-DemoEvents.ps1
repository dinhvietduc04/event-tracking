[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5191',
    [string]$IngestA = $env:DEMO_A_INGEST,
    [string]$ReadA = $env:DEMO_A_READ,
    [string]$IngestB = $env:DEMO_B_INGEST,
    [string]$ReadB = $env:DEMO_B_READ,
    [string]$KeyFile = (Join-Path $PSScriptRoot '../demo-keys.local.json'),
    [DateTimeOffset]$Anchor = [DateTimeOffset]::UtcNow.AddMinutes(-1),
    [switch]$SubmitOnly
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $KeyFile) {
    $keys = Get-Content -Raw -LiteralPath $KeyFile | ConvertFrom-Json
    if (!$IngestA) { $IngestA = $keys.DEMO_A_INGEST }
    if (!$ReadA) { $ReadA = $keys.DEMO_A_READ }
    if (!$IngestB) { $IngestB = $keys.DEMO_B_INGEST }
    if (!$ReadB) { $ReadB = $keys.DEMO_B_READ }
}
if (!$IngestA -or !$ReadA -or !$IngestB -or !$ReadB) { throw 'Provide four project keys or generate demo-keys.local.json with New-LocalEnvironment.ps1.' }
$BaseUrl = $BaseUrl.TrimEnd('/')
$readyDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
while ($true) {
    try { $null = Invoke-RestMethod "$BaseUrl/health/ready" -TimeoutSec 3; break }
    catch {
        if ([DateTimeOffset]::UtcNow -ge $readyDeadline) { throw 'API did not become ready within 30 seconds.' }
        Start-Sleep -Milliseconds 100
    }
}
$fixture = 'm2-' + $Anchor.ToUniversalTime().ToString('O')
$events = @()
$types = @('page_view', 'page_view', 'login', 'purchase')
for ($i = 0; $i -lt $types.Count; $i++) {
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("${fixture}:$i"))
    $events += @{
        eventId = [Guid]::new([byte[]]$hash[0..15]).ToString()
        eventType = $types[$i]; schemaVersion = 1; userId = 'synthetic-user'; sessionId = 'synthetic-session'
        occurredAt = $Anchor.AddSeconds($i).ToUniversalTime().ToString('O')
        properties = @{ fixture = $fixture; sequence = $i }
    }
}
function Send-Json([string]$Path, [string]$Key, [object]$Payload, [int[]]$Expected) {
    $response = Invoke-WebRequest "$BaseUrl$Path" -Method Post -Headers @{ Authorization = "Bearer $Key" } `
        -ContentType 'application/json' -Body ($Payload | ConvertTo-Json -Depth 8 -Compress) -SkipHttpErrorCheck
    if ([int]$response.StatusCode -notin $Expected) { throw "Expected HTTP $Expected, got $($response.StatusCode): $($response.Content)" }
    return $response.Content | ConvertFrom-Json
}
$null = Send-Json '/v1/events/batch' $IngestA @{ events = $events } @(200, 202)
$retry = Send-Json '/v1/events/batch' $IngestA @{ events = $events } @(200, 202)
if (@($retry.events | Where-Object { !$_.alreadyAccepted }).Count -ne 0) { throw 'Retry was not deduplicated.' }
$null = Send-Json '/v1/events' $IngestB $events[0] @(200, 202)
$invalid = $events[0].Clone(); $invalid.eventType = ''
$null = Send-Json '/v1/events' $IngestA $invalid @(400)
$changed = $events[0].Clone(); $changed.eventType = 'changed'
$null = Send-Json '/v1/events' $IngestA $changed @(409)
if ($SubmitOnly) {
    [pscustomobject]@{ Anchor = $Anchor.ToString('O'); Submitted = 5; RetriesDeduplicated = $true } | ConvertTo-Json
    return
}
$filter = '?propertyName=fixture&propertyValue=' + [Uri]::EscapeDataString(($fixture | ConvertTo-Json -Compress))
function Read-Counts([string]$Key) {
    $counts = @{}
    foreach ($item in (Invoke-RestMethod "$BaseUrl/v1/analytics/events$filter" -Headers @{ Authorization = "Bearer $Key" })) { $counts[$item.eventType] = [int]$item.count }
    return $counts
}
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
do {
    $a = Read-Counts $ReadA; $b = Read-Counts $ReadB
    if ($a.page_view -eq 2 -and $a.login -eq 1 -and $a.purchase -eq 1 -and $a.Count -eq 3 -and $b.page_view -eq 1 -and $b.Count -eq 1) { break }
    Start-Sleep -Milliseconds 50
} while ([DateTimeOffset]::UtcNow -lt $deadline)
if ($a.page_view -ne 2 -or $a.login -ne 1 -or $a.purchase -ne 1 -or $a.Count -ne 3 -or $b.page_view -ne 1 -or $b.Count -ne 1) { throw 'Expected isolated, deduplicated fixture counts did not arrive.' }
[pscustomobject]@{ Anchor = $Anchor.ToString('O'); ProjectA = $a; ProjectB = $b; InvalidEvent = 400; ChangedPayload = 409; RetriesDeduplicated = $true; Verification = 'passed' } | ConvertTo-Json -Depth 4
