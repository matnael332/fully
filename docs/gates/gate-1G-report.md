# Gate 1G Report — Foundation Hardening

## Status

**Gate 1G: PASS** — closed by the product owner on 2026-09-28 by approving this closure commit after the
final read-only verification in Azure. **Gate 1 (Foundation): PASS** — sub-gates 1A through 1G are each
PASS.

What the Gate proves: Web `/health/ready` and the Worker heartbeat prove the real dependencies under the
workloads' own managed identities — PostgreSQL by a connection **as the runtime role** and Service Bus by a
side-effect-free link on the `ai-jobs` queue; the runtime role `workcontinuity_app` was created in Azure by
a dedicated bootstrap job that **ran exactly once**; the first migration **ran exactly once** and applied
the two Gate 1D migrations; Web and Worker run the Gate 1G images with the readiness probe **enabled in
Dev** and a new Web revision Healthy behind it; application telemetry (requests, dependencies, traces,
logs) flows to Application Insights and Log Analytics with **no secret value** anywhere; a pinned secret
scan runs in CI; Security tests grew from 35 to 58 and cover workflows, Bicep boundaries, environment
isolation and readiness safety; the fresh-environment smoke test was satisfied locally from a clean slate
(product-owner decision of 2026-09-27: LOCAL SUFFICIENT).

**Carried forward as post-Gate-1 hardening follow-ups, not part of this Gate's acceptance:** managed-identity
token warm-up for the first readiness call (see *Observations*); the Web image's harmless
`libgssapi_krb5.so.2` stderr line (the Worker Dockerfile already installs the library); Application
Insights still accepts instrumentation-key ingestion (a hardening option); KEDA scaler warnings caused by
the apps having no scale rules (cosmetic); Service Bus link-open and peek are not instrumented, so no
Service Bus spans appear (readiness state is carried as span properties instead); the Synthetic
region/subscription decision; the Free Trial upgrade decision before 2026-10-27.

## Scope

The six items of Development Gate 1G: OpenTelemetry baseline; health/readiness; secret scan;
architecture/security tests; environment isolation tests; fresh-environment smoke test.

Excluded on purpose: any Gate 2 work (events, Service Bus processing, Inbox/Outbox pipeline); readiness
timing changes or token warm-up (deliberately deferred by the product owner so the probe design was proven
as written); Synthetic deployment; new Azure resources beyond the bootstrap job; any RBAC change.

## What was delivered

| Item | Delivered | Evidence |
|---|---|---|
| Azure-aware readiness | `DependencyProbe` (rewritten) + `DependencyProbeOptions` + `DependencyProbeRegistration` in `App.Infrastructure/Health`: PostgreSQL proven by opening a connection as the runtime role and running `SELECT 1`; Service Bus proven by a side-effect-free operation under the process's own claim — Web opens a send link (Data Sender), Worker peeks (Data Receiver) — on the sessionless `ai-jobs` queue; 5 s budget per dependency; detail values are type names and reason codes only; Azure uses `ServiceBus__FullyQualifiedNamespace` + `ManagedIdentityCredential(AZURE_CLIENT_ID)`, local uses the emulator connection string | commit `9651ed3`; live `GET /health/ready` → 200 with `postgres` and `service-bus` healthy |
| Runtime-role bootstrap | `RuntimeRoleBootstrap` (new type, separate from `DatabaseMigrator`) behind Worker `--bootstrap-runtime-role`: password reaches the server only as a `set_config` bind parameter, DDL runs server-side with `format('%L')`; creates or updates the role as `LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT`, grants existing tables and sequences, sets default privileges for future ones, and ends by logging in as the role; the JSON result never contains the password | execution `caj-wce-dev-neu-bootstrap-1uur7bk` Succeeded |
| Bicep | New manual job `caj-wce-dev-neu-bootstrap` (jobs identity, Key Vault references `postgres-admin-connection-string` and `postgres-runtime-role-password`), parameter `bootstrapJobImage` / `WCE_DEV_BOOTSTRAP_JOB_IMAGE`; the image-drift guard covers all four resources and tolerates a resource that is not deployed yet; readiness probe `periodSeconds 20`, `timeoutSeconds 15`, `failureThreshold 6`; no RBAC, identity or Synthetic file changed | deployment `wce-dev-20260928-065309` (1 create, 0 delete) |
| OpenTelemetry baseline | Web: `Azure.Monitor.OpenTelemetry.AspNetCore` 1.6.0, role `work-continuity-web`, Npgsql spans, correlation id tagged on the request span; Worker: `Azure.Monitor.OpenTelemetry.Exporter` 1.8.3 + console logging, role `work-continuity-worker`, a `worker.readiness-check` span per heartbeat carrying readiness properties, SIGTERM-aware loop; both export only when `APPLICATIONINSIGHTS_CONNECTION_STRING` is present | AppRequests (Web), AppDependencies and AppTraces rows for both roles; AppExceptions 0 |
| Secret scan | CI job `secret-scan`: gitleaks 8.30.1 pinned by SHA-256, run directly (no action, no token), full history plus working tree, `.gitleaks.toml` with an allow-list only for Azure role-definition GUIDs in Bicep | every CI run below: "no leaks found" |
| Architecture / security / isolation tests | Security tests 35 → 58: `WorkflowSecurityTests`, `InfrastructureBoundaryTests`, `EnvironmentIsolationTests`, `ReadinessSafetyTests`; the Architecture package rule is an explicit allow-list (`Azure.Identity` and `Azure.Messaging.ServiceBus` in Infrastructure, the two exporters in Web/Worker, AI packages forbidden everywhere); `docs/architecture/dependency-rules.md` updated | 58/58 and 13/13 on every run |
| Local parity | Compose service `db-bootstrap` runs before `db-migrator` through the same Worker entry point; `eng/dev.ps1 check` verifies its exit code; `eng/verify-local-env.py` expects it; Worker Dockerfile installs `libgssapi-krb5-2`; `docs/development/local-development.md` updated | clean-slate smoke 2026-09-27: role created, bootstrap, migration, readiness 200 in 54 s |
| CI fix | The GitHub Ubuntu runner exports `ACCEPT_EULA=Y` globally, so the three Service Bus emulator tests ran in CI against nothing; the emulator rule now honours only the `.env` licence line or an explicit `SERVICEBUS_TEST_CONNECTION_STRING`, as a pure function with its own tests | commit `fc9bb3e`; CI run 36374164214 |
| Readiness probe enabled | `param webReadinessProbeEnabled = true` in `main.dev.bicepparam` (Dev only; Synthetic keeps the default) | commit `450c13d`; deployment `wce-dev-20260928-091451`; Web revision `--0000003` Healthy |

## Commits, runs and deployments

| When (UTC) | What | Result |
|---|---|---|
| 2026-09-28 03:15 | Commit A `9651ed3087b90081e61849a117db78ba8acb76a3` (34 files); CI run 36373040147 | CI **FAILED**: 2 emulator tests (see *Problems*) |
| 2026-09-28 03:33 | Fix `fc9bb3e9158069064ce5c6e3a2deaac6073e069a` (test-only); CI run 36374164214 | SUCCEEDED: 266 tests, 263 passed, 3 skipped (the emulator tests), 0 failed; gitleaks 16 commits, no leaks |
| 2026-09-28 03:38–03:40 | CD run 36374505337, `deploy: false`, commit `fc9bb3e` | SUCCEEDED: `wce/web:fc9bb3e…` → `sha256:d97aabf6b1fb8ff8002e73dce2d417fd43bb14a62b560ec114898347ef574b80`, `wce/worker:fc9bb3e…` → `sha256:aa74781be3ee7ec63933e7dea9973eb15f25ddfd2e2b93bcc1b50330cb6d1153`, each pushed once; deploy job skipped |
| 2026-09-28 03:55–03:59 | `wce-dev-20260928-065309` — bootstrap job created, migration job image → Worker `fc9bb3e`, apps adopted by the guard (owner via `eng/infra.ps1 deploy -Approve`) | Succeeded, 23 nested deployments, 0 failed operations |
| 2026-09-28 04:10:08–04:10:44 | Bootstrap execution `caj-wce-dev-neu-bootstrap-1uur7bk` | Succeeded |
| 2026-09-28 04:19:02–04:19:38 | Migration execution `caj-wce-dev-neu-migrate-9uw56m9` | Succeeded |
| 2026-09-28 04:20–04:24 | CD run 36377312213, `deploy: true`, commit `fc9bb3e` | SUCCEEDED: tags reused; new revisions `ca-wce-dev-neu-web--0000002` and `ca-wce-dev-neu-worker--0000002` Healthy/Running, 0 restarts |
| 2026-09-28 05:36 | Commit B `450c13dc1713b32cbaacd91c33c5058dc9117bae` (one file); CI run 36382627242 | SUCCEEDED: 266 tests, 263 passed, 3 skipped; gitleaks 17 commits, no leaks |
| 2026-09-28 06:15–06:19 | `wce-dev-20260928-091451` — readiness probe on (owner via `eng/infra.ps1 deploy -Approve`, all four images adopted by the guard) | Succeeded, 23 nested deployments, 0 failed operations; Web revision `--0000003` |

Every Azure write above was either the product owner's interactive `eng/infra.ps1 deploy` or an
explicitly approved single step; the two job starts were `az rest POST …/start` issued once each.

## Runtime role and migration

| Item | Value |
|---|---|
| Bootstrap job | `caj-wce-dev-neu-bootstrap`: identity `id-wce-dev-neu-jobs`, manual trigger, `--bootstrap-runtime-role`, 0.5 CPU / 1 GiB, replica timeout 600 s, image `crwcedev33f52b.azurecr.io/wce/worker:fc9bb3e…` |
| Bootstrap result (stdout JSON) | `role: workcontinuity_app`, `created: true`, `canLogin: true`, `rowLevelSecurityEnforced: true`, `tablesGranted: 0` (no tables existed yet; default privileges cover the tables the migration created), `loginVerified: true`, `succeeded: true`; no password or connection string in the line |
| Migration job | `caj-wce-dev-neu-migrate`: same identity and image, `--migrate-database`, Key Vault reference `postgres-admin-connection-string` only |
| Migration result (stdout JSON) | `appliedMigrations: [20260926213721_InitialGate1DSchema, 20260926213758_TenantIsolationAndVectorExtension]`, `pendingMigrations: []`; the migrator applies pending migrations only — no `EnsureDeleted`, drop, truncate or reset path exists |
| Executions | exactly **1** per job after every later step; neither job is ever started by CD or by a Bicep deployment |
| Platform evidence | Key Vault secret sync succeeded for both jobs; image pulled by its `fc9bb3e…` tag; container exit code 0; execution completed |

## Readiness and probes

| Item | Value |
|---|---|
| Web `/health/live` | 200 `{"status":"live","service":"work-continuity-web"}`; liveness probe unchanged (initial delay 10 s, period 30 s, failure threshold 3) |
| Web `/health/ready` | 200 `{"ready":true,"dependencies":[{"name":"postgres","healthy":true,"detail":null},{"name":"service-bus","healthy":true,"detail":null}]}` in 0.3–0.8 s at steady state |
| Readiness probe (Dev, since `wce-dev-20260928-091451`) | HTTP GET `/health/ready` on port 8080, initial delay 5 s, period 20 s, timeout 15 s, failure threshold 6 — exactly the template; the Worker has no probe by design |
| Worker heartbeat | `readiness=ready dependencies=postgres=healthy,service-bus=healthy` from the first heartbeat 2 s after start; 0 degraded lines on revision `--0000002` |
| First revision with the probe | `ca-wce-dev-neu-web--0000003`: provisioned 06:18:54Z, container started 06:19:11Z, first platform probe 06:19:15.98Z → **200 in 1.5 s**, then 200 every 20 s (3–9 ms); replica ready, 0 restarts; `--0000002` retired with reason `ManuallyStopped` at 06:20:02Z (single-revision mode) |

## Telemetry

| Table | Evidence (2026-09-28, both roles) |
|---|---|
| AppRequests | `work-continuity-web`: `GET /health/live` 200, `GET /health/ready` 200 (and the single 503 described below); `wce.correlation_id` present on 100 % of request spans |
| AppDependencies | `postgresql` spans (`CONNECT workcontinuity`, `postgresql`) for both roles against `psql-wce-dev-neu-33f52b`; `worker.readiness-check` InProc span on every Worker heartbeat with `wce.readiness`, `wce.dependency.postgres`, `wce.dependency.service-bus` properties; `ManagedIdentityCredential.GetToken` and `GET /msi/token` once per Web process (no such span is recorded for the Worker) |
| AppTraces | Worker heartbeat and start-up lines; Web start-up and the single Service Bus timeout (severity 3) |
| AppExceptions | 0 rows |
| ContainerAppConsoleLogs_CL | rows for both new revisions and for both job executions, including the two JSON result lines |
| ContainerAppSystemLogs_CL | revision, replica, image-pull, Key Vault sync and job lifecycle events |
| Sensitive values | regex sweep (passwords, keys, tokens, JWTs, connection strings, client secrets) over console logs, system logs and all four telemetry tables: **0 hits**; every job and identity line read manually; client ids and correlation ids are public identifiers |

## Observations (non-blocking)

**Cold-start Service Bus timeout.** The very first `GET /health/ready` on revision `--0000002`
(04:26:14Z, before the probe existed, from an external call) returned 503 after 9.2 s with
`service-bus` = `ServiceBusException:ServiceTimeout`: the Web's first managed-identity token acquisition
took 7.1 s (`GET /msi/token` 6.3 s), which consumed the 5 s Service Bus budget before the AMQP link could
open. PostgreSQL succeeded in the same request. Every later call returned 200 because the token is
cached, and the Web log shows exactly one `warn:` and one `fail:` line, both from that call. On revision
`--0000003`, with the probe enabled, the same acquisition took 0.9 s and the first probe passed. With
failure threshold 6 and period 20 s, a first-probe miss only delays a replica's admission by one period;
it cannot remove an already-ready replica. Recorded as a **post-Gate-1 hardening follow-up**: warm the
managed-identity token at start-up or size the per-dependency budget, only if it proves operationally
significant. No readiness timing was changed in Gate 1G.

**Retired Worker revision.** `ca-wce-dev-neu-worker--0000001` (the Gate 1F image, without the runtime
role) logged `readiness=degraded` until the CD rollout stopped it at 04:23Z — expected for the previous
image and not a fault of the new one.

## Verification (executed 2026-09-28)

| Check | Result |
|---|---|
| INIT integrity at resume | PASS — HEAD/`main`/`origin/main` at `f4535f7`, 34/34 SHA-256 hashes, snapshot exact, nothing staged, canonical documents untouched |
| `dotnet build` (warnings as errors) | PASS — 0 warnings, 0 errors, locally and in CI |
| Full test suite | PASS — 266 tests: 266 passed locally with the emulator; 263 passed + 3 skipped (emulator tests, by design) in CI; 0 failed |
| Security / Architecture / Unit / Integration | 58 / 13 / 138 / 57 passed |
| `eng/verify-architecture.py`, `eng/verify-local-env.py` | PASS |
| Bicep build and lint | PASS (local and CI) |
| gitleaks 8.30.1 (checksum verified) | no leaks — 16 commits at `fc9bb3e`, 17 at `450c13d`; working tree clean |
| What-if before `wce-dev-20260928-065309` | 1 create (bootstrap job, real image), 0 delete; migration job image modify; apps unchanged; 16 modify noise, 13 unsupported role assignments; verified property by property |
| Deployment `wce-dev-20260928-065309` | Succeeded; 23 nested deployments, 0 failed operations; 21 resources (20 + bootstrap job); 16 role assignments unchanged; 4 identities; 1 federated credential; 2 subnets; no Synthetic group |
| Bootstrap execution | 1 execution, Succeeded; JSON as in *Runtime role and migration* |
| Migration execution | 1 execution, Succeeded; both migrations applied; no destructive path |
| CD run 36377312213 (`deploy: true`) | SUCCEEDED; tags reused; revisions `--0000002` Healthy/Running, 1 replica, 0 restarts; identity, registries, secret references, env names and probes identical apart from the image |
| Readiness after the rollout | `/health/live` 200; `/health/ready` 200 with both dependencies healthy on every call after the first (see *Observations*); Worker `readiness=ready` on every heartbeat |
| Telemetry proof | as in *Telemetry*; independently re-verified by three read-only sweeps and eleven adversarial checks of every claim (0 refuted) |
| What-if before `wce-dev-20260928-091451` | 0 create, 0 delete, 17 modify: 16 noise + the Web probes array gaining the Readiness probe; 0 image lines; classified independently by three readers with the same result |
| Deployment `wce-dev-20260928-091451` | Succeeded; 23 nested deployments, 0 failed operations; parameters `webReadinessProbeEnabled = true`, all four images `fc9bb3e`; Web revision `--0000003` Healthy/Running, both probes installed as designed, 0 restarts, `--0000002` stopped normally; Worker unchanged on `--0000002`; jobs still 1 execution each; 21 resources; 16 role assignments; identities, network, secret references and images unchanged; sensitive sweep 0 hits |
| Fresh-environment smoke | local clean slate (`dev.ps1 down`, volume removed, `dev.ps1 up`) PASS in 54 s on 2026-09-27 — accepted as LOCAL SUFFICIENT by the product owner |
| Architecture/PRD contradictions | none |

## Problems found and fixed

1. **CI ran the emulator tests against nothing.** The GitHub Ubuntu runner image writes `ACCEPT_EULA=Y`
   into the global environment and the test helper treated a process-level licence flag like the `.env`
   licence line. Fixed in `fc9bb3e` by honouring only the `.env` line or an explicit test connection
   string, as a pure function with five tests; production code unchanged.
2. **Local Service Bus emulator crash on restart.** Restarting the compose stack against yesterday's SQL
   Server container raced its database recovery and left an orphaned data file, so the emulator exited
   with code 139 on every restart. Recreating only the `mssql` and `servicebus-emulator` containers
   (`docker compose … up -d --force-recreate --no-deps mssql servicebus-emulator`) restored the stack;
   `eng/dev.ps1 check` PASS. Noted in INIT.md; no code change.
3. **Tooling traps during verification.** The tool shell has no `jq`, `az … -o tsv` carries `\r` on
   Windows, and `first`/`last` are reserved KQL words; each produced a false "no data" once and was
   corrected before any conclusion was drawn. Noted in INIT.md.
4. **What-if noise.** Against the deployed environment, what-if reports 16 "modify" entries that are
   read-only property echoes, unresolved `reference()` expressions with identical values, or write-only
   defaults; each was classified property by property before every deployment and independently
   re-classified before the second one.

## Architecture consistency

Readiness proves dependencies under per-workload managed identities and never with a shared secret; the
runtime role is created by a dedicated, human-started job whose password never appears in logs, telemetry
or results; the schema owner is used only for migrations and the application connects as the non-superuser
role so row-level security holds; telemetry carries reason codes and identifiers, never raw work data or
secrets; AI is not involved anywhere in Gate 1; CD still changes only two images and the jobs remain on
the human-approved `eng/infra.ps1` path. All as written in System Architecture v1 and the invariants. No
contradiction found. No canonical document was changed.

**Gate 1G: PASS. Gate 1 (Foundation): PASS. Gate 2 (Event Backbone) is NEXT and NOT STARTED; it requires
explicit product-owner authorization.**
