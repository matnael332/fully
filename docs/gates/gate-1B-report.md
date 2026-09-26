# Gate 1B Report — Local Development Environment

## Scope

Gate 1B establishes a reproducible local development environment for the Work Continuity Engine foundation.

## Implemented

- Docker Compose stack for PostgreSQL 17, Azure Service Bus Emulator, the emulator's SQL Server dependency, Web/API, and Worker.
- Three local Service Bus queues matching System Architecture v1: `event-processing`, `ai-jobs`, and `action-jobs`.
- .NET 10 Docker build/runtime images for Web and Worker, so a local host does not require a .NET SDK when using Docker.
- Web liveness endpoint at `/health/live`.
- Web readiness endpoint at `/health/ready`, which verifies reachability of PostgreSQL and the Service Bus emulator.
- Worker dependency probe and periodic readiness logging.
- Local-only `.env` secret model with explicit EULA acceptance and placeholder rejection.
- PowerShell orchestration: `eng/dev.ps1 up|check|logs|down`.
- Static validation: `eng/verify-local-env.py`.

## Static verification

- Architecture dependency rules: PASS.
- Docker Compose YAML parse and expected service set: PASS.
- Service Bus emulator JSON parse and expected queue set: PASS.
- `.env` source-control exclusion: PASS.
- Placeholder-only secret example: PASS.
- Repository secret-pattern scan: PASS.

## Runtime verification (executed 2026-09-26)

Executed on Windows 11 with Docker 29.7.2 (linux containers) and Docker Compose v5.5.0.
The documented one-command path was run from a clean slate — `eng/dev.ps1 down`, Postgres
volume removed, then `eng/dev.ps1 up` — and reported `LOCAL DEV CHECK: PASS` with exit code
0 in 56 seconds.

- Docker image builds (Web and Worker): **PASS**.
- Container startup, all five services: **PASS**.
- PostgreSQL container health: **healthy** — PostgreSQL 17.11, `pg_isready` accepting
  connections, database and role `workcontinuity` present.
- Service Bus emulator `/health` response: **healthy** — HTTP 200; queues
  `event-processing`, `ai-jobs` and `action-jobs` created; AMQP port 5672 open.
- Web endpoints against live containers: **healthy** — `/` 200, `/health/live` 200, and
  `/health/ready` 200 reporting both `postgres` and `service-bus-emulator` healthy.
- Worker live-container check: **ready** — `--check-dependencies` returns `Ready:true`
  with exit code 0; the periodic loop transitions `degraded` to `ready`.
- `eng/dev.ps1 down`: **PASS** — clean teardown.

The script refuses to start until `.env` has non-placeholder local passwords and `ACCEPT_EULA=Y` is explicitly set. It was also confirmed to fail loudly (exit 1) rather than report a false PASS when a dependency does not become healthy.

## Defects found and repaired during runtime verification

The Service Bus emulator could not start at all as originally configured. It validates
queue properties on boot and exited with code 139 before serving any port:

- `DefaultMessageTimeToLive: P1D` exceeded the emulator maximum of 1 hour — changed to `PT1H`.
- `DuplicateDetectionHistoryTimeWindow: PT10M` exceeded the emulator maximum of 5 minutes — changed to `PT5M`.

Both are **emulator-only ceilings**. Real Azure Service Bus is not bound by them, so the
production values defined in Gate 1E remain independent. Dead-lettering on message
expiration stays enabled, so nothing is silently dropped locally, and invariant 64 already
requires application-level idempotency regardless of broker deduplication.

## Architecture consistency audit

No new PRD/System Architecture v1 contradiction was introduced. Gate 1B adds only local development transport/runtime scaffolding; it does not implement business schema, event processing, SaaS integration, AI behavior, or product UI.

## Gate status

**PASS — runtime verified and product-owner approved.**
