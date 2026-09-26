# Development Gates v1

Status: **ACTIVE**

Development proceeds one Gate at a time. A Gate is not considered complete until its acceptance criteria and required verification pass. Product owner explicitly approves movement between major Gates.

## Gate 1 — Foundation

### 1A — Repository & Solution Skeleton
- solution/repository structure;
- Web, Worker, Core, Infrastructure, Monday Connector, Windows Agent, Simulator, Tests;
- dependency boundaries;
- baseline conventions.

Acceptance:
- structure valid;
- architecture dependency rules pass;
- solution includes all intended projects;
- build verification when a .NET SDK is available.

### 1B — Local Development Environment
- Docker Compose;
- PostgreSQL;
- Service Bus emulator;
- Web + Worker local containers;
- local configuration/secrets pattern;
- health checks.

Acceptance:
- Compose config valid;
- secrets excluded from Git;
- services can be started on a Docker-enabled development machine;
- live health/smoke test passes.

### 1C — Core Platform Skeleton
- tenant context;
- identity abstractions;
- correlation IDs;
- result/error model;
- clock abstraction;
- basic audit contracts;
- feature flags.

### 1D — Database Foundation
- PostgreSQL schema baseline;
- migrations;
- tenant-aware entities;
- RLS foundation;
- pgvector;
- Inbox/Outbox tables.

### 1E — Azure Infrastructure as Code
- Bicep resources for Dev/Synthetic;
- Container Apps;
- ACR;
- PostgreSQL;
- Service Bus;
- Key Vault;
- monitoring;
- environment separation.

### 1F — CI/CD & Identity
- GitHub Actions build/test/container/deploy;
- GitHub→Azure OIDC/federated identity;
- Managed Identity for service access.

### 1G — Foundation Hardening
- OpenTelemetry baseline;
- health/readiness;
- secret scan;
- architecture/security tests;
- environment isolation tests;
- fresh-environment smoke test.

## Gate 2 — Event Backbone

Build canonical events, durable ingress, Inbox/Outbox, Service Bus processing, dedupe, ordering, quarantine, source-of-truth projection, Snapshot, retention basics.

Acceptance: duplicate/delayed/out-of-order events do not corrupt Snapshot and no event is silently lost.

## Gate 3 — monday Connector

Build real authentication, webhooks, source mapping, API reconciliation and initial sync.

Acceptance: a real monday change reaches canonical event and Snapshot end-to-end; reconciliation repairs missed event scenarios.

## Gate 4 — Work Memory + AI

Build Structured Work Memory, pgvector retrieval, Context Builder, AI Gateway, structured output, evidence validation, Fact/Inference/Unknown, Safe Mode.

Acceptance: Morning Brief uses real system facts and Golden Scenarios show zero critical unsupported facts.

## Gate 5 — Product UI

Build Desktop Hero Screen, compact mobile/PWA brief, clarification flow, overload/planning UX, prepared/approval sections.

Acceptance: complete start-of-day flow is understandable and usable; product owner reviews representative screens/flows.

## Gate 6 — Action Engine + Windows Agent

Build action state machine, policy/approval, WorkspacePlan, Windows Agent, capabilities, offline local queue, open/focus/minimize operations.

Acceptance: “Start Work” prepares a real Windows workspace and unsafe/unapproved actions are denied.

## Gate 7 — Security Hardening

Build/complete Entra SSO, tenant/resource authorization, RLS, secrets, device enrollment/revocation, JIT support access, kill switches, audit separation.

Acceptance: zero cross-tenant failures and zero unauthorized-action failures in security tests.

## Gate 8 — Synthetic Pilot

Run 20 workers × 31 days, Functional + Chaos + Security, deterministic ground truth, fault injection, KPI and ROI mechanics.

Acceptance: all synthetic Go/No-Go criteria pass.

## Gate 9 — Human Pilot Readiness

Polish, signed Agent package, real monday workspace validation, real Agent smoke test, monitoring, runbook, legal/privacy deployment review.

Acceptance: all prior Gates green and product owner approves entry into a 5–10 employee human pilot.

## Gate workflow

For each Gate:

```text
Build
→ Automated tests
→ Edge/failure cases
→ PRD/Architecture contradiction check
→ concise report/demo
→ PASS / FAIL
→ product-owner approval
```
