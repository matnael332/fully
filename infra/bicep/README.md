# Azure Infrastructure as Code — Gate 1E

Bicep definitions for the Dev and Synthetic environments. One set of templates; the two environments
differ only by parameters.

## Layout

```
infra/bicep/
  main.bicep                       Subscription-scope entry point: creates the resource group,
                                   deploys environment.bicep into it.
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
```

## Commands

All commands go through `eng/infra.ps1`. Nothing else in the repository invokes `az deployment`.

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

## What is deliberately not here

- **Azure Front Door / WAF** — required before the Human Pilot, not to prove the foundation.
- **Kubernetes, API Management, Redis, AI Search** — excluded by the frozen architecture.
- **Automatic job execution** — the migration job and the runtime-role bootstrap job (Gate 1G) are
  manual; nothing in Bicep or CD starts them. Each is started by an explicit, product-owner-approved
  `az rest POST …/start`, and each has run exactly once in Dev.
