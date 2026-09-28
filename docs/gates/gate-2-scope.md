# Gate 2 Scope — Event Backbone

## Status

**AUTHORIZED 2026-09-28** by the product owner, in fully auto-safe mode, from baseline `a4bfd18`
(Gate 1 closure; CI run 36397863168 green). Work proceeds one sub-gate at a time, and each sub-gate stops
at its owner boundary for review before the next begins.

| Sub-gate | State |
|---|---|
| 2A Event contract, validation and authority table | **PASS** — commit `7ba941a`, CI run 36403160623 green |
| 2B Durable ingress | **PASS** — commit `120183b`, CI run 36411854002 green (359 tests: 356 passed, 0 failed, 3 Service Bus emulator tests skipped by design) |
| 2C Outbox relay, publisher, consumer and dead-letter handling | **PASS** — commit `189f173`, CI run 36425641730 green (444 tests: 437 passed, 0 failed, 7 Service Bus emulator tests skipped by design, all of which passed locally) |
| 2D Processing pipeline and event ledger | **PASS** — commit `f97d22d`, CI run 36439994562 green (493 tests: 486 passed, 0 failed, 7 Service Bus emulator tests skipped by design; every 2D test ran and passed in CI) |
| 2E Snapshot projection | **PASS** — commit `e165852`, CI run 36449846577 green (524 tests: 517 passed, 0 failed, 7 Service Bus emulator tests skipped by design; every 2E test ran and passed in CI) |
| 2F Retention and metrics | **NEXT / NOT STARTED**; begins only on explicit product-owner authorization |
| 2G | Not started |
| Gate 3 — monday Connector | **NOT STARTED**; requires explicit authorization |

Azure Dev does **not** have the Gate 2B migration or schema: the `connections` table and the
`resolve_ingress_connection` function exist only in local databases. Neither does it have the Gate 2C
migration (the `outbox_pending_tenants` function and its owner read policy), the Gate 2D migration (the
three ledger tables and one index on `raw_receipts`) or the Gate 2E migration (the two Snapshot tables).
Applying them needs new images, an updated migration-job image and one migration-job run, each a separate
product-owner approval.

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

## 2C Definition of Done

1. **Transport contract in `App.Core`.** A versioned broker envelope (contract `1`), obtainable only through
   validation: from an outbox row on the relay side, from a received broker message on the consumer side. The
   `MessageId` is deterministic: the outbox message id, or after the n-th controlled re-drive that id with a
   fixed re-drive suffix. The `SessionId` is the tenant id: a receipt's object is unknown until 2D normalizes it,
   and the tenant is the narrowest key certain to hold every event of every object of that tenant, so
   per-object order is preserved and tenants never share a session. The native `CorrelationId` carries the
   ingress correlation id; typed application properties carry the contract version, message type, tenant,
   outbox message id (the event identity), causation (the receipt id) and re-drive count. The body is the
   content-free receipt marker in canonical form. Any other message type or body shape is refused, so no raw
   work content can reach the broker or its dead-letter queue.
2. **Tenant-less discovery through the approved mechanism only.** The outbox relay must find work before any
   tenant is known, and forced row-level security hides every outbox and tenant row from the runtime role
   without one. One new `SECURITY DEFINER` function, `outbox_pending_tenants(integer)`, returns only the ids of
   tenants that have unpublished outbox messages, oldest pending work first, at most 100 per call. It uses
   `search_path = pg_catalog, pg_temp` and schema-qualified names, has no dynamic SQL, has `EXECUTE` revoked from
   PUBLIC and granted to the runtime role only, and reads through an owner-only, read-only policy on pending
   outbox rows so it also works under Azure's non-superuser owner. It returns no message, payload or row id
   and locks or changes nothing. It is pinned by source-level and database-level tests, in one exact allowlist
   with the 2B resolver. No table or column changes.
3. **Outbox relay.** For each discovered tenant, in its own trusted-service tenant scope and transaction under
   forced row-level security, the relay locks a batch of that tenant's pending rows with
   `FOR UPDATE SKIP LOCKED`, so only committed rows are claimed and concurrent relays never hold the same row.
   It publishes each row and marks it published only after the broker accepted it, committing the marks while
   the locks are held. A failed send stops the tenant's batch and leaves the rest pending. A crash or rollback
   before commit releases the locks and leaves every unmarked row publishable. A crash after a send but
   before the commit republishes the same `MessageId`: broker duplicate detection absorbs that inside its
   window, and outside it the duplicate is safe by contract. A row that cannot form a valid envelope is never
   published or marked, is logged with its id and a reason code on every pass, and does not block the rows
   behind it. The lock is the claim, so no attempt or lease columns are needed.
4. **Service Bus publisher.** Sends to the existing `event-processing` queue with the envelope's `MessageId`,
   `SessionId`, `CorrelationId`, content type and typed properties. A failure returns a stable reason code,
   never an SDK message. The Service Bus SDK stays in `App.Infrastructure`.
5. **Transport-level consumer.** It validates every received message against the envelope before trusting any
   part of it. An invalid or unsupported message is dead-lettered with a stable reason code and never reaches
   the downstream handler. For a valid message it restores the tenant only from the validated broker metadata,
   as a trusted-service context in a fresh scope per message, invokes the downstream
   `ITransportMessageHandler`, and settles only after the handler reports:
   - success completes the message;
   - a transient failure or a handler exception abandons after bounded exponential backoff with jitter, and
     after 5 attempts dead-letters it as `transport.retries_exhausted`;
   - a permanent failure dead-letters it with the handler's reason code, if that code is a safe token.

   The Service Bus adapter is a peek-lock session processor that takes one message at a time per session and
   never auto-completes. A duplicate delivery reaches the handler with the same identity; persistent
   idempotency is 2D's.
6. **Dead-letter handling.** Every dead-letter decision is logged with ids, the reason code and the delivery
   count. A dead-letter reader returns safe metadata only: ids, tenant, type, reason code, counts and times, with
   no body and no free-text description. Re-drive is an explicit operation on one message named by its outbox
   id. It validates the envelope and refuses after 3 re-drives. It sends a copy that keeps the tenant, session,
   correlation, causation and outbox identity, with the re-drive count incremented and a deterministic new
   `MessageId`, so broker duplicate detection cannot swallow the copy. Only then does it remove the
   dead-lettered original. Nothing re-drives automatically.
7. **Composition, no activation.** `AddEventTransport` composes the relay and the consumer, and
   `AddServiceBusEventTransport` composes the publisher, the consumer host and the dead-letter queue. Nothing
   is registered in the Web or Worker hosts: no downstream handler exists until 2D, and a consumer without
   one must not run. Activating the Worker is a later, separately approved boundary, which includes Service
   Bus send access for the Worker identity in Azure.
8. **Hygiene.** Logs and errors carry ids, codes, counts and exception type names only. They never carry a
   message body, payload, SDK exception message or connection string. A capturing log provider proves this on
   every relay, consumer and dead-letter path.
9. **Tests.**
   - Unit tests for the envelope, its validation, the ids, the retry policy and the re-drive rules.
   - Integration tests against the real local PostgreSQL through the runtime role. For the relay: publication,
     a failed send, a crash before and after the send, concurrent relays, uncommitted rows, tenant isolation,
     unrelayable rows and logs. For the discovery function: its definition, grants and output, and a
     rehearsal of the non-superuser owner.
   - CI pipeline tests from ingress through the relay to the consumer on a deterministic in-memory transport
     that models sessions, the duplicate-detection window, abandon, delivery counts, dead-lettering and
     re-drive.
   - Local Service Bus emulator tests for send, session receive, retry, dead-lettering, reading the dead-letter
     queue, re-drive and duplicate detection. They are skipped in CI, where no emulator licence is accepted
     (decision 8).
   - Mutation checks showing the key tests are not vacuous.
10. **Out of scope and unchanged.** No event ledger, normalization, business ordering, Snapshot, monday or
    webhook logic, AI, retention purge or metric instruments (2D–2F). No Web, Worker, Bicep, workflow, package
    or Azure change, and no 2D work. Build with 0 warnings; the full suite passes locally.

## 2C closure (2026-09-28)

2C is **PASS**. Implementation commit `189f173922cf4d2adf04ee4e426b5ed5069470c2`; CI run 36425641730 green on
all three jobs: 444 tests, 437 passed, 0 failed, 7 skipped by design. The 7 skipped tests are the Service Bus
emulator tests (3 from Gate 1G readiness, 4 from 2C), which CI does not run because no emulator licence is
accepted there (decision 8); all 7 passed locally against the emulator. Azure Dev has neither the Gate 2B nor
the Gate 2C migration or schema.

Product-owner decisions at the 2C review:

1. **SessionId = TenantId** is the conservative, correctness-first transport choice for 2C, because a
   receipt's work item is not known until processing normalizes it. **Gate 2D must explicitly revisit whether
   finer-grained per-work-item ordering or session semantics are required once normalized object identity
   exists.** This closure does not change the 2C transport contract.
2. `outbox_pending_tenants` stays discovery-only: it returns tenant ids, and claiming and locking stay
   tenant-scoped under that tenant's row-level security. It is not to be broadened into a cross-tenant claim
   function.
3. No runtime activation yet. Registering the relay and consumer in the Worker, deploying, running the Azure
   migration job and granting Service Bus send access remain separate owner approval boundaries.

## 2D Definition of Done

1. **Pipeline.** A transport-independent `IEventProcessor` in `App.Core`, implemented in `App.Infrastructure`
   and handed to the 2C consumer as its `ITransportMessageHandler` by `AddEventProcessing`. It runs in the
   tenant scope the consumer restored from the validated envelope and follows the architecture §3 lifecycle:
   durable receipt, normalization, schema validation, deduplication, identity (the actor is preserved and never
   guessed), authority, ordering and conflict check, then the ledger. Nothing registers it in a host.
2. **Source-neutral normalization.** One `ISourceEventNormalizer` per source system; a second one for the same
   system fails composition. A normalizer is pure and deterministic, reads the raw payload without any tenant,
   and returns only source-derived parts: type, schema version, work object, provider revision, source times,
   changes, actor and reference. The pipeline sets every trusted part itself. The tenant comes from the receipt
   under row-level security. The source is the SaaS connector of the receipt's system and connection. The
   provider event id and receipt time come from the receipt, the correlation from the envelope, and the
   causation is the receipt id. The canonical event id is derived deterministically from the receipt id. No
   normalizer is registered in production, since the monday one belongs to Gate 3; a deterministic synthetic
   normalizer exists in tests only (decision 3). A missing normalizer quarantines the event as an unknown schema
   version; a normalizer's refusal or failure quarantines it as bad schema.
3. **Event ledger.** Three tenant-owned tables with forced row-level security, the tenant policy and guarded
   grants, and no tenant-less function:
   - `event_ledger`: one row per processed receipt. It holds the canonical event id; tenant; source system,
     kind and connection; provider event id; type and schema version; normalized object identity; provider
     revision; source-updated, occurrence, receipt and processing times; correlation; causation (the receipt);
     outcome; canonical quarantine reason; stable reason code; and the duplicate-candidate reference.
   - `event_ledger_fields`: each changed field's name, data type and ordering decision.
   - `field_ordering_states`: per tenant, object and field, the ordering evidence of the last accepted change,
     meaning its event, provider revision, source-updated time, occurrence time and provider event id. This is
     ordering state, not Snapshot state.

   The two ledger tables are append-only through restrictive row-level security policies. One additive index
   on `raw_receipts` serves duplicate-candidate lookups.
4. **Retention boundary.** Raw content stays only in `raw_receipts` (payload and fingerprint) and expires with
   it (2F). Canonical content (before and after values, actor, reference) exists only in memory while an event
   is processed and in the handoff. No ledger table can hold a value, an actor, a reference, a payload or a
   fingerprint. Schema-level and data-level tests prove the separation.
5. **Idempotency and deduplication.** One ledger row per receipt (unique on tenant and receipt), decided under a
   row lock on the receipt, so a redelivered or concurrently delivered message returns a deterministic
   already-processed no-op without relying on broker duplicate detection. The provider event id is unique per
   tenant and connection in the ledger, as it is for receipts. An event without one is compared by fingerprint
   with the earlier processed receipts of its connection and only flagged as a duplicate candidate, with the
   reference kept. It is never discarded on that basis.
6. **Ordering.** Per object and field, the incoming evidence is compared with the last accepted evidence.
   Provider revisions decide when both sides have one, and equal revisions mean equal. Otherwise the
   source-updated time decides, then the occurrence time; receipt time never orders.
   - Newer evidence advances the field.
   - Older evidence is stale and never overwrites.
   - Equal or incomparable evidence is ambiguous: the event needs reconciliation and no field advances.
   - An event with no ordering evidence also needs reconciliation.
   - Impossible ordering is quarantined. It covers a source time later than the receipt by more than a bounded
     clock-skew tolerance, an occurrence later than the object's update, and a different provider event
     claiming an already accepted revision.

   Field states are locked with `FOR UPDATE`, and concurrent first inserts converge through a retry.
7. **Authority and quarantine.** Authority is resolved per change by the 2A resolver. Any change the source is
   not authoritative for quarantines the whole event as an authority conflict; no authoritative subset is
   projected. The actor never decides authority, may stay unresolved and is never guessed, and no AI or intent
   source kind exists. Quarantine is durable and atomic: the ledger row with its canonical reason and the
   receipt's state commit in one transaction.
8. **Handoff to 2E.** An `EventProcessingResult` carries the outcome: accepted for projection, duplicate,
   stale, reconciliation required or quarantined. It also carries the reason, the object identity and every
   field's decision with the previous and incoming ordering evidence. Only an accepted result exposes the
   canonical event, in memory, for projection, and only for its advanced fields; any other outcome cannot
   produce a handoff. No Snapshot state is written.
9. **Transport mapping and crash recovery.** Every settled outcome completes the message. A receipt that is not
   visible in the message's tenant, whether forged or cross-tenant, is rejected to the dead-letter queue.
   Persistence failures and races are transient and retried. A crash before the commit leaves nothing, and
   redelivery reprocesses the receipt. A crash after the commit and before completion turns redelivery into a
   no-op. No transaction spans tenants.
10. **2C session review.** The decision between tenant-level sessions and finer per-object sessions is
    evaluated on correctness, ordering guarantees, throughput and crash recovery, and recorded. The existing
    transport contract is not changed.
11. **Hygiene.** Logs and errors carry ids, outcomes, reason codes and counts only, never a payload or a value.
12. **Tests.**
    - Unit tests for the ordering precedence and the result invariants.
    - Integration tests against the real local PostgreSQL through the runtime role, covering: first-time
      acceptance; exact duplicate delivery; duplicate provider event id; fingerprint candidate; delayed older
      revision and timestamp; ambiguous and impossible ordering; authority conflict; unresolved actor; that AI
      cannot become a source of truth; tenant isolation and forged tenants; crash before and after the commit;
      quarantine never reaching the handoff; separation of raw content and ledger metadata; and log hygiene.
    - CI pipeline tests on the in-memory transport.
    - Mutation checks on the critical guards.
13. **Out of scope and unchanged.** No Snapshot or projection (2E), no purge or metrics (2F), no monday or
    webhook logic, no AI or Work Memory, no Web or Worker activation, no Bicep, workflow, package or Azure
    change, and no change to the frozen documents. Build with 0 warnings; the full suite passes locally.

## 2D session review (decided at the 2D review; see "2D closure")

Recommendation: **keep SessionId = TenantId**. The Gate 2C transport contract is unchanged.

1. **Correctness does not depend on the session.** Processing orders every object field by the source's own
   evidence, under row locks on the receipt and on the field's ordering state, and one ledger row per receipt
   makes redelivery a no-op. Broker order is never relied on. Redelivered, concurrent and reordered messages
   converge on the same ledger and ordering state; the tests prove it, including a forced interleaving in
   which an older event holds a field while a newer one waits for it.
2. **Per-object sessions would need a transport-contract change.** The relay publishes a receipt before
   anything has normalized it, so no object identity exists at publish time. Keying sessions by object would
   need payload parsing in the relay or at ingress (breaking the content-free envelope and source-neutral
   ingress), a second object-keyed hop after normalization (a new message type), or a source-specific key at
   ingress (Gate 3 knowledge). Each is an envelope v2 and a separate owner decision.
3. **Ordering guarantees.** A tenant session delivers each tenant's messages one at a time in outbox order,
   which already covers every object of the tenant, so the product itself introduces less reordering. Events
   the source itself reorders are handled by the evidence ordering, whatever the session.
4. **Throughput and crash recovery.** A tenant is processed serially. A message that keeps failing holds up
   its tenant until its bounded retries end and it is dead-lettered; other tenants' sessions proceed. A crash
   releases the session lock and redelivery is idempotent. Per-object sessions would raise a busy tenant's
   throughput and confine a poison message to its object: a throughput gain, not a correctness one.
5. **When to revisit.** When 2F's metrics show per-tenant lag or head-of-line blocking, or when Gate 3's initial
   sync needs more throughput for one tenant. Processing already tolerates concurrent delivery, so such a change
   would touch the transport only.

## 2D closure (2026-09-28)

2D is **PASS**. Implementation commit `f97d22dc133b8cf1813ebaad6f0771d8bd0cdf06`; CI run 36439994562 green on
all three jobs: 493 tests, 486 passed, 0 failed, 7 skipped by design. The 7 skipped tests are the Service Bus
emulator tests (3 from Gate 1G readiness, 4 from 2C), which CI does not run because no emulator licence is
accepted there (decision 8); every 2D test ran and passed in CI. Azure Dev has none of the Gate 2B, 2C or 2D
migrations or schema.

Product-owner decisions at the 2D review:

1. **SessionId remains TenantId.** Database ordering and idempotency are the source of truth. Finer-grained
   sessions may be revisited later only if real throughput or metrics justify it. The 2C transport contract is
   unchanged.
2. **The 5-minute source clock-skew tolerance is approved for now.** It remains configurable
   (`EventProcessingOptions.ClockSkewTolerance`) and must be validated against real connector behavior in later
   Gates; it is not a fixed product assumption.
3. **Any authority conflict quarantines the whole event.** No authoritative subset is projected.

## 2E Definition of Done

1. **Projection point.** Only a 2D result accepted for projection reaches the Snapshot, through its in-memory
   `ProjectionHandoff`, and only for the fields 2D marked as advanced. Projection runs inside the same
   tenant-scoped transaction as the 2D ledger entry, its fields, the ordering state and the receipt's state, so
   all of them commit together or not at all, and no transaction spans tenants. Projection re-decides neither
   authority nor ordering: it applies 2D's decision and evidence as given. Nothing registers it in a host.
2. **Canonical value rule.** A field value is null (the source cleared the field) or single-line text of at most
   1,024 characters under the shared identifier rules: well formed, no control, format or line-separator
   character, and no surrounding whitespace. Canonical validation enforces it for before and after values, so a
   malformed or oversized value quarantines the event as bad schema (`event.value.invalid`) before authority and
   ordering are decided. A value is stored as the source's canonical text, compared ordinally and never
   interpreted.
3. **Snapshot model.** Two tenant-owned tables with forced row-level security, the tenant policy and guarded
   grants, and no tenant-less function:
   - `snapshot_objects`: one row per tenant and work object, with its version and the event that produced it.
   - `snapshot_fields`: one row per tenant, object and field. It holds the data type, the current value, the
     2A authority rule that made the source authoritative, and the event source (kind, system and connection).
     It also holds the canonical event id, the provider event id and revision, the source-updated and occurrence
     times, and the observed (receipt) and processed times. Two versions complete it: the field's own, and the
     object version at which the field last changed.

   No column holds a before value, an actor, a reference, a payload or a fingerprint. Rows are updated in place
   and never deleted: restrictive policies refuse deletes whatever privileges a later bootstrap re-grants.
4. **Provenance and tenant safety.** Every foreign key includes the tenant. A field row references its object
   row and the ledger event that produced it; an object row references its latest ledger event. Provenance can
   therefore never cross tenants, even for the schema owner. Every provenance value is exactly the accepted
   event's metadata and 2D ordering evidence.
5. **Versioning.** An object's version counts the accepted events projected onto it: 1 for the first, plus 1
   for each later one, however many fields it advanced. A field's version counts the accepted changes projected
   onto it, including one that repeats the current value with newer evidence. Duplicate, stale, ambiguous,
   reconciliation-required and quarantined results never reach projection, so they change no version. The
   object row is locked (`FOR UPDATE`) during projection, so concurrent events of one object get distinct,
   gap-free versions. Wall-clock time orders nothing.
6. **Multi-field atomicity.** One accepted event projects all of its advanced fields and exactly one object
   version in one transaction. Its stale fields keep their value, provenance and version.
7. **Freshness.** Freshness is derived from the stored factual times: source update, occurrence, observed and
   processed. No canonical document defines a threshold, so none is stored or invented.
8. **Consistency guard.** Before overwriting a field, projection checks that the Snapshot holds exactly the event
   2D compared against, which is the ordering state's last event. If it does not, nothing commits and the
   message is retried and then dead-lettered, instead of silently overwriting a divergent Snapshot.
9. **Crash recovery.** A crash before the commit leaves nothing, even after the Snapshot rows were written
   inside the transaction, and redelivery projects the event once. A crash after the commit and before broker
   completion turns redelivery into a ledger no-op, so neither the Snapshot nor its versions change.
10. **Facts only.** No AI, inference or intent source kind exists, and the event source is always the
    receipt's connector. The actor is neither stored nor used, and authority comes only from the 2A table.
11. **Hygiene.** Logs carry ids, the object version and counts only, never a value.
12. **Tests.** Unit tests for the value rule; security tests for the Snapshot entities and their only write
    path; integration tests against the real local PostgreSQL through the runtime role for every required case;
    a migration round trip; and mutation and vacuity checks on the critical guards.
13. **Out of scope and unchanged.** No purge or metrics (2F), no acceptance closure (2G), no monday or webhook
    logic, no Work Memory or AI, no read API, endpoint or UI, and no Web or Worker activation. No Bicep,
    workflow, package or Azure change, and no change to the frozen documents. Build with 0 warnings; the full
    suite passes locally.

## 2E closure (2026-09-28)

2E is **PASS**. Implementation commit `e165852e4a659e49107571a9bcf84c9a1329a056`; CI run 36449846577 green on
all three jobs: 524 tests, 517 passed, 0 failed, 7 skipped by design. The 7 skipped tests are the Service Bus
emulator tests (3 from Gate 1G readiness, 4 from 2C), which CI does not run because no emulator licence is
accepted there (decision 8); every 2E test ran and passed in CI. Azure Dev has none of the Gate 2B, 2C, 2D or
2E migrations or schema.

Product-owner decisions at the 2E review:

1. **Snapshot projection stays in the same database transaction as the Gate 2D ledger write.** Atomicity and
   crash safety require it; ledger and Snapshot are never split into separate commits or transactions.
2. **The canonical field-value rule is approved.** NULL means the source cleared the field. Invalid values are
   rejected and quarantined. Source facts are never silently truncated or rewritten, and validation stays
   deterministic.
3. **The 1,024-character field-value limit is approved for Gate 2E.** `EventText.MaxValueLength` stays the one
   centralized limit, never repeated as a magic number. Gate 3 must validate it against real connector data;
   if real data needs more, the limit is widened explicitly, and values are never truncated.
