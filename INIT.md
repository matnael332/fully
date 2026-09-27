# INIT — Project Handoff State

Last updated: 2026-09-27 (after Gate 1F closure). Read this first when resuming.

## Where the project is

| Gate | Status |
|---|---|
| 1A Repository & Solution Skeleton | PASS |
| 1B Local Development Environment | PASS |
| 1C Core Platform Skeleton | PASS |
| 1D Database Foundation | PASS |
| 1E Azure Infrastructure as Code | PASS — Dev deployed and verified 2026-09-27 |
| **1F CI/CD & Identity** | **PASS** — real images delivered to Dev through GitHub OIDC and verified 2026-09-27; declared by the product owner |
| 1G Foundation Hardening | **NEXT / NOT STARTED** — requires explicit product-owner authorization |

**Latest commit:** the Gate 1F closure commit on `main` (run `git log -1`). It follows `8a7d404`
(privilege-boundary assertion + strict shell in CD), `1661700` (dedicated migration-job image parameter),
`f487430` (CD workflow, runner pin, readiness parameter, image variables, drift guard), `260686f`
(deployment identity), `3c68848` (CI), `a412267` (Gate 1E closure). Working tree was clean after it.

## Rules that must not be broken (from CLAUDE.md and the canonical docs)

- Work only on the currently authorized Gate. Gate 1G is not authorized yet.
- Never modify `docs/canonical/PRD-v1.md`, `SYSTEM-ARCHITECTURE-v1.md`,
  `ARCHITECTURE-INVARIANTS-v1.md` or `DEVELOPMENT-GATES-v1.md`. If a contradiction appears, STOP and
  report it instead of editing them. (`PROJECT-STATUS.md` in the same folder is a status document.)
- Never run `az deployment ... create` without explicit product-owner approval. Use `eng/infra.ps1`,
  which requires `-Environment`, `-SubscriptionId` and `-Approve`; the product owner runs `deploy`
  interactively in their own terminal (it confirms against a live what-if).
- Never dispatch `CD Dev` with `deploy: true` without explicit product-owner approval; it is the only
  automated write path to Azure and it may change nothing but the Web and Worker images.
- Never trigger the migration job. It still holds the placeholder image, has its own image parameter
  (`WCE_DEV_MIGRATION_JOB_IMAGE`) and has never been executed. Its first run is Gate 1G work.
- Never grant the GitHub deployment identity anything on the migration job; the CD workflow asserts that
  reading it is denied and fails on success.
- One approval boundary at a time: show the exact command or edit script, wait for "go", run, report,
  stop. Read-only checks need no approval. Scripted edits use two-phase verify-then-write with exact
  occurrence counts and preserve each file's line endings.
- Do not fabricate a PASS. If a check cannot run, report it as pending.
- Do not weaken a test or a security rule to make a Gate pass.

## What Gate 1F delivered and verified

**GitHub:** private repository `matnael332/fully`; Actions enabled; default workflow token read-only; no
environments or branch protection (GitHub Free private repository, 403 evidence) — the approval gate is a
manual `workflow_dispatch` from `main` run by the product owner. Seven repository **variables**
(`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, `ACR_LOGIN_SERVER`,
`WEB_APP_NAME`, `WORKER_APP_NAME`); **no secrets**.

**`CI`** (`.github/workflows/ci.yml`): every push and pull request to `main`; restore, build (warnings as
errors), architecture and local-environment verifiers, the full test suite against PostgreSQL 17 + pgvector
with the runtime role, Bicep build and lint, Release images with start-up smoke tests. `ubuntu-24.04`,
SHA-pinned actions, no Azure access.

**`CD Dev`** (`.github/workflows/cd-dev.yml`): manual, `main`-only, inputs `commit` (40-char SHA on
`main`) and `deploy` (boolean). Publish job: immutable SHA tags `crwcedev33f52b.azurecr.io/wce/{web,worker}:<sha>`
— an existing tag is reused, never overwritten. Deploy job: asserts the identity is denied reading the
migration job; refuses while the Web ingress still targets port 80; re-resolves the digest; PATCHes only
`properties.template` with the new image; polls to Healthy/Running; Web `/health/live` must be 200; Worker
must stay Healthy after 60 s; identity/registry/secret/ingress/env/probe/resource configuration must be
unchanged. Every step runs under `bash -eo pipefail`.

**Deployment identity:** `id-wce-dev-neu-github`, federated credential `github-main` with subject
`repo:matnael332@223243154/fully@1390662248:ref:refs/heads/main` (GitHub's immutable subject format);
`AcrPush` on the registry, `Container Apps Contributor` on the Web app and on the Worker app only. No
Managed Identity Operator (proven unnecessary: the merge patch omits `identity`). 16 role assignments in
`rg-wce-dev-neu` (13 from Gate 1E + 3).

**Runs and deployments (all 2026-09-27):** CI runs 36317953496, 36323519792, 36326066378, 36327135490,
36329536123 all green; deployment `wce-dev-20260927-163546` (identity); publish-only CD run 36326240690;
owner rollout `wce-dev-20260927-175625` (first real images: Web port 8080 + liveness probe, Worker image);
first `deploy: true` CD run 36328631074 (tags reused, two app writes, no new revision, verified); final
no-op `deploy: true` run 36330256517 on the fixed workflow (privilege assertion passed).

**Live state, verified read-only:** Web revision `ca-wce-dev-neu-web--0000001` and Worker revision
`ca-wce-dev-neu-worker--0000001` on the `f4874302…` tags, Healthy/Running, 0 restarts; `GET /health/live`
→ 200; migration job on the placeholder, 0 executions; PostgreSQL Ready; 20 resources.

**Bicep additions:** `modules/deploymentIdentity.bicep`; parameters `webReadinessProbeEnabled` (default
false until `/health/ready` is Azure-aware) and `migrationJobImage`; Dev images from `WCE_DEV_WEB_IMAGE`,
`WCE_DEV_WORKER_IMAGE`, `WCE_DEV_MIGRATION_JOB_IMAGE` (default placeholder). `eng/infra.ps1` image-drift
guard: when a variable is unset and the live app or job runs a real image, the live image is adopted, so a
redeploy cannot revert it; an explicit variable always wins. Synthetic is untouched.

## Azure facts

- Tenant: the owner's single home tenant ("Default Directory"). It enforces MFA for Azure Resource
  Manager: sign in with `az login --tenant <tenant id>` (plain `az login` fails with AADSTS50076).
- Subscription: "Azure subscription 1" (id via `az account list`), **Free Trial**, spending limit on,
  created 2026-09-27; the 30-day trial window ends **2026-10-27** unless upgraded to Pay-As-You-Go, after
  which Azure disables resources until upgrade. 12 months of free services run until 2027-10-27.
- Free Trial quota: **one Container Apps environment per region**. Dev uses North Europe's.
- Dev: `rg-wce-dev-neu`, North Europe, name token `33f52b` (registry `crwcedev33f52b`, vault
  `kv-wce-dev-33f52b`, server `psql-wce-dev-neu-33f52b`, namespace `sb-wce-dev-neu-33f52b`, storage
  `stwcedev33f52b`, environment `cae-wce-dev-neu`, apps `ca-wce-dev-neu-web|worker`, job
  `caj-wce-dev-neu-migrate`, identities `id-wce-dev-neu-web|worker|jobs|github`).
- The two Dev passwords (`WCE_DEV_POSTGRES_ADMIN_PASSWORD`, `WCE_DEV_POSTGRES_RUNTIME_ROLE_PASSWORD`) are
  held by the product owner only. **Every redeployment must reuse the same values**, because the
  PostgreSQL administrator password is set from that parameter on each deployment.
- Key Vault purge protection is off (foundation environments must be tear-down-able); Human Pilot must
  enable it. Log Analytics `disableLocalAuth` must stay off (Container Apps ingests with the shared key).

## Open items carried into Gate 1G

1. **Azure-aware readiness**: make `/health/ready` probe the real dependencies, then set
   `webReadinessProbeEnabled = true` (Bicep) — the parameter exists and is off.
2. **Runtime-role bootstrap in Azure** (`workcontinuity_app`): a job entry point that reads
   `postgres-runtime-role-password` from Key Vault and runs `CREATE ROLE`; never a migration. Until it
   runs, Web and Worker cannot connect to the database by design.
3. **First migration run**: give the job a real image through `WCE_DEV_MIGRATION_JOB_IMAGE` on an owner
   Bicep deploy, then trigger it once with approval; then database connectivity, telemetry, OpenTelemetry
   baseline, secret scan, environment-isolation tests and a fresh-environment smoke test (Gate 1G list).
4. **Not yet exercised by a CD run**: a real image *change* (the new-revision path); it happens naturally
   on the first `deploy: true` run for a later commit. The privilege-boundary assertion passed in the
   no-op run 36330256517.
5. **Synthetic**: IaC exists and is isolated but is not deployed; its parameter file still says
   `israelcentral`, which cannot host Container Apps, and on Free Trial it cannot share North Europe with
   Dev. Needs a product-owner region/subscription decision.
6. **Free Trial upgrade decision before 2026-10-27.**

## Technical notes needed to continue safely

- CRLF working copies while git stores LF (`text=auto eol=lf`): `environment.bicep`,
  `containerRegistry.bicep`, `roleAssignments.bicep`, `serviceBus.bicep`, `CLAUDE.md`, `README.md`,
  `docs/canonical/PROJECT-STATUS.md`. Edit scripts must detect and preserve each file's line endings.
- `dotnet test` under Microsoft.Testing.Platform: use `dotnet test --project <csproj> -c Debug`; do not
  pass `--nologo` (it reaches the test host and yields "Zero tests ran"). Security (35) and
  Architecture (13) test projects run without Docker; the full suite needs `./eng/dev.ps1 up`.
- What-if against the deployed environment reports about 16 "modify" entries that are noise (server-side
  defaults, read-only properties, `reference()` expressions, secret references echoed as changes) and 13
  "unsupported" role assignments. Read the property lines before treating any as real; the two rollout
  previews in `docs/gates/gate-1F-report.md` show what a real change looks like.
- `az role assignment list --all` returns scopes in lower case (`/resourcegroups/`); filter
  case-insensitively. Windows PowerShell 5.1 `ConvertFrom-Json` emits a JSON array as one object unless
  assigned to a variable first, and converts ISO timestamps to `DateTime`; count and parse accordingly.
- On this machine `bash` on PATH is the WSL relay; Git Bash is `C:\Program Files\Git\usr\bin\bash.exe`.
- Working preferences: show the exact `git commit` command and wait for "go" unless a consolidated
  approval covers it; messages ≤10 lines via `-m` flags, `Co-Authored-By` only, no `Claude-Session`
  trailer; never `docker ... prune` or `git clean`, remove only resources this session created by name.

## Exact next recommended step

1. Read `CLAUDE.md`, `docs/canonical/PROJECT-STATUS.md`, `docs/gates/gate-1F-report.md`.
2. Confirm the tree is clean: `git log --oneline -1 && git status --short`.
3. Do **not** start Gate 1G and do **not** deploy, dispatch `CD Dev` with `deploy: true`, or change Azure
   resources until the product owner explicitly authorizes it. Before 2026-10-27 the product owner must
   decide on the Free Trial upgrade.

## Useful commands

```powershell
./eng/dev.ps1 up | check | logs | down          # local stack
./eng/db.ps1 status | migrate | reset            # local database
./eng/infra.ps1 build | lint                     # Bicep, no Azure
./eng/infra.ps1 validate -Environment dev -SubscriptionId <id>   # read-only, needs az login
./eng/infra.ps1 whatif   -Environment dev -SubscriptionId <id>   # read-only, needs az login; prints the image guard
dotnet test WorkContinuityEngine.sln -c Debug    # needs local stack running
python eng/verify-architecture.py
python eng/verify-local-env.py
gh run list --workflow ci.yml --limit 5          # CI history
gh workflow run cd-dev.yml --ref main -f commit=<sha> -f deploy=false   # publish images only (approval required)
```
