# Project Status

## Canonical product state
- PRD v1: FROZEN
- System Architecture v1: FROZEN baseline
- Architecture Invariants: 83
- First SaaS connector: monday.com
- Target cloud: Azure
- MVP OS Agent: Windows

## Development state

### Gate 1A — Repository & Solution Skeleton
Status: **PASS — runtime verified and product-owner approved**

### Gate 1B — Local Development Environment
Status: **PASS — runtime verified and product-owner approved**

### Gate 1C — Core Platform Skeleton
Status: **PASS — implemented, tested and product-owner approved**

Tenant context, identity abstractions, correlation context, clock abstraction, result/error model,
feature flags, audit contracts and an operation context. Contracts and primitives only: no database
schema, no authentication flow, no monday logic, no AI, no UI.

### Gate 1D — Database Foundation
Status: **PASS — implemented, tested and product-owner approved**

PostgreSQL operational foundation: controlled EF Core migrations, tenant-aware persistence,
PostgreSQL row-level security as a second isolation boundary, pgvector enabled, durable raw-receipt
(inbox) and transactional outbox storage, and base persistence abstractions. Four tables only; no
event pipeline, no Service Bus publishing, no Work Memory, no AI, no authentication.

### Gate 1E — Azure Infrastructure as Code
Status: **PASS — Dev deployed, verified and product-owner approved (2026-09-27)**

Modular Bicep for the Dev and Synthetic environments: resource group per environment, VNet with
delegated subnets, Log Analytics, Application Insights, private ACR (admin disabled), Key Vault
(RBAC, per-secret access), Blob Storage (no shared keys), Service Bus Standard with the three canonical
queues (local auth disabled), private PostgreSQL Flexible Server with the `vector` extension
allow-listed, three per-workload managed identities with 13 least-privilege role assignments,
VNet-integrated Container Apps environment, Web and Worker apps, and a manual migration job. All 22
resource types use GA API versions. Dev/Synthetic isolation is proven by diffing the compiled parameters.

Dev is deployed in `rg-wce-dev-neu`, North Europe (Israel Central lacks Azure Container Apps; West Europe
rejects new customers on this subscription). Deployment attempt 1 failed at the Web and Worker apps
because no role assignment existed on the Application Insights Key Vault secret; after that fix and the
declaration of the `Microsoft.Storage` service endpoint Flexible Server adds to its subnet, attempt 2
succeeded: 22 of 22 module deployments, Web and Worker Healthy/Running, PostgreSQL Ready, 13 role
assignments present, Web root HTTP 200 with the placeholder image.

Carried forward, not Gate 1E acceptance: real application health endpoints, runtime-role bootstrap,
database connectivity and telemetry wait for real images (Gate 1F). Synthetic IaC exists and remains
isolated but is not deployed; its Israel Central parameter cannot host Container Apps and needs a later
region/subscription decision (the Free Trial allows one Container Apps environment per region).

### Gate 1F — CI/CD & Identity
Status: **NEXT / NOT STARTED — requires explicit product-owner authorization**

### Gate 1G onward
Status: **NOT STARTED unless explicitly approved by the product owner**

## Verified evidence for Gate 1A / 1B

Verified on 2026-09-26 on a Windows 11 machine with .NET SDK 10.0.400, Docker 29.7.2
and Docker Compose v5.5.0. All commands below were executed, not inspected.

| Check | Result |
|---|---|
| `dotnet restore` | PASS — 11/11 projects |
| `dotnet build` | PASS — 0 warnings, 0 errors |
| Architecture dependency rules | PASS — all 7 production boundaries valid |
| Solution membership | PASS — 11 projects on disk, 11 in solution |
| Docker local smoke test (`eng/dev.ps1 up`, clean slate) | PASS |
| PostgreSQL | healthy — PostgreSQL 17.11, `pg_isready` accepting connections |
| Service Bus emulator | healthy — `/health` 200, queues `event-processing`, `ai-jobs`, `action-jobs` created |
| Web readiness | healthy — `/health/live` 200, `/health/ready` 200 |
| Worker dependencies | ready — `--check-dependencies` reports `Ready:true` |
| Architecture/PRD contradictions | none |

Two local Service Bus emulator configuration ceilings were repaired to let the emulator
start: `DefaultMessageTimeToLive` (max 1h) and `DuplicateDetectionHistoryTimeWindow`
(max 5m). These are emulator-only limits and do not constrain the Azure Service Bus
configuration defined in Gate 1E.

## Verified evidence for Gate 1C

Verified on 2026-09-27 in the same environment.

| Check | Result |
|---|---|
| `dotnet restore` | PASS |
| `dotnet build` | PASS — 0 warnings, 0 errors |
| `dotnet test` (whole solution) | PASS — 188 tests, 0 failed, 0 skipped |
| Architecture verification (script and test suite) | PASS |
| Gate 1B local environment regression | PASS |
| Correlation middleware | PASS — automated tests plus a live HTTP call |
| `AUTO_EXECUTION_FROM_LEARNING` | OFF by default |
| Canonical identity independent of email | PASS — asserted across the whole Core assembly |
| Tenant context not derivable from untrusted request data | PASS — type system plus a source scan |
| Audit contracts free of raw work content | PASS — by member name and by member type |
| Architecture/PRD contradictions | none |

Test infrastructure now exists. `xunit.v3` is the single test dependency; `dotnet test` is opted
into the Microsoft.Testing.Platform runner through `global.json`, because the .NET 10 SDK removed
the VSTest path for such projects.

See `docs/gates/gate-1C-report.md` for the full report.

## Verified evidence for Gate 1D

Verified on 2026-09-27 in the same environment.

| Check | Result |
|---|---|
| `dotnet restore` | PASS |
| `dotnet build` | PASS — 0 warnings, 0 errors |
| `dotnet test` (whole solution) | PASS — 230 tests, 0 failed, 0 skipped |
| Architecture verification (script and test suite) | PASS |
| Gate 1B local environment regression | PASS from a clean slate |
| Migration from an empty database | PASS — re-running is a no-op |
| RLS read / write / delete isolation | PASS |
| Mismatched tenant insert | PASS — refused |
| Missing tenant context | PASS — fails closed |
| Connection-pool tenant leakage | PASS — proven with a single-connection pool |
| Raw receipt deduplication | PASS |
| Transactional outbox | PASS — commits and rolls back together |
| pgvector | PASS — extension 0.8.6, real vector operation |
| UTC persistence | PASS |
| Secret scan | PASS |
| Architecture/PRD contradictions | none |

Two operational notes for developers:

- The local PostgreSQL image is now `pgvector/pgvector:pg17` (same PostgreSQL 17.11). Plain
  `postgres` images ship no vector extension. Production remains Azure Database for PostgreSQL
  Flexible Server, where pgvector is enabled per server. Switching the image requires a one-time
  `./eng/db.ps1 reset`.
- The application connects as a non-owner, non-superuser role (`workcontinuity_app`), because
  PostgreSQL row-level security is bypassed by superusers. The schema owner is used only for
  migrations.
- `dotnet test` now requires the local stack to be running, because the database tests are real
  integration tests and fail rather than skip when PostgreSQL is unreachable.

See `docs/gates/gate-1D-report.md` for the full report.

## Verified evidence for Gate 1E

Verified on 2026-09-27. Azure CLI 2.90.0, Bicep CLI 0.47.16.

| Check | Result |
|---|---|
| `az bicep build` (main + both parameter files) | PASS — 0 warnings, 0 errors |
| `az bicep lint` | PASS — 0 diagnostics |
| Preview API versions | none |
| Repository secret scan | PASS |
| `dotnet build` / `dotnet test` | PASS — 230/230 |
| Architecture verification | PASS |
| Gate 1B regression (clean slate) | PASS |
| Azure authentication | tenant-scoped `az login` (MFA enforced); Free Trial subscription nominated by the product owner |
| Region eligibility | Israel Central rejected (no Container Apps); West Europe rejected (not accepting new customers); North Europe selected after full validation |
| Azure validation / what-if (Dev, North Europe) | PASS; what-if reviewed before each attempt |
| Dev deployment | attempt 1 FAILED (missing role assignment on the Application Insights secret); **attempt 2 SUCCEEDED** — 22 of 22 modules |
| Post-deployment verification (read-only) | Web and Worker Healthy/Running; PostgreSQL Ready; 13 role assignments; subnet endpoint present; Web root HTTP 200 (placeholder) |
| Synthetic | IaC defined and isolated; **not deployed** |

See `docs/gates/gate-1E-report.md` for the full report: region selection, both deployment attempts, the
post-deployment verification, and the follow-ups carried into later Gates (runtime-role bootstrap job,
real images, Synthetic deployment decision).

## Immediate recommended next action in Claude Code

Gate 1A through Gate 1E are closed. Gate 1F (CI/CD & Identity) is next and has not started; do not begin
it, and do not deploy or change Azure resources, without explicit product-owner authorization.
