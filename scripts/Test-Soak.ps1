<#
.SYNOPSIS
    Extended soak testing script checking for memory leaks and resource exhaustion over time.
#>
[CmdletBinding()]
param(
    [string]$TargetUrl = "http://localhost:5000",
    [string]$ApiKey = "test-project-a-ingest-00000000000000000000",
    [int]$DurationMinutes = 15,
    [int]$BatchRatePerSecond = 5
)

$ErrorActionPreference = 'Stop'

Write-Host "=== Starting Soak Stability Test ==="
Write-Host "Target: $TargetUrl | Duration: $DurationMinutes minutes | Rate: $BatchRatePerSecond batches/sec"

$totalSeconds = $DurationMinutes * 60
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$intervalCheckSeconds = 60
$nextCheck = $intervalCheckSeconds

$client = [System.Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $ApiKey)
$client.Timeout = [TimeSpan]::FromSeconds(10)

$readClient = [System.Net.Http.HttpClient]::new()
$readClient.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", "test-project-a-read-0000000000000000000000")

$endpoint = "$($TargetUrl.TrimEnd('/'))/v1/events/batch"
$outboxEndpoint = "$($TargetUrl.TrimEnd('/'))/v1/status/outbox"

$batchesSent = 0
$errors = 0

while ($sw.Elapsed.TotalSeconds -lt $totalSeconds) {
    # Generate batch of 5 events
    $events = @()
    for ($i = 0; $i -lt 5; $i++) {
        $events += @{
            eventId = [Guid]::NewGuid().ToString("D")
            eventType = "soak_heartbeat"
            schemaVersion = 1
            userId = "soak-user-$([Random]::Shared.Next(1, 500))"
            occurredAt = [DateTimeOffset]::UtcNow.ToString("O")
            properties = @{ tick = $batchesSent }
        }
    }

    $json = [System.Text.Json.JsonSerializer]::Serialize(@{ events = $events })
    $content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, "application/json")

    try {
        $res = $client.PostAsync($endpoint, $content).GetAwaiter().GetResult()
        if ($res.IsSuccessStatusCode) { $batchesSent++ } else { $errors++ }
    } catch {
        $errors++
    }

    # Periodic checkpoint logging
    if ($sw.Elapsed.TotalSeconds -ge $nextCheck) {
        $elapsedMin = [Math]::Round($sw.Elapsed.TotalMinutes, 1)
        Write-Host "[$elapsedMin min] Batches sent: $batchesSent | Errors: $errors"

        # Check outbox status if accessible
        try {
            $outboxRes = $readClient.GetAsync($outboxEndpoint).GetAwaiter().GetResult()
            if ($outboxRes.IsSuccessStatusCode) {
                $outboxJson = $outboxRes.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                Write-Host "    Outbox Status: $outboxJson"
            }
        } catch {}

        $nextCheck += $intervalCheckSeconds
    }

    [System.Threading.Thread]::Sleep([int](1000 / $BatchRatePerSecond))
}

$sw.Stop()
Write-Host "`n=== Soak Test Completed ==="
Write-Host "Total Run Time: $([Math]::Round($sw.Elapsed.TotalMinutes, 1)) minutes"
Write-Host "Total Batches:  $batchesSent"
Write-Host "Total Errors:   $errors"

return [PSCustomObject]@{
    DurationMinutes = $sw.Elapsed.TotalMinutes
    TotalBatches = $batchesSent
    Errors = $errors
    Status = if ($errors -eq 0) { "Healthy" } else { "Degraded" }
}
