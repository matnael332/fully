# Gate 2 Report — Event Backbone

## Status

**Gate 2 (Event Backbone): PASS — closed by the product owner on 2026-09-29.** Sub-gates 2A to 2G are each PASS,
with their commits and green CI runs (see *Gate 2 by sub-gate*). 2G is commit `59f74e1`, whose CI run 36552321567 was
green on all three jobs: 613 tests, 604 passed, 0 failed, 9 Service Bus emulator tests skipped by design; locally
613/613 passed, the emulator tests included. The acceptance criterion holds on the whole path, with the evidence
below.

What the Gate proves (Development Gates v1): **duplicate, delayed and out-of-order events do not corrupt the
Snapshot, and no event is silently lost.** The 2G suite runs the real composition end to end — ingress, the outbox
relay, the broker, the transport consumer, processing, the event ledger and the Snapshot — against the local
PostgreSQL through the non-superuser runtime role, on the deterministic in-memory broker that CI uses (owner
decision 8) and on the local Service Bus emulator. Every scenario states the Snapshot that the source's own
ordering evidence implies and requires exactly that, with its provenance, whatever the order, duplication, delay or
redelivery. Every delivery ingress acknowledged must end in exactly one durable, observable state, and every
delivery it refused must have stored nothing. Faults are injected at every durable boundary, cross-tenant misuse is
attempted at every layer, and 16 single-guard mutations of production code and 20 weakenings of row-level
security each make the new tests fail.

## Scope

Gate 2 is the Event Backbone of Development Gates v1: canonical events, durable ingress, Inbox/Outbox, Service Bus
processing, dedupe, ordering, quarantine, source-of-truth projection, Snapshot and retention basics
(`docs/gates/gate-2-scope.md`, *Canonical scope*). 2G adds evidence, not features: five new test files and
documentation. It changes no production code, migration, configuration, workflow, Bicep file, package or Azure
resource.

Out of scope, and untouched: everything Gate 3 names (monday authentication, webhooks, source mapping, API
reconciliation, initial sync), Work Memory and AI (Gate 4), UI (Gate 5), actions and the Agent (Gate 6), SSO and
authorization (Gate 7), the synthetic pilot and its performance targets (Gate 8); activating the relay, the
consumer, processing or the purge in a host; and every Azure change.

## Gate 2 by sub-gate

| Sub-gate | Delivered | Commit and CI |
|---|---|---|
| 2A | Canonical event contract (CloudEvents-compatible, retained metadata separated from expiring content), deterministic validation, the six quarantine reasons, a closed schema registry, and the authority table and resolver for the six PRD §5 data types | `7ba941a`; CI 36403160623 green |
| 2B | In-process durable ingress: the tenant from the connection only, verification by exactly one verifier, receipt and content-free outbox marker in one transaction, acknowledgement only after the commit, duplicates by provider event id | `120183b`; CI 36411854002 green (359 tests: 356 passed, 3 skipped by design) |
| 2C | Transport contract 1 (message id = outbox id, session = tenant), the outbox relay (discovery-only tenant function, per-tenant `FOR UPDATE SKIP LOCKED`, mark after send), the Service Bus publisher, the consumer (validate, restore the tenant, bounded retry, dead-letter) and dead-letter read-back and re-drive | `189f173`; CI 36425641730 green (444: 437 passed, 7 skipped by design) |
| 2D | Processing pipeline and append-only event ledger: idempotency by receipt, duplicate candidates, per-field ordering (revision, then source update time, then occurrence time), ambiguity held for reconciliation, impossibility and authority quarantine | `f97d22d`; CI 36439994562 green (493: 486 passed, 7 skipped) |
| 2E | Field-level Snapshot with provenance and versions, projected in the ledger's own transaction, with a consistency guard | `e165852`; CI 36449846577 green (524: 517 passed, 7 skipped) |
| 2F | Raw-content retention (purge at 28 days by the database clock, 30-day hard limit, receipt time stamped by PostgreSQL), the Worker purge entry point, and the event-backbone metrics | `259be35`; CI 36542058355 green (584: 577 passed, 7 skipped) |
| 2G | Whole-path acceptance, the crash and recovery matrix, tenant-isolation acceptance, emulator acceptance, mutation and vacuity evidence, the contradiction audit and this report | `59f74e1`; CI 36552321567 green (613: 604 passed, 9 skipped by design) |

## What 2G added

All in `tests/App.Integration.Tests/Acceptance/`, in the existing `postgres` database collection:

| File | Tests | Role |
|---|---|---|
| `WholePath.cs` | — | The harness. A source-facing host (ingress and the relay) and a processing host (the consumer with processing as its downstream handler), composed as the hosts compose them and sharing one broker and the database. Older pending outbox work from other tests is relayed aside first, as the emulator tests do, so every count on the broker is the test's own. `GroundTruth` computes the expected Snapshot from the source's own evidence, independently of the ordering policy. The accounting checks that nothing is silently lost. `CommitFaults` fails chosen ingress or processing commits after their writes, and counts what they had written; `SettlementLost` is a consumer that dies before its settlement reaches the broker. |
| `Gate2AcceptanceTests.cs` | 10 | The acceptance criterion: duplicates at every hop, delay and disorder, retries and dead-lettering, invalid and untrusted input, and seeded runs |
| `CrashRecoveryMatrixTests.cs` | 14 | One whole-path test per durable boundary |
| `TenantIsolationAcceptanceTests.cs` | 3 | Tenants that share ids through the whole path; a row-level-security sweep of every tenant-owned table; forged broker metadata and a foreign processing scope |
| `ServiceBusAcceptanceTests.cs` | 2 | The whole path over the local Service Bus emulator (skipped in CI, decision 8) |

Documentation: the 2G Definition of Done in `docs/gates/gate-2-scope.md`, this report, and the status in `INIT.md`
and `docs/canonical/PROJECT-STATUS.md`.

## Acceptance evidence

"No event is silently lost" is checked the same way in every whole-path test: the tenant holds exactly the receipts
ingress acknowledged, each with one outbox marker and none left unpublished; nothing is left in flight on the
broker; and each receipt has exactly one of a ledger entry, with a receipt state that matches its outcome, or a
dead letter with a stable, catalogued reason code while the receipt stays intact, unsettled and re-drivable.
"The Snapshot is not corrupted" means the Snapshot equals the ground truth; every fact is held by the ledger event of
the provider event that stated it, and carries exactly the provenance it was accepted on (ledger entry, ledger field,
ordering state, object and the receipt whose database-stamped time is its observed time); and each object moved
exactly one version per event accepted for it.

| Test | Scenario | What must hold |
|---|---|---|
| Duplicates at every hop | The source repeats its event, then repeats the id with altered content; the relay dies after the send inside the broker's duplicate window and republishes; the consumer commits and dies before settling; the settled message is delivered again | One receipt, one marker, one ledger entry, one Snapshot version; the altered repeat stored nothing; the broker absorbed the republication; processing recognized two duplicates |
| A repeat without a provider event id | The same content twice with no provider id | Both stored (ingress cannot tell them apart); the second is flagged as a duplicate candidate of the first and held for reconciliation; the Snapshot holds the first fact once |
| Delay and disorder | Three waves, the newest changes first: revisions of one object; per-field ordering across two fields of another; source update times only; occurrence times only; conflicting signals (a higher revision with an earlier update time, a later update time for an earlier occurrence); an equal update time with another value; an event without evidence | The Snapshot equals the source's truth; precedence is revision, then source update time, then occurrence time; the tie and the unevidenced event are held for reconciliation and change nothing |
| Retries and dead-lettering | A newer change fails its commit twice; an older change of the same field and another object's change fail every time | The first is retried with bounded backoff and succeeds; the other two are dead-lettered at the attempt limit, visibly and with their identity, without holding up the rest; after the fix both are re-driven: the older change arrives after the newer one and is stale, the other applies |
| Invalid and untrusted input | Six ingress refusals (unknown and disabled connection, suspended tenant, another source, failed verification, malformed) and ten processing quarantines (not the source's schema, unknown source and canonical schema versions, invalid field, source time after receipt, occurrence after update, a field the source is not authoritative for, another connection's object, a payload claiming to be the employee, an accepted revision claimed again) | Refusals are unacknowledged and store nothing anywhere; each quarantine has its canonical reason and code, is completed and never retried; only the valid event reaches the Snapshot; no refused or quarantined content appears in the Snapshot or in any log |
| Seeded runs, 5 seeds | Two tenants with the same object ids and provider event ids; 15 events each (three objects, five revisions, a random subset of three fields); shuffled; about three in ten repeated by the source; the relay late, crashing before or after a send, sometimes past the duplicate window; consumers dying before settling; settled messages delivered again; the first processing commit of about one receipt in seven failing | Per tenant: the Snapshot equals the ground truth, provenance and versions hold, one ledger entry per stated event, outcomes only accepted or stale, no dead letter, nothing lost; no value in any log |

Seeded runs (the disturbances each seed actually produced):

| Seed | Source repeats | Relay crashes after a send (past the window) | Relay crashes before a send | Lost settlements | Settled messages delivered again | Failed processing commits |
|---|---|---|---|---|---|---|
| 1 | 13 | 3 (1) | 0 | 7 | 1 | 4 |
| 7 | 8 | 1 (1) | 5 | 6 | 1 | 8 |
| 42 | 14 | 2 (2) | 1 | 2 | 2 | 3 |
| 2026 | 8 | 2 (0) | 2 | 5 | 3 | 11 |
| 90210 | 10 | 2 (2) | 1 | 6 | 7 | 9 |

Each run carries 30 stated events (15 per tenant) plus its source repeats. Two separate executions produced identical
figures for all five seeds; each run requires at least one source repeat and one fault, so a seed can never pass
while disturbing nothing.

## Crash and recovery matrix

Each row interrupts one durable boundary and then recovers from the database and the broker alone. Every row
converges on the fault-free ledger and Snapshot, or fails visibly; no row loses or doubles an effect.

| Boundary | Fault | Result | 2G whole-path evidence | Component evidence (2B to 2F) |
|---|---|---|---|---|
| Ingress write | Commit fails after the receipt and the marker were written | Not acknowledged; neither row survives (2 rows were written inside the failed transaction); the source's retry is stored once and converges | `Ingress_failing_at_its_commit_…` | `IngressTests.A_failure_inside_the_write_is_not_acknowledged_…`, `A_payload_only_the_database_refuses_…` |
| Receipt and outbox atomicity | As above | Both or neither | same test | `PersistenceFoundationTests.A_receipt_and_its_outbox_message_commit_together`, `A_failure_after_both_writes_…`, `A_rejected_outbox_insert_also_rolls_back_the_receipt` |
| Ingress acknowledgement | Committed, acknowledgement lost, source retries | Answered as a duplicate of the stored receipt; processed once | `A_lost_ingress_acknowledgement_…` | `IngressTests.A_repeated_delivery_is_acknowledged_as_a_duplicate_…`, `Concurrent_deliveries_of_one_event_store_exactly_one_receipt_and_one_marker` |
| Relay, before the send | Process dies before the broker has the message | Marker stays pending; sent once later; processed once | `A_relay_crash_before_the_send_…` | `OutboxRelayTests.A_crash_before_the_send_…` |
| Relay, send refused | Broker outage | Marker stays pending; published and processed once later | `A_broker_refusing_a_send_…` | `OutboxRelayTests.A_failed_send_leaves_that_message_…`, `The_relay_loop_publishes_backs_off_…` |
| Relay, after the send, inside the window | Process dies before the mark commits | Republished under the same id; the broker absorbs it; processed once | `A_relay_crash_after_the_send_inside_the_duplicate_window_…` | `OutboxRelayTests.A_crash_after_the_send_and_before_the_commit_…`; emulator `Duplicate_detection_on_the_emulator_…` |
| Relay, after the send, outside the window | As above, window passed | Delivered twice; the ledger absorbs the copy; one Snapshot version | `A_relay_crash_after_the_send_outside_the_duplicate_window_…` | `OutboxRelayTests.A_crash_after_the_send_and_before_the_commit_…` |
| Relay commit | The relay's database connection dies between the send and the commit | Commit failure reported; republished under the same id; processed once | `A_relay_commit_lost_after_the_send_…` | `OutboxRelayTests.A_commit_lost_after_the_send_…`, `Concurrent_relays_publish_every_message_exactly_once`, `Only_committed_markers_are_ever_claimed` |
| Broker duplicate | The same message twice before processing | Second is a no-op | `A_duplicate_broker_delivery_before_processing_…` | `EventProcessingTests.An_exact_duplicate_delivery_…`, `Concurrent_deliveries_of_one_receipt_…` |
| Lost lock before the processing commit | The Worker dies mid-processing (cancelled after every decision) | Nothing committed; a restarted consumer processes it once on delivery 2 | `A_consumer_lost_before_the_processing_commit_…` | `EventProcessingTests.A_crash_before_the_commit_…`; `EventTransportConsumerTests.Cancellation_while_handling_…` |
| Lost settlement after the processing commit | Committed, settlement lost | Redelivery is a no-op; no version moves | `A_settlement_lost_after_the_processing_commit_…` | `EventProcessingTests.A_crash_after_the_commit_…`; `SnapshotProjectionTests.A_crash_after_the_commit_and_before_completion_…` |
| Lost settlements up to the broker's delivery limit | Every settlement lost until the broker's own limit | Dead-lettered by the broker (`MaxDeliveryCountExceeded`), visible; the receipt was already processed; re-drive is a no-op | `Settlements_lost_up_to_the_broker_delivery_limit_…` | — |
| Consumer: transient failure | Processing commit fails | Abandoned with bounded backoff, retried to success | retry test in `Gate2AcceptanceTests` | `EventTransportConsumerTests.A_transient_failure_is_retried_…`, `A_handler_that_throws_is_retried_…` |
| Consumer: retries exhausted | Every attempt fails | Dead-lettered with `transport.retries_exhausted` at the attempt limit; re-driven after the fix | retry test | `TransportPipelineTests.Retries_exhausted_reach_the_dead_letter_queue_…`; emulator `The_consumer_host_completes_retries_dead_letters_…` |
| Consumer: permanent failure | Forged tenant, session mismatch, content expired | Dead-lettered at once with its reason, never retried | forged-metadata and purge tests | `EventTransportConsumerTests.A_permanent_failure_is_dead_lettered_at_once_…`, `Malformed_or_unsupported_messages_…`; `TransportPipelineTests.A_poison_message_is_dead_lettered_visible_and_re_driven_…` |
| Snapshot and ledger in one transaction | Processing commit fails after the object and both facts were written | Nothing survives (3 Snapshot rows were written inside the failed transaction); the redelivery projects once at version 1 | `A_processing_commit_failing_after_the_snapshot_was_written_…` | `SnapshotProjectionTests.A_crash_at_the_commit_after_the_snapshot_was_written_…`; `EventBackboneMetricsTests.A_projection_that_fails_is_counted_once_…` |
| Snapshot consistency | Snapshot disagrees with the ordering state | Never overwritten; retried, then dead-lettered | — | `SnapshotProjectionTests.A_snapshot_that_disagrees_with_the_ordering_state_…` |
| Concurrency | Concurrent events of one field or object | Row locks until commit; converge on the newest; gap-free versions | — | `EventProcessingTests.A_field_read_for_ordering_stays_locked_…`, `Concurrent_events_of_one_field_…`; `SnapshotProjectionTests.An_object_being_projected_stays_locked_…`, `Concurrent_events_on_different_fields_…` |
| Restart | Every host disposed with one event settled, one processed but unsettled, one on the broker and one committed in the outbox | New hosts converge from the database and the broker alone; the unsettled one is a duplicate | `A_restart_resumes_from_the_database_and_the_broker_alone_…` | — |
| Content purged before processing | Retention purges an unprocessed receipt | Refused with `processing.receipt.content_expired` and dead-lettered, never guessed or discarded; the receipt stays; re-drive is refused again and stays visible; another tenant with the same ids is untouched | `Content_purged_before_processing_…` | `RawContentRetentionTests.A_receipt_whose_content_expired_before_processing_…`, `A_processed_receipt_redelivered_after_the_purge_…`, `Ingress_still_recognizes_a_repeated_provider_event_…` |
| Purge runs | Failed batch, concurrent runs, rows held by processing | Rolled back whole and retried; every receipt purged exactly once; held rows skipped and purged later | — | `RawContentRetentionTests.A_failed_batch_rolls_back_whole_…`, `Concurrent_runs_purge_every_expired_receipt_exactly_once`, `A_receipt_held_by_processing_or_another_run_…` |
| Combined | Random mixtures of the above under five seeds | Converge on the ground truth | seeded runs | — |

## Tenant isolation

No cross-tenant read, write or projection was found. Every check below ran as the runtime role.

| Layer | Misuse attempted | Result | Evidence |
|---|---|---|---|
| Database, every tenant-owned table (10) | In its own scope each tenant reads, updates and deletes the other's rows, moves its own rows to the other, and writes a copy of its own row labelled with the other tenant — both directions: 20 reads and 80 write attempts | Every read sees 0 rows; every write affects 0 rows or is refused with SQLSTATE 42501, and nothing else is accepted; row counts unchanged; each tenant sees its own rows (not vacuous) | `Every_tenant_owned_table_refuses_…`; RLS vacuity below; `TenantIsolationTests`, `ConnectionPoolIsolationTests` and the per-table security tests of 2B to 2F |
| Ingress | A payload naming another tenant | The tenant comes from the connection only | `Tenants_sharing_object_and_provider_event_ids_…`; `IngressTests.The_tenant_comes_from_the_connection_…` |
| Outbox discovery and relay | — | Every published message carries its receipt's tenant as property and session | same test; `OutboxRelayTests.Each_tenant_is_relayed_only_in_its_own_scope_…`; `OutboxDiscoveryDatabaseSecurityTests` |
| Broker metadata | B's tenant naming A's processed and unprocessed receipts; A's tenant in B's session; A's tenant naming B's receipt | Dead-lettered at once (`processing.receipt.not_visible`, `transport.envelope.session_mismatch`); neither tenant changed; the genuine message then processed in A alone | `Forged_broker_metadata_and_a_foreign_processing_scope_…`; emulator `A_forged_message_on_the_emulator_…` |
| Processing scope | A's genuine message processed in B's scope | Refused with `processing.tenant.mismatch` before anything is read | same test; `EventProcessingTests.A_message_processed_in_another_tenants_scope_is_refused` |
| Ledger and Snapshot | Same object ids and provider event ids in both tenants, interleaved in different orders | Each converges on its own truth; each sees only its own ledger, fields, ordering state and Snapshot | `Tenants_sharing_object_and_provider_event_ids_…`; `EventProcessingTests.Tenants_keep_separate_ledgers_…`; `SnapshotProjectionTests.Tenants_keep_separate_facts_…` |
| Retention | One tenant's receipt purged, another's with the same ids not due | Only the due tenant's content purged | `Content_purged_before_processing_…`; `RawContentRetentionTests.Each_tenant_is_purged_in_its_own_scope_…`; `RetentionDatabaseSecurityTests.The_purge_statement_clears_only_its_own_tenants_expired_content_…` |
| Logs and metrics | Two tenants through every step | No log record names both tenants or carries content; metric tags are bounded and carry no id or value | `Tenants_sharing_object_and_provider_event_ids_…`; `EventBackboneMetricsTests` |

## Local Service Bus emulator

The two new tests ran against the local emulator (`./eng/dev.ps1 up`), with the real consumer host restricted to the
tests' own sessions and processing as its downstream handler:

| Test | Scenario | Result |
|---|---|---|
| `Out_of_order_events_converge_over_the_emulator_and_a_republished_message_id_is_absorbed` | The newest revision first, then two older ones; the newest republished under its message id; then a follower on another object | The Snapshot holds the newest revision and the follower; the older two are stale; one ledger entry per receipt; processing recorded no duplicate, so the emulator absorbed the republication; no dead letter |
| `A_forged_message_on_the_emulator_is_dead_lettered_and_changes_no_tenant` | Tenant B's identity naming tenant A's processed receipt, published ahead of B's own event with the same ids | Dead-lettered with `processing.receipt.not_visible`; A and B each hold exactly their own event and ledger entry |

With the seven earlier emulator tests (three for readiness, four for transport), all nine passed locally in the full
run. CI skips them by design, because no emulator licence is accepted there (decision 8).

## Evidence that the suite is not vacuous

Each mutation weakens exactly one guard in production code, is applied alone by a scratchpad driver that first
checks the file's baseline hash and that its anchor occurs exactly once, rebuilds the solution, runs the 27
in-memory 2G tests, then restores the file and verifies its hash again.

| # | Guard weakened (one line of production code) | File | 2G tests that failed (of 27) |
|---|---|---|---|
| M1 | Idempotency: the ledger no longer decides that a receipt was already processed | `EventProcessor.cs` | 11: all five seeded runs, duplicates at every hop, the relay crash outside the window, the lost settlement after the commit, the broker delivery limit, the restart, the duplicate broker delivery |
| M2 | Ordering: a lower revision counts as newer | `OrderingPolicy.cs` | 10: all five seeded runs, disorder, retries, both isolation tests that process events, the restart |
| M3 | Ordering: an older change advances (last write wins by arrival) | `OrderingPolicy.cs` | 8: all five seeded runs, disorder, retries, tenants sharing ids |
| M4 | Precedence: the revision is ignored, so a source time decides | `OrderingPolicy.cs` | 2: disorder (conflicting signals), invalid input (an accepted revision claimed again) |
| M5 | Precedence: the source update time is ignored, so the occurrence time decides | `OrderingPolicy.cs` | 1: disorder (conflicting signals) |
| M6 | Impossibility: a source time after the receipt is accepted | `OrderingPolicy.cs` | 1: invalid input |
| M7 | Impossibility: an occurrence after the reported update is accepted | `OrderingPolicy.cs` | 1: invalid input |
| M8 | Authority: an event is quarantined only when no change is authoritative | `EventProcessor.cs` | 1: invalid input |
| M9 | Dead-lettering: a permanently refused message is completed and dropped | `EventProcessingRegistration.cs` | 2: forged metadata, content purged before processing |
| M10 | Dead-lettering: a message that fails envelope validation is completed and dropped | `EventTransportConsumer.cs` | 1: forged metadata |
| M11 | Dead-lettering: a message out of retries is completed and dropped | `TransportRetryPolicy.cs` | 1: retries |
| M12 | Relay: a send the broker refused is marked published | `OutboxRelay.cs` | 1: broker refusing a send |
| M13 | Ingress: a write that did not commit is acknowledged | `EventIngressService.cs` | 1: ingress failing at its commit |
| M14 | Tenant-session binding: a message is accepted in any session | `TransportEnvelope.cs` | 1: forged metadata |
| M15 | Tenant scope: processing no longer compares the scope's tenant with the message's | `EventProcessor.cs` | 1: forged metadata |
| M16 | Retention: content that expired before processing is no longer refused | `EventProcessor.cs` | 1: content purged before processing |

All 16 were caught; after each, the file was restored and its SHA-256 matched the baseline, and all eight files
matched at the end. M4 and M5 were caught only after the disorder test gained conflicting ordering signals (see
*Problems found*).

Row-level security, weakened in the local database one table at a time (`DISABLE ROW LEVEL SECURITY`, restored by an
exit trap, flags and policies compared before and after):

| Table | Row-level security disabled: rows of the other tenant the sweep then saw | Tenant policy's `WITH CHECK` removed: how the foreign-labelled copy was then refused |
|---|---|---|
| `tenants` | 1 | SQLSTATE 23505, a key constraint |
| `users` | 1 | 23505 |
| `raw_receipts` | 5 | 23505 |
| `outbox_messages` | 5 | 23505 |
| `connections` | 1 | 23505 |
| `event_ledger` | 5 | 23505 |
| `event_ledger_fields` | 6 | 23505 |
| `field_ordering_states` | 3 | 23503, a foreign key |
| `snapshot_objects` | 2 | 23503 |
| `snapshot_fields` | 3 | 23503 |

Each run failed the sweep on exactly the weakened table, with messages such as "raw_receipts shows 5 rows of another
tenant." and "raw_receipts, write a row labelled with another tenant: 0 rows, SQLSTATE 23505."

Each weakening was applied to one table at a time and restored by the script (an exit trap covers interruption);
the flags, the policies and their full expressions of all ten tables were identical before and after each run.
Without `WITH CHECK`, a row labelled with the other tenant is no longer refused by row-level security, only by a key
constraint, which the sweep does not accept as isolation. Moving a row to the other tenant stays refused even then,
because PostgreSQL applies the policy's `USING` expression to the updated row whenever the statement reads the
table.

## Test counts

| Project | Gate 2F (CI run 36542058355) | Gate 2G (CI run 36552321567; the same counts locally) |
|---|---|---|
| Unit | 251 | 251 |
| Security | 101 | 101 |
| Architecture | 14 | 14 |
| Integration | 218 | 247 (+29: acceptance 10, crash matrix 14, tenant isolation 3, emulator 2) |
| **Total** | **584** (577 passed, 7 emulator tests skipped by design) | **613**: in CI 604 passed, 0 failed, 9 emulator tests skipped by design; locally 613 passed |

CI run 36552321567 reported exactly that: the 9 skipped tests are the Service Bus emulator tests (3 from Gate 1G, 4
from 2C, 2 from 2G), and all 27 in-memory 2G tests ran and passed; the whole integration assembly took 19 s in CI.

## Verification (executed 2026-09-29)

| Check | Result |
|---|---|
| Starting state | HEAD `dcf5496` == `origin/main`, working tree clean |
| Baseline before 2G | Build 0 warnings, 0 errors; full suite 584 passed locally, including the 7 emulator tests |
| `dotnet build WorkContinuityEngine.sln --no-incremental` (warnings as errors) | PASS — 0 warnings, 0 errors |
| Full local suite (`dotnet test --solution`) | PASS — 613 tests: 613 passed, 0 failed, 0 skipped (Unit 251, Security 101, Architecture 14, Integration 247), with the local emulator |
| CI run 36552321567 on `59f74e1` | SUCCESS on all three jobs: build with 0 warnings and 0 errors, both verifier scripts PASS, 613 tests (604 passed, 0 failed, 9 emulator tests skipped by design), Bicep build and lint; gitleaks scanned 32 commits and the working tree, no leaks; Web and Worker Release images built and passed their smoke tests |
| The 29 2G tests | PASS — 29 passed in 55 s: acceptance 10, crash matrix 14, tenant isolation 3, emulator 2 |
| Seeded runs | Reproducible: identical disturbance figures in two executions |
| `eng/verify-architecture.py`, `eng/verify-local-env.py` | PASS, exit 0 |
| Mutation checks | 16 of 16 caught; every mutated file restored with its baseline SHA-256 |
| Database vacuity | 20 of 20 caught (row-level security disabled, then the write check removed, on each of the 10 tenant-owned tables); flags and policies identical before and after |
| Production code | Unchanged: no file under `src/` differs from `dcf5496` |
| Frozen documents | PRD, System Architecture, Architecture Invariants and Development Gates unchanged |
| Azure | Untouched: no Azure command of any kind was run |
| Gate 3 | NEXT / NOT STARTED |

## Contradiction audit

No contradiction was found with PRD v1, System Architecture v1, the Architecture Invariants v1, Development
Gates v1 or the Gate 2 owner decisions, and no frozen document changed.

**PRD v1.** §5: authority by data type in code, never by AI, no "latest source wins" (2A resolver; 2G quarantines).
§11: the canonical event carries every core field; the ledger keeps identity and provenance (2A, 2D). §12: raw work
events and payloads roll off within 30 days, the Snapshot is kept (2F). §17: tenant as a security boundary, tenant
context server-side, no cross-tenant access, no secrets or raw content in logs (2G isolation and hygiene). §21 and
§22: the duplicate and delayed/out-of-order scenarios and tenant isolation are proven for the backbone; the
synthetic pilot itself is Gate 8.

**System Architecture v1.** §3: the lifecycle runs ingress, verification, tenant resolution, durable receipt and
acknowledgement, then normalization, validation, dedupe, authority, ordering, canonical event and projection; the
acknowledgement never waits for downstream work; ordering is per object and field with the frozen precedence and no
receipt-order last-write-wins; ambiguity is not overwritten and is recorded for reconciliation; provider event ids
dedupe per tenant and connection and fingerprints only flag candidates; consumers are idempotent; Snapshot fields
carry provenance; quarantine reasons never reach the Snapshot. Identity resolution of actors is limited to marking
them unresolved (decision 7); work-memory update and downstream brief work are Gate 4. §8: tenant isolation for
relational data, queues and logs. §9: event-driven modular monolith; PostgreSQL is operational truth and Service
Bus is transport; dead-letter handling; receipt and outbox marker committed before the acknowledgement. §10:
correlation from ingress through the broker to the Snapshot; receipt, relay, queue, dead-letter and projection
metrics; no secrets or payloads in logs. §11: drop, delay, duplicate, reorder and corrupt fault injection and
cross-tenant and tenant-id-manipulation probes are exercised for the backbone. §14: 2G changed no behavior,
authority path, retention, isolation or source of truth.

**Development Gates v1.** The Gate 2 acceptance criterion is met with automated tests, edge and failure cases, this
contradiction check and this report; PASS and product-owner approval remain the owner's steps.

**Owner decisions.** The nine Gate 2 decisions hold: the three tenant-less functions only, returning ids (1);
in-process ingress with acknowledge-after-commit (2); the synthetic source in tests only (3); the field-level
Snapshot with provenance and code-defined authority (4); retention separated from ledger metadata (5); refusals
before tenant resolution store nothing (6); unresolved actors preserved (7); the in-memory broker in CI and the
emulator locally (8); the purge entry point without scheduling (9). So do the sub-gate decisions: session = tenant
with the database as the source of ordering and idempotency; `outbox_pending_tenants` discovery-only; no runtime
activation; the 5-minute clock-skew tolerance; whole-event quarantine on any authority conflict; projection in the
ledger's transaction; the canonical value rule and the 1,024-character limit; purge at 28 days by the database
clock, with the receipt time stamped by PostgreSQL.

**Architecture Invariants v1** — each of the 83:

| # | Invariant | Gate 2 status | Evidence or owning Gate |
|---|---|---|---|
| 1 | Every event belongs to a tenant before meaningful processing | Satisfied | Ingress resolves the tenant from the connection before any write (2B); every Gate 2 table is tenant-owned under forced RLS; the envelope carries the tenant and the consumer restores it per message (2C); processing refuses a scope whose tenant differs (2D). 2G: isolation tests; mutants M14, M15 |
| 2 | Every fact shown to a user is traceable to a source | Satisfied for Snapshot facts | Each fact names its ledger event, receipt, source, connection, provider event id, revision and times (2E); 2G `AssertProvenanceAsync` on every whole-path fact, also after purge (2F). Display: Gates 4–5 |
| 3 | AI never executes an action directly | Not applicable | No AI and no actions in Gate 2 (Gates 4, 6) |
| 4 | Every action passes through the Policy Engine | Not applicable | Gate 6 |
| 5 | Source of Truth is defined by code/configuration, not AI | Satisfied | `AuthorityTable` and `SourceOfTruthResolver` in code (2A); payload claims ignored; 2G quarantine cases; mutant M8 |
| 6 | Mobile and desktop consume the same cloud state | Not applicable | Gate 5; the Snapshot is one cloud state |
| 7 | Work Memory is updated continuously | Not applicable | Gate 4 |
| 8 | Local Agent is a limited sensor/executor | Not applicable | Gate 6 |
| 9 | Intelligence Layer failure does not disable the Fact Layer | Not applicable | Gate 4; the Gate 2 fact path has no AI dependency |
| 10 | No component failure creates an unauthorized external action | Not applicable | Gate 2 has no outbound capability (Gate 6) |
| 11 | Fact Events and AI Insights use separate stores/paths | Satisfied for the fact side | No AI, inference or intent source kind; the database refuses them in `snapshot_fields` (2E). AI side: Gate 4 |
| 12 | Receipt time alone never determines business ordering | Satisfied | Revision, then source update time, then occurrence time; no evidence is held for reconciliation; receipt time only bounds impossibility (2D). 2G out-of-order, precedence and seeded tests; mutants M2–M5 |
| 13 | Unknown/invalid/untrusted events do not reach Snapshot | Satisfied | Ingress refusals (2B); quarantine of bad schema, unknown version, impossible ordering, authority conflict (2D); forged messages dead-lettered (2C). 2G invalid-input and forged tests; mutants M6–M8, M10, M14 |
| 14 | Every critical Snapshot field stores provenance | Satisfied | `snapshot_fields` provenance columns and tenant-consistent keys (2E); 2G provenance checks |
| 15 | Outbound intent is not fact until confirmed | Satisfied structurally | Only accepted events from authoritative sources are projected; no intent source kind (2E). Actions: Gate 6 |
| 16 | Mutation retries are idempotent | Satisfied for Gate 2 writes | Receipt write, relay publish and mark, processing and projection are idempotent under retry and redelivery (2B–2E). 2G duplicates, crash matrix and seeded runs; mutant M1. Outbound SaaS mutations: Gate 6 |
| 17 | Webhooks provide speed; reconciliation provides correctness | Deferred to Gate 3 | Gate 2 records `ReconciliationRequired` with its reason; repair by API reconciliation is Gate 3 |
| 18 | No blind Last-Write-Wins | Satisfied | Per-field ordering; ambiguous never overwrites (2D). 2G tests; mutant M3 |
| 19 | User identity mapping is never guessed by AI | Satisfied for Gate 2 | Unresolved actors are preserved and never guessed (decision 7). Identity mapping: Gates 3 and 7 |
| 20 | Full personal event history is not retained indefinitely | Satisfied in code; activation deferred | Raw content purged at 28 days, 30-day limit, database clock (2F); the ledger keeps metadata only (decision 5). Scheduling in Azure: separate approval (decision 9) |
| 21 | AI is never a Source of Truth | Satisfied | No AI source kind; database check constraint (2A, 2E) |
| 22 | A fact without valid evidence is not presented as confirmed | Satisfied for the Snapshot | Unevidenced or ambiguous events are never projected (2D). Presentation: Gates 4–5 |
| 23 | Vector Memory is retrieval-only | Not applicable | Gate 4 |
| 24 | Retrieval scoped by tenant/permission before semantic search | Not applicable | Gate 4 |
| 25 | External content is data, not instruction | Satisfied | Payload claims about source, authority or tenant change nothing (2D; 2G payload naming another tenant) |
| 26 | Model confidence alone cannot drive critical decisions | Not applicable | Gate 4 |
| 27 | Significant AI output passes schema and policy validation | Not applicable | Gate 4 |
| 28 | User clarification may convert an Unknown into a confirmed fact | Not applicable | Gate 5 |
| 29 | AI failure triggers Safe Mode | Not applicable | Gate 4 |
| 30 | Hard Reject not based only on AI inference | Not applicable | Gate 6 |
| 31 | AI never communicates directly with the Local Agent | Not applicable | Gate 6 |
| 32 | Agent accepts only typed/allowlisted actions | Not applicable | Gate 6 |
| 33 | No arbitrary remote-code execution | Not applicable | Gate 6; Gate 2 adds none |
| 34 | Critical policies enforced in cloud and locally | Not applicable | Gate 6 |
| 35 | Approve All approves only a closed action set | Not applicable | Gate 6 |
| 36 | Material change invalidates approval | Not applicable | Gate 6 |
| 37 | SaaS mutations use the official connector/API | Not applicable | Gates 3 and 6 |
| 38 | Local Agent is not a business-authority source | Satisfied structurally | The resolver makes a Local Agent source authoritative for PC activity only; task status and deadlines need the object's home connector (2A). Agent: Gate 6 |
| 39 | Unsafe close of unsaved work is not automatic | Not applicable | Gate 6 |
| 40 | A sent command is not success until verified | Not applicable | Gate 6 |
| 41 | Offline Agent receives no new authority | Not applicable | Gate 6 |
| 42 | Execution Layer has an independent kill switch | Not applicable | Gates 6–7 |
| 43 | Commands only to Agents declaring the capability | Not applicable | Gate 6 |
| 44 | Tenant is a security boundary across the product | Satisfied for Gate 2 | 2G tenant-isolation acceptance at every layer; RLS vacuity |
| 45 | Tenant context is resolved server-side | Satisfied | From the connection at ingress and from the validated envelope in the consumer; never from a payload (2B, 2C; 2G) |
| 46 | Authentication is never sufficient without authorization | Satisfied for ingress | A verified source also needs an active connection and an active tenant (2B). User authorization: Gate 7 |
| 47 | Email is not a security identifier | Satisfied | No email identifier anywhere (1C, unchanged) |
| 48 | Every resource access checked for tenant and resource permission | Tenant: satisfied; resource permission deferred | RLS on every tenant-owned table (2G sweep). Resource permission: Gate 7 |
| 49 | Manager visibility aggregate by default | Not applicable | Gates 5 and 7 |
| 50 | Support has no persistent cross-tenant raw-data access | Satisfied at storage | The three tenant-less functions return ids only; owner read policies are narrow (decision 1). Support access: Gate 7 |
| 51 | Support elevation scoped, time-bound, audited | Not applicable | Gate 7 |
| 52 | Tenant isolation across DB, cache, queues, vector, logs, AI | Satisfied for DB, queues and logs | RLS; session and envelope tenant per message; logs carry one tenant's ids and no content (2G). Cache, vector and AI context: not built (Gates 4, 7) |
| 53 | Secrets not in code, config, DB fields or logs | Satisfied | gitleaks in CI; no secret column; proofs and payloads never logged. The synthetic HMAC key and the emulator's published development key are test constants, not credentials |
| 54 | SaaS and device credentials are separate trust domains | Not applicable | Gates 3 and 6 |
| 55 | Raw payload and security audit retention are separate | Satisfied | Purge touches raw receipt content only; logs and metrics carry no content (2F) |
| 56 | Production/Pilot and Demo/Synthetic do not share a trust boundary | Satisfied for Gate 2 | The synthetic source exists only in tests (decision 3) |
| 57 | No unnecessary surveillance capability in the Agent | Not applicable | Gate 6 |
| 58 | Permission revocation enforced at access time | Partly (connections); rest deferred | A disabled connection is refused at ingress (2B, 2G). Permissions: Gate 7 |
| 59 | Any cross-tenant leakage is a No-Go, including in synthetic testing | Satisfied | No cross-tenant read, write or projection found; RLS vacuity proves the sweep would see one |
| 60 | MVP is a Modular Monolith | Satisfied | No new service or deployable |
| 61 | PostgreSQL is the primary operational store | Satisfied | Ordering, idempotency, ledger and Snapshot live in PostgreSQL |
| 62 | Vector retrieval uses pgvector | Not applicable | Gate 4 (extension enabled in 1D) |
| 63 | Service Bus is transport, not Source of Truth | Satisfied | Convergence despite broker duplicates, redelivery and dead-letter re-drive (2G) |
| 64 | Application idempotency mandatory even with broker dedupe | Satisfied | Duplicates outside the broker window are absorbed by the ledger (2G); mutant M1 |
| 65 | No Kubernetes/AKS in MVP | Satisfied | Unchanged |
| 66 | Local Agent does not require SYSTEM privileges | Not applicable | Gate 6 |
| 67 | Mobile MVP is a PWA | Not applicable | Gate 5 |
| 68 | Dev, Synthetic and Human Pilot use separate boundaries | Satisfied | Gate 2 ran locally only; no Azure change |
| 69 | Infrastructure is defined as code | Satisfied | Unchanged since Gate 1 |
| 70 | Production Agent packages are signed | Not applicable | Gate 9 |
| 71 | Deployment authentication is federated | Satisfied | Unchanged since Gate 1F |
| 72 | Module boundaries remain enforceable | Satisfied | Architecture tests and `eng/verify-architecture.py` pass |
| 73 | Synthetic Pilot Ground Truth exists outside the system under test | Consistent; owned by Gate 8 | 2G's ground truth is computed from the source's own evidence, independently of the ordering policy |
| 74 | Deterministic simulator and real monday integration tested separately | Consistent; owned by Gates 3 and 8 | Gate 2 uses only the synthetic source |
| 75 | Every simulation run reproducible via seed and version | Consistent; owned by Gate 8 | 2G seeded runs use fixed seeds and report what each disturbed |
| 76 | Critical fact hallucination in Golden Scenarios is a No-Go | Not applicable | Gate 4 |
| 77 | Unknown/abstention preferred to an invented fact | Satisfied for the Snapshot | Undecidable order is never guessed (2D) |
| 78 | Synthetic ROI is not real ROI proof | Not applicable | Gate 8 |
| 79 | Learned auto-execution OFF in Human MVP | Not applicable | Gate 6 |
| 80 | Synthetic Pilot becomes a permanent regression suite | Consistent; owned by Gate 8 | The 2G acceptance suite runs in CI on every push |
| 81 | Real Windows Agent smoke test before Human Pilot | Not applicable | Gate 9 |
| 82 | Real monday Connector end-to-end before Human Pilot | Not applicable | Gates 3 and 9 |
| 83 | Fault Injection is part of the Go Gate | Satisfied for Gate 2 | Crash matrix, seeded fault injection and mutation checks (2G). Synthetic Pilot fault injection: Gate 8 |

## Problems found

**No production defect was found**, so 2G changes no production code. Found and corrected while writing the
evidence, each in the new tests only:

1. One expectation of the disorder test was wrong: an older revision that arrives after a newer one but changes a
   field the newer one did not is the first change of its field and correctly applies. The test's expectation was
   corrected; the product behaved as specified.
2. Planning the mutations showed that no whole-path scenario had conflicting ordering signals, so a precedence
   error, such as a source time outranking the revision, would have gone unnoticed. The disorder test gained a
   higher revision with an earlier update time and a later update time for an earlier occurrence; mutations M4 and
   M5 prove they bite.
3. Four scenarios were added after the first pass to close evidence gaps: a repeat with altered content under the
   same provider event id; a repeat without a provider event id; an older dead-lettered change re-driven after a
   newer one; and lost settlements up to the broker's own delivery limit.

## Remaining risks and deferred items

1. **Nothing runs in Azure yet.** Azure Dev has none of the Gate 2B to 2F migrations or schema, and no host runs the
   relay, the consumer, processing or the purge. Deploying new images, the migration-job run, Worker activation with
   Service Bus send access, and the purge with its hourly job and five alerts are each separate owner approvals
   (2C decision 3, decision 9, 2F items 13 to 15).
2. **Reconciliation is Gate 3.** Ambiguous and unevidenced events are recorded as `ReconciliationRequired` and change
   nothing; nothing repairs them until Gate 3's API reconciliation.
3. **Dead letters need an operator.** Re-drive exists (`IDeadLetterQueue.RedriveAsync`, three re-drives per message)
   but there is no runbook, alert or UI for it yet. A message whose settlement is lost up to the broker's own
   delivery limit (5 in the emulator, 10 in Azure) is dead-lettered although its receipt was processed: visible and
   harmless, and its re-drive is a no-op (new 2G test).
4. **Session = tenant.** One tenant's messages are processed one at a time, so a failing message delays that tenant's
   later messages for up to its five bounded attempts. Accepted at 2C and 2D; the 2F metrics exist to revisit it.
5. **Unprocessed content that reaches the purge age is lost for processing.** It is refused and dead-lettered with
   `processing.receipt.content_expired`, visibly, never guessed. It needs a receipt to wait 28 days unprocessed: a
   processing outage that long, or a dead letter left unattended that long, which item 3's operator work prevents.
6. **The real broker is proven locally only.** CI runs the in-memory broker (decision 8); the Service Bus adapter is
   proven on the local emulator (9 tests, including the 2 new ones), not yet against Azure Service Bus.
7. **Performance is measured, not gated.** The pilot targets belong to Gate 8.

## Azure state

Unchanged. No Azure command, deployment, job start, CD dispatch, RBAC, identity, network or Key Vault change was
made in 2G or by the Gate 2 closure, and the 2G push triggered CI only. Azure Dev remains as recorded at the Gate 2F
closure: none of the Gate 2B to 2F migrations or schema, Web and Worker on their Gate 1G images, each job with
exactly one execution.

## Decision

**Gate 2 (Event Backbone): PASS — closed 2026-09-29.** The acceptance criterion is met on the whole path, every
durable boundary recovers or fails visibly, tenant isolation holds at every layer, the evidence is proven not
vacuous, and no contradiction exists. The product owner approved the 2G change set; it was committed as `59f74e1`
and pushed; its CI run 36552321567 was green (613 tests: 604 passed, 0 failed, 9 emulator tests skipped by design);
and the owner then ordered this closure. The remaining risks and deferred items above stay open. Gate 3 (monday
Connector) is **NEXT / NOT STARTED** and requires explicit authorization.
