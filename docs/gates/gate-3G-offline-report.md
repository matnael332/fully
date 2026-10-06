# Gate 3G Offline Implementation Report — monday harness (offline slice)

This is an offline implementation report. It is not a Gate 3 closure report, it does not close Gate 3G, and nothing in
it is evidence of how monday behaves.

## Status

- **Offline implementation: READY FOR OWNER REVIEW.**
- **Gate 3G: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.**
- **Real acceptance: NOT PROVEN.**
- **Real execution: NOT AUTHORIZED.**
- **Gate 3 closure: NOT AUTHORIZED.**
- **CI for the current changes: NOT RUN.** Everything below was run locally on an uncommitted working tree. The last
  green CI run proves only the checkpoint it tested.
- **Real monday calls: none. Azure actions: none. Standalone database provisioning: none. Commit or push: none.**

The work was done on branch `main` at HEAD `cc1602dafb1db3a4b2ddadd091fe1306f3eda7ae`, which equals the local
`origin/main` reference. Nothing is staged and nothing is committed. No file under `src/` changed, and the six frozen
documents are unchanged.

## Authorization and scope

On 2026-10-05 the product owner authorized, in SAFE AUTO MODE, the start of Gate 3G for a narrowly bounded offline
implementation only: a non-production offline harness, its targeted architecture and security guards and tests, the
necessary solution registration, non-frozen scope, status and report documentation, and local build, test and
targeted mutation verification. The repository's record of that authorization, the owner decisions it contains
(labelled D3G-1 to D3G-8 by the record, not by the owner), the 20-item Definition of Done, the design and the
contradiction audit are in `docs/gates/gate-3-scope.md`, section "3G — Offline harness implementation (authorized
2026-10-05)". That section was written before the code. It was amended after the first canonical audit of 2026-10-05,
when code already existed, and the amended parts are marked as such in the record. The order cannot be shown from
Git, because the whole change set is uncommitted.

Everything the owner excluded stayed excluded. There was no contact with monday of any kind, no real credential, no
Azure or Key Vault action, no tunnel or public listener, no system certificate installation, no host or network
configuration change, no change under `src/`, no change to a plugin, hook, permission or local Claude configuration,
and no stage, commit, push, workflow dispatch or deployment.

## What was implemented

One new non-production project, `tests/App.Harness.Monday`, is an executable that composes existing production
components through their public interfaces. It is registered in the solution and referenced only by
`App.Integration.Tests`. It takes no package reference. It has one framework reference, `Microsoft.AspNetCore.App`,
which is the shared framework of the SDK and is needed for its two loopback listeners. It is marked as not a test
project and not publishable, and its internals are visible only to `App.Integration.Tests`.

### Three boundaries in one process

- **A worker container.** It holds persistence as the runtime role, the transport's relay and consumer, processing
  with the monday normalizer, reconciliation and initial sync with the monday readers, the authorization store, the
  credential source and the existing `MondayConnectionAuthorizer`.
- **A webhook listener.** It maps only the existing monday webhook endpoint, through the existing
  `AddMondayWebhookIntake` and `MapMondayWebhooks`. Nothing of that implementation is duplicated. It binds loopback
  only.
- **An operator listener.** It maps only the operator start, callback and terminal routes. It binds loopback only and
  serves only over TLS, from a certificate that the caller supplies. The two listeners are separate applications with
  separate route tables.

### Trust

- The tenant id and the canonical user id are parsed once, from server-side configuration, in `HarnessOptions`.
- `TrustedFlow` holds the only `SetResolved` call of the harness. It starts a fresh execution flow and a fresh
  dependency scope for every operator action and every cleanup tick, and resolves the configured tenant as a trusted
  service identity. The production `SetResolved` allowlist and its test are unchanged.
- The relay, the consumer and the two listeners start on detached flows that carry no tenant, so the existing ingress
  and transport code keeps its own tenant resolution. The consumer takes only the configured tenant's session, and
  the harness names no dead-letter queue.
- No route, query, header, cookie, body or console argument can supply a tenant or a user. A connection to restore is
  named at the console, never by a request.
- The operator is a fixed identity built from the configured canonical user, with the identity provider key
  `LocalDemo` and a fixed subject. Whether that key is right for a real run is an open owner question (see below).

### Fail-closed seams

- The default secret store answers "unavailable" to every operation, and the default provider transport ends every
  request with a connection error without sending it. Only a test can supply a fake for either.
- Every provider client is built in one place, over a guarded transport that refuses any address other than the
  connector's three fixed monday endpoints before the inner transport is reached.
- The harness constructs no network handler, has no setting that enables live execution, and launches no browser.
- The standalone entry point additionally requires an approved database, and the list of approved databases is empty.
  The executable therefore cannot run standalone in this slice: it stops with a closed refusal before any connection.
- The composition refuses to start without an operator certificate, with a transport that is not the local emulator,
  with an unreachable database, or as a database role that is a superuser, has BYPASSRLS, or owns (or is a member of
  the role that owns) a table of the schema.

### Operator safety

- The start route needs a single-use capability that is minted at the console and is valid for two minutes. Minting a
  new capability replaces the old one and cancels a pending browser binding.
- Every request must be a GET, over TLS, on loopback, for the exact configured Host, and from no foreign origin. A
  start that a site initiated, and a request that is not a navigation, are refused when the browser reports that.
- A successful start sets a host-only, HttpOnly, Secure binding cookie. The callback needs that binding in addition to
  the existing OAuth state checks. A callback without it never reaches the authorizer, so it consumes nothing.
- Start and callback answers are never cached and send no referrer. The callback redirects to a static terminal page
  that refuses any parameter.
- A route that fails answers a closed refusal with the same headers and logs only the type of the exception.

### Hosting

- The PKCE verifier cleanup runs once at start-up, before the harness reports that it started, and then every five
  minutes, through the trusted flow of the configured tenant. The interval is a constant in code and is read from no
  configuration. A cleanup that fails or throws is counted, logged by its type only, and does not end the loop.
- Initial sync, reconciliation, the sweep and SourceScope configuration are single, bounded operator actions. Nothing
  schedules them.

### Evidence and diagnostics

- The current-work reader selects only from `active_snapshot_fields` and `active_snapshot_objects`, inside the
  tenant-scoped transaction. The console prints the length of a value and never the value.
- The operational evidence reader runs fixed, parameterless SELECT statements that return counts, states, times and
  fixed outcome codes. No statement selects a payload.
- Diagnostics are in-memory counters keyed by closed enumerations, with booleans, counts and bounded numbers only.
  Unknown categories are counted as "Other".
- The webhook observer counts the form of the `Authorization` header, the algorithm and type that a token's header
  names (from closed lists), and the class of the answer. It counts claim names, the audience relation and a bounded
  lifetime only for a request that the existing endpoint accepted. It never reads the body and keeps no token, claim
  value or header value.

### Logging

- The harness owns its logger factory. Only categories of the product and of the harness reach the sink, framework
  categories are silenced by category and not by level, an exception is logged by its type name only, and a log
  provider added later is refused and counted. No telemetry exporter is registered.
- A record of a product category is shown only when it belongs to the configured tenant. A record that names another
  tenant, a record logged on another tenant's flow, and a product record that no tenant can be told for are withheld
  and only counted. This was added after the tenant security review (see "Reviews").

## Changed-file inventory

Modified tracked files (7):

- `CLAUDE.md`: the Gate 3 status sentence for 3G.
- `INIT.md`: the 3G status and the handoff text.
- `README.md`: the 3G status sentence.
- `docs/canonical/PROJECT-STATUS.md`: the Gate 3 status. This is a status document, not one of the six frozen
  documents.
- `docs/gates/gate-3-scope.md`: the 3G status row, one owner-boundary bullet, and the 3G offline section.
- `WorkContinuityEngine.sln`: the registration of `App.Harness.Monday`.
- `tests/App.Integration.Tests/App.Integration.Tests.csproj`: one project reference to the harness.

New files in the harness project (18):

- `tests/App.Harness.Monday/App.Harness.Monday.csproj`
- `tests/App.Harness.Monday/Program.cs`
- `tests/App.Harness.Monday/HarnessProgram.cs`
- `tests/App.Harness.Monday/Composition/HarnessHost.cs`
- `tests/App.Harness.Monday/Composition/PerCallAuthorizationStore.cs`
- `tests/App.Harness.Monday/Configuration/HarnessOptions.cs`
- `tests/App.Harness.Monday/Diagnostics/HarnessDiagnostics.cs`
- `tests/App.Harness.Monday/Diagnostics/WebhookRequestObserver.cs`
- `tests/App.Harness.Monday/Evidence/EvidenceReaders.cs`
- `tests/App.Harness.Monday/Hosting/HarnessLogging.cs`
- `tests/App.Harness.Monday/Hosting/VerifierCleanupLoop.cs`
- `tests/App.Harness.Monday/Operator/OperatorActions.cs`
- `tests/App.Harness.Monday/Operator/OperatorAuthorizationGate.cs`
- `tests/App.Harness.Monday/Operator/OperatorConsole.cs`
- `tests/App.Harness.Monday/Operator/OperatorListener.cs`
- `tests/App.Harness.Monday/Provider/ProviderTransport.cs`
- `tests/App.Harness.Monday/Trust/DetachedFlow.cs`
- `tests/App.Harness.Monday/Trust/TrustedFlow.cs`

New test files (10):

- `tests/App.Integration.Tests/Harness/HarnessTestHost.cs` (test support, no tests of its own)
- `tests/App.Integration.Tests/Harness/HarnessCompositionTests.cs`
- `tests/App.Integration.Tests/Harness/HarnessFailClosedTests.cs` (it holds two test classes, `HarnessFailClosedTests`
  and `HarnessOptionsTests`)
- `tests/App.Integration.Tests/Harness/HarnessTenantIsolationTests.cs`
- `tests/App.Integration.Tests/Harness/HarnessOperatorSafetyTests.cs`
- `tests/App.Integration.Tests/Harness/HarnessVerifierCleanupTests.cs`
- `tests/App.Integration.Tests/Harness/HarnessLeakPreventionTests.cs`
- `tests/App.Integration.Tests/Harness/HarnessBackboneLoopTests.cs`
- `tests/App.Architecture.Tests/HarnessBoundaryTests.cs`
- `tests/App.Security.Tests/HarnessTenantTrustTests.cs`

New documentation (1):

- `docs/gates/gate-3G-offline-report.md` (this report)

Not changed: every file under `src/`; the six frozen documents; every file under `docs/premium/`; every Dockerfile,
workflow, compose file, infrastructure file and `eng/` script; every existing test and guard; and every plugin, hook,
skill, agent and local Claude setting.

## Tests added

There are 92 new tests: 80 integration tests, 8 architecture guards and 4 security guards.

- `HarnessCompositionTests` (4): the offline composition runs authorization, intake, processing, initial sync and
  reconciliation against fakes; current work is what the active views show, and the state of a disabled scope is not
  current work even though the raw Snapshot still holds it; sync, reconciliation, sweep and scope configuration happen
  only when the operator asks; and the console takes only its closed commands and never echoes what was typed.
- `HarnessFailClosedTests` (9): without secret storage nothing starts, nothing is verified and nothing is sent;
  without a fake provider a code exchange and a read end at the refusing transport, seen by an intercepting
  transport and never by the network; the guarded path refuses every foreign address; the defaults refuse everything;
  the standalone entry point does not run; the composition refuses a missing certificate, a missing local transport,
  the schema owner and an unreachable database; and the role check reads each privilege by itself.
- `HarnessOptionsTests` (34 cases, one of them a 31-row theory; the class is in `HarnessFailClosedTests.cs`): the
  reviewed configuration boundary, including the
  emulator-only transport and the empty list of approved standalone databases.
- `HarnessTenantIsolationTests` (8): an operator action leaves no tenant on its caller's flow and takes none from it;
  two harnesses for two tenants act concurrently and each sees only its own tenant; another tenant's connection is
  refused by every operator action with no provider call and no change; evidence and current work show the configured
  tenant only; nothing a request carries names the tenant or the user; the consumer takes only its own session;
  another tenant's work passing through the harness leaves none of its identifiers in the log, the console or the
  diagnostics; and the log shows a product record only when it is the configured tenant's.
- `HarnessOperatorSafetyTests` (12): the capability, its expiry and replacement; the Host, origin, method, site and
  navigation checks; a client that reports no fetch metadata is still held by the capability and the binding; the
  browser binding and its lifetime; the caching and referrer headers and the parameter-free terminal page; each
  listener serves only its own routes; the checks behind the TLS and loopback binding; reauthorization named only at
  the console; and the closed refusal of a failing route.
- `HarnessVerifierCleanupTests` (6): the start-up run; the five-minute timer driven by a manual time provider; a
  refused deletion stays due and is retried; a failing or throwing cleanup does not end the loop; the cleanup acts for
  the configured tenant only; and stopping the harness stops the timer.
- `HarnessLeakPreventionTests` (5): synthetic sentinels with positive controls over the log, the console, the
  diagnostics, telemetry and every text column of the database; a control showing that the framework's own request
  log would write a callback's query string and that the harness pipeline has no such category; the logger factory;
  the shape of what diagnostics can hold; and the webhook observer. The business-body sentinel is asserted to appear
  only in its authorized raw receipt, separately from the credential and callback-secret sentinels, which appear
  nowhere.
- `HarnessBackboneLoopTests` (2): started from a flow that carries another tenant, the loops and the cleanup still run
  on flows of their own; and over the local Service Bus emulator the harness publishes its own delivery and consumes
  its own session only.
- `HarnessBoundaryTests` (8, static): placement and references; nothing in production and no image, workflow or
  deployment path names the harness; no real provider transport, fallback or enabling setting; no monday write, webhook
  management or document of its own; no administrative database path, and statements that only read approved
  metadata; current work only through the two active views; loopback-only listeners with no exporter, request log or
  framework logging; and only the cleanup scheduled, at five minutes, by a constant.
- `HarnessTenantTrustTests` (4, static): exactly one tenant-assignment site, as a trusted service; the tenant and the
  canonical user parsed only at the configuration boundary; no harness source takes a tenant or a user from a
  request; and every operator action takes its tenant and its user from the trusted chain alone.

The static guards are lexical. They pin source text and counts, with positive controls, and they block a regression
in the source. They do not prove the absence of a semantic bypass such as reflection.

## Verification results

All results are local. The machine is Windows 11 with the local Docker stack (PostgreSQL as the non-superuser runtime
role, and the Service Bus emulator).

### Baseline, on the unchanged checkpoint

The baseline was taken before any change: a clean non-incremental build with 0 warnings and 0 errors, both repository
verifiers PASS, the six frozen documents unchanged, and 1,602 of 1,602 tests passed with none skipped (unit 949,
architecture 35, security 164, integration 454, the emulator tests included).

### Final, on the final tree

The final run was made after the mutation campaign, on the restored tree, starting from a clean non-incremental build.

- **Build:** `dotnet build` of the whole solution, non-incremental: succeeded with 0 warnings and 0 errors.
- **Repository verifiers:** `eng/verify-architecture.py` PASS and `eng/verify-local-env.py` PASS.
- **Unit tests:** 949 total, 949 passed, 0 failed, 0 skipped.
- **Architecture tests:** 43 total, 43 passed, 0 failed, 0 skipped. This is the baseline's 35 plus the 8 new guards.
- **Security tests:** 168 total, 168 passed, 0 failed, 0 skipped. This is the baseline's 164 plus the 4 new guards.
- **Integration tests:** 534 total, 534 passed, 0 failed, 0 skipped, run in five namespace batches of 86, 102, 180, 86
  and 80. This is the baseline's 454 plus the 80 new harness tests. The Service Bus emulator tests ran and passed.
- **Full local suite:** 1,694 total, 1,694 passed, 0 failed, 0 skipped.
- **Frozen documents:** the SHA-256 of each of the six frozen documents equals its recorded value and its blob at HEAD: 6 of 6
  unchanged.
- **`git diff --check`:** clean, with no whitespace error.
- **Production tree and index:** no file under `src/` is modified or added, and nothing is staged.

Checks that were not run or are blocked:

- **CI: NOT RUN.** The change set is uncommitted.
- **Standalone smoke run of the harness executable: NOT PERFORMED.** No standalone database is approved, and the
  executable refuses to start.
- **Fresh-database evidence: not required by this slice.** The change set contains no migration and no schema,
  policy or grant change.
- **Anything against real monday or Azure: NOT RUN and NOT AUTHORIZED.**

## Mutation and vacuity evidence

Targeted mutation and vacuity evidence was produced for the new guards of the harness only. Each mutant changes one or
two places in a harness file under `tests/App.Harness.Monday`. No file under `src/` was mutated, no test was mutated,
and no historical campaign was reopened.

**Result: 97 of 97 mutants CAUGHT. None survived, none was invalid, and none was left broken or not run.**

How the campaign was run:

- Before each mutant, every anchor was verified to occur exactly once, and nothing was written otherwise.
- A control run on the unmutated tree came first: every detecting test passed there (36 integration, 7 architecture
  and 4 security tests). A mutant counts as CAUGHT only when a named detecting test that passes on the unmutated tree
  fails with an xunit assertion, or with the specific failure the plan declared in advance.
- After each mutant the original bytes were restored and the file's SHA-256 was compared with the recorded baseline
  before the verdict was written. At the end, all 18 harness files matched the baseline, no journal remained, and a
  clean non-incremental build preceded the final test run.
- A mutant marked STATIC below was detected by a source-reading guard only. Such a mutant was built but never
  executed, so a mutant that names a network handler, binds a public address or holds a write document never ran.
- The evidence is not schema-dependent: no mutant changes a migration, a policy or a grant, so the fresh-database rule
  does not apply to it.
- Nine of the 97 verdicts were recorded before the interruption and 88 after it, all against the same baseline hashes.

The mutants, by group:

**Provider egress denial (7 of 7 caught)**

- E01: The default transport answers 200 instead of refusing. CAUGHT.
- E02: The guarded path lets a foreign address through to the transport. CAUGHT.
- E03: The default secret store accepts a write. CAUGHT.
- E04: STATIC: a network handler is named in the harness (never called, never run). CAUGHT.
- E05: STATIC: a client is built off the guarded path (never called, never run). CAUGHT.
- E06: STATIC: a configuration key that would enable live execution. CAUGHT.
- E07: STATIC: the fallback when no transport is given is a platform handler (never run). CAUGHT.

**Tenant assignment and isolation (18 of 18 caught)**

- T01: A trusted run is not detached: it runs on its caller's flow. CAUGHT.
- T02: A detached start that returns a value keeps the caller's execution context. CAUGHT.
- T03: A detached start of a loop keeps the caller's execution context. CAUGHT.
- T04: STATIC: the tenant is assigned under another resolution source. CAUGHT.
- T05: STATIC: a second tenant-assignment call site in the harness. CAUGHT.
- T06: STATIC: a tenant parsed outside the configuration boundary. CAUGHT.
- T07: STATIC: a request handler reads a tenant from the request. CAUGHT.
- T08: An operator action builds its run for a tenant taken from an argument. CAUGHT.
- T09: STATIC: the consumer is not limited to the configured tenant's session (never run: no emulator residue).
  CAUGHT.
- T10: A message of another tenant's session is handed to the consumer. CAUGHT.
- T11: The role decision ignores the superuser attribute. CAUGHT.
- T12: The role decision ignores BYPASSRLS. CAUGHT.
- T13: The role decision ignores table ownership. CAUGHT.
- T14: The catalog read reports no superuser. CAUGHT.
- T15: The catalog read reports no BYPASSRLS. CAUGHT.
- T16: The catalog read reports no table ownership. CAUGHT.
- T17: A role the catalog does not answer about is assumed unprivileged. CAUGHT.
- T18: STATIC: the host starts whatever the role check found (never run as a privileged role). CAUGHT.

**Operator start and browser binding (27 of 27 caught)**

- B01: The start capability is not single use. CAUGHT.
- B02: The start capability never expires. CAUGHT.
- B03: The presented capability is not compared with the minted one. CAUGHT.
- B04: The capability lives twenty minutes instead of two. CAUGHT.
- B05: The browser binding is not compared: any well-formed binding passes. CAUGHT.
- B06: The state is not compared with the pending authorization's. CAUGHT.
- B07: The pending authorization is not single use. CAUGHT.
- B08: The browser binding does not expire. CAUGHT.
- B09: Minting a new capability keeps the earlier pending browser binding. CAUGHT.
- B10: The Host check is removed. CAUGHT.
- B11: The Origin check is removed. CAUGHT.
- B12: A start that a site initiated is accepted. CAUGHT.
- B13: A request that is not a navigation is accepted. CAUGHT.
- B14: Answers may be cached. CAUGHT.
- B15: Answers send a referrer. CAUGHT.
- B16: The terminal page accepts parameters. CAUGHT.
- B17: The callback's redirect carries its query string on. CAUGHT.
- B18: The binding cookie is readable by script. CAUGHT.
- B19: An operator route also exists on the webhook listener. CAUGHT.
- B20: The method check is removed. CAUGHT.
- B21: STATIC: the operator listener is bound without TLS (never run). CAUGHT.
- B22: The start accepts parameters beside the capability. CAUGHT.
- B23: The authorizer is called before the browser binding is matched. CAUGHT.
- B24: A route's failure is left to the server instead of the closed refusal. CAUGHT.
- B25: The transport-security check behind the TLS binding is removed (defence in depth). CAUGHT.
- B26: The loopback check behind the loopback binding is removed (defence in depth). CAUGHT.
- B27: The check of the port a request arrived on is removed (defence in depth). CAUGHT.

**Current-work access and evidence (6 of 6 caught)**

- W01: Current-work fields are read from the raw Snapshot table. CAUGHT.
- W02: Current-work objects are read from the raw Snapshot table. CAUGHT.
- W03: STATIC: the raw Snapshot entity set is used (never called). CAUGHT.
- W04: The console prints the value of current work. CAUGHT.
- W05: An evidence statement selects the payload. CAUGHT.
- W06: STATIC: a statement that changes rows is held (never executed). CAUGHT.

**Logging and diagnostic restrictions (17 of 17 caught)**

- L01: Every category is allowed, the framework's included. CAUGHT.
- L02: An exception's text is rendered into the record. CAUGHT.
- L03: The owner decision is not applied: every allowed record is shown. CAUGHT.
- L04: A record that names another tenant is not treated as that tenant's. CAUGHT.
- L05: A record logged on another tenant's flow is shown. CAUGHT.
- L06: A product record that no tenant can be told for is shown. CAUGHT.
- L07: A tenant the template names as an id or a text is not recognized. CAUGHT.
- L08: A tenant in a form that cannot be compared counts as the configured one. CAUGHT.
- L09: The harness's own categories may name another tenant. CAUGHT.
- L10: A withheld record is not counted. CAUGHT.
- L11: STATIC: the operator listener keeps the framework's logger factory. CAUGHT.
- L12: A diagnostic recorder that takes a text. CAUGHT.
- L13: Claims are counted for tokens the endpoint refused. CAUGHT.
- L14: The operator log includes the request's query string. CAUGHT.
- L15: The console echoes a line it does not recognize. CAUGHT.
- L16: A measured lifetime is kept without its bound. CAUGHT.
- L17: The typed tenant value of a record is not recognized. CAUGHT.

**PKCE hosting (8 of 8 caught)**

- P01: The start-up cleanup does not run. CAUGHT.
- P02: The interval is fifty minutes instead of five. CAUGHT.
- P03: The periodic cleanup is never started. CAUGHT.
- P04: The periodic cleanup stops after its first tick. CAUGHT.
- P05: A cleanup that throws ends the loop. CAUGHT.
- P06: The loop is given something other than the operator action to run. CAUGHT.
- P07: STATIC: the start-up cleanup is not awaited before the harness reports started. CAUGHT.
- P08: An exception's text is logged by the cleanup. CAUGHT.

**Fail-closed configuration (8 of 8 caught)**

- F01: The standalone gate permits every database (the run still ends before any connection: no certificate). CAUGHT.
- F02: A database is approved for a standalone run. CAUGHT.
- F03: A transport that is not on loopback is accepted as the emulator. CAUGHT.
- F04: The redirect address need not be on loopback. CAUGHT.
- F05: The redirect address need not be HTTPS. CAUGHT.
- F06: An Azure Service Bus namespace is accepted. CAUGHT.
- F07: The composition starts with no broker at all. CAUGHT.
- F08: The composition starts without a certificate check. CAUGHT.

**Other static guards (6 of 6 caught)**

- G01: STATIC: a monday write document is held (never sent). CAUGHT.
- G02: STATIC: an administrative database path is named. CAUGHT.
- G03: STATIC: the webhook listener is bound to every address (never run). CAUGHT.
- G04: STATIC: something besides the cleanup is delayed (never called). CAUGHT.
- G05: STATIC: the harness project is publishable. CAUGHT.
- G06: STATIC: the harness's internals are visible to a second assembly. CAUGHT.

## Reviews

Three read-only review agents were used. Each is advisory, none closes or approves a Gate, and each could read the
working tree but could run nothing: the build, test and mutation results it saw were statements supplied to it. A
review is only as complete as what its report says it read.

### Canonical auditor

- **First audit, on an earlier state of the change set: CONFORMS WITH FOLLOW-UP.** It found no contradiction with a
  frozen rule or an owner decision. It asked for four corrections of the record: the status wording "built and
  verified" was premature at that time; the mapping of the D3G labels to the owner's sections was missing; design
  choices added by the record were not marked as such; and the contradiction audit did not cover the fixed operator
  identity or revocation at access time. The last three were applied in `docs/gates/gate-3-scope.md`.
- **Final audit, on the final tree with a draft of this report: CONFORMS WITH FOLLOW-UP.** It again found no
  contradiction. It confirmed the three applied corrections. It asked for three more corrections of wording, all
  applied afterwards: the status sentence in `CLAUDE.md` and `PROJECT-STATUS.md` now says that the verification was
  local, on an uncommitted tree with CI not run, and that the fakes are the provider traffic and the credentials while
  the tests also use the local database and the Service Bus emulator; the scope record and this report now say that
  the 3G section was amended after the first audit, when code already existed; and the inventory now says where
  `HarnessOptionsTests` lives. In this audit the auditor read the documents in full and the harness source only by
  search, and it did not open the test sources.

### Tenant security reviewer

- **First review, on an earlier state: BLOCKED BY TENANT SECURITY.** The harness log policy passed every product
  category, and the product's unchanged all-tenant outbox relay logs the identifiers of each tenant it works for.
  Run on a database that holds other tenants' pending rows, the harness would have shown their identifiers at its log
  sink and console. No test had a foreign-tenant sentinel. The review also listed gaps: intake and relay are
  all-tenant on a shared database; absent fetch-metadata headers were untested; the role check's clauses were not
  shown to matter separately; and no test showed that a disabled scope is hidden from current work.
- **What was changed, in the harness and its tests only.** The log policy now decides whose each record is and
  withholds, counting only, every product record that is not the configured tenant's. Tests were added for another
  tenant's delivery and relay pass through the harness (with eight foreign identifiers searched for in the log, the
  console and the diagnostics, counters showing that the records were produced and withheld, and a positive control),
  for the attribution rules, for a client without fetch metadata, for the role check, and for a disabled scope.
- **Re-review, on the final tree: PASS WITH FOLLOW-UP.** The reviewer found no tenant-isolation breach and judged the
  earlier blocking finding resolved in the code it read, with the new foreign-tenant test non-vacuous. Its follow-ups
  are carried in "Open questions for the owner": the all-tenant intake and relay on a shared database (question 2),
  and the residual part of the role check (question 5). It also noted a limit: the log policy recognizes a tenant by
  a typed tenant value or by a value named `TenantId` or `Tenant`; no product log template names a tenant in any
  other way today, and a future one that did so on the configured tenant's own flow would not be recognized. It asked
  that fresh tenant and row-level-security evidence be supplied again at Gate verification. In the re-review it read
  the logging, host and evidence sources and the tenant-isolation tests in full, and the rest by search.

### Gate verifier

- **Verdict, on the final tree: READY WITH FOLLOW-UP.** The claim it verified is that the offline slice is ready for
  the owner's review. It is not an approval and not a closure, and the verifier never closes a Gate.
- It mapped the 20 items of the Definition of Done to evidence and found no defect and no conflict with a Gate. It
  checked that the totals are consistent: the full suite is the sum of its parts, and the 92 new tests are the
  increase over the baseline.
- Its follow-ups: the test, mutation and review results are statements of the implementing agent, whose xunit reports
  and mutation driver sit outside the repository, so the owner may want them retained; the mutation verdicts were
  recorded in two parts around the interruption; the two open questions of the tenant security re-review stay open; no
  per-class totals of the isolation tests were supplied to it, only the suite totals; the design choices stricter than
  the owner's wording wait for the owner's confirmation; and the owner's verbatim authorization is not stored in the
  repository.
- It read the scope record, the status table and the authorization, and it did not read the harness source or the
  tests line by line.

## Problems found during the work

- **Other tenants' identifiers could reach the harness log.** Found by the tenant security review, and fixed in the
  harness. See "Reviews".
- **The cleanup loop's fault path could not be tested.** The first test of a throwing cleanup did not exercise it: the
  product's own cleanup absorbs a failing secret store and reports the verifier as still due. The loop now takes the
  one thing it runs as a parameter, the harness passes it the operator action, a guard pins that, and a test shows
  that a failing or throwing run is counted as such, logged by its type only, and followed by the next tick.
- **Two defence-in-depth checks were not reachable by any test.** In an earlier mutation run, removing the
  transport-security check and the loopback check behind the operator listener's own TLS and loopback binding
  survived, because no socket of the harness can deliver such a request. A direct test of the request checks was
  added, with a third mutant for the port check, and the harness's internals were made visible to its own tests only,
  under a guard.
- **A missing security header made a test throw instead of fail.** The header assertions were rewritten so that a
  missing header is an assertion failure.
- **The framework's log configuration beat a global filter in a control test.** A category rule in a copied settings
  file outranked the filter. The control now sets the levels explicitly, which also shows why a quiet level is not a
  control.
- **An unreachable database made the start throw.** The start now answers a closed refusal.

## Interruption and recovery

The work was done in two sessions on 2026-10-05. The first session was interrupted by a power outage during its final
test run, after a background mutation run had already been stopped by the system for low memory. The second session
audited the working tree before doing anything else, and changed nothing by reset, clean, stash or checkout.

- Every changed file was checked for truncation and corruption, and none was found. The last repository edit predated
  the last successful build of the first session.
- The mutation driver had left one harness file mutated when it was stopped. The first session restored it from its
  byte backup before the outage. The second session verified independently that all 18 harness files match the
  recorded SHA-256 baseline and the byte backups, and that no mutation journal remained.
- Nothing that the outage interrupted is counted. The clean build, the mutation control run, every mutant that had
  not finished, and the whole final test run were executed again in the second session. The nine mutants that had
  finished on the final tree before the interruption, with the tree's hashes unchanged since, were kept.
- An earlier mutation run of 95 mutants on an earlier state of the harness is not counted either, because harness and
  test files changed after it.
- The baseline on the unchanged checkpoint was not repeated: its saved reports are complete and the checkpoint is
  unchanged.
- The local Docker stack had stopped with the outage. Its existing containers were started again by name. Nothing was
  created, provisioned, migrated or bootstrapped.

## Deliberately not implemented, and deferred

- **No real provider transport and no real secret store.** There is no adapter for either, and no setting that could
  select one.
- **No standalone run.** No database is approved for it, so no standalone smoke run was performed, and none is
  reported as passed. The harness was exercised only in process, by its tests, through the existing test fixtures and
  their established lifecycle.
- **No webhook registration, no monday write, no `webhooks:write`.** D3C-1 and the read-only scope are unchanged.
- **No scheduling** of initial sync, reconciliation, sweep or scope configuration.
- **Deferred diagnostic observations.** Whether a webhook request is a challenge or an event, and anything else about
  the body of a delivery or the shape of an API answer, would need a copy of the payload or production support. These
  observations are deferred explicitly and are not captured.
- **No change to `docs/architecture/dependency-rules.md`.** Its existing rule already covers the harness: no
  production project may reference a project under `tests/`.
- **No change to the Premium "Core Gate 3" row.** By decision F it changes only when Gate 3 as a whole changes state,
  and it needs the owner's explicit approval.
- **No change to local review tooling.** Several local skills and agents still describe 3G as "NEXT / NOT STARTED".
  That is a stale expectation of the tools, reported here and not modified.

## Open questions for the owner

None of these blocks the offline slice. Each is undecided, and this slice decides none of them.

1. **Logs and other tenants.** The harness now withholds every product log record that is not the configured
   tenant's. The reviewer's alternative was an owner decision that platform-operator logs may carry tenant ids. The
   stricter behavior was chosen because it needs no decision; the owner may relax it.
2. **All-tenant intake and relay on a shared database.** The webhook listener is the product's endpoint, unchanged,
   so it accepts a valid delivery for any connection in the database, and the relay publishes every tenant's pending
   rows. Only the consumer and the operator actions are limited to the configured tenant. On a dedicated database
   this does not arise. Whether a real run may ever share a database is part of the standalone-resources decision.
3. **The operator's identity provider key.** The offline slice uses `LocalDemo`. Which key an owner-operated DEV/TEST
   harness uses in a real run is not decided.
4. **The loopback HTTPS redirect address and TLS on the operator listener.** Both fix how a real run would have to
   register its redirect address. They are design choices of this slice.
5. **The role check.** It reads the `pg_roles` and `pg_tables` catalogs about the current role, which is beyond the
   list of evidence readers the owner named. It does not detect a role that is merely a member of a superuser or
   BYPASSRLS role. The local environment has no role with exactly one of the three privileges and the tests create
   none, so the catalog read is exercised only for the runtime role (none of the three) and the schema owner (all
   three); the decision itself is tested for all eight combinations.
6. **Design choices stricter than the owner's wording.** They are listed in the Definition of Done in
   `docs/gates/gate-3-scope.md`: the guard against a telemetry exporter and request logging, loopback-only listeners,
   the `__Host-` binding cookie, the fetch-metadata checks, the exact `127.0.0.1` Host, and the fixed two-minute and
   ten-minute lifetimes.

## Real-execution blockers

Every blocker stays open. This slice resolves none of them.

- Approved durable storage for real secrets. No local-secret-store exception is approved.
- The monday app and workspace, and the exact approved runs.
- Compatible webhook provisioning and public ingress. No tunnel or public listener is approved.
- Remote-revocation behavior.
- Decision E and the other MUST CONFIRM IN 3G items, including item move and copy identity, cursor binding to
  authority, the HS256 algorithm and the raw-or-`Bearer` header form of the webhook token.
- Dedicated standalone runtime resources, including the standalone database. Neither Azure nor any particular
  provisioning path is approved.

Fake observations are not provider behavior. The composition test sees test-minted HS256 tokens in the bare header
form and a fake provider's confirmation of a revocation. Those observations confirm nothing about monday, and real
authenticated webhook evidence is not waived.

## Tooling notes

- The repository's bundled mutation runner mutates only `src/`. Because this slice may not mutate production code, a
  separate driver with the same verdict taxonomy was used for the harness files. The driver, its plan and its
  per-mutant reports are kept outside the repository and are not part of the change set.
- The D3G labels in the scope record are the record's own and are not the owner's numbering.

## Contradiction check

After the work, the change set was compared again with the canonical hierarchy and the owner decisions on record.

- **PRD v1 and System Architecture v1:** no product behavior and no architecture changed. No file under `src/`
  changed, and the six frozen documents are byte-identical to the checkpoint.
- **Architecture invariants:** the tenant is resolved server-side from configuration and never from a request; the
  harness connects as the runtime role and refuses a privileged one; secrets stay behind the connector's secret-store
  port; logs carry no payload, credential or other tenant's identifier; and offline fakes are kept separate from real
  monday evidence.
- **Owner decisions:** D3C-1 and the read-only scope, R3B-1 and R3B-2, the PKCE hosting decision, decisions A to F,
  Gate 3F PASS / CLOSED and the F39 exception are all unchanged. The harness is the owner-operated DEV/TEST harness
  that R3B-1 names, and it adds no anonymous production OAuth-start endpoint.
- **Scope discipline:** no new package, service, framework, database, queue or AI capability, and no Gate 4 or
  Premium work.

**Result: no contradiction found.** Both canonical audits reached the same result. The open questions above are
undecided matters for the owner, not contradictions.

## Conclusion

The offline slice is implemented, locally verified and reviewed, and it stops at the owner-review boundary. Completing
it is not permission for real execution.

- **Offline implementation: READY FOR OWNER REVIEW.**
- **Gate 3G: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.**
- **Real acceptance: NOT PROVEN.**
- **Real execution: NOT AUTHORIZED.**
- **CI for the current changes: NOT RUN.**
- **Real monday calls: none.**
- **Azure actions: none.**
- **Standalone database provisioning: none.**
- **Commit or push: none.**

## Addendum — offline Key Vault adapter slice (S-1 to S-5)

This addendum records a second offline slice, added on 2026-10-05 after the checkpoint above was merged as commit
`884962f6a3e4f7ca820bd8159e7ac97fa2528cdd`. It is uncommitted. It does not change the status of Gate 3G and is not
provider evidence: the vault and monday are fakes throughout.

### Authorization

On 2026-10-05 the product owner approved S-1 to S-5 exactly as defined in the corrected
`docs/gates/gate-3G-live-enablement-plan.md` and authorized only the offline adapter slice that plan defines. The
decisions are restated closely, not verbatim, in `docs/gates/gate-3-scope.md`, section "3G — Offline Key Vault
secret-store adapter (S-1 to S-5, approved 2026-10-05)". No real vault, Azure resource, permission grant, identity,
real secret, webhook provisioning or live run is authorized. An earlier unauthorized local draft of similar files was
removed on the owner's instruction before this slice began; nothing of it was reused (verification evidence outside
the repository, in the owner's `FULLY-EVIDENCE` folder: `FULLY-3G-cleanup-verification-20261005T145047Z`).

On 2026-10-06 the owner clarified, in their words: "S-1 permits only the OFFLINE Key Vault adapter that does not hold,
use, or obtain any real credential. D3G-1 remains fully in force and continues to prohibit every real-credential adapter
and every real connection to Azure or Key Vault." This does not authorize live execution. The adapter holds, obtains
and uses no real credential: its token source is a seam with no real implementation, and its tests use a synthetic
token against a fake vault.

### What was implemented

- `tests/App.Harness.Monday/Secrets/KeyVaultSecretStore.cs`: an `IMondaySecretStore` over the documented Key Vault REST
  interface. It takes an injected handler (sent through an `HttpMessageInvoker`), an injected access-token source, the
  connector's existing `IMondayDelay` seam and a `TimeProvider`. It sends only get (`secrets/{name}/`, the latest
  version), set (`PUT secrets/{name}`) and delete (`DELETE secrets/{name}`) with the pinned API version `2025-07-01`,
  and only to an address that is a vault origin by meaning: HTTPS, the effective default port, one vault label under
  `.vault.azure.net`, and no path, query, user information or fragment. Host-name case, an implied root path and an
  explicit port 443 are the same origin, and every request is built from the parsed address.
- Status mapping (S-4, S-5): a 200 with a well-formed body is success; only a 404 with a well-formed Key Vault error
  body is not-found, and only for get and delete; everything else is unavailable. A delete's success means the name
  left the active namespace and no longer answers an ordinary get; it does not mean the value was destroyed.
- Attempts (S-5): at most three, retried only for 429, 500, 502, 503, 504, a connection failure or an attempt timeout;
  five seconds per attempt; ten seconds per get and fifteen per set or delete; Retry-After honored up to two seconds
  within the budget; 409 never retried; a retried set sends identical bytes; caller cancellation propagates; nothing is
  cached; a timeout is never taken as proof that a write did not happen.
- `KeyVaultSecretCodec.cs` (S-3): marked base64url without padding under one exact content type, decoded strictly; a
  malformed marked value is unavailable and never read as plain text; plain text only under `wce-monday-client-secret`
  and `wce-monday-signing-secret`; any other content type, the empty one included, is unavailable; values above 18,000
  bytes are refused before any request.
- `IKeyVaultAccessTokenSource.cs`: the token seam. No implementation reaches an identity provider.
- Not wired: no harness host, entry point or standalone gate names the adapter; the default executable still has no
  usable secret store. No change under `src/`, no package, no project reference.

### Tests and guards

- `HarnessKeyVaultSecretStoreTests` (84 test cases): exact round trip of byte sequences up to 18,000 bytes including
  invalid UTF-8; plain text only under the two owner-placed names; unknown content types; malformed marked values;
  refusal above the bound with no request; the status mapping row by row, including a 404 without a Key Vault error
  body; retry classes, waits, Retry-After and budgets on a stepped clock; identical bytes on a retried set; 409 never
  retried; ambiguous set and delete settled by a later attempt; unsettled ambiguity with the write present in the vault;
  caller cancellation before, during and between attempts; no cache; request shapes; refused and equivalent vault
  addresses; the public surface; two tenants' names in one shared vault, each reaching only its own entry; and sentinels
  for the token, the value and the vault's answer text, with positive controls.
- `HarnessKeyVaultConnectorFlowTests` (4 tests, the real local database as the runtime role): authorization, renewal
  and disconnect; adoption of an abandoned renewal from the vault by a later instance; verifier cleanup with a refused
  deletion that stays due; and the size of the token material the connector actually writes.
- The fake vault (`HarnessKeyVaultFakes.cs`) keeps deleted names reserved: a get or delete of a deleted name is
  not-found and a set under it is 409. The connector flows never rewrote a deleted name.
- `HarnessBoundaryTests`: one new guard covering placement, non-wiring, the one invoker, the three methods, the path
  and API version, the origin checks, the status and retry constants, the encoding constants, and no log, cache or
  mutable static state. No existing assertion was changed, and the existing guards (no network handler, no sleep, no
  `HttpClient` outside the two existing files) apply to the new files unchanged.

Measured, not assumed: token material written by the connector with both tokens at the maximum length was 16,711 bytes
when nothing needed escaping, inside the bound, and 57,671 bytes with a refresh token of characters the default JSON
encoder escapes, above the bound. That authorization failed closed with a storage failure, the pair was revoked, and
nothing was written to the vault.

### Verification

**First run (2026-10-05), against the shared local test database.** Clean build 0 warnings, 0 errors; both verifiers
PASS; unit 949/949; architecture 44/44; security 168/168; integration 86/86, 102/102, 180/180 and 86/86; the harness
namespace 163 of 168, with **5 failed**. The five are existing harness tests that need the outbox relay to reach their
own tenant (`HarnessCompositionTests`, `HarnessLeakPreventionTests`, two `HarnessBackboneLoopTests`, and one
`HarnessTenantIsolationTests` case). The existing `PersistenceFoundationTests` write outbox rows of the types
`receipt.received` and `test.utc`, which the relay cannot relay and, by design, leaves pending; they accumulate in the
shared local database with every local run. Exactly 100 tenants held such rows, the oldest dated 2026-03-04, and the
relay's tenant discovery returns at most 100 tenants per pass, oldest first, so no newer tenant was reached. Those rows
were not removed (D3G-4), and no test, guard or relay limit was changed.

**Revalidation (2026-10-06), against a temporary isolated database.** The database-isolation mechanism is the one the
repository already uses: the test fixture reads `POSTGRES_DB` from the environment ahead of `.env`
(`tests/App.Integration.Tests/Database/LocalEnvironment.cs`) and migrates the database it is given from zero
(`DatabaseFixture`), and the existing fresh-database tests create their own throwaway databases on the same local
server. A new database, `wce_kv_evidence_20261006t022855`, was created on the local server and given only the
database-scoped grants of the local init script `infra/local/postgres/init/10-runtime-role.sh` (lines 29 to 36: connect,
schema usage, default privileges) for the existing runtime role. No `ALTER ROLE` was run; the runtime role stayed
NOSUPERUSER, NOBYPASSRLS, NOCREATEROLE, NOCREATEDB, with no role membership, and its password and every shared object
were left untouched. Every run below set `POSTGRES_DB` to it.

- Clean non-incremental build: 0 warnings, 0 errors. `eng/verify-architecture.py` PASS; `eng/verify-local-env.py` PASS.
- The five previously failing tests, first: **5 of 5 passed**.
- Full local suite: **1,783 of 1,783 passed, 0 failed, 0 skipped**: unit 949, architecture 44 (the harness guards
  included), security 168, integration 622 in five namespace batches (86, 102, 180, 86 and 168). The Service Bus emulator
  tests ran; no environment-dependent exclusion applied. The tenant-isolation classes all passed in it
  (`TenantIsolationTests` 8, `ConnectionPoolIsolationTests` 3, `TenantIsolationAcceptanceTests` 3,
  `PersistenceSecurityTests` 11, `HarnessTenantIsolationTests` 8), as did the adapter class (84) and the flow class (4).
- The code and test files of the slice are byte-identical to the copies packaged on 2026-10-05, before these runs; their
  later file times come from the mutation driver restoring each file after its mutant.
- Shared database `workcontinuity`: no row count or content changed. It was fingerprinted read-only before and after
  the runs and again after the temporary database was dropped, covering exact row counts of every public table, pending
  outbox rows (198 rows, 151 tenants), a content hash of the outbox, a content hash of the tenants, and the cumulative
  insert and delete counters. Every value was identical. The one difference was the cumulative `tup_updated` statistics
  counter (9,208 to 9,215). That counter also counts updates to system catalogs (for example `pg_statistic` and
  `pg_class`), and the only client seen connected to the shared database during the runs was the local Docker Worker. A
  five-minute quiet window afterwards, with no test running, showed no further change. The +7 is therefore not
  attributed precisely; it is recorded here as unexplained. The temporary database was dropped by exact name afterwards;
  its grants can no longer be re-queried, and the setup command with its output is their record. No shared row was
  deleted. The owner accepted the +7 on 2026-10-06 as a non-blocking evidence note, without a claimed cause, and
  accepted this hand-run temporary-database lifecycle as compliant with D3G-4 for this evidence run (owner decisions 2
  and 3 below).
- Six frozen documents unchanged; `git diff --check` clean; nothing under `src/` changed; nothing staged.
- **CI: NOT RUN.**

### Mutation evidence

44 targeted mutants of the new adapter, codec and guard, harness files only, each restored and verified by hash, rerun
in full on 2026-10-06 against the isolated database after a fresh baseline and control run: **43 CAUGHT, 1 SURVIVED**.

The survivor, K05, removes the explicit canonical-encoding check from `KeyVaultSecretCodec.DecodeMarked`
(`bytes.Length <= MaxValueBytes && string.Equals(Base64Url.EncodeToString(bytes), text, StringComparison.Ordinal)`
becomes `bytes.Length <= MaxValueBytes`). The two can differ only on a text that the .NET base64url decoder accepts and
that does not re-encode to itself. Base64url decodes in independent four-character quanta, and only a final partial
quantum (two or three characters) has unused bits. An offline proof, run on .NET 10.0.11, the runtime the tests use,
compared the real compiled codec with a faithful model of the mutant over every final quantum of two, three and four
characters, alone and after a full prefix quantum, plus malformed and canonical controls: 17,309,696 inputs, of which
400,896 are non-canonical. The decoder accepted none of the non-canonical texts, and the original and the mutant
disagreed on **0** inputs. **K05: EQUIVALENT MUTANT — PROVEN**, accepted by the owner on 2026-10-06 for .NET 10.0.11
(owner decision 1 below). The explicit canonical re-encoding check stays in the code as defense in depth. Every
non-equivalent mutant is caught. On 2026-10-05, two mutants that first survived (an attempt timeout of six seconds, a
write budget of sixteen) exposed a test that compared against the adapter's own constants; that test now asserts the
approved values.

### Reviews

Rerun on 2026-10-06 against the revalidated tree, in two rounds; every round's hand-back is in the evidence package.

- canonical-auditor: **CONFORMS WITH FOLLOW-UP**. Round one found no contradiction with a frozen rule or owner decision,
  but four record defects: a wrong suite total (1,883 instead of 1,783), "in the owner's words" for the restated S-1 to
  S-5, "not modified" stronger than the evidence, and this section's placeholder. All four are corrected. Follow-up for
  the owner: confirm that the hand-run, database-scoped setup of the temporary database falls within the test
  lifecycle of D3G-4. The owner confirmed it on 2026-10-06 (decision 3).
- tenant-security-reviewer: **PASS WITH FOLLOW-UP**. Round one returned NEEDS INPUT, because the database setup and the
  role attributes had been stated, not shown. Round two received the setup command with its output, the role query
  and the per-class isolation results, and found no tenant-isolation breach. Follow-ups: rerun fresh-database RLS
  evidence on the tree before a Gate verification; for a future live adapter, an owner decision on vault-side tenant
  separation (today it rests on tenant-embedded secret names, and the client secret and Signing Secret are app-global
  by design), together with the S-4 deferrals.
- gate-verifier: **READY WITH FOLLOW-UP**, for owner review of this offline slice only, not a closure. Both earlier
  gaps (the 5 failures and K05) are closed by the evidence. Round one asked how the test runs are tied to the tree; the
  slice's 7 code and test files are byte-identical to the copies packaged before the runs. Follow-ups: CI NOT RUN
  (behind commit and push); the owner accepts or rejects K05 as equivalent and the +7 note; the shared database's stale
  rows remain the owner's decision. The owner settled the last three on 2026-10-06 (decisions 1, 2 and 4).

`READY WITH FOLLOW-UP` means ready for owner review and checkpoint of this OFFLINE slice only. It does not mean Gate 3G
closure, Gate 3 closure, live readiness, Azure approval or monday approval.

### Owner decisions on the evidence (2026-10-06)

Recorded in `docs/gates/gate-3-scope.md`, section "Offline adapter slice: owner decisions on the evidence". In short:

1. K05 is accepted as an EQUIVALENT MUTANT for the current runtime, .NET 10.0.11. The defensive canonical re-encoding
   check stays as defense in depth.
2. The shared-database `tup_updated` +7 is accepted as a NON-BLOCKING EVIDENCE NOTE. No cause is claimed. The evidence
   showed no shared application-row count change, no shared content-fingerprint change and no shared rows deleted.
3. The hand-run temporary isolated database lifecycle is accepted as compliant with D3G-4 for this evidence run.
4. The stale rows in the shared local database are not cleaned as part of Gate 3G and do not block this slice. Any
   cleanup is a separate maintenance task that needs its own authorization.

None of these authorizes live execution, a real credential, provider contact, shared-database cleanup, a commit or a
push.

### Not done and still open

- No real handler, real token source, vault, identity, role, retention or permanent-removal policy (the future items of
  S-4), and no live run. Stage B and C prerequisites of the plan stay open, as do U1 to U9 and every real-execution
  blocker. D3G-1 stays fully in force.
- The connector's worst-case token material (about 98.7 KB) stays outside the adapter's supported subset by owner
  decision S-3.
- The shared local test database still holds the stale unrelayable outbox rows. By owner decision 4 they are not
  cleaned in Gate 3G and do not block this slice; any cleanup is a separately authorized maintenance task.
- The slice has no schema change; the isolated database above was migrated from zero by the existing fixture, which
  also stands as the slice's fresh-database evidence.

Status: **Gate 3G: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY. Real acceptance: NOT PROVEN. Real execution: NOT
AUTHORIZED. Gate closure: NOT AUTHORIZED. CI for the current changes: NOT RUN. No real monday, Azure, Key Vault, OAuth or
identity-provider contact. No real secret. Commit or push: none.**
