# Production Deployment & Disaster Recovery Runbook

This document details the production operational lifecycle for EventTracking, covering initialization, automated migrations, runtime privilege separation, key encryption, disaster recovery drills, and rollback runbooks.

---

## 1. Deployment Checklist

### Pre-Deployment Verification
- [ ] Database TLS is enabled with trusted root CA and hostname matching (`SslMode=VerifyFull`).
- [ ] Environment variables configured:
  - `ASPNETCORE_ENVIRONMENT=Production`
  - `ConnectionStrings__Tracking`: Least-privilege runtime connection string (role `event_tracking_api` or `event_tracking_worker`).
  - `ConnectionStrings__Operator`: Administrative connection string used **only** during migration/provisioning tasks.
  - `DataProtection__Certificate__Path` or `DataProtection__Certificate__Base64`: RSA certificate for encrypting ASP.NET Core Data Protection keys at rest.
  - `Storage__Profile=Distributed` (or `Hosted`).
  - `ReverseProxy__TrustForwardedProto=true` (when behind an edge proxy like Vercel or Cloudflare).

### Step 1: Database Migration & Schema Setup
Run database migrations using the operator entrypoint:
```powershell
dotnet EventTracking.Api.dll --migrate
```
*Note: In production, the API and Worker processes run under restricted runtime roles that cannot alter schema.*

### Step 2: Runtime Role Grants & Verification
Ensure least-privilege grants are applied:
```powershell
# For API runtime role:
dotnet EventTracking.Api.dll --grant-runtime --Administration:RoleName=event_tracking_api --Administration:RoleKind=Api

# For Worker runtime role:
dotnet EventTracking.Api.dll --grant-runtime --Administration:RoleName=event_tracking_worker --Administration:RoleKind=Worker
```

### Step 3: Data Protection Key Protection
Encrypt stored session/antiforgery keys with the configured RSA key:
```powershell
dotnet EventTracking.Api.dll --protect-keys
```

### Step 4: Health Check Verification
Verify liveness and readiness probes:
- `GET /health/live` -> `200 OK` `{"status":"alive","profile":"Distributed","durability":"durable"}`
- `GET /health/ready` -> `200 OK` `{"status":"ready","profile":"Distributed"}`
- `GET /metrics` -> `200 OK` (Prometheus scrape format)

---

## 2. Rollback Procedure

In the event of an unhealthy deployment:

1. **Traffic Re-routing:** Immediately revert the edge proxy or container orchestrator to the previous stable release tag.
2. **Backward Schema Compatibility:** Migrations follow expand/contract patterns:
   - Columns are added as nullable or with defaults before application code uses them.
   - Deprecated columns are dropped only in subsequent releases.
3. **Session Invalidation (if compromised):**
   - Bump user session versions via `--dashboard-admin` or operator command to immediately terminate existing cookie sessions.
4. **Outbox Recovery:**
   - Any unpublished broker outbox messages remain stored durably in `broker_outbox` and will resume delivery upon worker restart.

---

## 3. Backup Schedule & Retention Policy

| Backup Type | Frequency | Retention | Target |
|-------------|-----------|-----------|--------|
| Full Dump (`pg_dump -Fc`) | Daily at 02:00 UTC | 30 Days | Encrypted S3 / Cloud Storage |
| Continuous Archiving (WAL) | Continuous (RPO < 5 min) | 7 Days | Streaming WAL Archive |
| Monthly Snapshot | 1st of month | 365 Days | Cold Archive |

---

## 4. Disaster Recovery & Analytics Reconciliation Drill

Disaster recovery must be tested regularly using `scripts/Test-RestoreDrill.ps1`.

### Manual Restore Procedure
1. Create a fresh target database:
   ```bash
   createdb -h <host> -U postgres event_tracking_restored
   ```
2. Restore compressed backup:
   ```powershell
   pwsh ./scripts/Restore-Database.ps1 -BackupPath "./backups/db-2026-09-19.dump" -TargetDatabase "event_tracking_restored"
   ```
3. Reconcile analytics between pre-backup baseline and restored instance:
   - Compare `SELECT count(*), coalesce(sum(stored_bytes), 0) FROM events;`
   - Compare `SELECT count(*) FROM event_identity;`
   - Run fixture reconciliation via `GET /v1/status/outbox` and ClickHouse reconciliation `--reconcile-clickhouse`.
