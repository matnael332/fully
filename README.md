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
- Architecture/PRD contradictions: none.
- Real test-project infrastructure is deferred to a later Gate; `dotnet test` currently discovers zero tests.
- Gate 1C onward: do not start without explicit product-owner approval.

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
