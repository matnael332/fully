# Work Continuity Engine

Canonical implementation repository for **PRD v1** and **System Architecture v1**.

## Read first

Claude Code and human developers must read:

1. `CLAUDE.md`
2. `docs/canonical/PRD-v1.md`
3. `docs/canonical/SYSTEM-ARCHITECTURE-v1.md`
4. `docs/canonical/ARCHITECTURE-INVARIANTS-v1.md`
5. `docs/canonical/DEVELOPMENT-GATES-v1.md`
6. `docs/canonical/PROJECT-STATUS.md`

## Current development state

- Gate 1A — Repository & Solution Skeleton: **PASS**. `dotnet restore` PASS; `dotnet build` PASS with 0 warnings and 0 errors; architecture dependency rules PASS.
- Gate 1B — Local Development Environment: **PASS**. Docker local smoke test PASS; PostgreSQL healthy; Service Bus emulator healthy; Web readiness healthy; Worker dependencies ready.
- Gate 1C — Core Platform Skeleton: **PASS**. Tenant context, identity abstractions, correlation context, clock, result/error model, feature flags, audit contracts and operation context. `dotnet test` PASS with 188 tests.
- Gate 1D — Database Foundation: **PASS**. PostgreSQL migrations, tenant-aware persistence, row-level security, pgvector, raw-receipt inbox and transactional outbox. `dotnet test` PASS with 230 tests.
- Gate 1E — Azure Infrastructure as Code: **PASS**. Modular Bicep for Dev and Synthetic; Dev deployed and verified in North Europe on 2026-09-27 with placeholder images; Synthetic defined, not deployed. Real images followed in Gate 1F.
- Architecture/PRD contradictions: none.
- Gate 1F — CI/CD & Identity: **PASS**. GitHub Actions CI on every push; manual `main`-only CD to Dev through GitHub OIDC (no Azure secret in GitHub); immutable SHA-tagged images; Web and Worker run real images in Dev; the migration and bootstrap jobs stay outside CD.
- Gate 1G — Foundation Hardening: **PASS**. Azure-aware readiness under managed identity (PostgreSQL as the runtime role, Service Bus link); runtime-role bootstrap job and first migration each executed exactly once in Dev; OpenTelemetry to Application Insights; pinned gitleaks in CI; 58 Security tests; readiness probe enabled in Dev. `dotnet test` PASS with 266 tests.
- Gate 1 — Foundation: **PASS**.
- Gate 2 — Event Backbone: **AUTHORIZED (2026-09-28), IN PROGRESS**. Scope, owner decisions and sub-gates: `docs/gates/gate-2-scope.md`. Gate 3 is not started.

See `docs/canonical/PROJECT-STATUS.md` for the full verified evidence table.

## Architectural direction

- Event-driven Modular Monolith for MVP.
- .NET 10 backend/worker.
- React + TypeScript UI in the UI gate.
- Windows Agent is isolated from business authority.
- monday.com is the first SaaS connector.
- PostgreSQL is the primary operational store; pgvector is semantic retrieval only.
- AI is never Source of Truth and never directly executes actions.

## Local development

See `docs/development/local-development.md`.

Static checks:

```bash
python3 eng/verify-architecture.py
python3 eng/verify-local-env.py
```

On Windows the interpreter is normally `python` (the `python3` alias resolves to the
Microsoft Store stub and fails):

```powershell
python eng/verify-architecture.py
python eng/verify-local-env.py
```

On a Windows development machine with Docker Desktop, use the repository local-development script to perform the live smoke test documented in Gate 1B.

Database commands:

```powershell
./eng/db.ps1 status    # list applied and pending migrations
./eng/db.ps1 migrate   # apply pending migrations
./eng/db.ps1 reset     # destroy and recreate the LOCAL database volume
```

The database tests are real integration tests against the local PostgreSQL container, so
`dotnet test` requires the local stack to be running (`./eng/dev.ps1 up`).

Azure infrastructure (Bicep) commands — see `infra/bicep/README.md`:

```powershell
./eng/infra.ps1 build                                              # local
./eng/infra.ps1 lint                                               # local
./eng/infra.ps1 whatif -Environment dev -SubscriptionId <id>       # read-only, needs az login
```

Continuous integration and delivery — see `docs/gates/gate-1F-report.md`. `CI`
(`.github/workflows/ci.yml`) runs on every push and pull request to `main` with no Azure access.
`CD Dev` (`.github/workflows/cd-dev.yml`) is manual, `main`-only and run by the product owner; it
publishes immutable SHA-tagged images and may update only the Web and Worker apps. Infrastructure
changes and the migration and bootstrap jobs never go through CD; they stay on `eng/infra.ps1`.

```powershell
gh workflow run cd-dev.yml --ref main -f commit=<40-char sha on main> -f deploy=false   # publish images only
gh workflow run cd-dev.yml --ref main -f commit=<40-char sha on main> -f deploy=true    # publish and update Web/Worker
```
