---
name: event-driven-systems
description: Use when designing, implementing, reviewing or debugging FULLY event flow - canonical events, ingress and durable receipts, Inbox/Outbox, Service Bus publishing or consumption, deduplication, per-object ordering, retries and backoff, dead-letter or quarantine handling, crash recovery, correlation/causation, Snapshot projection, or raw-event retention.
---

# Event-Driven Systems in FULLY

## Overview

Every inbound event ends in exactly one durable, observable state: processed, quarantined with a
canonical reason, or dead-lettered and surfaced. Replaying anything never changes the outcome.
Authority stays with architecture §3, §9 and §10, PRD §5, §11 and §12, and the approved decisions in
`docs/gates/gate-2-scope.md`; this skill only makes them operational. A design that needs to deviate
follows the stop-and-report rule in CLAUDE.md.

## Build on what exists

| Need | Use |
|---|---|
| A canonical event | `CanonicalEvent.Create`; map a failure with `CanonicalEventErrors.QuarantineReasonFor` |
| Authority | `SourceOfTruthResolver` only; never a second rule set |
| Tenant-scoped data | `ITenantScopedDatabase`; row-level security is forced |
| Receipt plus outbox marker | One `ExecuteAsync` transaction, committed before acknowledging the source |
| Transport | `event-processing` (sessions), `ai-jobs`, `action-jobs`; the Service Bus SDK lives only in App.Infrastructure |
| Work that starts without a tenant | Only the approved narrow database functions (decision 1) |

## Checklist for every change

1. **Lifecycle step.** Name the architecture §3 steps touched. Acknowledge only after the durable commit.
2. **Tenant.** Resolved server-side before persistence, carried on every message, re-established per
   message; never two tenants in one transaction. A tenant mismatch finds nothing under row-level
   security and is quarantined or dead-lettered, not retried forever.
3. **Idempotency key per hop.** Receipt: tenant, connection and source event id. Broker: message id is
   the outbox id. Consumer: canonical event id or a processed marker. Redelivery adds no effect; broker
   duplicate detection is defence in depth only.
4. **Ordering.** Per object and field: provider revision, then source update time, then occurrence time.
   Receipt or processing time never decides. Ambiguous: keep the current value and record that
   reconciliation is needed. Impossible: quarantine. Session id is tenant plus object key.
5. **Failure routing.** Transient: bounded exponential backoff with jitter. Invalid or unauthoritative:
   quarantine with its canonical reason, no retry. Exhausted: dead-letter, counted, visible and
   re-drivable. Never catch-and-log; never complete a message before the state it represents is committed.
6. **Crash recovery.** For each step, state what happens if the process dies just before and just after
   its commit, and show that the relay, sweeper or redelivery converges.
7. **Correlation and causation.** Keep the ingress correlation id end to end; causation is the receipt or
   event that caused this one; propagate the trace context.
8. **Hygiene.** Logs, telemetry and errors carry ids, reason codes and counts only, never payloads, field
   values or secrets.
9. **Retention.** Raw content expires by 30 days and the ledger keeps metadata only. If a schema cannot
   separate the two, stop (decision 5).

## Tests each change needs

- Duplicate: the same source event id; the same broker message twice; a fingerprint-only candidate.
- Delayed and out of order: an older change arriving after a newer one never overwrites it; equal-rank
  ambiguity is flagged, not guessed.
- A crash between commit and acknowledgement, followed by redelivery.
- A poison message ends quarantined or dead-lettered with a reason, never silently.
- Tenant A's message can neither read nor change tenant B's state.
- CI runs the pipeline on the in-memory transport; sessions, lock loss and dead-lettering are proven
  against the local emulator (decision 8).

## Common mistakes

| Mistake | Instead |
|---|---|
| Ordering by receipt or processing time | Revision, then source update time, then occurrence time |
| Relying on broker duplicate detection | Application-level idempotency at every hop |
| Retrying invalid data | Quarantine with the canonical reason |
| Scanning tables across tenants to find work | The approved claim function |
| Logging the payload while debugging | Log ids and reason codes; reproduce locally |
