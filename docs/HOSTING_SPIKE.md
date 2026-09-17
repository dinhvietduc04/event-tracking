# Vercel / Neon container spike (deployed; health checks passed)

Status, September 16, 2026: the user deployed the container on Vercel. The initial Neon migration has been applied, and the public `/health/live` and `/health/ready` endpoints both return HTTP 200 with the Hosted profile. Hosted ingestion/retry tests, runtime TLS verification, provider quotas and cloud cold-start measurements remain pending.

## Prepared artifacts

- `Dockerfile.vercel` publishes the .NET 10 API and runs it as the image's non-root user.
- `vercel.json` explicitly selects the container runtime and routes every request to the API service. Keep both files at Vercel's configured project root (`./`).
- `Storage__Profile=Hosted` persists queryable events and identities before returning 200; no inbox or database worker is created for hosted requests.
- The application honors `PORT`; set Vercel's project `PORT=8080` to match the image default.
- `scripts/Test-HostedSpike.ps1` submits 12 concurrent duplicate requests, verifies immediate count=1, waits an explicit idle interval and checks persistence again. It records an ignored `hosted-spike.local.json` evidence file without keys.
- `--storage-info` reports database size, pending work and PostgreSQL's `pg_stat_ssl` value for the current backend. With a connection pooler, that value alone does not verify the application's TLS connection to the pooler.

Vercel documents root `Dockerfile.vercel` detection and an HTTP container port selected by `PORT` (default 80 if not configured). The documentation also describes scale-down after 30 seconds idle for previews and 5 minutes for production; verify the behavior on the actual account at deployment time. These are provider statements, not measured results for this app. [Vercel Container Images](https://vercel.com/docs/functions/container-images).

## Future private-preview procedure

1. Create/select the intended personal Vercel project and a dedicated Neon PostgreSQL database. Keep the deployment private/protected while milestone 5 production controls are incomplete.
2. Use an operator connection to apply migrations and insert the two hashed project credentials with `--migrate --provision`. Generate independent random ingest/read keys; keep raw values in a private local secret mechanism. Do not put them into the Docker image, build arguments, JavaScript or repository.
3. Configure runtime `ConnectionStrings__Tracking` using the Neon hostname/database/role and `SSL Mode=VerifyFull;GSS Encryption Mode=Disable;Maximum Pool Size=10;Timeout=15;Command Timeout=30`. Use a trusted root CA if required by the provider. Keep `Storage__Profile=Hosted`, `PORT=8080`, `Storage__MigrateOnStartup=false`, `ASPNETCORE_ENVIRONMENT=Production`, and leave the demo disabled. Size pool limits against the provider's connection allowance and anticipated instance count.
4. Deploy the prepared `Dockerfile.vercel` as a preview. Check startup, `/health/live`, `/health/ready` and an HTTPS request. Use the chosen deployment's protection mechanism when testing; the supplied script assumes the caller can reach the preview and does not implement provider-specific bypass headers.
5. Verify database TLS from the running container using connection telemetry and the effective `VerifyFull` configuration. `--storage-info` queries `pg_stat_ssl`; through a pooler, it does not establish the TLS state of the application's connection to the pooler. Record hosted certificate verification separately.
6. Set `SPIKE_INGEST_KEY` and `SPIKE_READ_KEY` privately, then run the smoke script below. Compare first-request and post-idle latency; use Vercel logs to confirm an actual instance scale-down/new start. Health monitors and other traffic can prevent scale-down.
7. Review Vercel usage and Neon database/compute/connection quotas in their dashboards. Record the observed allowance, baseline usage, configured app limits and headroom. Test a deliberately small app quota and verify 503 without a new accepted identity, then restore the intended limit. Provider quotas are separate from app logical reservations and PostgreSQL admission size checks.
8. Record results, scheduling/backup requirements and a keep/reject decision for the hosting candidate. Leave the host provisional until every applicable check has evidence.

```powershell
./scripts/Test-HostedSpike.ps1 -BaseUrl https://your-private-preview.example -IdleSeconds 45
```

The script's `actualScaleDownVerified`, `tlsDatabaseVerified` and `providerQuotaReviewed` fields deliberately remain false: those checks need separate provider/operator evidence. Merely waiting 45 seconds does not prove a cold start. Record missing checks as pending, not passed.

Npgsql 10 can attempt GSSAPI on Linux images without Kerberos libraries; explicitly disabling GSS avoids that optional-protocol warning when the intended transport is TLS or local password authentication. This does not disable TLS. [Npgsql 10 release notes](https://www.npgsql.org/doc/release-notes/10.0.html).

## Local evidence and open items

### Deployment routing diagnosis, September 15, 2026

The user's initial deployment at `https://event-tracking-ruby.vercel.app/` returned Vercel's `x-vercel-error: NOT_FOUND` for `/`, `/health/live`, `/health/ready`, and `/shop/index.html`. Those requests did not reach the API.

The repository now includes an explicit container service and catch-all rewrite, following [Vercel's ASP.NET Core deployment guide](https://vercel.com/kb/guide/dot-net-asp-net-on-vercel-with-docker). After redeployment, Vercel invoked the API but startup failed with `Apply database migrations with --migrate before starting the API.`

The existing `20260913062840_InitialPostgres` migration was applied to the configured Neon database using the application operator command `--migrate --storage-info` with the Hosted profile. It created the schema and initialized the profile; no API credentials were provisioned by this command. Subsequent HTTPS checks returned HTTP 200 with `{"status":"alive","profile":"Hosted","durability":"durable"}` from `/health/live` and `{"status":"ready","profile":"Hosted"}` from `/health/ready`. No redeployment was needed after the migration. The operator's `pg_stat_ssl` query returned false through the pooled endpoint; hosted TLS verification remains pending.

The Vercel image now enables `Dashboard__Enabled=true`, and the homepage redirects to `/dashboard/`. Signed-out visitors see the analytics login screen. Before deploying the milestone 4 image, apply its `DashboardAccess` migration and provision a dashboard account using the operator workflow in [MILESTONE_4.md](MILESTONE_4.md). The original development shop remains a separate opt-in feature. These routing changes await redeployment; the health-check evidence above describes the earlier image.

Local verification built and started this Dockerfile, honored `PORT=8181`, ran as UID 1654, committed one queryable event across 12 concurrent requests, and retained it through a container restart. The local PostgreSQL connection reported TLS=false, as expected for that local setup. The smoke script was run with `IdleSeconds=0` locally; no cloud scale-down was simulated or claimed.

| Check | Current result |
| --- | --- |
| .NET image build, startup, non-root user, configured port | Passed locally |
| Concurrent retries and immediate persisted counts | Passed locally |
| Persistence through process/container restart | Passed locally |
| Vercel public deployment and HTTPS health routing | Passed: live and ready return 200, Hosted profile |
| Neon TLS/certificate validation from hosted runtime | Pending runtime verification |
| Actual Vercel cold starts and scale-down recovery | Pending cloud measurements |
| Provider quotas, cleanup scheduling and backup recovery | Pending provider review / milestone 5 |

The free hosted profile removes dependence on continuously running processing. It does not establish production readiness or free-plan capacity for a benchmark dataset. The production-release gate remains milestone 5.
