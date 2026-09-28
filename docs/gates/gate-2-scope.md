# Gate 2 Scope — Event Backbone

## Status

**AUTHORIZED 2026-09-28** by the product owner, in fully auto-safe mode, from baseline `a4bfd18`
(Gate 1 closure; CI run 36397863168 green). Work proceeds one sub-gate at a time, and each sub-gate stops
at its owner boundary for review before the next begins.

| Sub-gate | State |
|---|---|
| 2A Event contract, validation and authority table | **PASS** — commit `7ba941a`, CI run 36403160623 green |
| 2B Durable ingress | Implemented and verified locally; at its owner boundary, uncommitted |
| 2C–2G | Not started |
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

## 2B Definition of Done

1. An in-process ingress: `IEventIngress` in `App.Core`, implemented in `App.Infrastructure` and composed by
   `AddEventIngress`. No public endpoint and no Web or Worker registration (decision 2); the source adapter
   that calls it arrives with its connector Gate.
2. A delivery has no tenant member. The tenant is resolved server-side from the registered connection only
   (invariants 1 and 45) and set as a trusted-service context inside the ingress flow, never visible to the
   caller afterwards. Ingress refuses to run in a scope that already carries a tenant.
3. Order of checks: delivery shape and storable JSON, then the connection through the single approved
   function, then source match, then verification by exactly one registered verifier (fail closed; none is
   registered in production), then tenant status. Unknown or disabled connections, suspended or missing
   tenants, a source mismatch, failed verification and malformed input are rejected before any payload is
   persisted, each with a stable code and a canonical quarantine reason. There is no tenant-less payload or
   quarantine store (decision 6).
4. The raw receipt and a content-free outbox marker (the receipt id only) are written in one tenant-scoped
   transaction under forced row-level security. A delivery is acknowledged only after that commit; any
   failure rolls both back and returns a retryable, unacknowledged outcome. No outcome is silent.
5. A repeated source event id on the same connection is acknowledged as a duplicate of the stored receipt,
   with no second receipt or marker, including under concurrent delivery. A repeat with different content
   keeps the first receipt and logs a warning. A delivery without a provider event id is stored each time
   with its fingerprint; flagging such candidates is 2D work.
6. Correlation: the caller's correlation id, or a generated one, is returned on every outcome and stored on
   the marker. The marker's receipt id is the causation for the processing that follows.
7. Database: a tenant-owned `connections` table with the same forced row-level security and tenant policy
   as every tenant-owned table, and no secret column (invariant 53). Exactly one `SECURITY DEFINER`
   function, `resolve_ingress_connection(uuid)`: it returns only an active connection's tenant id and
   source system, runs with `search_path = pg_catalog, pg_temp`, has `EXECUTE` revoked from PUBLIC and
   granted to the runtime role only, and reads through an owner-only, read-only, active-rows-only policy so
   it also works under Azure's non-superuser owner. Every other table's policies are unchanged. All of this
   is pinned by source-level and database-level tests.
8. Logs and errors carry identifiers, codes and reasons only, never the payload or the proof, proven with a
   capturing log provider across every path, Entity Framework Core's own records included.
9. Tests: unit, security and integration tests against the real local PostgreSQL through the runtime role,
   covering acceptance, duplicate and concurrent duplicate delivery, rollback and retry, unknown and
   disabled connections, inactive tenants, verification failures, tenant isolation, log hygiene and a
   rehearsal of the non-superuser owner, plus mutation checks showing the key tests are not vacuous.
10. No Service Bus publication, outbox relay, consumer, dead-letter handling, Snapshot, Web, Worker, Bicep,
    workflow, package or Azure change, and no 2C work. Build with 0 warnings; the full suite passes locally.
