# Gate 1F Report — CI/CD & Identity

## Status

**Gate 1F: PASS** — declared by the product owner on 2026-09-27 after the first real continuous-delivery
run to Dev was verified read-only in Azure. Sub-gates: 1F-A design PASS, 1F-B repository PASS, 1F-C CI
PASS, 1F-D deployment identity PASS, 1F-E continuous delivery PASS.

What the Gate proves: GitHub Actions builds, tests and images on every push; GitHub authenticates to Azure
by OIDC federation to a dedicated managed identity with **no long-lived Azure credential or client secret
in GitHub**; Web and Worker run **real Release images** in Dev and are healthy; continuous delivery can
update only those two apps and holds no permission on anything else; the migration job is **unchanged
and has never been executed**; Managed Identity Operator is **proven unnecessary**.

**Carried forward to Gate 1G, not part of this Gate's acceptance:** Azure-aware readiness (`/health/ready`
still probes local-only targets; the readiness-probe parameter is off), the runtime-role bootstrap job,
the first migration run, database connectivity, telemetry and OpenTelemetry, the Synthetic
region/subscription decision, and the Free Trial upgrade decision before 2026-10-27. One CD path has not
yet been exercised by a run: a real image *change* through CD (the new-revision path); it happens
naturally on the first `deploy: true` run for a later commit. The privilege-boundary assertion was
exercised by the final no-op run 36330256517 (read denied, assertion passed).

## Scope

The three items of Development Gate 1F: GitHub Actions build/test/container/deploy; GitHub→Azure
OIDC/federated identity; managed identity for service access (the per-workload identities from Gate 1E
remain the only way the apps reach Azure services).

Excluded on purpose: GitHub environments and branch protection (unavailable on this private repository:
GitHub returned HTTP 403 "Upgrade to GitHub Pro or make this repository public" for branch protection,
and environments are documented as public-only on the Free plan), so the approval gate is a manual
`workflow_dispatch` from `main` run by the product owner; deploying by digest (undocumented for Container
Apps, tags are made immutable by the workflow instead); Synthetic delivery; any infrastructure change
through CD.

## What was delivered

| Sub-gate | Delivered | Evidence |
|---|---|---|
| 1F-A Design | Decisions: private repository; `workflow_dispatch` on `main` as the approval gate; infrastructure stays on `eng/infra.ps1`; first real-image rollout by the owner's Bicep deploy, then CD may change only Web/Worker images; migration job image and execution outside CD; readiness and runtime-role bootstrap may move to 1G; published images are Release; least privilege — Container Apps role scoped to the two apps, Managed Identity Operator only with proof | approved 2026-09-27 |
| 1F-B Repository | Private repository `matnael332/fully`, `main` pushed, Actions enabled, default token permission read-only | `a412267` on `origin/main` |
| 1F-C CI | `.github/workflows/ci.yml` — see *Workflows*; Dockerfiles take `ARG CONFIGURATION` | commit `3c68848`; first run 36317953496 SUCCEEDED |
| 1F-D Deployment identity | `infra/bicep/modules/deploymentIdentity.bicep`: user-assigned identity `id-wce-dev-neu-github`, federated credential `github-main`, three role assignments | commit `260686f`; deployment `wce-dev-20260927-163546` Succeeded (3 min 31 s, 0 failed operations); CI run 36323519792 SUCCEEDED |
| 1F-E Continuous delivery | `.github/workflows/cd-dev.yml` — see *Workflows*; seven repository **variables** (no secrets); `ci.yml` pinned to `ubuntu-24.04`; `webReadinessProbeEnabled` Bicep parameter (default off); Dev images from `WCE_DEV_WEB_IMAGE` / `WCE_DEV_WORKER_IMAGE`; image-drift guard in `eng/infra.ps1` | commit `f487430`; CI run 36326066378 SUCCEEDED; publish-only CD run 36326240690 SUCCEEDED |
| 1F-E correction | Dedicated `migrationJobImage` parameter (`WCE_DEV_MIGRATION_JOB_IMAGE`) so a Web/Worker rollout never changes the job; drift guard extended to the job | commit `1661700`; CI run 36327135490 SUCCEEDED |
| First real rollout | Product owner's `eng/infra.ps1 deploy -Approve` with the two published tags | deployment `wce-dev-20260927-175625` Succeeded (3 min 42 s, 22 nested deployments, 0 failed operations) |
| First `deploy: true` CD run | Reused both tags, PATCHed Web then Worker, verified | run 36328631074 SUCCEEDED |
| Privilege-boundary fix | Replaced an unreadable before/after snapshot of the migration job with an assertion that the CD identity is **denied** reading it; every step runs under `bash -eo pipefail` | commit `8a7d404`; CI run 36329536123 SUCCEEDED |
| Final no-op `deploy: true` run | Exercised the fixed workflow on the same commit: read of the job denied with `AuthorizationFailed`, assertion passed; tags reused; two app writes; no new revision; job untouched | run 36330256517 SUCCEEDED |

## Identity and RBAC

| Item | Value |
|---|---|
| Identity | `id-wce-dev-neu-github`, user-assigned, never attached to a running workload |
| Federated credential | `github-main`; issuer `https://token.actions.githubusercontent.com`; audience `api://AzureADTokenExchange`; subject `repo:matnael332@223243154/fully@1390662248:ref:refs/heads/main` — GitHub's **immutable** subject format for repositories created after 2026-07-15 (owner and repository ids embedded); the classic `repo:owner/name:ref:...` form would be accepted by Azure and then fail silently at every token exchange |
| Role assignments | `AcrPush` on `crwcedev33f52b` (legacy registry-permissions mode; pull included); `Container Apps Contributor` on `ca-wce-dev-neu-web`; `Container Apps Contributor` on `ca-wce-dev-neu-worker` — scoped to each app, **not** the resource group |
| Not granted | anything on the migration job, the environment, Key Vault, PostgreSQL, Service Bus, storage, networking, role assignments; **no Managed Identity Operator** |
| Proof that Managed Identity Operator is unnecessary | Container Apps update is a JSON merge patch whose body omits `identity`; run 36328631074 PATCHed both apps successfully with that body |
| Proof of the boundary | The identity's read of the migration job returns `AuthorizationFailed`; the workflow asserts exactly that, and run 36330256517 passed the assertion |
| Total in `rg-wce-dev-neu` | 16 role assignments (13 from Gate 1E + these 3); none created by any CD run |

Secrets: none. GitHub holds seven repository variables (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, `ACR_LOGIN_SERVER`, `WEB_APP_NAME`, `WORKER_APP_NAME`),
all non-secret identifiers. The `secrets` store is empty.

## Workflows

**`CI`** (`ci.yml`) — on every push and pull request to `main`, and manually. `permissions: contents:
read`; no Azure access. Job `build-test` on `ubuntu-24.04`: PostgreSQL 17 + pgvector service, the
non-owner runtime role created by the **unchanged** local init script (`docker cp` / `docker exec`),
`dotnet restore`, `dotnet build` with warnings as errors, `eng/verify-architecture.py`,
`eng/verify-local-env.py`, the **full test suite**, Bicep build and lint. Job `container-images`: Release
builds of Web and Worker, Web start-up smoke (`/health/live` 200 with a well-formed but unreachable
connection string — liveness must not need a database), Worker no-configuration smoke (running after
20 s with 0 restarts). Every action is pinned to a commit SHA. Release configuration was proven by
reading `AssemblyConfigurationAttribute` from the built assemblies with `MetadataLoadContext`.

**`CD Dev`** (`cd-dev.yml`) — `workflow_dispatch` only; inputs `commit` (full 40-character SHA that must
be an ancestor of `main`) and `deploy` (boolean). `permissions: id-token: write, contents: read`;
`concurrency: cd-dev`; every step under `bash -eo pipefail`; jobs run only when `github.ref` is `main`.

- Job `publish`: checks out the exact commit; OIDC login; `az acr login`; for each of Web and Worker
  resolves the tag `<registry>/wce/<app>:<sha>` with `docker manifest inspect`: **absent → build in
  Release and push once; present → reuse its digest and push nothing**. After publishing, a second
  independent resolve must agree; a tag that changed digest during the run fails the job. Any other
  registry error fails closed.
- Job `deploy` (only with `deploy: true`): OIDC login; **asserts the identity is denied reading the
  migration job** (a successful read fails the run as privilege creep; any other error fails it); for
  each app GETs it, refuses if the Web ingress still targets port 80 (placeholder configuration — the
  first real rollout must go through Bicep, which also flips port and probes), re-resolves the digest and
  refuses a moved tag, then PATCHes **only** `properties.template` with the image replaced (JSON merge
  patch replaces arrays, so the whole template is sent; `identity` and `configuration` are never sent),
  polls until the latest revision runs the image and is Healthy/Running with at least one replica, then
  Web: `/health/live` must return 200; Worker: still Healthy/Running after 60 s. Finally the identity,
  registry, secret-name, ingress, environment, probe and resource configuration must be identical to the
  pre-PATCH snapshot.

**Rollback:** dispatch `CD Dev` with a previous SHA (its tags are immutable and still present), or the
owner's Bicep deploy with the previous or the placeholder images.

## Guards

| Guard | Where | What it prevents |
|---|---|---|
| Immutable SHA tags | `cd-dev.yml` publish job | `AcrPush` could technically overwrite a tag; the workflow never pushes to an existing tag and fails if a tag moves during a run |
| Image drift | `eng/infra.ps1` `Protect-Images` | An infrastructure redeploy silently reverting Web, Worker or the migration job to the placeholder: when `WCE_DEV_WEB_IMAGE`, `WCE_DEV_WORKER_IMAGE` or `WCE_DEV_MIGRATION_JOB_IMAGE` is unset and the live resource runs a real image, the live image is adopted; an explicit variable always wins |
| Separate migration-job image | `migrationJobImage` in Bicep | A Web/Worker rollout changing the job (found by what-if before the first rollout; fixed in `1661700`) |
| Privilege boundary | `cd-dev.yml` deploy job | Privilege creep on the CD identity: the run passes only when reading the job is denied |
| Placeholder refusal | `cd-dev.yml` deploy job | CD cannot perform the first rollout (port 80 → 8080 and probes belong to Bicep) |
| Pinned runners and actions | both workflows | `ubuntu-latest` migrating to Ubuntu 26 on 2026-10-19; unpinned action tags |
| Ancestor check | `cd-dev.yml` publish job | Publishing a commit that is not on `main` |

## Verification (executed 2026-09-27)

| Check | Result |
|---|---|
| `dotnet build` (CI, warnings as errors) | PASS on every run listed below |
| Full test suite in CI (PostgreSQL 17 + pgvector + runtime role) | PASS on every run listed below — 230 tests, 0 failed, 0 skipped on run 36329536123 |
| `eng/verify-architecture.py`, `eng/verify-local-env.py` | PASS (CI and local) |
| Bicep build and lint | PASS (CI and local) |
| Security tests (`App.Security.Tests`) | PASS — 35/35, run locally after every change |
| CI runs on `main` | 36317953496 (`3c68848`), 36323519792 (`260686f`), 36326066378 (`f487430`), 36327135490 (`1661700`), 36329536123 (`8a7d404`) — all SUCCEEDED, both jobs |
| GitHub repository state | private; Actions enabled; default workflow token read-only; no environments, protection rules or branch protection (Free plan, 403 evidence); OIDC subject prefix `repo:matnael332@223243154/fully@1390662248` |
| Deployment identity (read-only) | identity, federated credential (issuer/subject/audience exact) and exactly three role assignments present; 16 assignments in the resource group |
| Azure what-if before each Bicep deployment | reviewed: 1F-D 5 to create (identity, credential, 3 assignments), 0 delete; 1F-E edits 0 create / 0 delete; rollout preview: Web image + port 8080 + liveness probe, Worker image, migration job no change, 0 create / 0 delete |
| Publish-only CD run 36326240690 (`deploy: false`) | both repositories absent before; both images built and pushed **once**; deploy job skipped |
| Registry | `wce/web:f4874302…` → `sha256:9a9ae3e3d192029c34c46f6538e1282aa1ab5de5728cd34c3e5726ff92aba6de`; `wce/worker:f4874302…` → `sha256:8f5fe9b3807efd5b6895a7a1734cc5a1037be5d18dc3c0191a656dea64a073b3`; one manifest per repository, unchanged by every later run |
| Bicep rollout `wce-dev-20260927-175625` | Succeeded; 22 nested deployments, 0 failed operations; no resource created; 16 role assignments, none new; Key Vault, Service Bus, storage and networking properties unchanged |
| `ca-wce-dev-neu-web` after rollout | revision `ca-wce-dev-neu-web--0000001` Healthy/Running, 1 replica, 0 restarts; image exact; ingress 8080; liveness `/health/live`; `GET /health/live` → **200** `{"status":"live","service":"work-continuity-web"}`; identity, registry identity and secret references unchanged |
| `ca-wce-dev-neu-worker` after rollout | revision `ca-wce-dev-neu-worker--0000001` Healthy/Running, 1 replica, 0 restarts after a 253 s hold; image exact; identity, registry identity and secret references unchanged |
| First `deploy: true` CD run 36328631074 | SUCCEEDED; tags reused (nothing pushed); activity log shows **exactly two writes** by the identity, `Microsoft.App/containerApps/write` on Web and Worker; same image → no new revision; `/health/live` 200; Worker stable after 60 s; snapshots equal |
| Final no-op `deploy: true` CD run 36330256517 (workflow at `8a7d404`) | SUCCEEDED; the assertion step ran under `bash --noprofile --norc -e -o pipefail` and printed "privilege boundary holds: the deployment identity is denied reading the migration job (AuthorizationFailed)"; tags reused, nothing pushed; exactly two Container App writes in the activity log; same revisions, 0 restarts, `/health/live` 200; 20 resources, 16 role assignments, latest deployment unchanged |
| Migration job after every step | image `mcr.microsoft.com/k8se/quickstart:latest`; manual trigger, `--migrate-database`; **0 executions**; last modified only by the Bicep rollout |
| Platform after every step | PostgreSQL Ready; Container Apps environment Succeeded; 20 resources; 16 role assignments |
| Architecture/PRD contradictions | none |

## Problems found and fixed

1. **GitHub's immutable OIDC subject format.** The repository's OIDC settings use
   `repo:OWNER@OWNER-ID/REPO@REPO-ID`; the federated credential was built from the numeric ids, verified
   against the repository before deployment.
2. **`BCP120` in `deploymentIdentity.bicep`.** Role-assignment names used the identity's `principalId`,
   which is not deploy-time constant; names now use `guid(scope.id, identity.id, roleId)`.
3. **`AcrPush` can overwrite a tag.** Azure offers no tag immutability on this SKU, so the workflow makes
   SHA tags immutable itself (see *Guards*); no permission was broadened.
4. **The Worker image parameter also fed the migration job.** The rollout what-if showed the job's
   image changing to the Worker image. The owner stopped the rollout; a dedicated `migrationJobImage`
   parameter with its own variable and drift-guard entry was added, Synthetic's compiled output stayed
   byte-identical, and the what-if then showed no change to the job.
5. **The CD workflow's "migration job unchanged" check was vacuous.** The identity cannot read the job
   (by design), the `az rest | jq` pipeline returned jq's exit status, both snapshots became nulls and
   compared equal. Replaced by the privilege-boundary assertion; `defaults.run.shell: bash` gives every
   step `-e` and `-o pipefail`. The Azure evidence for the job was independently verified with the owner
   account (image, executions, last-modified time).
6. **`dotnet test --nologo` under Microsoft.Testing.Platform runs zero tests.** The flag reaches the test
   host; CI and local commands do not pass it.
7. **Release proof.** BuildKit's build log does not show `ARG` substitution reliably, so Release was
   proven by reading the assembly configuration attribute from the built images with `MetadataLoadContext`.
8. **A `docker container prune` in a draft smoke step was rejected.** It would have deleted unrelated
   stopped containers; the workflow removes only the containers it created, by exact name.

## Deployments and CD runs

| When (UTC) | What | Result |
|---|---|---|
| 2026-09-27 13:36–13:40 | `wce-dev-20260927-163546` — deployment identity, credential, 3 role assignments (owner via `eng/infra.ps1 deploy -Approve`) | Succeeded, 3 min 31 s, 0 failed operations |
| 2026-09-27 14:31–14:33 | CD run 36326240690, `deploy: false` | Succeeded; two images pushed once |
| 2026-09-27 14:57–15:01 | `wce-dev-20260927-175625` — first real images, Web port 8080 + liveness probe (owner via `eng/infra.ps1 deploy -Approve` with `WCE_DEV_WEB_IMAGE` and `WCE_DEV_WORKER_IMAGE`) | Succeeded, 3 min 42 s, 0 failed operations |
| 2026-09-27 15:11–15:13 | CD run 36328631074, `deploy: true`, commit `f487430` | Succeeded; tags reused; two app writes; verified |
| 2026-09-27 15:37–15:40 | CD run 36330256517, `deploy: true`, commit `f487430` (no-op; workflow at `8a7d404`) | Succeeded; privilege assertion passed; tags reused; two app writes; verified |

## Architecture consistency

Managed identity for every Azure access path, no shared secrets, per-workload identities, Bicep as the
only infrastructure path, Dev/Synthetic as separate boundaries, AI never involved in delivery: all as
written in System Architecture v1 §9 and the invariants (52, 53, 56, 63, 68, 69). CD is a narrow,
auditable path that changes two images and nothing else; infrastructure and the migration job remain on
the human-approved `eng/infra.ps1` path. No contradiction found. No canonical document was changed.

**Gate 1F: PASS. Gate 1G (Foundation Hardening) is NEXT and NOT STARTED; it requires explicit product-owner
authorization.**
