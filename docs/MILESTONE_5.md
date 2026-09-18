# Milestone 5: hosted operations

Status, September 18, 2026: **M5-01 and M5-02 are implemented locally**. M5-01 covers project administration and account recovery. [M5-02's production security and migration guide](PRODUCTION_SECURITY.md) covers runtime/operator isolation, TLS, restricted service grants, encrypted cookie keys and shared login limits. Milestone 5 as a whole remains unfinished. These changes have not been applied to the hosted database or deployed publicly.

The user requested continuing milestone 5 after milestone 4. Work proceeds in task-sized slices, as specified in the project plan. The Hosted profile is the first release target; unfinished RabbitMQ work in M3-02–04 remains a prerequisite for the planned distributed release.

## Task ledger

| Task | Scope | Status |
| --- | --- | --- |
| M5-01: administration and account recovery | Project roles, membership changes, key issue/rotation/revocation, password reset, account disable/enable, session invalidation, transactional audit records | Implemented and verified locally; API/operator interface |
| M5-02: production configuration and secrets | Least-privilege database roles, operator/runtime separation, TLS verification, protection-key encryption and recovery, shared login abuse limits, security configuration validation, migration command | Implemented locally; [setup and verification](PRODUCTION_SECURITY.md) |
| M5-03: telemetry and alerts | Metrics/traces, error/rejection alerts, storage growth, lag and applicable dead-letter alerts; injected-failure evidence | Next |
| M5-04: deployment and recovery | Deployment/rollback runbook, scheduled external backups and retention, executed restore drill with analytics reconciliation | Pending |
| M5-05: subject data controls | Export/deletion covering identities, events, inbox and all deployed replay paths; erasure suppression and backup expiration policy | Pending |
| M5-06: release evidence | Load/soak/failure tests, quota and cold-start tests, measured operating limits/cost, architecture decisions and incident/demo guide | Pending |

These are local task records, not published external issues. A graphical administration screen, self-service password reset, organization-wide roles, and public registration are not included in M5-01.

## Upgrade and permission model

Apply `20260917145452_HostedAdministration` with the API operator `--migrate` before starting upgraded API/worker instances. It adds `project_memberships.can_manage` (default false), `dashboard_users.session_version` (default zero), and `audit_records`. Existing data and grants are preserved. Existing sessions lack the new version claim and must sign in again. Once upgraded, cookies continue to work across instances using the existing shared Data Protection keys.

| Role | Analytics | Demo writes | Members, keys and audit feed |
| --- | --- | --- | --- |
| viewer | Yes | No | No |
| contributor | Yes | Yes | No |
| admin | Yes | Yes | Yes, for that project |

Roles are derived from `can_demo` and `can_manage`. Existing/bootstrap memberships remain contributors (or viewers if previously restricted); the migration and bootstrap never promote them. Newly created projects make their creator an admin. Any active dashboard account can still create a project, subject to the existing 20-membership limit. `/dashboard-api/session` now includes `canManage` and `role` in each project.

An operator must explicitly grant the first admin on an existing project. Dashboard admins can grant existing, active accounts viewer/contributor/admin access and remove membership. They cannot change their own membership. Project mutations serialize on the project row and recheck authorization after acquiring the lock, so concurrent admins cannot remove one another and leave the project without an admin. Operator account disable can still make a project temporarily unmanaged; use the operator recovery command to restore access.

Membership updates take effect on subsequent requests without a new login. Requests already authorized may finish. Removing dashboard membership does not revoke separately issued API keys; revoke those explicitly when offboarding a credential holder.

## Operator account recovery

The operator already has trusted database access; `Administration:Actor` is a recorded operator label, not an additional authentication credential. Use a private terminal and a database connection supplied by your secret mechanism. Never pass a password as a command-line argument or commit it to settings. `--dashboard-admin` requires a durable profile and can be combined with `--migrate`, but not other operator actions.

For the **local Compose database**, set the connection explicitly so ignored Hosted overrides cannot redirect the operation:

```powershell
$local = Get-Content -Raw .env | ConvertFrom-StringData
$env:ConnectionStrings__Operator = "Host=127.0.0.1;Port=$($local.POSTGRES_PORT);Database=event_tracking;Username=event_tracking;Password=$($local.POSTGRES_PASSWORD);GSS Encryption Mode=Disable"
$env:ASPNETCORE_ENVIRONMENT = 'Testing' # Local operator workflow; configured/remote operations use Production and verified TLS.
$env:DOTNET_ENVIRONMENT = 'Testing'
$env:Storage__Profile = 'Distributed' # Use Hosted only for an already Hosted database.
$env:Storage__MigrateOnStartup = 'false'
$env:Storage__AllowProfileTransition = 'false'
dotnet run --no-launch-profile --project EventTracking.Api -- --migrate
if ($LASTEXITCODE -ne 0) { throw 'Migration failed.' }

$env:Administration__Action = 'grant-admin'
$env:Administration__Username = 'demo' # An existing dashboard account.
$env:Administration__ProjectId = 'a' # An existing membership, not a new grant.
$env:Administration__Actor = 'local-operator'
$env:Administration__Reason = 'Establish the first administrator for project a'
dotnet run --no-launch-profile --project EventTracking.Api -- --dashboard-admin
if ($LASTEXITCODE -ne 0) { throw 'Administration failed.' }
```

Supported actions:

| `Administration__Action` | Additional configuration | Effect |
| --- | --- | --- |
| `grant-admin` | `ProjectId`, existing active account/membership | Grants admin and demo permissions on that project |
| `reset-password` | `Password`, 16–256 characters | Replaces the password hash, increments session version; does not enable a disabled account |
| `disable` | None | Disables login and invalidates every existing session |
| `enable` | None | Enables login with the current password; old cookies remain invalid |

Every action requires `Username`, `Actor` (1–100 characters) and `Reason` (1–500 characters). Actor/reason cannot contain control characters. No password or raw key is written to audit records. Account recovery and its audit record commit in one transaction; audit failure rolls back recovery.

Example password reset using the same explicit database settings:

```powershell
$env:Administration__Action = 'reset-password'
$env:Administration__Reason = 'Recover the local demo account'
$secret = Read-Host 'New password (16–256 characters)' -AsSecureString
try {
    $env:Administration__Password = [Net.NetworkCredential]::new('', $secret).Password
    dotnet run --no-launch-profile --project EventTracking.Api -- --dashboard-admin
    if ($LASTEXITCODE -ne 0) { throw 'Password reset failed.' }
} finally {
    Remove-Item Env:Administration__Password -ErrorAction SilentlyContinue
    $secret.Dispose()
}
```

Bootstrap remains insert-only and cannot undo a reset, disable, or membership removal. Use the existing `--dashboard-provision` workflow to create an account before assigning its roles. Bootstrap intentionally grants contributor access to its explicitly configured projects; use an admin to change those memberships afterward.

## Authenticated administration API

All routes below are relative to `/dashboard-api/projects/{projectId}/admin`. They require an authenticated dashboard admin for the route's project. API Bearer keys cannot authorize them. POSTs also require `X-CSRF-Token` obtained from `/dashboard-api/auth/token` using the same cookie session. Responses use `Cache-Control: no-store`; the existing dashboard rate/body limits apply.

| Method and route | Input | Response |
| --- | --- | --- |
| `GET /members` | None | Up to 200 members: username, disabled state, role |
| `POST /members` | `{ "username": "analyst", "role": "viewer" }` | `204`; roles are viewer, contributor, admin, or remove |
| `GET /keys` | None | Up to 100 active key hashes and permissions; never raw keys |
| `POST /keys` | `{ "permissions": ["ingest"] }` | `200`: generated `key`, `keyHash`, permissions |
| `POST /keys/rotate` | `{ "keyHash": "<64 hexadecimal characters>" }` | `200`: replacement key/hash, same permissions; old key revoked atomically |
| `POST /keys/revoke` | `{ "keyHash": "<64 hexadecimal characters>" }` | `204`: active key revoked |
| `GET /audit` | None | Latest 100 project audit records, newest first |

Keys allow `ingest`, `read`, or both, never dashboard administration. Generated credentials contain 256 random bits; PostgreSQL stores only their SHA-256 hash. Copy the returned key directly into the producer's secret mechanism. It cannot be retrieved again. Lists and audit entries identify credentials by hash. The existing dashboard tracking bundle does not receive server credentials.

Rotation revokes the old credential immediately on commit; requests authenticated before that commit may finish. For a gradual rollout, issue a new key, update producers, verify delivery, then revoke the old one. Rotation is not idempotent: repeating a rotation of the old hash returns `404`. If the response is lost, inspect the active-key list/audit feed, revoke the unknown replacement and issue a fresh key. Retrying issuance may create an additional key; inspect/revoke it as appropriate.

The API limits new grants to 20 memberships per account and 200 members per project. New key issuance allows up to 20 active keys per project; rotation remains possible at that limit. Operator/bootstrap provisioning is trusted and is not a substitute for public onboarding quotas. Invalid input returns `400`, unauthenticated requests `401`, insufficient permission `403`, absent target/active key `404`, and quota/self-change/disabled-target conflicts `409`. Database/audit failures return `503` without committing the administrative mutation.

## Audit behavior and operational limits

Successful project creation, account provisioning, membership changes, key provisioning/issuance/rotation/revocation, and operator account actions write an audit record in their transaction. Startup provisioning emits records only for newly inserted records. Entries contain server time, actor ID or operator label, optional project, action, target ID/hash, and server-constructed details. Failed/denied actions do not create audit rows; request status logs remain the existing source for rejection monitoring.

Project admins see only their project's latest 100 entries. Account-wide recovery records have no project and are available through the trusted operator database workflow. There is no API to edit or delete audit records. This is not a tamper-proof audit store: database owners can change records. M5-02 restricts runtime audit access to select/insert; external audit export, audit retention, and storage-growth alerts remain future operations work. Event retention does not remove audit records or revoked credential records; their storage must be included in capacity planning and future cleanup policy.

Do not roll back to an older API after using password reset or disable/enable: older versions do not enforce the session version. Keep the additive schema in place and fix forward. A necessary rollback requires stopping all API instances and invalidating dashboard cookies with a new shared Data Protection application name before running older code; it also removes this slice's administration behavior. A full tested deployment/rollback runbook remains M5-04.

## Verification

Real PostgreSQL tests cover role enforcement and live changes, CSRF and Bearer/cookie separation, cross-project isolation, key rotation/revocation and secret omission, account recovery across instances, audit-failure rollback, concurrent admin removal, concurrent quota enforcement, and upgrade from the previous schema without privilege escalation. The broader suite also exercises Hosted/Distributed ingestion, workers, persistence, dashboard behavior and existing cookie continuity.

Run with the local PostgreSQL test connection described in [README](../README.md#test):

```powershell
dotnet test EventTracking.slnx --configuration Release
dotnet ef migrations has-pending-model-changes --project EventTracking.Persistence --startup-project EventTracking.Api
git diff --check
```

Verified locally for M5-01: **56 .NET tests passed, no skips**, including seven new administration tests. EF reports no pending model changes, and `git diff --check` passes. A separate command-line smoke on a disposable Hosted database successfully executed `--migrate --dashboard-provision`, `--dashboard-admin` grant-admin and disable, then verified persisted admin access, disabled/session-version state and both operator audit records. The disposable database was removed afterward.

Tests use isolated `m2_test_*` databases; the operator smoke used `m5_operator_smoke_*`. Neither migrated the running local application's database or the hosted database. No frontend files changed in this slice, and browser tests were not rerun. Remote CI and public deployment have not been run.

M5-02 verification, September 18: **73 .NET tests passed with no skips**, plus a full local Production API/TLS PostgreSQL smoke and command-wrapper migration checks. [Detailed results](PRODUCTION_SECURITY.md#local-verification). The existing local application database and hosted database remain unchanged; M5-03 telemetry/alerts is next.
