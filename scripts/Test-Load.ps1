<#
.SYNOPSIS
    Sustained load testing harness for EventTracking API ingestion.
.PARAMETER TargetUrl
    Base URL of the EventTracking API (default http://localhost:5000).
.PARAMETER ApiKey
    Ingest-capable Project API Key.
.PARAMETER DurationSeconds
    Duration of the load test in seconds (default 60s for local testing).
.PARAMETER TargetRps
    Target batches per second (each batch has 10 events, e.g., 10 rps = 100 events/s).
#>
[CmdletBinding()]
param(
    [string]$TargetUrl = "http://localhost:5000",
    [string]$ApiKey = "test-project-a-ingest-00000000000000000000",
    [int]$DurationSeconds = 60,
    [int]$TargetRps = 10,
    [int]$BatchSize = 10
)

$ErrorActionPreference = 'Stop'

Write-Host "=== Starting EventTracking Ingestion Load Test ==="
Write-Host "Target: $TargetUrl"
Write-Host "Duration: $DurationSeconds s | Target RPS: $TargetRps batches/s | Batch Size: $BatchSize events"

$client = [System.Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $ApiKey)
$client.Timeout = [TimeSpan]::FromSeconds(10)

$endpoint = "$($TargetUrl.TrimEnd('/'))/v1/events/batch"
$latencies = [System.Collections.Generic.List[double]]::new()
$errors = 0
$success = 0

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$intervalMs = 1000.0 / $TargetRps

while ($stopwatch.Elapsed.TotalSeconds -lt $DurationSeconds) {
    $loopStart = [System.Diagnostics.Stopwatch]::GetTimestamp()

    # Generate synthetic batch
    $events = @()
    for ($i = 0; $i -lt $BatchSize; $i++) {
        $events += @{
            eventId = [Guid]::NewGuid().ToString("D")
            eventType = "load_test_event"
            schemaVersion = 1
            userId = "user-$([Random]::Shared.Next(1, 1000))"
            occurredAt = [DateTimeOffset]::UtcNow.ToString("O")
            properties = @{
                iteration = $success + $errors
                source = "load-harness"
                random_payload = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
            }
        }
    }

    $json = (@{ events = $events } | ConvertTo-Json -Depth 6 -Compress)
    $content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, "application/json")

    $reqStart = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $response = $client.PostAsync($endpoint, $content).GetAwaiter().GetResult()
        $reqStart.Stop()
        $latencies.Add($reqStart.Elapsed.TotalMilliseconds)

        if ($response.IsSuccessStatusCode) {
            $success++
        } else {
            $errors++
        }
    } catch {
        $errors++
    }

    # Throttle to target RPS
    $elapsedMs = ([System.Diagnostics.Stopwatch]::GetTimestamp() - $loopStart) * 1000.0 / [System.Diagnostics.Stopwatch]::Frequency
    $delay = [Math]::Max(0, [int]($intervalMs - $elapsedMs))
    if ($delay -gt 0) {
        [System.Threading.Thread]::Sleep($delay)
    }
}

$stopwatch.Stop()

# Calculate stats
$latenciesArray = $latencies.ToArray()
[Array]::Sort($latenciesArray)

function Get-Percentile([double[]]$arr, [double]$p) {
    if ($arr.Length -eq 0) { return 0 }
    $index = [int]([Math]::Ceiling($arr.Length * $p) - 1)
    return $arr[[Math]::Clamp($index, 0, $arr.Length - 1)]
}

$p50 = Get-Percentile $latenciesArray 0.50
$p95 = Get-Percentile $latenciesArray 0.95
$p99 = Get-Percentile $latenciesArray 0.99

$actualRps = ($success + $errors) / $stopwatch.Elapsed.TotalSeconds
$eventsPerSec = $actualRps * $BatchSize

Write-Host "`n=== Load Test Results ==="
Write-Host "Total Batches Sent: $($success + $errors)"
Write-Host "Successful Batches: $success"
Write-Host "Failed Batches:     $errors"
Write-Host "Actual RPS:         $([Math]::Round($actualRps, 1)) batches/s"
Write-Host "Throughput:         $([Math]::Round($eventsPerSec, 1)) events/s"
Write-Host "Latency p50:        $([Math]::Round($p50, 2)) ms"
Write-Host "Latency p95:        $([Math]::Round($p95, 2)) ms"
Write-Host "Latency p99:        $([Math]::Round($p99, 2)) ms"

return [PSCustomObject]@{
    TotalBatches = $success + $errors
    SuccessfulBatches = $success
    FailedBatches = $errors
    ActualRps = $actualRps
    ThroughputEventsPerSec = $eventsPerSec
    LatencyP50Ms = $p50
    LatencyP95Ms = $p95
    LatencyP99Ms = $p99
    DurationSeconds = $stopwatch.Elapsed.TotalSeconds
}
