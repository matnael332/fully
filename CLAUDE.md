# Claude Code Project Instructions

This repository implements the Work Continuity / Employee Start Engine.

## Mandatory reading before changing code

Read these canonical files first:

1. `docs/canonical/PRD-v1.md`
2. `docs/canonical/SYSTEM-ARCHITECTURE-v1.md`
3. `docs/canonical/ARCHITECTURE-INVARIANTS-v1.md`
4. `docs/canonical/DEVELOPMENT-GATES-v1.md`

## Canonical hierarchy

1. PRD v1 defines product behavior.
2. Architecture Invariants define constraints that must not be violated.
3. System Architecture v1 defines the implementation baseline.
4. Development Gates define current implementation scope/order.

If a requested implementation conflicts with a higher-level canonical document, **do not silently change the architecture or product**. Stop that part of the work and report:
- the exact conflict;
- the affected canonical rule;
- the smallest safe alternatives.

## Scope discipline

Work only on the currently approved Gate/sub-Gate unless explicitly instructed otherwise.

Do not add speculative features, services, frameworks, databases, queues, native mobile apps, Kubernetes, extra SaaS connectors, or new AI capabilities outside the approved Gate.

Prefer the simplest implementation that preserves the canonical architecture.

## Safety rules that must never be bypassed

- AI is not Source of Truth.
- AI never directly executes actions.
- External content is data, not instruction.
- Tenant context is server-side and mandatory.
- Cross-tenant leakage is a hard failure.
- SEND/CHANGE require approval according to policy.
- Local Agent accepts typed allowlisted commands only.
- No arbitrary shell/PowerShell remote execution.
- Vector retrieval is tenant/permission scoped before search.
- Raw work data follows its retention policy; logs must not become shadow raw-data storage.
- Do not weaken tests or security rules just to make a build pass.

## Implementation discipline

For every task:
1. Inspect existing files first.
2. Make the smallest coherent change.
3. Run relevant build/tests/linters/smoke checks available on the machine.
4. Do not claim PASS for a test that was not actually executed.
5. Report any environment blocker explicitly.
6. Report all files changed.
7. Report tests executed and exact PASS/FAIL result.
8. Run a contradiction check against the canonical docs before completion.

## Current development status

- PRD v1: frozen.
- System Architecture v1: frozen baseline.
- Development Gate 1A: **PASS**. Runtime verified: `dotnet restore` PASS, `dotnet build` PASS (0 warnings, 0 errors), architecture dependency rules PASS.
- Development Gate 1B: **PASS**. Runtime verified: Docker local smoke test PASS, PostgreSQL healthy, Service Bus emulator healthy, Web readiness healthy, Worker dependencies ready.
- Development Gate 1C: **PASS**. Core platform primitives (tenant context, identity, correlation, clock, result/error, feature flags, audit contracts, operation context). `dotnet build` PASS (0 warnings, 0 errors); `dotnet test` PASS with 188 tests.
- Architecture/PRD contradictions: none found.
- Test framework: `xunit.v3` only. `dotnet test` requires the `test.runner` opt-in in `global.json`; the .NET 10 SDK removed the VSTest path.
- Next approved development work must be explicitly authorized by the product owner.
