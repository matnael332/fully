# INIT — Project Handoff State

Last updated: 2026-09-29 (Gate 2, Event Backbone, PASS and closed; Gate 3 next, not started). Read this first
when resuming.

## Where the project is

| Gate | Status |
|---|---|
| 1A Repository & Solution Skeleton | PASS |
| 1B Local Development Environment | PASS |
| 1C Core Platform Skeleton | PASS |
| 1D Database Foundation | PASS |
| 1E Azure Infrastructure as Code | PASS |
| 1F CI/CD & Identity | PASS — closure commit `f4535f7` |
| 1G Foundation Hardening | **PASS** — commits `9651ed3` (implementation), `fc9bb3e` (CI fix), `450c13d` (Dev readiness probe on), plus this closure commit (Commit C) |
| **Gate 1 — Foundation** | **PASS** |
| Gate 2 — Event Backbone | **PASS — closed 2026-09-29.** 2A (event contract, validation, authority table) **PASS**, commit `7ba941a`. 2B (durable ingress) **PASS**, commit `120183b`, CI run 36411854002 green (359 tests: 356 passed, 0 failed, 3 Service Bus emulator tests skipped by design). 2C (outbox relay, publisher, consumer, dead-letter handling) **PASS**, commit `189f173`, CI run 36425641730 green (444 tests: 437 passed, 0 failed, 7 Service Bus emulator tests skipped by design, all of which passed locally). 2D (processing pipeline and event ledger) **PASS**, commit `f97d22d`, CI run 36439994562 green (493 tests: 486 passed, 0 failed, 7 Service Bus emulator tests skipped by design; every 2D test ran and passed in CI). 2E (Snapshot projection) **PASS**, commit `e165852`, CI run 36449846577 green (524 tests: 517 passed, 0 failed, 7 Service Bus emulator tests skipped by design; every 2E test ran and passed in CI). 2F (retention and metrics) **PASS**, commit `259be35`, CI run 36542058355 green (584 tests: 577 passed, 0 failed, 7 Service Bus emulator tests skipped by design; every 2F test ran and passed in CI). 2G (acceptance suite, crash recovery, tenant isolation and the Gate report) **PASS**, commit `59f74e1`, CI run 36552321567 green (613 tests: 604 passed, 0 failed, 9 Service Bus emulator tests skipped by design; every other 2G test ran and passed in CI; 613/613 locally). Acceptance satisfied: duplicate, delayed and out-of-order events do not corrupt the Snapshot and no event is silently lost. Report, risks and deferred items: `docs/gates/gate-2-report.md`. Azure Dev has **none** of the 2B, 2C, 2D, 2E or 2F migrations or schema. Scope and owner decisions: `docs/gates/gate-2-scope.md` |
| Gate 3 — monday Connector | **NEXT / NOT STARTED.** Requires explicit product-owner authorization. |

**Repository pointers:** `main` == `origin/main` == the Gate 2 closure commit, whose parent is
`59f74e16e1332f70aca9924967ee082e84b798e8` (Gate 2G; CI run 36552321567 green on all three jobs: 613 tests,
604 passed, 0 failed, 9 Service Bus emulator tests skipped by design; every other 2G test ran and passed in CI;
gitleaks found no leaks). Earlier: `dcf5496` (Gate 2F status closure), `259be35` (Gate 2F), `2bb480e` (Gate 2E
status closure), `e165852` (Gate 2E), `4b54bec` (the `event-driven-systems` skill aligned with the Gate 2D session
decision), `32e2909` (Gate 2D status closure), `f97d22d` (Gate 2D), `1f59a96` (Gate 2C status closure), `189f173`
(Gate 2C), `3209e57` (Gate 2B status closure), `120183b` (Gate 2B), `23607f9` (project skills), `7ba941a` (Gate
2A). The working tree is clean. The local database carries the 2B, 2C, 2D, 2E and 2F migrations (with the 2F
follow-up `RawContentPurgeAge`), applied by the integration tests; Azure Dev carries none of them.

**Product-owner decisions on record (2026-09-28):** Gate 1G runtime verification approved; Commit B
(readiness probe on in Dev) approved and deployed; the single cold-start Service Bus timeout is a
non-blocking observation carried as a post-Gate-1 hardening follow-up (no readiness timing change, no
token warm-up in Gate 1); Gate 1 / Gate 1G closure committed as `a4bfd18`. Gate 2 was then authorized with
nine design decisions and the 2A–2G decomposition, recorded in `docs/gates/gate-2-scope.md`. The 2C review
approved three more, recorded there under "2C closure": SessionId = TenantId for 2C, to be revisited in 2D;
`outbox_pending_tenants` stays discovery-only; and no runtime activation yet. The 2D review approved three
more, recorded under "2D closure": SessionId remains TenantId, with database ordering and idempotency as the
source of truth; the 5-minute source clock-skew tolerance is approved for now, stays configurable and must be
validated against real connectors; and any authority conflict quarantines the whole event. The 2E review
approved three more, recorded under "2E closure": Snapshot projection stays in the same transaction as the 2D
ledger write; the canonical field-value rule, where NULL means the source cleared the field, invalid values are
quarantined and nothing is truncated or rewritten; and the 1,024-character value limit, kept only in
`EventText.MaxValueLength`, which Gate 3 must validate against real connector data. The 2F reviews (2026-09-29)
approved nine more, recorded in the 2F Definition of Done: no fourth tenant-less function for exact outbox
backlog counts; raw content purged at 28 days with the 30-day hard limit and an hourly future cadence; the
database clock stays the authoritative safety clock, its guard moved from 720 to 672 hours; a separate follow-up
migration, `RawContentPurgeAge`; exit code 3 only for overdue or limit-exceeded content that remains after a run;
a timing model derived from stated assumptions, with no unconditional guarantee during outages; the activation
preconditions recorded, not created; and database time as the authoritative retention clock: PostgreSQL `now()`
decides purge eligibility, the 28-day boundary and the 30-day classification and measures every age, while the
Worker's clock only times runs; and the receipt time itself stamped by PostgreSQL at insert, through a `now()` default
and a trigger that replaces any supplied value and refuses any change, so no ingress clock moves a receipt's age.
Gate 2G needed no new design decision. On 2026-09-29 the product owner approved the 2G change set (commit `59f74e1`,
CI run 36552321567 green) and ordered the formal closure: Gate 2 is PASS and closed, and Gate 3 is next and not
started.

## Rules that must not be broken (from CLAUDE.md and the canonical docs)

- Work only on an explicitly authorized Gate. Gate 2 is closed (PASS; `docs/gates/gate-2-scope.md`,
  `docs/gates/gate-2-report.md`). Gate 3 is not authorized.
- Never modify `docs/canonical/PRD-v1.md`, `SYSTEM-ARCHITECTURE-v1.md`, `ARCHITECTURE-INVARIANTS-v1.md`
  or `DEVELOPMENT-GATES-v1.md`. If a contradiction appears, STOP and report it. (`PROJECT-STATUS.md`
  in the same folder is a status document.)
- Never run `git clean`, `git reset`, `git checkout -- <file>`, `git stash` or anything that discards work.
- Never run `az deployment ... create` without explicit product-owner approval. Use `eng/infra.ps1`,
  which requires `-Environment`, `-SubscriptionId` and `-Approve`; the product owner runs `deploy`
  interactively in their own terminal (it confirms against a live what-if).
- Never dispatch `CD Dev` with `deploy: true`, start a Container Apps job, create Azure resources, change
  RBAC or deploy Synthetic without explicit approval. **Both jobs have exactly one execution; do not
  start either again unless the product owner orders it** (re-running the bootstrap with the same
  password is a no-op; re-running the migration is a no-op while nothing is pending).
- Never grant the GitHub deployment identity anything on the migration or bootstrap jobs.
- One approval boundary at a time; read-only checks need no approval. Scripted edits use two-phase
  verify-then-write with exact occurrence counts and preserve each file's line endings.
- Do not fabricate a PASS. Do not weaken a test or a security rule to make a Gate pass.

## What Gate 1G delivered (all committed on `main`)

- **Azure-aware readiness** (`src/App.Infrastructure/Health/DependencyProbe*.cs`): PostgreSQL proven as
  the runtime role (`SELECT 1`), Service Bus by a side-effect-free operation under the process's own claim
  (Web opens a send link, Worker peeks) on the sessionless `ai-jobs` queue; 5 s budget per dependency;
  reason codes only. Azure: `ServiceBus__FullyQualifiedNamespace` + `ManagedIdentityCredential
  (AZURE_CLIENT_ID)`; local: `ServiceBus__ConnectionString`.
- **Runtime-role bootstrap** (`RuntimeRoleBootstrap.cs`, Worker `--bootstrap-runtime-role`, Bicep job
  `caj-wce-dev-neu-bootstrap`, `bootstrapJobImage` / `WCE_DEV_BOOTSTRAP_JOB_IMAGE`).
- **OpenTelemetry baseline** to Application Insights from both roles, gated on
  `APPLICATIONINSIGHTS_CONNECTION_STRING`; `wce.correlation_id` on request spans; Worker
  `worker.readiness-check` span per heartbeat.
- **Security**: CI `secret-scan` job (gitleaks 8.30.1 pinned by SHA-256, `.gitleaks.toml`); Security
  tests 35 → 58; explicit package allow-list in the Architecture tests.
- **Local parity**: compose `db-bootstrap` before `db-migrator`; `dev.ps1 check` verifies it.
- **CI fix** (`fc9bb3e`): the GitHub Ubuntu runner exports `ACCEPT_EULA=Y` globally; the emulator tests
  now run only when `.env` accepts the licence or `SERVICEBUS_TEST_CONNECTION_STRING` is set.
- **Dev readiness probe on** (`450c13d`): `param webReadinessProbeEnabled = true` in
  `infra/bicep/parameters/main.dev.bicepparam`; Synthetic keeps the default (off).

Full evidence: `docs/gates/gate-1G-report.md`.

## Azure state (verified read-only 2026-09-28, after deployment `wce-dev-20260928-091451`)

- Dev in `rg-wce-dev-neu` (North Europe, token `33f52b`): **21 resources**, **16 role assignments**,
  4 identities, 1 federated credential, subnets `snet-apps 10.10.0.0/23` and `snet-postgres 10.10.2.0/28`.
- Web `ca-wce-dev-neu-web`: revision `ca-wce-dev-neu-web--0000003` Healthy/Running, 1 replica, 0 restarts,
  image `crwcedev33f52b.azurecr.io/wce/web:fc9bb3e9158069064ce5c6e3a2deaac6073e069a`
  (digest `sha256:d97aabf6b1fb8ff8002e73dce2d417fd43bb14a62b560ec114898347ef574b80`); liveness
  `/health/live` (10 s / 30 s / 3) **and readiness `/health/ready` (5 s / 20 s / timeout 15 s / 6)**;
  `GET https://ca-wce-dev-neu-web.wonderfulsmoke-9e138fb2.northeurope.azurecontainerapps.io/health/ready`
  → 200 with `postgres` and `service-bus` healthy.
- Worker `ca-wce-dev-neu-worker`: revision `ca-wce-dev-neu-worker--0000002` Healthy/Running, 0 restarts,
  image `…/wce/worker:fc9bb3e9158069064ce5c6e3a2deaac6073e069a`
  (digest `sha256:aa74781be3ee7ec63933e7dea9973eb15f25ddfd2e2b93bcc1b50330cb6d1153`); no probe by design;
  heartbeat `readiness=ready` every 30 s.
- Jobs (both manual, identity `id-wce-dev-neu-jobs`, image `…/wce/worker:fc9bb3e…`):
  `caj-wce-dev-neu-bootstrap` — **1 execution** `caj-wce-dev-neu-bootstrap-1uur7bk` Succeeded
  2026-09-28 04:10Z; `caj-wce-dev-neu-migrate` — **1 execution** `caj-wce-dev-neu-migrate-9uw56m9`
  Succeeded 04:19Z (migrations `20260926213721_InitialGate1DSchema`,
  `20260926213758_TenantIsolationAndVectorExtension`).
- Runtime role `workcontinuity_app` exists in Azure PostgreSQL (LOGIN, NOSUPERUSER, NOBYPASSRLS, default
  privileges set); the schema is migrated; the apps connect as that role.
- Registry `crwcedev33f52b`: `wce/web` and `wce/worker` each hold exactly two tags (`f4874302…`, `fc9bb3e…`).
- Deployments today: `wce-dev-20260928-065309` (bootstrap job created, migration image) and
  `wce-dev-20260928-091451` (probe on), both Succeeded, 23 nested deployments, 0 failed operations.
- Telemetry: AppRequests (Web); AppDependencies and AppTraces from both roles; AppExceptions 0; console and
  job logs in `ContainerAppConsoleLogs_CL`; sensitive-value sweep 0 hits.
- Synthetic: not deployed. Application Insights: workspace-based, local auth still enabled (follow-up).

## Post-Gate-1 hardening follow-ups (not authorized work; record only)

1. **Managed-identity token warm-up / readiness cold start.** The first `/health/ready` on a fresh Web
   container can spend its 5 s Service Bus budget on the first token acquisition (7.1 s observed once at
   04:26Z on revision `--0000002`; 0.9 s on `--0000003`). Tolerated by failure threshold 6; fix only if it
   becomes operationally significant (warm the token at start-up or size the budget).
2. Web Dockerfile: install `libgssapi-krb5-2` to silence the harmless Npgsql stderr line (Worker already does).
3. Application Insights `DisableLocalAuth` (instrumentation-key ingestion) — hardening option.
4. KEDA `ScaledObjectCheckFailed` warnings: the apps declare no scale rules; cosmetic.
5. Service Bus spans are not emitted for link-open/peek; readiness state is carried as span properties.
6. Synthetic region/subscription decision; Free Trial upgrade before **2026-10-27**.

## Exact next step

1. Gate 3 (monday Connector) is NEXT / NOT STARTED. It begins only when the product owner explicitly authorizes
   it. The risks and deferred items Gate 2 leaves open are listed in `docs/gates/gate-2-report.md`.
2. Azure Dev has none of the Gate 2B, 2C, 2D, 2E or 2F migrations. Applying them needs new images, an updated
   migration-job image and one migration-job run, each a separate approval; the migration job keeps exactly
   one execution until the owner orders a rerun. Running the relay, the consumer and the processing pipeline
   in the Worker, including Service Bus send access for the Worker identity, is a further separate approval.
   So is activating the raw-content purge (`--purge-raw-content`), which runs only when invoked explicitly
   until then. Activation needs the hourly job and all five alerts of the 2F Definition of Done (items 13 to 15).
   Content is purged at 28 days; while every hourly run succeeds it is never older than 28 days 1 hour 21
   minutes, and a purge outage of about the 48-hour lead margin or longer can pass the 30-day limit, which the
   alerts report but cannot prevent.
3. No Azure write, job start or `deploy: true` without separate approval; Gate 3 not before explicit
   authorization. When it is authorized, Gate 3 must validate the 1,024-character field-value limit against
   real connector data and widen it explicitly if needed, never truncating.

Rollback of Gate 1G runtime state, if ever ordered: dispatch `CD Dev` with `f4874302456a50c935a5429deed07699312de37c`
to restore the previous images; redeploy Bicep with `webReadinessProbeEnabled` removed (probe off); the
bootstrap job can be deleted by exact name and the role dropped by name; migrations carry `Down` and the
database holds no data. Tags are immutable and additive.

## Azure facts

- Tenant: the owner's single home tenant ("Default Directory"). MFA is enforced for Azure Resource
  Manager: sign in with `az login --tenant <tenant id>` (plain `az login` fails with AADSTS50076).
- Subscription "Azure subscription 1" (`c1830e72-d806-4cb1-8bff-8627fb01c5ea`), **Free Trial**, spending
  limit on; trial window ends **2026-10-27** unless upgraded. Quota: one Container Apps environment per region.
- Dev names: registry `crwcedev33f52b`, vault `kv-wce-dev-33f52b`, server `psql-wce-dev-neu-33f52b`,
  namespace `sb-wce-dev-neu-33f52b`, storage `stwcedev33f52b`, environment `cae-wce-dev-neu`, apps
  `ca-wce-dev-neu-web|worker`, jobs `caj-wce-dev-neu-migrate|bootstrap`, identities
  `id-wce-dev-neu-web|worker|jobs|github`, Log Analytics `log-wce-dev-neu` (customerId
  `ee16ea70-7f88-4d3f-97f7-ca0baf77b86d`), App Insights `appi-wce-dev-neu`.
- The two Dev passwords (`WCE_DEV_POSTGRES_ADMIN_PASSWORD`, `WCE_DEV_POSTGRES_RUNTIME_ROLE_PASSWORD`) are
  held by the product owner only; every redeployment must reuse the same values. The signed-in owner
  account has no Key Vault data-plane role (by design), so secret metadata cannot be listed.
- Image variables (`WCE_DEV_WEB_IMAGE`, `WCE_DEV_WORKER_IMAGE`, `WCE_DEV_MIGRATION_JOB_IMAGE`,
  `WCE_DEV_BOOTSTRAP_JOB_IMAGE`) may stay unset: `eng/infra.ps1` adopts the live images (all four are the
  `fc9bb3e` images now). An explicitly set variable always wins.
- GitHub: private `matnael332/fully`; CD identity `id-wce-dev-neu-github` (AcrPush on the registry,
  Container Apps Contributor on the two apps only, no access to the jobs; seven repository variables, no secrets).
- Job start (only when approved): `az rest --method post --url "https://management.azure.com/subscriptions/c1830e72-d806-4cb1-8bff-8627fb01c5ea/resourceGroups/rg-wce-dev-neu/providers/Microsoft.App/jobs/<job>/start?api-version=2024-03-01"`;
  poll `.../executions?api-version=2024-03-01`; read the JSON result line from `ContainerAppConsoleLogs_CL`
  where `ContainerGroupName_s startswith '<execution name>'` (ingestion takes 1–5 minutes).

## Technical notes needed to continue safely

- CRLF working copies while git stores LF: `CLAUDE.md`, `README.md`, `docs/canonical/PROJECT-STATUS.md`,
  `infra/bicep/environment.bicep`, `containerRegistry.bicep`, `roleAssignments.bicep`, `serviceBus.bicep`,
  `Directory.Packages.props`, `docs/architecture/dependency-rules.md`, `eng/dev.ps1`,
  `eng/verify-local-env.py`, `tests/App.Architecture.Tests/DependencyBoundaryTests.cs`. Edit scripts must
  detect and preserve each file's line endings (check with `file`, do not assume).
- The tool's Git Bash has **no `jq`**; parse `az` JSON with `--query` (JMESPath) or Python. `az … -o tsv`
  output carries `\r` on Windows: pipe through `tr -d '\r'` before reusing a value (a trailing `\r` in a
  deployment name yields HTTP 400 "Bad Request"). KQL: `first` and `last` are reserved words.
- Through the PowerShell tool, `./eng/dev.ps1 up` reports failure because docker compose writes pull
  progress to stderr; judge the stack by `./eng/dev.ps1 check` and `docker ps`. If the Service Bus
  emulator exits with code 139 after a restart (SQL Server recovery race on a reused container), recreate
  only those two containers: `docker compose --env-file .env -f infra/local/docker-compose.yml up -d
  --force-recreate --no-deps mssql servicebus-emulator`. Never `docker … prune`.
- `dotnet test` under Microsoft.Testing.Platform: `dotnet test --project <csproj> -c Debug` or the
  solution path; never pass `--nologo`. Unit (251), Security (101) and Architecture (14) run without Docker;
  the full suite (584, with Integration 218) needs `./eng/dev.ps1 up`; the seven Service Bus emulator tests
  skip when `.env` has no `ACCEPT_EULA=Y`, as in CI.
- The GitHub Ubuntu runner exports `ACCEPT_EULA=Y` globally; never treat a process-level licence flag as
  evidence that a local service exists.
- What-if against the deployed environment reports 16 "modify" noise entries (read-only property echoes,
  unresolved `reference()` expressions, write-only defaults) and 13 "unsupported" role assignments; read
  the property lines before treating any as real. Verified classification: `docs/gates/gate-1G-report.md`.
- Windows PowerShell 5.1 quirks: `ConvertFrom-Json` emits a JSON array as one object unless assigned to
  a variable first; `az role assignment list --all` returns lower-case `/resourcegroups/` scopes; a native
  command writing to stderr under `$ErrorActionPreference = 'Stop'` becomes a terminating error. Do not put
  `Remove-Item` or `cmd /c` in commands run through the tool sandbox.
- On this machine `bash` on PATH is the WSL relay; Git Bash is `C:\Program Files\Git\usr\bin\bash.exe`.
- The Bash tool halves backslashes inside commands, even in quoted heredocs. Write any script that
  contains backslashes (C# character literals, regular expressions, escapes) with the Write tool and run
  it from the file; verify the written bytes before trusting a check that also passed through the shell.
- Working preferences: commit messages ≤10 lines via `-m` flags, `Co-Authored-By` only, no
  `Claude-Session` trailer; never `docker … prune` or `git clean`; remove only resources this session
  created, by exact name.

## Useful commands

```powershell
./eng/dev.ps1 up | check | logs | down          # local stack (bootstrap → migrate → web/worker)
./eng/db.ps1 status | migrate | reset            # local database
./eng/infra.ps1 build | lint                     # Bicep, no Azure
./eng/infra.ps1 validate -Environment dev -SubscriptionId <id>   # read-only, needs az login; prints the image guard
./eng/infra.ps1 whatif   -Environment dev -SubscriptionId <id>   # read-only, needs az login
dotnet test WorkContinuityEngine.sln -c Debug    # needs local stack running
python eng/verify-architecture.py
python eng/verify-local-env.py
gh run list --workflow ci.yml --limit 5          # CI history
gh run list --workflow cd-dev.yml --limit 5      # CD history
```
