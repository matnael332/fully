# Azure Infrastructure as Code — Gate 1E

Bicep definitions for the Dev and Synthetic environments. One set of templates; the two environments
differ only by parameters.

## Layout

```
infra/bicep/
  main.bicep                       Subscription-scope entry point: creates the resource group,
                                   deploys environment.bicep into it.
  harness-vault.bicep              Gate 3G DEV/TEST harness vault (Stage B1): a subscription-scope
                                   entry point of its own, never referenced by main.bicep.
  environment.bicep                One complete environment: naming, wiring, outputs.
  parameters/
    main.dev.bicepparam            Dev parameters.
    main.synthetic.bicepparam      Synthetic parameters.
  modules/
    network.bicep                  VNet, delegated subnets, private DNS zone for PostgreSQL.
    logAnalytics.bicep             Telemetry sink with a daily ingestion cap.
    applicationInsights.bicep      Workspace-based Application Insights.
    containerRegistry.bicep        Private ACR; admin user disabled.
    keyVault.bicep                 RBAC-mode vault.
    keyVaultSecret.bicep           One secret; value is a secure parameter, never an output.
    storage.bicep                  Blob storage for large/temporary approved content; no shared keys.
    serviceBus.bicep               Standard namespace and the three canonical queues.
    postgres.bicep                 Flexible Server, private access, vector extension allow-listed.
    managedIdentity.bicep          One user-assigned identity per workload.
    roleAssignments.bicep          Least-privilege RBAC, Key Vault scoped per secret.
    containerAppsEnvironment.bicep VNet-integrated environment.
    containerApp.bicep             Reusable app definition (Web, Worker).
    containerAppsJob.bicep         Reusable job definition (migration and runtime-role bootstrap jobs; later scheduled/synthetic jobs).
    harnessVault.bicep             The harness vault, its audit workspace and the runtime role's one assignment.
```

## Commands

Dev and Synthetic commands go through `eng/infra.ps1`. The Gate 3G DEV/TEST harness vault has its own owner-run
path, `eng/harness-vault.ps1` (see below). Nothing else in the repository invokes `az deployment`. `build` and `lint`
compile and lint both entry points.

```powershell
./eng/infra.ps1 build                                                       # local, no Azure
./eng/infra.ps1 lint                                                        # local, no Azure
./eng/infra.ps1 validate -Environment dev       -SubscriptionId <id>        # read-only, needs az login
./eng/infra.ps1 whatif   -Environment dev       -SubscriptionId <id>        # read-only, needs az login
./eng/infra.ps1 deploy   -Environment dev       -SubscriptionId <id> -Approve
./eng/infra.ps1 deploy   -Environment synthetic -SubscriptionId <id> -Approve
```

`-Environment` and `-SubscriptionId` are mandatory for every Azure-facing command, so the target is
always stated rather than inherited from whatever `az login` last selected. `deploy` additionally
requires `-Approve` and then runs with `--confirm-with-what-if`, so approval is given against the
actual planned change set. Deployments are incremental: they never delete a resource that is absent
from the template.

## Secrets

No credential exists in this directory. The parameter files read the two PostgreSQL passwords from
environment-specific variables at deployment time:

| Environment | Variables |
|---|---|
| Dev | `WCE_DEV_POSTGRES_ADMIN_PASSWORD`, `WCE_DEV_POSTGRES_RUNTIME_ROLE_PASSWORD` |
| Synthetic | `WCE_SYNTHETIC_POSTGRES_ADMIN_PASSWORD`, `WCE_SYNTHETIC_POSTGRES_RUNTIME_ROLE_PASSWORD` |

The names are environment-specific on purpose: a Dev value can never be handed to Synthetic. The script
refuses to validate, what-if or deploy while either variable is unset. Inside Azure the values live in
that environment's Key Vault, read by each workload's managed identity and scoped per secret.

## Images

The Dev parameter file reads the four container images from the operator's environment and defaults
to the placeholder: `WCE_DEV_WEB_IMAGE`, `WCE_DEV_WORKER_IMAGE`, `WCE_DEV_MIGRATION_JOB_IMAGE`,
`WCE_DEV_BOOTSTRAP_JOB_IMAGE`. The script's image-drift guard adopts the live image of each app and of
each job when its variable is unset (a resource that is not deployed yet is left alone), so an
infrastructure redeploy can never silently revert a real image to the placeholder; an explicitly set
variable always wins. GitHub CD (`.github/workflows/cd-dev.yml`) updates only the Web and Worker
images; the migration and bootstrap job images change only through this path.

## Readiness probe

`webReadinessProbeEnabled` (default `false`) adds the Web readiness probe on `/health/ready` (initial
delay 5 s, period 20 s, timeout 15 s, failure threshold 6) once a real image is deployed. Dev sets it to
`true` since Gate 1G (`main.dev.bicepparam`); Synthetic keeps the default. The Worker has no probe: its
heartbeat performs the same dependency check and logs `readiness=ready|degraded`.

## Gate 3G DEV/TEST harness vault (Stage B1)

Authored offline under owner decisions B-2, B-3, B-4, B-8 and B-9 (approved 2026-10-08; record:
`docs/gates/gate-3-scope.md`). Nothing of it is deployed: deploying it, creating the Entra identity and every later
real step are separate owner approvals.

- `harness-vault.bicep` creates the dedicated resource group `rg-wce-harness-neu` in North Europe and the custom role
  `wce-harness-keyvault-secrets-runtime`: exactly `secrets/getSecret/action`, `secrets/setSecret/action` and
  `secrets/delete`, no control-plane action, assignable only within that resource group.
- `modules/harnessVault.bicep` creates one Standard vault with Azure RBAC, soft-delete retention of 7 days and purge
  protection left off; a firewall that denies by default, with no trusted-service bypass and at most one admitted
  public IPv4 address, for an approved run window only; its own Log Analytics workspace for the vault's `AuditEvent`
  logs; and, only when a runtime principal is given, the runtime role's one assignment at the vault.
- `eng/harness-vault.ps1 whatif|deploy -SubscriptionId <id> [-RuntimePrincipalObjectId <id>] [-AllowedIpv4Address <ip>] -Approve`
  previews or deploys only this entry point; without `-Approve` it shows the plan and sends nothing.
- The Entra application, its service principal and its certificate credential are not in Bicep: they belong to
  `eng/harness-identity.ps1` (Azure CLI only, every command behind `-Approve`, verified by read-back). The Microsoft
  Graph Bicep extension is not approved.

## What is deliberately not here

- **Azure Front Door / WAF** — required before the Human Pilot, not to prove the foundation.
- **Kubernetes, API Management, Redis, AI Search** — excluded by the frozen architecture.
- **Automatic job execution** — the migration job and the runtime-role bootstrap job (Gate 1G) are
  manual; nothing in Bicep or CD starts them. Each is started by an explicit, product-owner-approved
  `az rest POST …/start`, and each has run exactly once in Dev.
