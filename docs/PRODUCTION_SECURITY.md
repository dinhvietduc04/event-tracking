# M5-02: production configuration and database commands

M5-02 adds enforced production settings, separate operator/runtime database connections, reviewed service grants, certificate-encrypted Data Protection keys, and shared login-attempt limits. These controls are implemented for the current PostgreSQL Hosted and Distributed profiles. Deployment, external backups, monitoring/alerts, and the remaining milestone 5 release criteria are separate work.

## Migrate the database

From the repository root on Windows, with Docker PostgreSQL running:

```powershell
.\migrate-db.cmd
```

The command uses PowerShell 7 and the .NET 10 SDK. It applies pending migrations to the **local** `event_tracking` database using `.env`'s local PostgreSQL password/port, and initializes the Distributed profile on an empty schema. It ignores Hosted connection overrides. It does not create the database, seed accounts/keys, or change an existing storage profile. A repeat run is safe; an already-current schema is unchanged.

Equivalent PowerShell command, also available on Linux/macOS with PowerShell 7:

```powershell
./scripts/Migrate-Database.ps1
# For an already Hosted local database:
./scripts/Migrate-Database.ps1 -Profile Hosted
```

`-Database` selects a different local database for tests. The script restores temporary process environment settings after completion or failure. The Windows wrapper propagates a nonzero exit status on failure. Migration failure must stop deployment.

A configured/remote target must be explicit. Supply `ConnectionStrings__Operator` through your private secret mechanism, then identify the intended host and database:

```powershell
.\migrate-db.cmd -Target Configured -Profile Hosted -ExpectedHost your-direct-db-host -ExpectedDatabase event_tracking
```

Configured mode requires `SSL Mode=VerifyFull;GSS Encryption Mode=Disable` in the operator connection. It never reads connection overrides from `.env` and never falls back to the runtime connection. Host/database mismatch is rejected before connecting or applying migrations. Use the provider's direct database endpoint for operator work. The endpoint must match the certificate hostname and be trusted by the OS or by a supplied `Root Certificate` file.

This version adds migration `20260917151900_SharedLoginLimits`, after M5-01's `HostedAdministration`. Apply migrations, then reapply runtime grants before upgrading runtime instances. The migration command leaves existing runtime grants unchanged so new table access is always an explicit review step.

## Production startup requirements

Production controls apply in every environment except the explicit `Development` and `Testing` environments. `Testing` is used for isolated local HTTP/database regressions; it is not a deployment environment. The local Compose worker now explicitly uses Development, like the local API.

Required API runtime configuration:

| Setting | Value/purpose |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `DOTNET_ENVIRONMENT` | `Production` as well, if configured |
| `Storage__Profile` | `Hosted` for the initial hosted release; `Distributed` when deploying a worker |
| `Storage__MigrateOnStartup` | `false` |
| `Storage__AllowProfileTransition` | `false` |
| `ConnectionStrings__Tracking` | Restricted **API login**, never the schema owner/operator; verified TLS required |
| `AllowedHosts` | Explicit hostnames separated by semicolons; no wildcard, protocol or port |
| `Dashboard__Enabled` | `true` to serve the dashboard |
| `DataProtection__Certificate__Path` **or** `DataProtection__Certificate__Base64` | PFX containing the active RSA private certificate |
| `DataProtection__Certificate__Password` | PFX password, if used; supply as a secret |
| `ReverseProxy__TrustForwardedProto` | `true` only behind a proxy that controls the protocol header |
| `ReverseProxy__ExclusiveIngress` | Must be `true` when trusting forwarded protocol; the container must actually be reachable only through that proxy |

The Vercel image still opts into forwarded protocol, so configure `ReverseProxy__ExclusiveIngress=true` explicitly along with the other production settings before deploying it. The assertion does not configure a firewall or prove the hosting provider's ingress policy. Direct Kestrel TLS deployments leave both proxy settings false. Forwarded hosts and client IPs remain untrusted.

Production requests require HTTPS, except health routes used by internal probes. Insecure API requests return `400`; they are not redirected with credentials. The existing cookies remain Secure/HttpOnly/SameSite=Strict, POSTs require CSRF tokens, and HSTS is enabled for eligible HTTPS hosts. Host filtering uses the explicit `AllowedHosts` list.

Runtime startup rejects Volatile storage, automatic migration/profile transitions, the prototype shop, wildcard hosts, operator connection settings, dashboard bootstrap passwords, project key provisioning settings, and any `Administration` settings. Remove those environment entries entirely from the runtime deployment. Empty operator/administration entries can still be rejected; the provided scripts remove them explicitly when restoring an absent variable.

Worker runtime configuration uses `DOTNET_ENVIRONMENT=Production`, `Storage__Profile=Distributed`, and `ConnectionStrings__Tracking` with a separate **worker login**. It needs no dashboard secrets or certificate. The worker rejects operator/provisioning settings, automatic migration, and profile transitions too.

All production database connections, including operator commands, require `SSL Mode=VerifyFull;GSS Encryption Mode=Disable`. `Require` encrypts transport without the hostname/chain verification this application requires. Detailed PostgreSQL errors, parameter logging and persisted connection security information are rejected. See [Npgsql security modes](https://www.npgsql.org/doc/security.html).

Startup opens the connection using those validated settings, so a missing TLS endpoint, wrong hostname, or untrusted certificate prevents startup. `--storage-info` reports `tlsVerified` for that verified client connection; the existing `tls` field reports the database backend's `pg_stat_ssl` state. Behind a TLS-terminating pooler these describe different connections. This does not independently attest to encryption between the pooler and PostgreSQL.

## Create and grant restricted service logins

Use a dedicated application database. The operator owns the application schema and has migration/provisioning rights. Create separate logins through the provider or an operator `psql` session; keep passwords out of command-line arguments and SQL files:

```sql
CREATE ROLE event_tracking_api LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE event_tracking_worker LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
```

Use `\password event_tracking_api` and `\password event_tracking_worker` interactively in `psql`, or the provider's secret workflow, to set passwords. Hosted deployments need only the API login. Do not grant either login ownership or membership in another role.

In a private operator shell, with `ConnectionStrings__Operator` and `Storage__Profile` set for the intended database:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:DOTNET_ENVIRONMENT = 'Production'
$env:Administration__RoleName = 'event_tracking_api'
$env:Administration__RoleKind = 'Api'
dotnet run --configuration Release --no-launch-profile --project EventTracking.Api -- --grant-runtime
if ($LASTEXITCODE -ne 0) { throw 'Runtime grant failed.' }
# For Distributed deployments, repeat with RoleName=event_tracking_worker and RoleKind=Worker.
```

The command operates only on an existing restricted LOGIN role. It rejects superusers, role/database creators, replication/BYPASSRLS roles, role memberships, and database/public-schema/table owners. It does not create or print passwords. Its changes and audit record commit atomically.

`--grant-runtime` clears the selected role's database/schema/table/sequence/column grants in the current database before applying the reviewed set. It also removes PUBLIC table/sequence access, PUBLIC schema creation, and PUBLIC temporary-table privileges. **Use a dedicated application database**: these PUBLIC changes affect other users of that same database. No future tables receive automatic runtime grants. Re-run the command after migrations, and review the allowlist in `RuntimeDatabaseAccess.cs` when adding schema.

| Capability | API login | Worker login | Operator |
| --- | --- | --- | --- |
| Event ingestion / analytics | Yes | Project committed inbox work into events | Yes |
| Dashboard memberships / API credentials | Application-required reads/writes | No access | Yes |
| Account password/disabled/session-version changes | No write access | No access | Yes |
| Audit records | Select/insert; no update/delete | No access | Yes |
| Cookie key storage | Select/insert; no update/delete | No access | Yes, including wrapping/recovery |
| Login rate counters | Select/insert/update | No access | Yes |
| Event/source deletion and retention | No | No | Yes |
| Schema migrations / profile changes | No | No | Yes |

Some tables have an `UPDATE(id)` column grant solely because PostgreSQL row-locking reads require an update privilege. The worker can update only `inbox.processed_at`, not source payloads. Both services can lock the storage-state row but cannot update its `profile` column. Production startup verifies table/column/sequence privileges and rejects unexpected grants or grant options. This role separation limits a runtime compromise; project isolation is still enforced by application authorization, not database row-level security.

Rotate a database service credential by creating a new restricted login, applying the same service grants, updating the runtime secret and replacing instances. Verify operation with the new login before disabling the old login. Existing pooled sessions can survive a password change, so retirement also requires draining/replacing those instances or terminating the old role's sessions through the operator. Do not put the operator connection in API/worker deployments.

## Encrypt, rotate and recover cookie keys

Supply an RSA PFX with its private key (minimum 2048 bits). The active certificate must be within its validity period. Certificates are loaded into ephemeral key storage; PFX bytes are cleared after import. Use a secret-mounted path or a provider secret containing Base64 PFX data. `.secrets/`, `.pfx`, and `.p12` files are excluded from Git and Docker build contexts. Put PEM private material under `.secrets/` too; do not embed secrets in Docker build arguments or source settings.

New database key records use ASP.NET Core certificate encryption. Existing plaintext records need an explicit conversion: simply enabling encryption only changes future key writes. This uses the framework's [Data Protection certificate encryption and recovery configuration](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).

For first adoption:

1. Stop all API instances that could write plaintext keys. Back up the database and the active private certificate through separate protected channels.
2. In the operator shell, configure `DataProtection__Certificate__Path` or `Base64` and its password. Run:

   ```powershell
   dotnet run --configuration Release --no-launch-profile --project EventTracking.Api -- --protect-keys
   if ($LASTEXITCODE -ne 0) { throw 'Protection-key conversion failed.' }
   ```

3. Start all API instances with the restricted API connection and the same certificate. Production startup rejects retained plaintext keys or keys it cannot decrypt. Key IDs/lifetimes remain intact, preserving otherwise-valid cookies and CSRF tokens.

`--protect-keys` locks the key table, encrypts existing plaintext and rewraps already certificate-encrypted records using the active certificate, and records the count/certificate thumbprint in an audit transaction. It preserves revocation records. Unknown encryption formats or missing old private certificates fail the transaction. Never delete key records as a substitute for migration or recovery.

For certificate rotation, schedule a coordinated API restart. Keep the old private certificate available via:

```text
DataProtection__PreviousCertificates__0__Path=<old certificate secret path>
DataProtection__PreviousCertificates__0__Password=<old certificate password>
```

`Base64` is also supported for each previous certificate. Set the new active certificate, retain the old one as previous, stop old instances, run `--protect-keys`, then start the API instances consistently. Expired previous certificates remain usable for decryption; they cannot serve as the active encryption certificate. Recovery tests cover this sequence and prove that cookies remain decryptable after rewrapping and restart.

Keep each old private certificate and its password for as long as any retained backup may contain keys encrypted by it. A database-only backup is insufficient to recover encrypted cookie keys. Restoring without the matching certificate must fail, rather than silently discard old sessions/keys. A full external backup/restore schedule and tested deployment rollback remain M5-04 work.

## Shared login limits

Before password verification, every durable API instance uses PostgreSQL to acquire an attempt from a shared global budget (120 attempts per 60-second window) and a normalized-account bucket (10 attempts per 60-second window). Each window starts with its first attempt and resets lazily using database time. Failed and successful attempts both count. Counters survive instance restarts; database failure returns `503` instead of bypassing the limit.

Account names hash into 4096 fixed buckets, plus one global bucket, so arbitrary usernames cannot grow an unbounded table. Bucket collisions conservatively share a budget. Raw usernames, passwords and forwarded client IPs are not stored in these counters. Saturated counters are capped, and no scheduled cleanup is needed. Rejection returns `429` with a conservative `Retry-After: 60`. Existing per-process IP/request limits still apply; a proxy connection may share that local budget. These limits favor the small personal deployment and can reduce login availability under attack; they are not a commercial availability guarantee.

## Local verification

Run the real PostgreSQL test suite as documented in README. The new tests verify strict configuration rejection, role restrictions and grant repair, shared attempt persistence, encryption of legacy key records, certificate rotation and recovery.

To exercise actual Production startup and TLS in isolation:

```powershell
./scripts/Test-ProductionProfile.ps1
```

The script creates a disposable PostgreSQL 17 container, a synthetic certificate authority and TLS certificate, and a restricted runtime login. It uses the configured migration command, checks wrong-host/untrusted-CA rejection, starts a loopback Production API, and verifies HTTPS enforcement, Secure login, encrypted cookie keys, admin key issuance and durable analytics. It stops/removes its container and temporary secrets afterward. It never reads a hosted connection. This is local production-profile evidence, not a Vercel/Neon deployment or external restore drill.

Verified September 18, 2026: **73 .NET tests passed, no skips**; the full production-profile smoke above passed. The local `migrate-db.cmd` wrapper and PowerShell migration script also passed against a disposable database: all five migrations applied, a repeat run was idempotent, remote overrides were ignored, no accounts were bootstrapped, and the caller's environment was restored. EF reports no pending model changes; Compose validation and `git diff --check` pass. CI now includes the production-profile smoke, but remote CI has not been run. Existing local application data and the hosted database were not migrated.
