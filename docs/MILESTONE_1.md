# Milestone 1 implementation and demo

Historical milestone 1 record. Milestone 2 supersedes the volatile general API defaults and producer; use [MILESTONE_2.md](MILESTONE_2.md) for current setup. The explicit Volatile profile and legacy development script remain available for prototype regressions.

Implemented September 13, 2026 against `PROJECT_PLAN.md`. The requested `docs/PROJECT/_PLAN.md` was absent; this is the roadmap available in the repository. The roadmap explicitly calls for one milestone at a time, so this slice implements milestone 1. Milestones 2–7 remain future work.

## Decisions and task ledger

These task-sized records serve as the local implementation backlog; no external issues were published.

| Task | Decision/result | Status |
| --- | --- | --- |
| M1-01: contract | `/v1/events`, explicit caller ID/schema/time, bounded properties, Problem Details, UTC half-open queries; legacy adapters documented | Complete |
| M1-02: project access | Development configuration contains SHA-256 key hashes, project IDs, permissions and revocation; general routes require project keys; local shop has a reserved project | Complete |
| M1-03: overload and visibility | Bounded channel rejects with 503; actual-byte body limits; per-project 429; structured acceptance/processing/request logs and liveness | Complete |
| M1-04: verification | Legacy tests use credentials; contract/isolation/overload tests; GitHub Actions Release build/test workflow | Complete locally; hosted CI runs after push |
| M1-05: developer demo | Random local key provisioning, deterministic fixture producer, API contract and setup guide | Complete |

Keep project access in a separate module and use the authenticated context at every general data read/write. Keep the current API and worker process together for this milestone. The channel provides immediate overload rejection with no silent drop. The existing in-memory event store is still volatile and unbounded. Do not describe `202` as durable until PostgreSQL acceptance commits are implemented in milestone 2. Event-ID deduplication and conflicting-payload checks also start in milestone 2.

The shop remains a local demo. Its session IDs and server/browser source distinction are preserved; the general API cannot write to or query the reserved shop project. No privileged API key is embedded in shop JavaScript.

## Run the demo

Requires .NET 10 and PowerShell 7. From the repository root, provision credentials without writing raw keys to disk:

```powershell
. ./scripts/Start-Development.ps1 -ProvisionOnly
```

This sets hashed API key configuration and four random demo credential environment variables in the current terminal. Start the API from that terminal:

```powershell
dotnet run --project ./EventTracking.Api
```

Open `http://localhost:5191/shop/index.html` to use the existing shop. To run the producer while the API runs, open a second terminal and pass its four key parameters using values from the provisioning terminal. Do not commit or put keys into shared transcripts.

A straightforward two-terminal sequence: provision in terminal A, copy the four environment values privately into the matching variables in terminal B, start the API in A, then in B run:

```powershell
./scripts/Send-DemoEvents.ps1
```

The producer also accepts `-BaseUrl`, `-IngestA`, `-ReadA`, `-IngestB`, `-ReadB`, and `-Anchor`. Use an anchor within the configured time window. Event IDs, event types, users, properties, and relative timestamps are deterministic for a given anchor; keys are random. It reads baseline counts, sends four events to project A and one to B, checks an invalid request returns 400, then polls until expected count increments appear. Run against otherwise idle demo projects. Re-running intentionally increments counts again because this milestone has no deduplication.

Expected first-run output, verified against a live local Kestrel process:

```json
{
  "ProjectA": { "page_view": 2, "login": 1, "purchase": 1 },
  "ProjectB": { "page_view": 1 },
  "InvalidEvent": "400",
  "Verification": "passed"
}
```

## Verification evidence

Final Release verification passed **23 tests** (the original 7 and 16 new cases), with zero build warnings/errors. Coverage includes both projects and both analytics route versions, user/session collisions, missing/revoked/wrong-permission keys, production rejection of development keys, validation errors, malformed/unknown fields, timestamp offsets and boundaries, exact-byte and chunked oversized requests, queue saturation/recovery and property preservation, project-shared rate limits, the explicit absence of milestone 1 deduplication, OpenAPI security and liveness. Queue tests stop the consumer and fill the actual bounded queue; asynchronous tests wait for observable results.

An already-running API locked the default Release executable. Verification used a fresh temporary artifacts directory, leaving that process running:

```powershell
$verificationArtifacts = Join-Path ([IO.Path]::GetTempPath()) ('event-tracking-m1-' + [Guid]::NewGuid().ToString('N'))
dotnet build EventTracking.slnx --configuration Release --artifacts-path $verificationArtifacts
dotnet test EventTracking.slnx --configuration Release --no-build --artifacts-path $verificationArtifacts
```

`git diff --check` also passed.

The deterministic producer was executed against Kestrel on `127.0.0.1:5299` and produced the counts shown above. Random-key provisioning was executed and verified separately. The CI workflow is committed as configuration only; remote CI has not been run in this workspace. This is not a deployment, durability test, or production readiness claim.

Next dependency: milestone 2, PostgreSQL projects/hashed credentials, durable inbox acceptance, transactional idempotent projection and real-service restart/recovery tests. See [API contract](API_V1.md) for current limits and compatibility changes.
