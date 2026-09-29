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
| 2F Retention and metrics | **PASS** — commit `259be35`, CI run 36542058355 green (584 tests: 577 passed, 0 failed, 7 Service Bus emulator tests skipped by design; every 2F test ran and passed in CI) |
| 2G Acceptance suite, crash recovery, tenant isolation and the Gate report | Authorized 2026-09-29; implemented and verified locally; at its owner boundary, uncommitted (evidence: `docs/gates/gate-2-report.md`) |
| Gate 3 — monday Connector | **NOT STARTED**; requires explicit authorization |

Azure Dev does **not** have the Gate 2B migration or schema: the `connections` table and the
`resolve_ingress_connection` function exist only in local databases. Neither does it have the Gate 2C
migration (the `outbox_pending_tenants` function and its owner read policy), the Gate 2D migration (the
three ledger tables and one index on `raw_receipts`), the Gate 2E migration (the two Snapshot tables) or the
Gate 2F migrations (`RawContentRetention` and `RawContentPurgeAge`: purgeable content columns and their check,
the content index, the retention function and its owner read policy, and the database's receipt-time stamp).
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

## 2F Definition of Done

1. **Raw content, its purge age and its limit.** Raw work-event content is a receipt's `payload` and
   `payload_fingerprint`, which is the 2D classification ("raw content stays only in `raw_receipts`, payload and
   fingerprint, and expires with it"). Its age is the database's own clock, PostgreSQL `now()`, less the receipt
   time `received_at_utc`, which the database itself stamps with `now()` when it stores the receipt: a trigger
   replaces any supplied value and refuses any later change, and the application never writes it. It is due for
   purge once that age reaches 28 days (672 hours) and must never be older than the 30-day hard limit. The database
   clock alone stamps and decides both; neither the ingress host's nor the purge Worker's clock, nor a provider's
   times, decide anything here. The purge age (the
   30-day limit less a 48-hour lead margin), the overdue age and the limit are fixed constants in
   `RawContentRetention`, not configuration values that could purge early or keep content longer.
2. **What is purged.** Purging clears `payload` and `payload_fingerprint` of due receipts in every processing
   state, and nothing else. The receipt row stays, because it is the durable dedupe metadata of the existing
   design: tenant, connection, source system, provider event id, schema version, receipt time and state. The
   outbox payload is only the receipt-id marker, and the ledger and Snapshot hold no raw content, so none of them
   is purged. The Snapshot and every piece of ledger metadata are retained.
3. **After the purge.** Ingress still recognizes a repeated provider event id through the retained receipt row. A
   redelivered message whose receipt was processed is still a ledger no-op. A receipt whose content was purged
   before it was processed is refused with `processing.receipt.content_expired` and dead-lettered, visibly and
   never silently. Duplicate-candidate flagging by fingerprint only reaches content not yet purged. Ledger and
   Snapshot provenance are untouched.
4. **Tenant safety.** One more narrowly scoped `SECURITY DEFINER` function, in owner decision 1's retention
   category: `retention_expired_tenants(p_limit)`. It takes no cutoff: it returns only the ids of tenants holding
   content that the database's own clock shows at least 672 hours old (the 28-day purge age in exact hours),
   oldest first, at most 100, so no caller can move its boundary or make it reveal recent activity.
   It has the fixed `search_path`, EXECUTE for the runtime role only, and an owner-only read policy for Azure's
   non-superuser owner that shows only content already past those 672 hours. Each tenant is purged in its own
   scope and transactions, as a trusted-service tenant, under forced row-level security. There is no
   cross-tenant read or write, and neither existing tenant-less function changes.
5. **Bounded, idempotent and concurrent-safe.** A run discovers a bounded number of tenants and purges each in
   bounded batches (`FOR UPDATE SKIP LOCKED`, oldest first) up to a per-tenant budget. The purge statement takes
   no time from the Worker: the database's clock bounds it at the same 672 hours and measures each cleared
   receipt's age, so a Worker clock running behind cannot delay a purge and one running ahead cannot purge early.
   It is deterministic, retry-safe and idempotent, because purged content never matches
   again. Overlapping runs skip each other's locked rows, and a receipt that processing holds is left to a later
   run.
6. **Worker entry point.** `--purge-raw-content` runs one bounded purge as the runtime role and prints a JSON
   summary of counts, ages, lateness statuses and a duration, then exits with the code item 14 defines. There is
   no HTTP endpoint, no schedule, no Container Apps Job and no Azure activation.
7. **Metrics.** One meter, `WorkContinuity.EventBackbone`, exported through the existing OpenTelemetry and
   Application Insights pipelines: the Worker adds a meter provider on its existing Azure Monitor exporter, and
   the Web adds the meter to its Azure Monitor distro. It has counters and histograms for ingress, the outbox
   relay, transport settlement and re-drive, processing, Snapshot projection and retention, including the age of
   the oldest content a run purged, the oldest due content it left, and `workcontinuity.retention.overdue` by
   level (`overdue`, `limit_exceeded`) and state (`purged`, `remaining`). Tags come only from bounded
   vocabularies: outcomes, canonical reasons, known reason codes, settlement kinds, receipt states and retention
   levels. No tenant, connection, object, event, receipt or actor id, and no value, ever appears in a tag.
   Per-tenant diagnosis stays in the existing structured logs. A measurement is taken only once what it measures
   has committed.
8. **Session decision support.** Processing lag (receipt to decision), processing duration, delivery counts,
   relay lag and tenants per relay pass make tenant-serial head-of-line symptoms observable without per-tenant
   dimensions.
9. **Migrations.** Additive `RawContentRetention`: it relaxes `NOT NULL` on the two content columns and checks
   that they are cleared together, and it adds a partial index over content-bearing receipts, the owner read
   policy and the function. Its Down restores `NOT NULL` without a default or backfill, so it refuses, and rolls
   back whole, while any content was purged, rather than invent content. The follow-up `RawContentPurgeAge`
   replaces the function with `retention_expired_tenants(p_limit)`, which takes no cutoff and applies the 672-hour
   bound by the database's clock, with the same grants, and alters the owner policy to the same bound. It also makes
   the receipt time the database's own: a `now()` default and the trigger `stamp_receipt_time_raw_receipts`, whose
   function is no definer, reads nothing, has the fixed `search_path` and EXECUTE revoked from PUBLIC, stamp every
   insert with `now()` and refuse any change; receipts stored before keep their receipt time. It changes nothing
   else. Its Down removes the trigger, its function and the default, and restores the `RawContentRetention`
   function (caller cutoff, 720-hour bound), its grants and the owner policy exactly; it touches no data, so it
   works after purges. EF reverts one
   migration per transaction, so a rollback past `RawContentRetention` while purged rows exist stops,
   consistently, at `RawContentRetention`.
10. **Hygiene.** Purge logs and metrics carry counts, codes, ages and ids only; the discovery function returns ids
    only.
11. **Tests.** Every required retention and metric test, security tests, mutation and vacuity checks, round trips
    of both migrations, and the full suite locally.
12. **Out of scope and unchanged.** No Gate 2G, no monday or webhook logic, no Work Memory or AI, and no UI. No
    host activation of the relay, the consumer or processing. No schedule, Azure resource, RBAC, identity or Key
    Vault change, and no change to the frozen documents. Build with 0 warnings; the full suite passes locally.
13. **Timing model.** The hourly schedule's assumptions are fixed in `RawContentPurgeSchedule` and must hold at
    activation:
    - cadence `C` = 1 hour between scheduled runs;
    - a scheduled run starts at most `d_s` = 5 minutes late;
    - a run takes at most `d_r` = 15 minutes (the job's timeout enforces it);
    - the database clock, which stamps each receipt's time and measures its age, is corrected by at most
      `d_c` = 1 minute while content ages;
    - each run can clear all due content: at most 100 tenants with due content and 10,000 due receipts per tenant
      per run, the default budgets.

    Ages are the database clock's at both ends. The database stamps content that arrives at `t` with `t` and shows
    it due at `t + 28 d`, or at the latest `t + 28 d + d_c` if its clock was corrected meanwhile, and any run that
    starts then clears it; no application clock plays a part. The next scheduled start after that moment is less
    than `C` away, may start `d_s` late, and clears the content within `d_r`. So, while every scheduled run
    succeeds, raw content is never older than `28 d + C + d_s + d_r + d_c` = **28 days, 1 hour and 21 minutes**
    (`NormalMaximumAge`; derived and tested). The overdue age, 28 days and 6 hours, lies 4 hours 39 minutes above
    it, so normal operation never reports overdue content; the 30-day hard limit lies 46 hours 39 minutes above
    it. This is not an unconditional guarantee: if purge runs stop or fail, because of the scheduler, the
    database or the Worker, content keeps aging, and without a successful run for about the 48-hour lead margin it
    can pass the physical 30-day limit. The system does not claim the limit during such an outage; it reports it
    (item 14 and the alerts of item 15).
14. **Lateness and exit codes.** Every run measures the oldest content it purged and the oldest due content left;
    the retention function names the tenant with the oldest due content, so that content is the oldest anywhere.
    Content at or past the overdue age (28 days 6 hours) is overdue; at or past 30 days the limit is exceeded.
    Either is logged (a warning, or critical at the limit) and counted whenever it is seen, purged or remaining.
    The Worker exits 0 when the run succeeded and no overdue or limit-exceeded content remains, including a run
    that cleared overdue content; 3 only when overdue or limit-exceeded content still remains after the run; 1
    when any step failed, including measuring what remains; and 2 when it is not configured. Every age, and so
    every lateness status, metric, log line and exit code, is measured by the database's clock
    (`now() - received_at_utc`) from a receipt time the database itself stamped; the Worker's clock only times the
    run and stamps the summary, and decides nothing.
15. **Activation preconditions (recorded, not created).** Activating the purge in Azure is a later, separately
    approved step that needs all of: an hourly purge job running `--purge-raw-content` (one replica, 15-minute
    timeout); an alert when no successful purge run (`workcontinuity.retention.runs` with outcome `completed` or
    `partial`) occurs for 3 hours; an overdue raw-content alert (`workcontinuity.retention.overdue`, level
    `overdue`); a critical 30-day limit-exceeded alert (level `limit_exceeded`); a failed purge run alert (outcome
    `failed`, or exit code 1); and an unprocessed-content-purged alert (`workcontinuity.retention.purged`, state
    `unprocessed`). None of them exists yet.

Product-owner decisions at the 2F review (2026-09-29):

1. **No fourth tenant-less function** for exact outbox backlog counts. Relay lag, the backlog flag, tenants per
   relay pass, processing lag and duration, and delivery counts are sufficient for 2F; exact global backlog counts
   may be revisited only if operational evidence shows they are needed.
2. **Purge at 28 days.** The hard retention limit stays 30 days, with a 48-hour lead margin; the future production
   cadence is hourly; no Azure scheduler or job is authorized in this gate.
3. **The database clock stays the authoritative safety clock.** Its purge guard moves from 720 to 672 hours with
   the purge age; provider timestamps never control retention.
4. **A separate follow-up migration** (`RawContentPurgeAge`) carries the 672-hour bound; the applied
   `RawContentRetention` migration is not edited.
5. **Exit code 3 only for overdue or limit-exceeded content that remains** at the end of a run. Lateness is
   reported whenever it is seen, a run that clears overdue content exits 0, and failures keep exit code 1.
6. **The timing model is derived from stated assumptions** and never claims an unconditional 30-day guarantee
   during a prolonged outage.
7. **The activation preconditions** are recorded for the later Azure activation and are not created now.
8. **Database time is the authoritative retention clock** (final 2F review, 2026-09-29). PostgreSQL `now()`
   decides purge eligibility, the 28-day boundary and the 30-day classification, and measures the age of content
   purged and left, so every overdue or limit-exceeded finding is the database's. The Worker's clock only measures
   durations and stamps non-authoritative timestamps, so its skew can neither delay deletion nor make overdue
   content look younger. The retention function takes no cutoff. The change went into the follow-up migration
   `RawContentPurgeAge` before it was committed, with no new function, permission or migration.
9. **The retention age's origin is database time too** (final 2F review, 2026-09-29). PostgreSQL stamps the
   receipt time `received_at_utc` with `now()` at insert: a `now()` default and the trigger
   `stamp_receipt_time_raw_receipts` replace any supplied value and refuse any later change, and the application
   never writes it and reads it back. It stays the one receipt time: the canonical metadata, the ledger and the
   Snapshot's observed time carry it as before, so provenance and the 2D impossibility check use the same database
   time, and provider times are untouched. An ingress clock far ahead can no longer delay a purge, nor one far behind
   cause an early one. The change went into the same follow-up migration, before its commit: no new column, index,
   tenant-less function or permission, and one trigger function that is no definer and reads nothing.

## 2F closure (2026-09-29)

2F is **PASS**. Implementation commit `259be35ff3649e467266103acd9e95a73d65ae54`; CI run 36542058355 green on
all three jobs: 584 tests, 577 passed, 0 failed, 7 skipped by design. The 7 skipped tests are the Service Bus
emulator tests (3 from Gate 1G readiness, 4 from 2C), which CI does not run because no emulator licence is
accepted there (decision 8); every 2F test ran and passed in CI. Azure Dev has none of the Gate 2B, 2C, 2D, 2E
or 2F migrations or schema, and nothing schedules or runs the purge there: its activation, with the hourly job
and the alerts of items 13 to 15, is a separate product-owner approval. The product-owner decisions of the 2F
reviews are the nine recorded above.

## 2G Definition of Done

Authorized 2026-09-29 from `dcf5496` (the Gate 2F closure). 2G adds evidence, not features: it proves the Gate 2
acceptance on the whole path and closes Gate 2 with a report. Every item derives from Development Gates v1, System
Architecture v1 §3 and §8 to §10, the Architecture Invariants, PRD v1 §5, §11, §12, §17 and §22, and the Gate 2
owner decisions.

1. **Whole-path acceptance suite.** New integration tests drive the real composition end to end on the
   deterministic in-memory broker (decision 8): ingress, the outbox relay, the broker with its sessions,
   duplicate-detection window, delivery counts, abandon, dead-lettering and re-drive, the transport consumer,
   processing, the ledger and the Snapshot, against the local PostgreSQL through the runtime role. Each scenario
   states its expected Snapshot from the synthetic source's own ordering evidence, the ground truth, and covers:
   - duplicates at every hop: a source repeat, a relay republish, a broker redelivery and a redelivery after a
     settled outcome;
   - delayed and out-of-order delivery of revisions and source times across objects and fields, and events
     without ordering evidence;
   - retries: a transient failure retried to success, and a failure past the retry limit dead-lettered and
     re-driven after the fix;
   - invalid and untrusted input: deliveries ingress refuses (unknown or disabled connection, source mismatch,
     failed verification, malformed input) and events processing quarantines (bad schema, unknown schema
     version, impossible ordering, authority conflict);
   - accepted events projected with their provenance;
   - a seeded run that reorders, duplicates and delays one ground truth under several fixed seeds.

   It passes only if the final Snapshot equals the ground truth whatever the order, duplication or redelivery;
   every fact's provenance matches its ledger entry and ordering state; nothing invalid or untrusted reaches the
   Snapshot; and **no event is silently lost**: every delivery ingress acknowledged ends in exactly one durable,
   observable state, a ledger entry with its outcome or a dead-letter entry with a stable reason code that
   re-drive can return, and every delivery it refused was reported to the source and stored nothing.
2. **Crash and recovery matrix.** Every material durable boundary is interrupted, before and after its commit
   where both exist, then redelivered or restarted from the database state alone. Each row must converge on the
   fault-free ledger and Snapshot or fail visibly, unacknowledged to the source or dead-lettered with a reason
   code, and never lose or double an effect: the ingress write and its acknowledgement, a lost acknowledgement
   included; receipt and outbox atomicity; the relay before the send, after the send and before its commit, and
   after a lost commit; broker duplicate delivery and a lost lock; consumer settlement (a transient retry,
   retries exhausted, a permanent failure, cancellation); processing before and after the ledger commit; the
   Snapshot and the ledger in one transaction; a restarted host; and content purged before processing, which is
   refused and dead-lettered, never discarded. The report maps each row to its evidence: the new whole-path tests
   and the 2B to 2F component tests.
3. **Tenant isolation acceptance.** Tenants that share object ids and provider event ids run through the whole
   path together while cross-tenant misuse is attempted at every layer: every tenant-owned table under row-level
   security (read, update, delete, and moving a row to another tenant); ingress tenant resolution from the
   connection only; outbox discovery and the relay (tenant ids only; each tenant published from its own scope
   under its own tenant and session); broker metadata (a forged tenant, a session that disagrees with the tenant,
   a causation that names another tenant's receipt); processing in a foreign scope; the ledger and the Snapshot;
   retention; logs and metrics. Any cross-tenant read, write or projection is a Gate FAIL.
4. **Local emulator acceptance.** The whole path also runs over the local Service Bus emulator with the real
   consumer host and processing: out-of-order events converge, a republished message id is absorbed, and a forged
   message is dead-lettered and changes nothing. Skipped in CI (decision 8).
5. **Evidence that the suite is not vacuous.** With the repository's existing approach, a scratchpad driver that
   changes one guard, runs the named tests, restores the file and verifies its hash, and database guards
   weakened and restored in the local database, deliberately weakening each critical protection makes the
   acceptance, crash or isolation tests fail: idempotency, ordering precedence, the impossibility and authority
   checks, dead-lettering, the relay's mark-after-send, acknowledge-after-commit, the tenant-session binding and
   row-level security.
6. **Contradiction audit.** The final 2A to 2G implementation is checked against PRD v1, System Architecture v1,
   all 83 Architecture Invariants, Development Gates v1 and the Gate 2 owner decisions. Each invariant is recorded
   as satisfied, with its evidence, not applicable to Gate 2, or deferred to a named later Gate. No frozen
   document changes.
7. **Gate 2 report.** `docs/gates/gate-2-report.md` records the acceptance evidence, test counts, the
   crash-recovery matrix, tenant-isolation results, mutation and vacuity evidence, the contradiction audit,
   remaining risks, deferred items and why, the Azure state, and a PASS or FAIL recommendation.
8. **Scope.** Tests and documentation. Production code changes only if the suite finds a real defect: the
   smallest root-cause fix with a regression test, reported. No monday or Gate 3 work, no Azure change, no
   activation of the relay, the consumer, processing or the purge in a host, and no change to the frozen
   documents. Build with 0 warnings; the full suite, both verifier scripts and the local emulator tests pass.
