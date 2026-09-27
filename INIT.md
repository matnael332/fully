# INIT — Project Handoff State

Last updated: 2026-09-27 (end of day, after Gate 1E closure). Read this first when resuming.

## Where the project is

| Gate | Status |
|---|---|
| 1A Repository & Solution Skeleton | PASS |
| 1B Local Development Environment | PASS |
| 1C Core Platform Skeleton | PASS |
| 1D Database Foundation | PASS |
| **1E Azure Infrastructure as Code** | **PASS** — Dev deployed and verified 2026-09-27; declared by the product owner |
| 1F CI/CD & Identity | **NEXT / NOT STARTED** — requires explicit product-owner authorization |

**Latest commit:** the Gate 1E closure commit on `main` (run `git log -1`). It follows `5309f91`
(Application Insights secret access fix + PostgreSQL subnet endpoint) and `525aec6` (North Europe
selection + placeholder Web app port fix). Working tree was clean after it.

## Rules that must not be broken (from CLAUDE.md and the canonical docs)

- Work only on the currently authorized Gate. Gate 1F is not authorized yet.
- Never modify `docs/canonical/PRD-v1.md`, `SYSTEM-ARCHITECTURE-v1.md`,
  `ARCHITECTURE-INVARIANTS-v1.md` or `DEVELOPMENT-GATES-v1.md`. If a contradiction appears, STOP and
  report it instead of editing them. (`PROJECT-STATUS.md` in the same folder is a status document.)
- Never run `az deployment ... create` without explicit product-owner approval. Use `eng/infra.ps1`,
  which requires `-Environment`, `-SubscriptionId` and `-Approve`; the product owner runs `deploy`
  interactively in their own terminal (it confirms against a live what-if).
- One approval boundary at a time: show the exact command or edit script, wait for "go", run, report,
  stop. Scripted edits use two-phase verify-then-write with exact occurrence counts and preserve each
  file's line endings.
- Do not fabricate a PASS. If a check cannot run, report it as pending.
- Do not weaken a test to make a Gate pass.

## What Gate 1E delivered and verified

**Bicep under `infra/bicep/`:** `main.bicep` (subscription scope, one resource group per environment),
`environment.bicep`, 14 modules, `parameters/main.dev.bicepparam` (North Europe, token `neu`),
`parameters/main.synthetic.bicepparam` (still Israel Central, see below), `README.md`.

**Dev environment, deployed in `rg-wce-dev-neu` (North Europe), unique name token `33f52b`:** VNet
`10.10.0.0/16` with delegated subnets and private DNS zone; Log Analytics (1 GB/day cap); Application
Insights; ACR Basic `crwcedev33f52b` (admin disabled); Key Vault `kv-wce-dev-33f52b` (RBAC, four
secrets); Storage `stwcedev33f52b` (shared key disabled); Service Bus Standard `sb-wce-dev-neu-33f52b`
with `event-processing`, `ai-jobs`, `action-jobs`; PostgreSQL Flexible Server 17 `psql-wce-dev-neu-33f52b`
(B1ms, 32 GB, private access only, `azure.extensions=VECTOR`); identities `id-wce-dev-neu-web|worker|jobs`
with 13 least-privilege role assignments; Container Apps environment `cae-wce-dev-neu`; apps
`ca-wce-dev-neu-web` (external ingress) and `ca-wce-dev-neu-worker`; manual job `caj-wce-dev-neu-migrate`.

**Verified after deployment attempt 2 (read-only, 2026-09-27):** 22/22 module deployments succeeded;
Web and Worker revisions Healthy/Running; PostgreSQL Ready; exactly 13 role assignments; PostgreSQL
subnet keeps its `Microsoft.Storage` endpoint; Web root returns HTTP 200 (placeholder page);
`/health/live` and `/health/ready` return 404 because the placeholder image serves only `/`.

**Images are placeholders** (`mcr.microsoft.com/k8se/quickstart:latest`). The Web app's port and probes
follow the image: placeholder = port 80, no probes; real image = 8080 + health probes. Do NOT trigger
the migration job while it holds the placeholder image.

## Azure facts

- Tenant: the owner's single home tenant ("Default Directory"). It enforces MFA for Azure Resource
  Manager: sign in with `az login --tenant <tenant id>` (plain `az login` fails with AADSTS50076).
- Subscription: "Azure subscription 1" (id via `az account list`), **Free Trial**, spending limit on,
  created 2026-09-27; the 30-day trial window ends **2026-10-27** unless upgraded to Pay-As-You-Go, after
  which Azure disables resources until upgrade. 12 months of free services run until 2027-10-27.
- Eleven resource providers were registered with approval (App, DBforPostgreSQL, ServiceBus, KeyVault,
  Storage, ContainerRegistry, OperationalInsights, Insights, AlertsManagement, ManagedIdentity, Network).
- Free Trial quota: **one Container Apps environment per region**. Dev uses North Europe's.
- Region evidence: Israel Central has every required service except Azure Container Apps; West Europe
  rejects this subscription ("not accepting new customers", visible only through `validate`); ten other
  regions validated; the product owner chose North Europe. Details in `docs/gates/gate-1E-report.md`.
- The two Dev passwords (`WCE_DEV_POSTGRES_ADMIN_PASSWORD`, `WCE_DEV_POSTGRES_RUNTIME_ROLE_PASSWORD`) are
  held by the product owner only. **Every redeployment must reuse the same values**, because the
  PostgreSQL administrator password is set from that parameter on each deployment.
- Estimated recurring cost at list prices: about $42–67/month (PostgreSQL free tier may cover B1ms + 32 GB
  for 12 months); the Log Analytics cap bounds the worst case.

## Open items carried into later Gates

1. **Runtime-role bootstrap in Azure** (`workcontinuity_app`): a job entry point that reads
   `postgres-runtime-role-password` from Key Vault and runs `CREATE ROLE`; never a migration. Until it
   runs, Web and Worker cannot connect to the database by design.
2. **Real images** via the CI/CD Gate (1F); then real health endpoints, database connectivity, telemetry
   and Log Analytics ingestion can be verified.
3. **Synthetic**: IaC exists and is isolated (own resource group, `10.20.0.0/16`, `WCE_SYNTHETIC_*`
   secrets) but is not deployed; its parameter file still says `israelcentral`, which cannot host
   Container Apps, and on Free Trial it cannot share North Europe with Dev. Needs a product-owner
   region/subscription decision.
4. **Key Vault purge protection is off** (foundation environments must be tear-down-able); Human Pilot
   must enable it. Re-creating Dev after a deletion requires purging the soft-deleted vault first.
5. **Log Analytics `disableLocalAuth` must stay off** (Container Apps ingests with the shared key).
6. Cost settings deliberately kept: `minReplicas=1` for both apps (the Worker has no scale rule and would
   never restart from 0), Log Analytics cap 1 GB/day (integer parameter; expected ingestion far below
   the free 5 GB/month).

## Technical notes needed to continue safely

- `environment.bicep`, `containerRegistry.bicep`, `roleAssignments.bicep`, `serviceBus.bicep` are CRLF in
  the working tree while git stores LF (`text=auto eol=lf`); git normalises on add and prints a warning.
  Edit scripts must detect and preserve each file's line endings.
- `dotnet test` under Microsoft.Testing.Platform: use `dotnet test --project <csproj> -c Debug`; do not
  pass `--nologo` (it reaches the test host and yields "Zero tests ran"). Security (35) and
  Architecture (13) test projects run without Docker; the full suite needs `./eng/dev.ps1 up`.
- What-if against a deployed environment reports about 13 "modify" entries that are noise (server-side
  defaults, read-only properties, `reference()` expressions) and 13 "unsupported" role assignments
  (their IDs depend on principal IDs). Read the property lines before treating any as real.
- EF Core Design stays pinned at 10.0.4; local PostgreSQL image is `pgvector/pgvector:pg17`.
- Working preferences: show the exact `git commit` command and wait for "go"; messages ≤10 lines via
  `-m` flags, `Co-Authored-By` only, no `Claude-Session` trailer.

## Exact next recommended step

1. Read `CLAUDE.md`, `docs/canonical/PROJECT-STATUS.md` and `docs/gates/gate-1E-report.md`.
2. Confirm the tree is clean: `git log --oneline -1 && git status --short`.
3. Do **not** start Gate 1F and do **not** deploy or change Azure resources until the product owner
   explicitly authorizes it. Before 2026-10-27 the product owner must decide on the Free Trial upgrade.

## Useful commands

```powershell
./eng/dev.ps1 up | check | logs | down          # local stack
./eng/db.ps1 status | migrate | reset            # local database
./eng/infra.ps1 build | lint                     # Bicep, no Azure
./eng/infra.ps1 validate -Environment dev -SubscriptionId <id>   # read-only, needs az login
./eng/infra.ps1 whatif   -Environment dev -SubscriptionId <id>   # read-only, needs az login
dotnet test WorkContinuityEngine.sln -c Debug    # needs local stack running
python eng/verify-architecture.py
python eng/verify-local-env.py
```
