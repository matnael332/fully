# Project Status

## Canonical product state
- PRD v1: FROZEN
- System Architecture v1: FROZEN baseline
- Architecture Invariants: 83
- First SaaS connector: monday.com
- Target cloud: Azure
- MVP OS Agent: Windows
- Premium architecture (`docs/premium/`): FROZEN — Premium Gate P0 PASS (2026-09-29); subordinate to the Core documents; nothing implemented yet

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
allow-listed, three per-workload managed identities with 13 least-privilege role assignments (16 in
the resource group since Gate 1F-D added the GitHub deployment identity's three),
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
Status: **PASS — implemented, verified in Azure and product-owner approved (2026-09-27)**

GitHub Actions CI on every push and pull request to `main`: restore, build with warnings as errors,
architecture and local-environment verifiers, the full test suite against PostgreSQL 17 + pgvector with
the runtime role, Bicep build and lint, and Release container images with start-up smoke tests. Manual,
`main`-only continuous delivery (`CD Dev`) publishes Web and Worker images tagged with the exact commit
SHA to the Dev registry and updates only the two Container Apps' images by a template-only PATCH.
Authentication is GitHub OIDC to a dedicated user-assigned identity with a federated credential bound to
this repository and `main`; no long-lived Azure credential or client secret exists in GitHub. The
identity holds AcrPush on the registry and Container Apps Contributor on the Web and Worker apps only;
Managed Identity Operator was proven unnecessary.

Guards: immutable SHA tags (an existing tag is reused, never overwritten); an image-drift guard in
`eng/infra.ps1` (infrastructure redeploys adopt the live Web, Worker and migration-job images unless
explicitly overridden); a dedicated migration-job image parameter; a privilege-boundary assertion (the
CD identity must be denied reading the migration job); refusal to deploy while the Web app still carries
the placeholder configuration; runners pinned to `ubuntu-24.04`. Sub-gates 1F-A (design), 1F-B
(repository), 1F-C (CI), 1F-D (deployment identity) and 1F-E (continuous delivery) are each PASS.

Real images run in Dev: Web `/health/live` returns 200 behind a liveness probe on port 8080; the Worker
is Healthy with no restarts; the migration job kept the placeholder image and was not executed until Gate 1G.
The first publish-only CD run and the first `deploy: true` CD run both succeeded and were verified
read-only in Azure.

Carried forward to Gate 1G (all delivered there except the Synthetic decision): Azure-aware readiness
(`/health/ready`; probe parameter off at the time), the
runtime-role bootstrap job, the first migration run, database connectivity, telemetry and OpenTelemetry,
and the Synthetic region/subscription decision. A real image change through CD (the new-revision path)
was first exercised by CD run 36377312213 in Gate 1G; the privilege-boundary assertion passed in run 36330256517.

### Gate 1G — Foundation Hardening
Status: **PASS — implemented, verified in Azure and product-owner approved (2026-09-28)**

Azure-aware readiness: Web `/health/ready` and the Worker heartbeat prove PostgreSQL by connecting as the
runtime role and Service Bus by a side-effect-free link on `ai-jobs`, each under the workload's own
managed identity, with reason codes only in the response. A dedicated runtime-role bootstrap job
(`caj-wce-dev-neu-bootstrap`, manual, jobs identity, password passed only as a bind parameter) created
`workcontinuity_app` in Azure and verified its login; the migration job then applied the two Gate 1D
migrations. Each job has executed exactly once. OpenTelemetry sends requests (Web) and dependencies, traces
and logs (both roles) to Application Insights, gated on the connection string. CI runs a pinned gitleaks
scan; Security tests grew to 58 (workflows, Bicep boundaries, environment isolation, readiness safety);
the full suite is 266 tests. The Dev readiness probe is enabled (`webReadinessProbeEnabled = true`) and
the Web runs revision `--0000003` Healthy behind it; the fresh-environment smoke test was satisfied by the
local clean-slate smoke (product-owner decision).

Non-blocking observation, carried as a post-Gate-1 hardening follow-up: the first readiness call after a
container start can spend its 5 s Service Bus budget on managed-identity token acquisition (one 503 was
observed before the probe existed; the probe's failure threshold of 6 tolerates it). Other follow-ups:
the Web image's `libgssapi_krb5` stderr line, Application Insights local-auth hardening, the Synthetic
region/subscription decision, and the Free Trial upgrade before 2026-10-27.

### Gate 1 — Foundation
Status: **PASS — Gates 1A through 1G closed (2026-09-28)**

### Gate 2 — Event Backbone
Status: **PASS — closed 2026-09-29. 2A PASS (`7ba941a`), 2B PASS (`120183b`), 2C PASS (`189f173`), 2D PASS (`f97d22d`), 2E PASS (`e165852`), 2F PASS (`259be35`), 2G PASS (`59f74e1`)**

Scope, the nine product-owner design decisions and the 2A–2G decomposition are recorded in
`docs/gates/gate-2-scope.md`. Each sub-gate stops at an owner boundary before the next begins. 2A adds
the canonical event contract, its validation and quarantine reasons, the CloudEvents attribute mapping
and the PRD §5 authority table to `App.Core`, with no database, infrastructure, host or Azure change.
2B adds the durable in-process ingress: tenant resolution from a registered connection through one narrowly
scoped database function, source verification that fails closed, and the raw receipt with its outbox
marker committed before acknowledgement. It publishes nothing to Service Bus and changes no Azure resource.
2B is PASS: commit `120183b`, CI run 36411854002 green (359 tests: 356 passed, 0 failed, 3 Service Bus emulator tests skipped by design). Azure Dev does not
yet have the Gate 2B migration or schema; applying it is a separate product-owner approval. 2C adds the
transport path. An outbox relay finds tenants with pending work through one more narrowly scoped database
function, claims each tenant's rows under that tenant's own row-level security, and marks them published only
after Service Bus accepted them. A versioned, content-free broker envelope carries deterministic message ids
and per-tenant sessions. A transport-level consumer validates every message before it restores the message's
tenant, and settles only after the downstream handler reports, with bounded retries and dead-lettering.
Re-drive is explicit and bounded. 2C is registered in no host and changes no Azure resource.
2C is PASS: commit `189f173922cf4d2adf04ee4e426b5ed5069470c2`, CI run 36425641730 green (444 tests: 437
passed, 0 failed, 7 Service Bus emulator tests skipped by design, all of which passed locally). Azure Dev has
neither the Gate 2B nor the Gate 2C migration or schema; applying them is a separate product-owner approval.
Owner decision: 2C uses the tenant as the Service Bus session, the conservative correctness-first choice, and
2D must explicitly revisit whether finer-grained per-work-item ordering or session semantics are required once
normalized object identity exists. 2D adds the processing pipeline and the event ledger. Each receipt is
normalized through a source-neutral normalizer. The pipeline validates the canonical event, resolves its
authority, and orders every object field by the source's own evidence under row locks. It records one
append-only, metadata-only ledger entry per receipt, which makes redelivery a no-op, and quarantines whatever
must never reach the Snapshot. Only an accepted event is handed on, in memory, for 2E. No Snapshot is
written, no normalizer is registered in production, 2D is registered in no host, and no Azure resource
changes. 2D is PASS: commit `f97d22dc133b8cf1813ebaad6f0771d8bd0cdf06`, CI run 36439994562 green (493 tests:
486 passed, 0 failed, 7 Service Bus emulator tests skipped by design; every 2D test ran and passed in CI).
Azure Dev has none of the Gate 2B, 2C or 2D migrations or schema; applying them is a separate product-owner
approval. Owner decisions at the 2D review: SessionId remains TenantId, with database ordering and idempotency
as the source of truth, and finer-grained sessions may be revisited later only if real throughput or metrics
justify it. The 5-minute source clock-skew tolerance is approved for now; it remains configurable and must be
validated against real connector behavior later. Any authority conflict quarantines the whole event, and no
authoritative subset is projected. 2E adds the Snapshot projection. An accepted result's advanced fields
become Snapshot facts in the same tenant transaction as the 2D ledger entry, so both commit together or not at
all, and projection re-decides neither authority nor ordering. Each fact keeps its value, the authority rule,
its source and object, the canonical event, the provider event id and revision, and the source, observed and
processed times, from which freshness is derived without an invented threshold. Each object keeps one version
per accepted event. A field value must now be bounded, single-line text; any other value quarantines the event
as bad schema. The Snapshot keeps no before value, actor, reference or payload, and no Snapshot row is ever
deleted. 2E is registered in no host and changes no Azure resource. 2E is PASS: commit
`e165852e4a659e49107571a9bcf84c9a1329a056`, CI run 36449846577 green (524 tests: 517 passed, 0 failed, 7 Service
Bus emulator tests skipped by design; every 2E test ran and passed in CI). Azure Dev has none of the Gate 2B,
2C, 2D or 2E migrations or schema; applying them is a separate product-owner approval. Owner decisions at the
2E review: Snapshot projection stays in the same database transaction as the 2D ledger write, which atomicity
and crash safety require. The canonical field-value rule is approved: NULL means the source cleared the field,
invalid values are rejected and quarantined, and source facts are never silently truncated or rewritten. The
1,024-character value limit is approved for 2E and stays centralized in `EventText.MaxValueLength`; Gate 3 must
validate it against real connector data and widen it explicitly if needed, never truncating. 2F adds raw-content
retention and the Event Backbone's metrics. A receipt's raw content, its payload and payload fingerprint, is due
for purge once the database's own clock shows it 28 days past the server-side receipt time, and must never be
older than the 30-day hard limit. The database clock stamps every receipt time and alone decides and measures
every age; neither an ingress host's nor the purge Worker's clock, nor a provider's times, ever decide. An explicit,
bounded and idempotent Worker purge
(`--purge-raw-content`) clears the content of due receipts in every processing state and keeps the receipt row,
the ledger, the ordering state, the Snapshot and the outbox marker, so ingress and processing idempotency hold
after the purge; a receipt whose content was purged before it was processed is refused and dead-lettered
visibly. The purge finds tenants through one more narrowly scoped database function, which takes no cutoff,
returns tenant ids only and never reports content younger than the 28-day purge age by the database's own clock,
and clears each tenant's rows under that tenant's own row-level security. Under the hourly schedule required at
activation, content is never older than 28 days 1 hour 21 minutes while every run succeeds; a purge outage of
about the 48-hour lead margin or longer can pass the 30-day limit, which is reported as overdue and then
limit-exceeded content, never claimed away. One meter, `WorkContinuity.EventBackbone`, reports ingress, relay,
transport, processing, Snapshot and retention metrics with bounded tags only, through the existing Application
Insights pipelines. Nothing schedules the purge, and 2F changes no Azure resource. The 2F reviews of 2026-09-29
approved nine decisions, recorded in the 2F Definition of Done, the last two making database time the authoritative
retention clock and the source of the receipt time itself. 2F is PASS: commit
`259be35ff3649e467266103acd9e95a73d65ae54`, CI run 36542058355 green (584 tests: 577 passed, 0 failed, 7 Service
Bus emulator tests skipped by design; every 2F test ran and passed in CI). Azure Dev has none of the Gate 2B to 2F
migrations or schema; applying them, and activating the purge with its hourly job and alerts, are separate
product-owner approvals. 2G (acceptance suite, crash recovery, tenant isolation and the Gate report) adds
whole-path acceptance, crash-recovery, tenant-isolation and local emulator tests, their mutation and vacuity
evidence, and the Gate 2 report (`docs/gates/gate-2-report.md`), with no production code change. 2G is PASS: commit
`59f74e16e1332f70aca9924967ee082e84b798e8`, CI run 36552321567 green (613 tests: 604 passed, 0 failed, 9 Service Bus
emulator tests skipped by design; every other 2G test ran and passed in CI; 613/613 locally with the emulator).
**Gate 2 is PASS and closed (2026-09-29):** duplicate, delayed and out-of-order events do not corrupt the Snapshot,
and no event is silently lost. The risks and deferred items the report lists stay open: nothing of Gate 2 runs in
Azure yet, reconciliation is Gate 3, and dead letters have no runbook, alert or UI yet.

### Premium Gate P0 — Architecture Freeze
Status: **PASS — ARCHITECTURE FROZEN (closed by the product owner on 2026-09-29)**

Fully Premium is a second capability layer of the same product: bounded autonomous execution on top of Core, reusing
Core and never creating a second Source of Truth. P0 froze its architecture, policies and Gate plan in `docs/premium/`
and audited them against every frozen Core document in `docs/gates/gate-P0-report.md`: no unresolved contradiction;
one intentional Premium policy difference, the Premium Autonomous Action Policy, from PRD v1 §7 and §9 and the
approval flow of System Architecture v1 §6; and three extensions. The product owner approved decisions D1 to D4 and
clarification C1 (structured system state as a Premium v1 information source) and corrected the audit: invariant 35
(Approve All) is COMPATIBLE. Nothing of Premium is implemented; each Premium rule takes effect only when its Premium
Gate passes. P0 is a planning prerequisite to Core Gate 3, not a technical dependency. Premium Gates P1 to P11 are
planned and NOT STARTED.

### Gate 3 — monday Connector
Status: **IN PROGRESS — authorized 2026-09-29; 3A PASS (approved 2026-09-30); 3B PASS (approved 2026-09-30); 3C PASS (approved 2026-09-30); 3D PASS (approved 2026-10-01); 3E PASS (approved 2026-10-02); 3F NEXT / NOT STARTED; 3G not started; later Gates not started**

Gate 3 builds real monday authentication, webhooks, source mapping, API reconciliation and initial sync on top of
the Gate 2 Event Backbone, reading and ingesting only. Scope, the verified monday platform baseline, sub-gates 3A to
3G, the Definition of Done, the Premium architecture §20 constraints and the Gate 2 items it must resolve:
`docs/gates/gate-3-scope.md`. 3A delivered the provider-neutral connector contract in Core and the read-only monday
client foundation (773 tests locally, 32/32 mutants caught); nothing runs against a real monday account or in Azure.
3B delivered the monday OAuth 2.1 connection lifecycle under owner decisions D3B-1 to D3B-3 and review decisions R3B-1
and R3B-2 (986 tests locally, 91/91 mutation and vacuity checks) and is PASS, approved on 2026-09-30; nothing runs
against a real monday account or in Azure. 3C delivered monday webhook intake under owner decision
D3C-1 (intake only: no real webhook registration, no `webhooks:write`, no monday write) and is PASS, approved on
2026-09-30 (1,167 tests locally, 45/45 mutation checks); nothing runs against a real monday account or in Azure.
3D delivered monday source mapping and normalization under owner decisions D3D-1 to D3D-8 and is PASS, approved on
2026-10-01 (1,291 tests locally, 24/24 mutation checks): only status-column changes on items are mapped, and every
monday webhook value is held as unconfirmed for reconciliation through the accepted `SourceValueConfirmation` Core
extension; nothing runs against a real monday account or in Azure. 3E (API reconciliation and authoritative reread)
was authorized on 2026-10-01 under owner decisions D3E-1 to D3E-9, continued under option A (D3E-10 and D3E-11),
and is PASS, approved on 2026-10-02 (1,420 tests locally; mutation checks 38/38, 32/32 and 13/13): only an
authenticated monday API reread confirms a value, transient and access failures are never held, and a read overtaken
by a reauthorization is discarded and retried; nothing runs against a real monday account or in Azure. The separate
3B maintenance commit `273faa6` stopped a callback refused as a replay from deleting the winning completion's PKCE
verifier. 3F (initial sync, watermark and buffered replay) is NEXT / NOT STARTED.

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

## Verified evidence for Gate 1F

Verified on 2026-09-27. Every GitHub run and Azure state below was executed or read, not inspected.

| Check | Result |
|---|---|
| CI on `main` (build, verifiers, full test suite with PostgreSQL 17 + pgvector, Bicep build/lint, Release image smoke) | PASS — runs 36317953496, 36323519792, 36326066378, 36327135490, 36329536123; 230 tests, 0 failed, 0 skipped on the latest |
| Security tests (`App.Security.Tests`) | PASS — 35/35 after every change |
| Azure credentials in GitHub | none — seven non-secret repository variables; OIDC federation only |
| Deployment identity | `id-wce-dev-neu-github`, federated credential `github-main` (immutable subject format), AcrPush on the registry, Container Apps Contributor on the Web app and on the Worker app; no Managed Identity Operator; 16 role assignments in `rg-wce-dev-neu` |
| Publish-only CD run (`deploy: false`) | PASS — run 36326240690; both images built and pushed once; deploy job skipped |
| First real-image rollout (owner, `eng/infra.ps1 deploy -Approve`) | PASS — `wce-dev-20260927-175625`, 22 nested deployments, 0 failed operations; what-if reviewed: Web image + port 8080 + liveness probe, Worker image, migration job no change |
| First `deploy: true` CD run | PASS — run 36328631074; tags reused; exactly two Container App writes in the activity log; verified |
| Final no-op `deploy: true` CD run (workflow at `8a7d404`) | PASS — run 36330256517; migration-job read denied (`AuthorizationFailed`), assertion passed; tags reused; two app writes; no new revision |
| Web in Dev | revision `ca-wce-dev-neu-web--0000001` Healthy/Running, real image, `GET /health/live` → 200 |
| Worker in Dev | revision `ca-wce-dev-neu-worker--0000001` Healthy/Running, real image, 0 restarts |
| Migration job | placeholder image, own image parameter, **0 executions**, unchanged by every CD run |
| Guards | immutable SHA tags; image-drift guard; privilege-boundary assertion; placeholder refusal; `ubuntu-24.04` and SHA-pinned actions |
| Synthetic | untouched (compiled parameters byte-identical) |
| Architecture/PRD contradictions | none |

See `docs/gates/gate-1F-report.md` for the full report: identity and RBAC, both workflows, the guards,
every run and deployment, the problems found and fixed, and the items carried into Gate 1G.

## Verified evidence for Gate 1G

Verified on 2026-09-28. Every GitHub run, job execution and Azure state below was executed or read, not
inspected.

| Check | Result |
|---|---|
| CI on `main` | PASS — runs 36374164214 (`fc9bb3e`) and 36382627242 (`450c13d`): 266 tests, 263 passed, 3 skipped (emulator tests, by design), 0 failed; gitleaks 8.30.1 no leaks. Run 36373040147 (`9651ed3`) FAILED because the runner exports `ACCEPT_EULA=Y` globally; fixed in `fc9bb3e` |
| Local full suite (with the emulator) | PASS — 266/266 |
| Security tests (`App.Security.Tests`) | PASS — 58/58 after every change |
| Publish-only CD run 36374505337 (`deploy: false`) | PASS — both `fc9bb3e` tags pushed once; digests in the report |
| Owner deployment `wce-dev-20260928-065309` | PASS — 1 create (bootstrap job), 0 delete; 23 nested deployments, 0 failed operations; 21 resources; 16 role assignments unchanged |
| Bootstrap job | PASS — 1 execution, Succeeded; `created`, `canLogin`, `rowLevelSecurityEnforced`, `loginVerified`, `succeeded` all true; no secret emitted |
| Migration job | PASS — 1 execution, Succeeded; `20260926213721_InitialGate1DSchema` and `20260926213758_TenantIsolationAndVectorExtension` applied, none pending; no destructive path |
| CD run 36377312213 (`deploy: true`) | PASS — tags reused; revisions `--0000002` Healthy/Running, 0 restarts; configuration unchanged apart from the image |
| Readiness | `/health/live` 200; `/health/ready` 200 with `postgres` and `service-bus` healthy; Worker `readiness=ready` on every heartbeat |
| Telemetry | AppRequests (Web); AppDependencies (PostgreSQL, `worker.readiness-check`, identity token) and AppTraces from both roles; AppExceptions 0; job and app logs in Log Analytics; sensitive-value sweep 0 hits |
| Owner deployment `wce-dev-20260928-091451` (probe on) | PASS — 0 create, 0 delete; only the Web probes changed; Web revision `--0000003` Healthy/Running with both probes as designed, 0 restarts, previous revision stopped normally; Worker unchanged; jobs still 1 execution each; 21 resources; 16 role assignments |
| Synthetic | untouched |
| Architecture/PRD contradictions | none |

See `docs/gates/gate-1G-report.md` for the full report: what was delivered, every commit, run,
deployment and job execution, readiness and telemetry evidence, the problems found and fixed, and the
post-Gate-1 hardening follow-ups.

## Immediate recommended next action in Claude Code

Gate 1 (Foundation) and Gate 2 (Event Backbone) are closed: Gates 1A through 1G and 2A through 2G are PASS (Gate 2
evidence: `docs/gates/gate-2-report.md`). Premium Gate P0 (Architecture Freeze) is PASS: the Premium architecture is
frozen (closed 2026-09-29; `docs/premium/`, `docs/gates/gate-P0-report.md`). Gate 3 (monday Connector) is in
progress: 3A, 3B, 3C, 3D and 3E are PASS (3E, API reconciliation and authoritative reread, approved 2026-10-02;
`docs/gates/gate-3-scope.md`), and 3F is NEXT / NOT STARTED. Do not start 3F or any later sub-gate, or
any Premium Gate P1 to P11, without the product owner's explicit authorization, do not start the bootstrap or
migration jobs (each has exactly one execution), and do not deploy,
dispatch `CD Dev` with `deploy: true`, or change Azure resources, without explicit product-owner
authorization. Azure Dev has none of the Gate 2B to 2F migrations. Before 2026-10-27 the product
owner must decide on the Free Trial upgrade.
