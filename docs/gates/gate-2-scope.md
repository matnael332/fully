# Gate 2 Scope — Event Backbone

## Status

**AUTHORIZED 2026-09-28** by the product owner, in fully auto-safe mode, from baseline `a4bfd18`
(Gate 1 closure; CI run 36397863168 green). Work proceeds one sub-gate at a time, and each sub-gate stops
at its owner boundary for review before the next begins.

| Sub-gate | State |
|---|---|
| 2A Event contract, validation and authority table | Implemented and verified locally; at its owner boundary, uncommitted |
| 2B–2G | Not started |
| Gate 3 — monday Connector | **NOT STARTED**; requires explicit authorization |

Standing constraints for all of Gate 2: no Azure write, no RBAC, identity, networking or Key Vault change,
no job start and no `CD Dev` with `deploy: true` without separate product-owner approval, so Azure Dev
stays unchanged; the frozen PRD, System Architecture, Architecture Invariants and Development Gates are not
modified; nothing monday-specific is implemented (that is Gate 3).

## Canonical scope

Development Gates v1 gives Gate 2 one sentence; the frozen documents define each item.

| Item | Requirement | Source |
|---|---|---|
| Canonical events | CloudEvents-compatible envelope with product extensions: id, source, type, subject, occurred, source-updated, received and processed times, tenant, actor and object identity, before/after change, source event id, schema version, correlation and causation ids, source reference, domain payload | PRD §11; architecture §3 |
| Durable ingress | Verify the source, resolve the tenant server-side, commit receipt and outbox marker together, and only then acknowledge | architecture §3, §9; invariants 1, 45 |
| Inbox/Outbox | The outbox relay publishes to Service Bus; consumers are idempotent | architecture §9 |
| Service Bus processing | `event-processing` queue; dead-letter handling mandatory; the broker is transport, not truth | architecture §9; invariants 52, 63 |
| Dedupe | Stable provider event id per tenant and connection; fingerprints only flag candidates; the application stays idempotent even with broker dedupe | architecture §3; invariant 64 |
| Ordering | Per object and field: provider revision, then source update time, then occurrence time; if ambiguous, do not overwrite, reconcile | architecture §3; invariants 12, 18 |
| Quarantine | Bad schema, unknown tenant, unknown schema version, impossible ordering, authority conflict and invalid authentication never reach Snapshot | architecture §3; invariant 13 |
| Source-of-truth projection | Authority by data type, defined in code, never by AI | PRD §5; invariants 5, 21 |
| Snapshot | Each critical field keeps value, authority, source object, source event, source update time, observed time, version and freshness; intent never becomes fact | architecture §3; invariants 14, 15 |
| Retention basics | Raw work events and payloads roll off after at most 30 days, separately from audit | PRD §12; invariants 20, 55 |
| Cross-cutting | Correlation from ingress through the broker to Snapshot; receipt latency, queue depth, dead-letter and projection latency metrics; no payloads or secrets in logs | architecture §10 |

**Acceptance** (Development Gates v1): duplicate, delayed and out-of-order events do not corrupt Snapshot,
and no event is silently lost. The Gate workflow adds automated tests, edge and failure cases, a
PRD/architecture contradiction check, a concise report and product-owner approval.

**Out of scope:** everything Gate 3 names — monday authentication, webhooks, source mapping, API
reconciliation and initial sync (Gate 2 only records that reconciliation is needed); Work Memory and AI
(Gate 4); SSO and authorization (Gate 7); the synthetic pilot, and the pilot performance targets, which
Gate 2 measures but does not gate on (Gate 8).

## Product-owner decisions (2026-09-28)

1. **Tenant-less operations — approved with a strict boundary.** Narrowly scoped database functions only,
   and only for the operations that must begin without a tenant: resolving an inbound connection to its
   tenant, claiming unpublished outbox work, and retention/purge work. They return only the minimum
   identifiers, status or counts; never return tenant payloads; offer no generic cross-tenant query; use a
   fixed, safe `search_path`; carry least-privilege `EXECUTE` grants; are pinned by an exact allowlist in the
   security/architecture tests; and leave every other tenant-isolation protection unchanged. This is the
   explicit approval architecture §14 requires for a tenant-isolation mechanism.
2. **Ingress surface — approved.** An in-process ingestion service with acknowledge-after-commit semantics.
   No public monday or webhook endpoint in Gate 2; public source verification and mapping stay in Gate 3.
3. **Synthetic source — approved.** A deterministic synthetic source and connection, in tests only. Nothing
   monday-specific.
4. **Snapshot shape — approved.** A generic field-level Snapshot carrying the required provenance
   attributes. Authority rules are explicit in code for the data types PRD §5 defines. AI never determines
   source-of-truth authority.
5. **Retention — approved with clarification.** The 30-day maximum applies to raw work-event and
   payload-bearing data: raw receipts and payloads, quarantine payload content, published outbox payload
   content and any other raw event body. Snapshot is kept. The event ledger retains only the minimal
   identity, provenance and dedupe metadata needed for correctness after payload expiry; raw payload and
   body content expires by 30 days. If the schema cannot cleanly separate the two, stop before implementing
   retention and report.
6. **Failures before tenant resolution — approved.** Invalid authentication and unknown connections are
   rejected before any payload is persisted. No tenant-less payload or quarantine store. Logs carry safe
   metadata and reason codes only, never payloads or secrets.
7. **Unmapped actors — approved.** The fact and event are preserved and the actor is marked unresolved.
   Actor identity is never guessed or synthesized. An unresolved actor alone is not a quarantine reason.
8. **Broker tests in CI — approved.** An in-memory transport test double for deterministic CI pipeline
   tests; real Service Bus emulator integration tests for local verification; no emulator licence
   acceptance in CI at this stage. The acceptance suite must still prove transport-independent processing
   semantics, while the local emulator tests prove the real broker adapter.
9. **Purge scheduling — approved.** The purge Worker entry point is implemented in Gate 2. No scheduled
   Azure Container Apps Job is created or deployed; Azure scheduling is a later, separately approved
   rollout boundary.

## Sub-gates

- **2A** Event contract, validation and authority table.
- **2B** Durable ingress.
- **2C** Outbox relay, publisher, consumer and dead-letter handling.
- **2D** Processing pipeline and event ledger.
- **2E** Snapshot projection.
- **2F** Retention and metrics.
- **2G** Acceptance suite, crash recovery, tenant isolation and the Gate report.

## 2A Definition of Done

1. `App.Core` holds a canonical event contract that carries every architecture §3 core concept plus the
   provider revision the ordering rule needs, and is only obtainable through validation.
2. The contract separates retained metadata (identity, provenance, ordering, dedupe) from expiring content
   (before/after values, actor, source reference), so decision 5 can be enforced by structure.
3. Validation is deterministic and total: malformed provider data yields a failed result with a stable,
   content-free error code, never an exception, and never fills a missing source time from receipt time.
4. A closed schema registry: an unknown type is bad schema; an unknown version of a known type is an unknown
   schema version.
5. The canonical quarantine reasons are exactly the six of architecture §3, and every validation error maps
   to one of them.
6. A CloudEvents 1.0 attribute mapping: required context attributes always present, product extensions
   named by the CloudEvents rule, optional values omitted rather than invented, no content in attributes.
7. The authority table covers exactly the six PRD §5 data types; a deterministic resolver decides authority
   from data type, source and the object's home only, fails closed, and cannot be influenced by the actor or
   by any AI. The source vocabulary has no AI or internal-intent member.
8. Actors are explicitly resolved or unresolved; a resolved actor requires a canonical user.
9. Tests prove each rule, including four mutation checks for vacuity. The one architecture test that banned
   source-of-truth logic "yet" is tightened into an AI ban plus a declaration allowlist, not deleted.
10. No change to Infrastructure, the database, migrations, Web, Worker, Bicep, workflows, packages or Azure.
    Build with 0 warnings; Unit, Security and Architecture tests, the full suite and both verifier scripts
    pass.
