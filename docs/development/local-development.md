# Local Development Environment — Gate 1B

## Goal

Run the MVP foundation locally with one Docker Compose stack:

- PostgreSQL 17
- Azure Service Bus Emulator
- SQL Server dependency required by the emulator
- ASP.NET Core Web/API
- .NET Worker

No local .NET SDK is required when using the Docker path because Web and Worker build inside .NET 10 SDK containers.

## First-time setup (Windows)

1. Install and start Docker Desktop with WSL2 enabled.
2. Copy `.env.example` to `.env`.
3. Replace both `CHANGE_ME...` passwords with strong local-only passwords.
4. Review the Microsoft license terms for the Azure Service Bus Emulator and SQL Server Linux; if accepted, set `ACCEPT_EULA=Y`.
5. From PowerShell at the repository root, run:

```powershell
./eng/dev.ps1 up
```

The script builds the Web and Worker containers, starts infrastructure, and checks readiness.

## Health endpoints

- Web liveness: `http://localhost:8080/health/live` — never touches a dependency.
- Web readiness: `http://localhost:8080/health/ready`
- Service Bus emulator health: `http://localhost:5300/health` (used by `eng/dev.ps1` while it waits for the emulator)

`/health/ready` is healthy only when the Web process can open a PostgreSQL connection as the runtime
role and open a Service Bus send link to the `ai-jobs` queue (Gate 1G). The same code runs in Azure
against the real services with the workload's managed identity; locally it uses the emulator's
published development connection string from `docker-compose.yml`. The body lists each dependency
with an exception type or reason code only, never a message or a connection detail. The Worker runs
the same checks, peeking instead of sending, every 30 seconds and on `--check-dependencies`.

## Runtime role and migrations

On first start the PostgreSQL init script creates the non-owner runtime role. Every start then runs
the `db-bootstrap` service (`--bootstrap-runtime-role`, the entry point the Azure bootstrap job
uses), which creates or updates the role, re-applies its grants and proves it can log in; only then
does the `db-migrator` service apply pending migrations. Both receive the administrative connection;
Web and Worker never do.

## Useful commands

```powershell
./eng/dev.ps1 check
./eng/dev.ps1 logs
./eng/dev.ps1 down
```

## Local secrets

`.env` is intentionally ignored by Git. Do not commit local passwords, SaaS tokens, AI keys, certificates, or production credentials. `.env.example` contains placeholders only.

## Service Bus queues

The emulator declares the three queues selected in System Architecture v1:

- `event-processing`
- `ai-jobs`
- `action-jobs`

These queues establish the local transport contract only. Business event handling is implemented in Gate 2.

## Scope boundary

Gate 1B verifies local infrastructure reachability. It intentionally does not add PostgreSQL business schema, migrations, Npgsql data access, or Azure Service Bus application consumers; those belong to later gates.
