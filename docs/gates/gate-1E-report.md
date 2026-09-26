# Gate 1E Report — Azure Infrastructure as Code

## Status

**IaC phase: PASS.** Every locally verifiable acceptance criterion holds.

**Final Gate 1E: PENDING REAL DEPLOYMENT.** No Azure resource has been created, updated or deleted.
This machine holds no Azure credentials, so server-side validation, what-if and region/SKU
availability could not be checked and are reported as pending rather than assumed.

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
| 11 role assignments | `roleAssignments.bicep` | Least privilege; Key Vault scoped **per secret** |
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
| `az account show` | **Not authenticated** |
| Azure validation | **Pending authentication** |
| Azure what-if | **Pending authentication** |
| Israel Central region/SKU availability | **Pending authentication — unverified** |
| Azure resources deployed | **NONE** |

The only `az` commands run were `--version`, `bicep install/version/build/lint/build-params` and
`account show`. No `az deployment` command was invoked.

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

## Remaining blockers before a real deployment

1. **`az login`** to a subscription the product owner nominates. Until then subscription, tenant,
   validation, what-if and region availability are unknown.
2. **Israel Central availability must be confirmed** for: Container Apps (Consumption, VNet-integrated),
   PostgreSQL Flexible Server Burstable `B1ms`/`B2s` with PostgreSQL 17 and the `vector` extension,
   Service Bus Standard, and Key Vault. The region is the architecture's default, not a verified fact.
3. **`validate` and `whatif`** must run and be reviewed before `deploy -Approve`.
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

## Architecture consistency

PostgreSQL primary store, pgvector inside PostgreSQL, Service Bus as transport only, Container Apps
for Web and Worker, Container Apps Jobs, Key Vault for secrets, managed identity for every Azure
access path, Bicep as IaC, Dev/Synthetic as separate boundaries with no shared database or secret:
all as written in System Architecture v1 §9 and the invariants (52, 53, 56, 63, 68, 69). No
contradiction found. No canonical document was changed.

**Gate 1F was not started.**
