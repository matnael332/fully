# Gate 1E Report — Azure Infrastructure as Code

## Status

**IaC phase: PASS.** Every locally verifiable acceptance criterion holds.

**Final Gate 1E: PENDING REAL DEPLOYMENT.** The first real Dev deployment (2026-09-27) failed at the
Web and Worker apps; the rest of the environment exists. See *Deployment attempts*.
On 2026-09-27 Azure authentication, subscription nomination, resource-provider registration (a
subscription setting, approved by the product owner), region eligibility and full server-side
validation of the Dev template were completed; see *Region selection*. What-if has been run and reviewed
(30 to create, 0 to modify, 0 to delete). The real deployment remains pending product-owner approval.

## Scope

Modular Bicep for the Dev and Synthetic environments defined in System Architecture v1, plus the
scripts to build, lint, validate, preview and deploy them. Nothing in this Gate deploys anything.

Excluded on purpose: Azure Front Door/WAF (required before the Human Pilot, not to prove the
foundation), Kubernetes, API Management, Redis, AI Search, native mobile infrastructure.

## What is defined

One set of templates. Dev and Synthetic are two deployments of the same templates with different
parameters — no infrastructure is copied.

| Resource | Module | Notes |
|---|---|---|
| Resource group (one per environment) | `main.bicep` | Subscription-scope entry point; the environment boundary is structural, not a naming convention |
| Virtual network, 2 delegated subnets, private DNS zone | `network.bicep` | Apps subnet /23 delegated to Container Apps; PostgreSQL subnet /28 |
| Log Analytics workspace | `logAnalytics.bicep` | 30-day retention, daily ingestion cap |
| Application Insights (workspace-based) | `applicationInsights.bicep` | Connection string stored in Key Vault, never output |
| Container Registry | `containerRegistry.bicep` | **Admin user disabled**; pulls by managed identity only |
| Key Vault | `keyVault.bicep` | RBAC authorization mode, soft delete |
| 4 Key Vault secrets | `keyVaultSecret.bicep` | Values are `@secure()` parameters; none is ever an output |
| Storage account + `work-content` container | `storage.bicep` | Blob-only, no public access, **shared-key access disabled** |
| Service Bus Standard + 3 queues | `serviceBus.bicep` | `event-processing`, `ai-jobs`, `action-jobs`; **local auth disabled** |
| PostgreSQL Flexible Server + database | `postgres.bicep` | Private access only; `vector` allow-listed |
| 3 user-assigned managed identities | `managedIdentity.bicep` | One per workload: web, worker, jobs |
| 13 role assignments | `roleAssignments.bicep` | Least privilege; Key Vault scoped **per secret** (11 originally; two added after deployment attempt 1, see problem 7) |
| Container Apps environment | `containerAppsEnvironment.bicep` | VNet-integrated; external ingress for webhooks, private egress |
| Web/API Container App | `containerApp.bicep` | External ingress, liveness `/health/live`, readiness `/health/ready` |
| Worker Container App | `containerApp.bicep` | No ingress |
| Migration Container Apps Job | `containerAppsJob.bicep` | Manual trigger, runs `--migrate-database`; the foundation for scheduled/synthetic jobs |

22 resource types, **all GA API versions — no preview API**.

## Identity and RBAC

Each workload has its own identity so no single identity can reach everything.

| Identity | ACR | Key Vault (per secret) | Service Bus | Storage |
|---|---|---|---|---|
| `id-…-web` | AcrPull | `postgres-app-connection-string`, `application-insights-connection-string` | Data Sender | — |
| `id-…-worker` | AcrPull | same two | Data Sender + Data Receiver | Blob Data Contributor |
| `id-…-jobs` | AcrPull | `postgres-admin-connection-string`, `postgres-runtime-role-password` | — | — |

The consequence: a compromised request-handling container **cannot read the administrative database
credential**, because that secret is granted only to the jobs identity, which is never attached to an
app that serves traffic. There is no registry admin password, no Service Bus SAS key, no storage
account key and no hardcoded service principal anywhere.

## PostgreSQL and pgvector

- Flexible Server, PostgreSQL 17 (the version Gate 1D validated locally), Burstable SKU.
- **Private access only**: delegated subnet plus private DNS zone; `publicNetworkAccess: Disabled`.
- `azure.extensions = VECTOR` is the **server-side allowlist prerequisite**. The application migration
  remains responsible for `CREATE EXTENSION vector` inside the database, exactly as in Gate 1D. This
  template does not duplicate or replace that migration.
- `require_secure_transport = ON`.
- Password authentication **and** Entra authentication enabled. Password auth carries the unchanged
  Gate 1D model (owner for migrations, non-owner `workcontinuity_app` for runtime, so row-level
  security is effective). Entra is enabled so a later Gate can move workloads to managed-identity
  database access without re-provisioning — nothing here forces that change.
- Both passwords are `@secure()` parameters with no default. They are never in source or output.

## Service Bus

Real Azure settings, deliberately not constrained by the local emulator's 1-hour TTL and 5-minute
duplicate-detection ceilings that Gate 1D repaired.

| Queue | Sessions | Duplicate detection | TTL | Why sessions |
|---|---|---|---|---|
| `event-processing` | **yes** | PT1H | P14D | Ordering is per work object, not global |
| `ai-jobs` | no | PT1H | P1D | Independent units; AI failure must not block the fact layer |
| `action-jobs` | **yes** | PT1H | P7D | One action's lifecycle must not be reordered |

All three: dead-lettering on expiration, `maxDeliveryCount` 10, lock PT5M. This matches the session
choices already made for the local emulator in Gate 1B. Duplicate detection is defence in depth only;
application-level idempotency remains mandatory (invariant 64).

## Dev / Synthetic isolation — proven, not asserted

Both parameter files were compiled with `az bicep build-params` and the resolved values diffed.
Every boundary-defining value differs: environment name, VNet `10.10.0.0/16` vs `10.20.0.0/16`, both
subnets, tags. Values that are identical are *names* (login, database, role, region, image), not
shared resources — each environment gets its own server, vault, namespace, registry, storage account,
network and identities under those names. Both secure parameters resolved to empty, confirming no
secret is in either file.

Secrets come from **environment-specific variables** (`WCE_DEV_*` vs `WCE_SYNTHETIC_*`), so a Dev
password can never be handed to Synthetic by accident. Synthetic and Human-Pilot/production data must
not share a trust boundary (invariant 56); this is where the Synthetic side of that is expressed.

## Scripts

`eng/infra.ps1 build | lint | validate | whatif | deploy`. `-Environment` and `-SubscriptionId` are
mandatory for every Azure-facing command, so the target is always stated, never inherited from
`az login`. `deploy` additionally requires `-Approve` and then runs `--confirm-with-what-if`, so
approval is given against the actual planned change set. Deployments are incremental and never delete
a resource absent from the template; complete mode is not offered. Nothing else in the repository
invokes `az deployment`.

## Verification (executed 2026-09-27)

| Check | Result |
|---|---|
| `az --version` | Azure CLI 2.90.0 (installed this session via winget, as authorised) |
| `az bicep version` | Bicep CLI 0.47.16 |
| `az bicep build` main.bicep | **PASS — 0 warnings, 0 errors** |
| `az bicep build-params` dev + synthetic | **PASS** |
| `az bicep lint` | **PASS — 0 diagnostics** |
| Preview API versions | **none** (22 resource types checked) |
| Repository secret scan | **PASS** — no Azure credentials, no literal passwords or connection strings, no keys/certificates, no tracked `.env`; local generated passwords appear only in the untracked `.env` |
| `dotnet restore` / `dotnet build` | PASS — 0 warnings, 0 errors |
| `dotnet test` | **PASS — 230/230**, 0 skipped |
| `eng/verify-architecture.py` | PASS |
| `eng/verify-local-env.py` | PASS |
| Gate 1B regression (`eng/dev.ps1 up`, clean slate) | PASS, 140 s |
| `az account show` | Authenticated 2026-09-27 via tenant-scoped login (MFA enforced); subscription `Azure subscription 1`, Free Trial, product-owner nominated |
| Resource providers | 11 namespaces registered 2026-09-27 with product-owner approval |
| Azure validation (`az deployment sub validate`, Dev) | **PASS in North Europe — 38 resources, provisioningState Succeeded** (2026-09-27) |
| Azure what-if (`az deployment sub what-if`, Dev, North Europe) | Before attempt 1: **30 to create, 0 to modify, 0 to delete**, 11 unsupported (the role assignments, whose IDs depend on principal IDs that exist only once the identities are created). After the problem-7 fix, against the partially deployed environment: **0 to create, 14 to modify, 16 no change, 2 to ignore, 13 unsupported** — the 13 are the role assignments including the two new ones; the 14 "modify" entries are what-if noise (server-side defaults, read-only properties and `reference()` expressions) plus the two failed apps, which re-provision; the 2 ignored are the smart-detection resources Application Insights created on its own (2026-09-27, reviewed by the product owner) |
| Region/SKU availability | Verified 2026-09-27; Israel Central and West Europe rejected, North Europe selected — see *Region selection* |
| Azure resources deployed | Dev attempt 1 (`wce-dev-20260927-113601`) **FAILED** at the Web and Worker apps; 20 of 22 module deployments succeeded and the environment otherwise exists in `rg-wce-dev-neu` — see *Deployment attempts* |

Azure-facing commands run on 2026-09-27, each shown to the product owner first: `az login` (tenant-scoped),
`az account` queries, read-only `az rest` GETs and `az provider show/list`, `az provider register`
(approved), `az postgres flexible-server list-skus`, `az deployment sub validate` and
`az deployment sub what-if`. `az deployment sub create` was run once, by the product owner through
`eng/infra.ps1 deploy -Approve` (attempt 1, failed; see *Deployment attempts*).

## Problems found and fixed

1. **Azure CLI and Bicep were not installed.** Installed via winget and `az bicep install` (tooling
   installation was authorised). No other system change.
2. **Three Bicep diagnostics on first build**: `anonymousPullEnabled` is not a property on
   `registries@2023-07-01`; `minimumTlsVersion`/`publicNetworkAccess` need `namespaces@2024-01-01`
   (GA); `reference()` cannot take a module output. Fixed with an approved, two-phase
   verify-then-write script that asserts exact occurrence counts before writing anything.
3. **My own count expectation was wrong once**: the queue API version occurs three times (one per
   canonical queue), not once. The assertion stopped the script before any file was written; the
   expectation was corrected to 3 with approval and re-run.
4. **`disableLocalAuth: true` on Log Analytics would have broken container logging.** The Container
   Apps environment's log-analytics destination ingests with the workspace shared key, which that
   setting disables. Found on my own review of the API-version list; removed before commit and
   documented in the module. The key is read inside the deployment and is never committed or output.
5. **The credential-scan test did not cover `.bicep`/`.bicepparam`.** Extended, since infrastructure is
   exactly where a literal connection string is most likely to be pasted. Still passes.
6. **The Web app could not have provisioned with the placeholder image.** The quickstart placeholder
   listens on port 80 and serves no health endpoints, but the Web app's ingress target port and both
   probes were fixed at 8080; Container Apps refuses a revision whose target port does not match the
   listening port, so the first deployment would have failed at that step. Found while reviewing the
   what-if output (2026-09-27). Fixed in `environment.bicep`: the Web app's port and probes now follow
   the image, so the placeholder provisions and the real image keeps 8080 with `/health/live` and
   `/health/ready`. The Worker has no ingress and no probes and needed no change.
7. **Web and Worker could not read the Application Insights connection string.** Dev deployment attempt 1
   failed at both apps with `ContainerAppOperationError`: "unable to fetch secret
   'appinsights-connection-string' using Managed identity". Both apps reference the Key Vault secret
   `application-insights-connection-string`, but `roleAssignments.bicep` granted Web and Worker only
   `postgres-app-connection-string`; listing every role assignment in the subscription confirmed that no
   assignment on the Application Insights secret existed for either identity. A missing assignment, not
   RBAC propagation. The identity table above described the intended grants, not the deployed ones. Fixed
   by adding two per-secret `Key Vault Secrets User` assignments (13 in total); no vault-wide access.
8. **The PostgreSQL subnet drifted from the template after attempt 1.** Flexible Server adds a
   `Microsoft.Storage` service endpoint to its delegated subnet on creation (confirmed on the live
   `snet-postgres`). `network.bicep` did not declare it, so the what-if before attempt 2 predicted its
   removal on the next VNet update, which Azure may refuse on an in-use delegated subnet. Fixed by
   declaring the endpoint on `snet-postgres` only; no other subnet or networking property changed.

## Remaining blockers before a real deployment

1. **Azure access — resolved 2026-09-27.** Tenant-scoped `az login` (MFA enforced), subscription
   `Azure subscription 1` (Free Trial, spending limit on) nominated by the product owner, eleven resource
   providers registered with approval.
2. **Region — resolved 2026-09-27.** The region is a Gate 1E parameter, not an architecture requirement.
   Israel Central and West Europe were rejected on evidence and North Europe selected after full
   validation; see *Region selection*.
3. **Deployment approval.** Attempt 1 (2026-09-27) failed at the Web and Worker apps for a missing role
   assignment (problem 7). After the fix, `validate` passed again and `whatif` against the partially
   deployed environment shows 0 create, 14 modify (noise plus the two apps re-provisioning), 16 no change,
   2 ignore and 13 unsupported role assignments. Attempt 2 awaits explicit product-owner approval and must
   be run interactively by the product owner with the **same two Dev passwords as attempt 1**, because the
   PostgreSQL administrator password is set from that parameter on every deployment.
4. **Runtime-role bootstrap in Azure is not implemented.** Locally, `workcontinuity_app` is created by a
   container init script. In Azure the equivalent must read `postgres-runtime-role-password` from Key
   Vault at run time and `CREATE ROLE` — a job entry point, not a migration, because a migration must
   never contain a credential. The Key Vault secret and the jobs identity's access to it are in place;
   the code is a follow-up. Until it runs, the Web and Worker apps cannot connect, by design.
5. **Placeholder images.** Both apps and the job reference `mcr.microsoft.com/k8se/quickstart` until
   the CI/CD Gate publishes real images to the environment registry.
6. **Recurring cost on deployment**: PostgreSQL Flexible Server (Burstable), Service Bus Standard,
   Container Apps (scale-to-zero for Worker is not used since `minReplicas` is 1 for both apps),
   Log Analytics ingestion (capped), Storage, Key Vault operations, ACR Basic. Key Vault purge
   protection is off so foundation environments can be torn down; Human Pilot must enable it.

## Deployment attempts

| Attempt | When (UTC) | Result | Detail |
|---|---|---|---|
| 1 — `wce-dev-20260927-113601`, run by the product owner via `eng/infra.ps1 deploy -Approve` | 2026-09-27 08:38–08:47 | **FAILED** after 9 min 29 s | 20 of 22 module deployments succeeded: resource group, network, private DNS zone and link, Log Analytics, Application Insights, registry, Key Vault and its four secrets, storage, Service Bus and queues, PostgreSQL server and database, three identities, 11 role assignments, Container Apps environment, migration job. `container-app-web` and `container-app-worker` failed with `ContainerAppOperationError`: unable to fetch secret `appinsights-connection-string` with the app's managed identity. Root cause: no role assignment on `application-insights-connection-string` for either identity (problem 7). Both apps exist in provisioning state Failed and will be re-provisioned by the next incremental deployment. |

## Region selection (2026-09-27)

The canonical documents do not fix an Azure region; the region is a Gate 1E parameter. Three regions were
evaluated for Dev against the real subscription, and the decision was taken by the product owner.

| Region | Outcome | Evidence |
|---|---|---|
| Israel Central | **Rejected** | Azure Container Apps is not offered in the region: absent from the `Microsoft.App` location list at subscription and tenant scope and from the Container Apps usages API; a public feature request has been open since August 2024 with no timeline. Every other required service and SKU (PostgreSQL 17 Burstable `B1ms`/`B2s`, Service Bus, Key Vault, Storage, Registry, Log Analytics, Application Insights, managed identities, VNet) is available there. |
| West Europe | **Rejected** | `az deployment sub validate` failed with `RequestDisallowedByAzure`: "The selected region is currently not accepting new customers" for this subscription. Provider location lists and SKU capability queries do not expose this restriction; only validation does. Confirmed on two independent runs. |
| North Europe | **Selected** | Full server-side validation of the Dev template **PASSED: 38 resources, `provisioningState` Succeeded**. Nine further candidates also passed and remain fallbacks: Sweden Central, Germany West Central, France Central, Poland Central, Italy North, Spain Central, Switzerland North, Austria East, UAE North. |

Subscription facts that constrain the deployment: Free Trial offer with the spending limit on; a Container
Apps quota of one managed environment per region, so Dev and Synthetic cannot share a region until the
subscription is upgraded; the Synthetic parameter file still names Israel Central and needs its own
decision before any Synthetic deployment.

## Architecture consistency

PostgreSQL primary store, pgvector inside PostgreSQL, Service Bus as transport only, Container Apps
for Web and Worker, Container Apps Jobs, Key Vault for secrets, managed identity for every Azure
access path, Bicep as IaC, Dev/Synthetic as separate boundaries with no shared database or secret:
all as written in System Architecture v1 §9 and the invariants (52, 53, 56, 63, 68, 69). No
contradiction found. No canonical document was changed.

**Gate 1F was not started.**
