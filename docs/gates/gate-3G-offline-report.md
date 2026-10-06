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

## Addendum — Stage B2 real transport and token source

This addendum records a third offline slice, implemented on 2026-10-06 on the uncommitted tree at
`56f61a02902f2d3f610a5e09686e560427a4fda4`. It does not change the status of Gate 3G and is not provider evidence:
Entra, the vault and monday are fakes throughout, and every certificate is generated in memory by a test.

### Authorization

On 2026-10-06 the product owner authorized Stage B2 of `docs/gates/gate-3G-live-enablement-plan.md` ("Gate 3G — Stage B
Live Readiness Preparation") for offline implementation only, under the approved decisions B-1 (authoring only), B-7
(certificate-credential service principal), B-10 (hand-written certificate assertion, no package, explicit credential,
pinned transport per destination) and B-11 (guards evolve only into equal-or-stricter pinned allowances, with mutation
evidence). No other B decision is approved. The record, written before any code, is `docs/gates/gate-3-scope.md`,
section "3G — Stage B2 real transport and token source (authorized 2026-10-06, offline only)". Before the full suite the
owner also approved, in this session, one temporary isolated database on the local server for the run, on the same
lifecycle as the one accepted on 2026-10-06. The owner was asked whether a temporary database
`wce_b2_evidence_<UTC timestamp>` could be created with only the database-scoped grants of
`infra/local/postgres/init/10-runtime-role.sh` lines 29 to 36, no `ALTER ROLE`, no change to the shared database, migrated
from zero by the fixture and dropped by exact name afterwards, and answered, in the owner's words: "Approve temp DB
(Recommended)". The verbatim authorization and that answer are kept in the evidence package outside the repository.

The Microsoft protocol contract was taken from public documentation read on 2026-10-06 ("Microsoft identity platform
certificate credentials" and "OAuth 2.0 client credentials flow"); no account, tenant or endpoint was contacted.

### Exact scope

New files, all under `tests/App.Harness.Monday/Live/`:

- `LiveTransport.cs`.
  - `CreatePinnedHandler()`, the only place the harness builds a network handler: a `SocketsHttpHandler` with
    `AllowAutoRedirect = false`, `UseCookies = false`, `UseProxy = false`, `Proxy = null`, `Credentials = null`,
    `PreAuthenticate = false`, `ConnectTimeout` 5 seconds, no automatic decompression, 64 KB of response headers at
    most, and `ActivityHeadersPropagator = null` (no trace header reaches an external host). Certificate validation is
    the platform's: no callback, no `SslOptions`, no connection hook.
  - `ForEntraToken(tenant)`, `ForKeyVault(vault)` and `ForMonday()`: each returns a destination guard over a fresh pinned
    handler of its own, so no connection or credential is shared between destinations, or between two transports of
    the same destination.
  - Destination guards. A guard passes a request to its handler only when it is exactly what the destination accepts,
    and otherwise refuses it before the handler sees it, as a connection failure that sent nothing and names no address
    or value. Every guard requires HTTPS on the default port 443, no user information, no fragment and no `Cookie`
    header.
    - Entra: only a POST to `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token` of the one configured tenant,
      with no query, a form body and no `Authorization` header (so no vault bearer can be sent to it).
    - Key Vault: only the adapter's own requests on the one configured vault (which must pass
      `KeyVaultSecretStore.IsVaultOrigin`): GET `/secrets/{name}/`, PUT `/secrets/{name}` with a JSON body, DELETE
      `/secrets/{name}`, each with exactly `?api-version=2025-07-01` and a non-empty Bearer credential. A form POST, the
      shape of a token request carrying the client assertion, never passes.
    - monday: only POSTs to the connector's three fixed endpoints (`MondayEndpoints.Token`, `Revoke`, `Api`), with no
      query.
- `EntraClientAssertion.cs`: the certificate assertion. Header `alg` PS256, `typ` JWT, `x5t#S256` (base64url SHA-256 of
  the certificate's DER encoding); claims `aud` (the tenant's v2.0 token endpoint), `iss` and `sub` (the client id), a
  fresh GUID `jti`, `nbf` and `iat` at the issue time, and `exp` five minutes later; an RSA signature with PSS padding
  over SHA-256. Only an RSA certificate with its private key, of at least 2,048 bits, inside its validity period, signs;
  anything else gives no assertion.
- `EntraCertificateTokenSource.cs`: an `IKeyVaultAccessTokenSource`.
  - Explicit credential only: a tenant id, a client id and a certificate, all given by the caller. No client secret, no
    environment, no configuration, no certificate store, no managed identity, no developer-tool or other credential
    chain, no `Azure.Identity`.
  - The request: one POST to that tenant's token endpoint with exactly `client_id`, `scope` =
    `https://vault.azure.net/.default`, `client_assertion_type` =
    `urn:ietf:params:oauth:client-assertion-type:jwt-bearer`, `client_assertion` and `grant_type` = `client_credentials`,
    bounded to 10 seconds.
  - The answer, fail-closed: a 200 with an `application/json` body of at most 64 KiB; exactly one `token_type` that is
    Bearer (any case); exactly one `access_token` of 1 to 16,384 visible ASCII characters (the adapter's own bound);
    exactly one numeric integer `expires_in` above 300 seconds and at most a day. Anything else, a refused, failed or
    timed-out request, or a certificate that cannot sign now gives no token (null), which the adapter answers as
    unavailable.
  - Caller cancellation propagates as cancellation and is never turned into "no token".
  - The token is kept in memory in the instance only, until five minutes before its expiry counted from before the
    request was sent; nothing is persisted, and a failure is never kept. One request runs at a time.
  - Nothing logs; `ToString` names the type only; no failure quotes a token, the assertion or an answer.

Not wired: nothing outside `Live/` names a Stage B2 type, nothing in the harness constructs the token source or calls a
`LiveTransport.For…` method, `HarnessStandaloneGate` still approves no database, and `HarnessProgram`, `HarnessHost`,
`HarnessOptions` and the entry point are unchanged. The default executable cannot reach a network.

### Guards (B-11)

Three existing guard methods of `tests/App.Architecture.Tests/HarnessBoundaryTests.cs` replaced a blanket ban by a pinned
allowance; no assertion was deleted and each rule is equal or stricter everywhere else:

- `SocketsHttpHandler`: formerly named nowhere; now held by exactly `Live/LiveTransport.cs` and constructed there exactly
  once. Every other network handler, client factory, socket, resolver and browser launch is still named nowhere.
- Handler construction: formerly `new …Handler(` nowhere; now the stricter pattern `new …Handler` (which also catches
  object-initializer construction) appears only in `Live/LiveTransport.cs`, once.
- `HttpMessageInvoker`: formerly the adapter only; now the adapter and the token source, each exactly one
  `new HttpMessageInvoker(_transport, disposeHandler: false)`.
- `HttpMethod`: the adapter still names exactly Delete, Get and Put; the token source names exactly one Post; the
  destination guards name exactly the methods they admit. `HttpMethod.Post` appears only in those two Stage B2 files;
  Patch and Head stay named nowhere.
- The name Key Vault outside `Secrets/`: formerly nowhere; now exactly the token source and the transport file.
- `TimeSpan.FromMinutes(5)`: formerly the cleanup interval only; now also the assertion lifetime and the refresh margin,
  once each, as pinned constants. The cleanup interval assertions are unchanged.

One new guard, `Stage_b2_transport_and_token_source_are_pinned_explicit_and_unwired`, pins: exactly three files under
`Live/`; no Stage B2 name outside `Live/`; no construction of the token source and no `LiveTransport.For…` call
anywhere; the empty approved-database list; no ambient or alternative credential anywhere in the harness
(`DefaultAzureCredential`, `Azure.Identity`, `TokenCredential`, `ClientCertificateCredential`, `ManagedIdentity`,
`client_secret`, `AZURE_`, the instance metadata address and others); no configuration, environment, certificate-store,
file, log, cache or mutable static state in `Live/`; every pinned handler setting and no certificate-validation override;
each destination's own guard over its own pinned handler; the token source's endpoint, scope, grant, assertion type,
bounds and form; and the assertion's PS256 form, SHA-256 thumbprint, PSS padding and key-size floor, with no RS256,
PKCS#1, SHA-1, HMAC, legacy `x5t` or ECDSA.

Before the guards were evolved, the old guards failed against the new files (three failures, recorded in the evidence),
which shows they were not vacuous for this change.

### Tests

78 new tests: 77 integration tests and 1 architecture guard.

- `HarnessEntraTokenSourceTests` (52 cases): the assertion's exact header and claims; its bounded lifetime; its PSS
  signature verified with the generated public key, with negative controls (another key, PKCS#1 padding, a changed
  claim); unique ids; unsuitable certificates (public only, ECDSA, 1,024-bit RSA) and empty identifiers refused;
  certificates outside their validity period sign nothing and send nothing; the exact token request and the binding of
  the sent assertion to the tenant and client; another tenant and client; 21 malformed answers; a well-formed answer
  with extra fields; any status but 200 (including redirects); a non-JSON media type; oversized answers, declared and
  streamed, with a positive control; the access-token bound; the kept token until the five-minute margin and the fresh
  assertion afterwards; failures never kept; nothing kept beyond an instance; one request for concurrent callers;
  caller cancellation before and during the request; the 10-second request bound ending a stalled request as no token;
  connection and other faults as no token; no rendering or public exposure of a token, assertion or answer, with a
  positive control; and compatibility with the existing `KeyVaultSecretStore` over both destination guards (set, get and
  delete with one token request, every vault request carrying the issued bearer), and the adapter unavailable with no
  vault request when no token is issued.
- `HarnessLiveTransportTests` (25 cases): the pinned handler's every setting; one guard and one fresh handler per
  destination and per call; guards rendering no value; the Entra guard passing exactly the token request and refusing
  11 other addresses and a bearer, a cookie, another method, another body or none; the vault guard passing the adapter's
  three requests and refusing 29 others (among them the token request's shape, other methods, other API versions and
  queries, other paths, deleted-secret and recover paths, other hosts, schemes, ports, user information, fragments,
  cookies and non-bearer credentials) and refusing a non-vault origin; the monday guard passing only the three
  endpoints; and a redirect handed back unfollowed, with its target refused.
- Every request in these tests goes to an in-memory handler; the pinned handler is only constructed and inspected. No
  test resolves a name or opens a socket.

### Verification

All local, on the uncommitted tree.

- Clean non-incremental build of the solution: 0 warnings, 0 errors (before and after the mutation campaign).
- `eng/verify-architecture.py` PASS; `eng/verify-local-env.py` PASS.
- Unit 949/949; architecture 45/45; security 168/168; the database-free harness classes 161/161 (the 84 adapter tests and
  the 77 new ones).
- **Full local suite: 1,861 of 1,861, none failed, none skipped** (unit 949, architecture 45, security 168, integration
  699, the Service Bus emulator tests included), run on a temporary isolated database, `wce_b2_evidence_20261006t065258`,
  created for the run on the local server with only the database-scoped grants of
  `infra/local/postgres/init/10-runtime-role.sh` (connect, schema usage, default privileges), no `ALTER ROLE` (the runtime
  role stayed NOSUPERUSER, NOBYPASSRLS, NOCREATEROLE, NOCREATEDB, with no membership), migrated from zero by the test
  fixture, and dropped by exact name afterwards. 1,861 is the earlier 1,783 plus the 78 new tests. The tenant-isolation
  classes passed in it (`TenantIsolationTests` 8, `ConnectionPoolIsolationTests` 3, `TenantIsolationAcceptanceTests` 3,
  `HarnessTenantIsolationTests` 8), as did the adapter class (84) and the adapter flow class (4).
- Shared database: a read-only fingerprint (exact row counts of every public table, content hashes of the outbox and the
  tenants, and the cumulative insert, delete and update counters) was identical before and after the run. Nothing in it
  was cleaned or changed.
- Secret scan with the CI-pinned gitleaks 8.30.1: the history (50 commits) and every changed path: no leaks. A direct
  search of the new files found no key, certificate, token or password material; the only credential-shaped strings are
  the synthetic `user:pass` of two refusal tests.
- `git diff --check` clean; the six frozen documents unchanged; nothing under `src/` changed.
- **CI: NOT RUN.**

### Mutation evidence

78 targeted mutants of the three Stage B2 files and of the harness files the evolved guards protect, each restored and
verified by SHA-256; no test and nothing under `src/` was mutated. A control run on the unmutated tree came first: the 77
new integration tests and the 10 harness guards passed. **77 CAUGHT, 0 SURVIVED, 1 INVALID.**

- Pinned transport (8 of 8 caught): redirects, cookies, proxy, ambient credentials, pre-authentication, a 30-second
  connect bound, trace propagation, and a handler shared between transports.
- Destination guards (20 of 20 caught, plus 1 invalid): a guard admitting everything (D01 did not compile as `if (false)`;
  its compiling form D01b was caught); HTTP; any port; user information; fragments; cookies; Entra any address, a
  bearer, any body, any method (D10, first caught by the static guard only; a test was added for a form body sent with
  another method and the rerun D10r was caught by it as well); vault any host, any query, no bearer, any write method, any
  read path, any origin; monday any method and any address; an empty tenant. D02 (the scheme check removed) is caught by
  the static guard only: behaviorally the default-port and port-443 checks already exclude HTTP, so the scheme check is
  defense in depth.
- Assertion (13 of 13 caught): RS256, PKCS#1 padding, a SHA-1 thumbprint, an unpinned audience, a wrong issuer or
  subject, a non-unique id, a 60-minute lifetime, a backdated `nbf`, a 1,024-bit key, the validity period ignored, a
  not-yet-valid certificate accepted, any certificate suitable.
- Token source (22 of 22 caught): wrong scope or grant, a missing assertion type, a client secret sent, a one-minute
  margin, a token kept forever or never, the token type, short or unbounded lifetimes, unchecked token characters, any
  status or media type, a 1 MiB body bound, caller cancellation swallowed, a 100-second request bound, duplicate fields,
  faults escaping as exceptions (declared failure: the exception escapes), no single flight, an unsuitable certificate at
  construction, a 64 KiB token bound, and a rendering that names the tenant.
- Static guards (14 of 14 caught): Stage B2 named outside `Live/`, an ambient credential named, a second network handler,
  a certificate-validation override, an approved database, an environment read, Key Vault named in a third file, POST in
  the adapter, five minutes in a fourth file, a third invoker, a certificate store, mutable static state, a second
  handler construction, and the legacy thumbprint.

The driver, the mutant plan, the control run and every per-mutant report are in the evidence package outside the
repository.

### Reviews

The three read-only review agents reviewed the change set as an offline slice only. Each is advisory, none approves or
closes anything, and each could read the tree but run nothing.

- **canonical-auditor: CONFORMS WITH FOLLOW-UP.** No contradiction with a frozen rule or an approved owner decision. It
  found the change within B-1 (authoring only), B-10 and B-11. It found no weakening in the guard evolution: each blanket
  ban became an exact-holder pin, and the handler pattern is broader than before. It also found nothing wired, no
  overstatement and nothing of an unapproved B decision. Its follow-ups:
  - the B2 authorization and the temporary-database approval were restated, not quoted. Both are now quoted in the
    owner's words (scope record and this addendum), with the verbatim text in the evidence package;
  - the 3G status row of `docs/gates/gate-3-scope.md` still said "Stage B2 NOT STARTED". It now records B2 as authorized
    and in progress, offline only;
  - review results were to be recorded here (this section);
  - `INIT.md` and `docs/canonical/PROJECT-STATUS.md` said B2 was not started. By the owner's instruction they were left
    until after owner review, and were aligned in the checkpoint documentation step of 2026-10-06.
- **tenant-security-reviewer: PASS WITH FOLLOW-UP.** No tenant-isolation breach and no credential crossing: the Entra
  guard refuses a bearer and the vault guard refuses the token request's form POST. Redirects are not followed, nothing
  is shared between destinations, and nothing is rendered or logged. Its follow-ups:
  - the same two record points as above;
  - hardening before any wiring decision: the Entra guard checks the typed `Authorization` header, so it should also
    refuse a malformed value added without validation, by name; and the guards could allowlist custom headers. No
    credential crossing exists today, because a credential-shaped value is always well formed. Recorded as follow-ups,
    not changed in this slice, so the tested code stays the code the evidence covers;
  - before any host composes this slice: decide telemetry handling for vault URL paths, which carry secret names that
    embed the Fully tenant id, and the source of the Fully tenant context (the R3B-1 harness chain);
  - raw evidence instead of statements, now in the evidence package.
- **gate-verifier: NOT READY — EVIDENCE GAP** in its first round, for the claim "ready for owner review" (not a closure
  verdict). It found no frozen-document change, no `src/` change, no wiring and no security or architecture defect. The
  gaps it named:
  - the main totals had been supplied as statements without commands, outputs and a tie to the tree;
  - the mutation per-mutant detecting tests and restoration hashes had not been supplied;
  - the other two reviews were not yet recorded;
  - the status row and the unquoted authorization (both since corrected).

  The raw evidence, tied to this tree by a source-hash list, was then assembled in the evidence package.
- **gate-verifier, second round: READY WITH FOLLOW-UP**, for owner review of this offline slice only. This is not PASS,
  not a closure and not live readiness. Against the raw outputs it mapped every acceptance item to evidence:
  - the owner's quoted authorization;
  - the per-test results (1,861, no failure, no skip);
  - the temporary database's lifecycle and the identical shared-database fingerprint;
  - every mutant's detecting tests and restore check, with the mutated `Live/` files byte-identical to the tree;
  - the verifiers, the secret scan and the frozen documents.

  Its follow-ups, all handled before packaging:
  - the package's file for the owner's temporary-database answer was empty. It now holds the answer verbatim;
  - this round's result was to be recorded here (this entry);
  - the manifest, ZIP and ZIP hash were to be produced (done at packaging);
  - test-file hashes would tighten the mutation tie. These were added: the test files' last writes precede the last
    mutation control run.

  It also listed the reviewers' hardening and wiring items as owed before any wiring decision, not before this review.

  The round-two evidence gaps are resolved in the final evidence package,
  `FULLY-3G-stage-B2-offline-evidence-20261006T071054Z.zip`:
  - the owner's temporary-database approval is present verbatim;
  - the test-file hashes are present;
  - the manifest is complete, with all 401 entries re-verified from inside the archive;
  - the ZIP's integrity test passed;
  - the ZIP's SHA-256 is recorded:
    `8556600255054e29677445cbce04c29516ed8c469e3807335753d0733f5eb534`.

### Owner review (2026-10-06)

The product owner reviewed the evidence package above (SHA-256
`8556600255054e29677445cbce04c29516ed8c469e3807335753d0733f5eb534`) and **accepted the Stage B2 implementation as a
CHECKPOINT CANDIDATE**. In the owner's words: "This is NOT yet Stage B2 PASS or closure." and "Final PASS requires the
separately authorized implementation commit/push and green CI for that exact commit."

The owner accepted the evidence:
- the full local suite of 1,861 of 1,861, with 0 failed and 0 skipped;
- the temporary isolated database lifecycle;
- the unchanged shared-database fingerprint;
- the mutation campaign: 77 CAUGHT, 0 SURVIVED, and 1 INVALID-BUILD (D01), superseded by the compiling D01b, which was
  CAUGHT;
- both repository verifiers PASS;
- the frozen documents unchanged;
- the secret scan;
- the three reviews: canonical-auditor CONFORMS WITH FOLLOW-UP, tenant-security-reviewer PASS WITH FOLLOW-UP, and
  gate-verifier round 2 READY WITH FOLLOW-UP.

**Accepted BEFORE-WIRING requirements.** They do not block this offline checkpoint, and they are mandatory before any
future wiring or real execution. None is resolved, and none is implemented in this slice:

1. Harden Authorization rejection by raw header name, not only the typed Authorization property.
2. Define and enforce destination-specific header allowlists.
3. Resolve telemetry handling for Key Vault URL paths, so that tenant-embedded secret names cannot leak through HTTP
   telemetry.
4. Resolve the trusted Fully tenant context used when this slice is wired.

### Known limitations and follow-ups

- **Nothing here is provider evidence.** No Entra, Key Vault or monday behavior is proven. To be learned only in an
  approved live run: whether Entra accepts this assertion (PS256, the five-minute lifetime, clock skew); the real token
  answer (`expires_in` as a number, `token_type` casing, token length and alphabet); whether `getSecret/action` alone
  suffices for a get; the vault's real error bodies; and monday's behavior through the pinned handler.
- The pinned handler was never exercised against a network, by design; its settings are verified by inspection and its
  redirect refusal by the guard's behavior with an in-memory handler.
- The token source refuses a token whose life is 300 seconds or less, and an `expires_in` given as a string; both are
  fail-closed choices of this slice, to be confirmed against real answers.
- No certificate is loaded from anywhere: a future run must supply the certificate through a separately approved,
  reviewed mechanism (the certificate store stays forbidden by guard today).
- The approval set for B1, B3, B4 and B5 (B-2 to B-6, B-8, B-9, B-12 to B-19) is not approved; nothing of those slices
  exists.
- The static guards are lexical: they pin source text and counts, with positive controls, and do not prove the absence
  of a semantic bypass such as reflection.

### Checkpoint, CI and owner PASS (2026-10-06)

- The reviewed implementation was committed as `d1468bb6a6bde130ec24dc72c0571255d1391f8c` ("feat: checkpoint Gate 3G
  Stage B2 offline transport", parent `56f61a0`, the 12 reviewed paths) and pushed, each by separate owner
  authorization.
- **CI run 37434239946 — SUCCESS** on that commit: secret scan (no leaks), build/verify/test with 1,861 total (1,851
  passed, 0 failed, 10 skipped: the expected Service Bus emulator tests in CI), and container images.
- The product owner then explicitly approved **"Stage B2 — Real Transport and Token Source = PASS"** (2026-10-06), for
  the offline Stage B2 slice only.

The PASS does not mean any of the following:
- Gate 3G PASS or Gate 3 PASS;
- real acceptance proven;
- real execution or live wiring authorized;
- Azure, Entra, Key Vault or monday execution authorized.

The four BEFORE-WIRING requirements above remain OPEN.

Status:
- **Stage B2: PASS — OFFLINE SLICE COMPLETE** (checkpoint `d1468bb6a6bde130ec24dc72c0571255d1391f8c`, CI run 37434239946
  — SUCCESS).
- **The four BEFORE-WIRING requirements: OPEN.**
- **Gate 3G: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY. Real acceptance: NOT PROVEN. Real execution: NOT AUTHORIZED. Gate
  3G closure: NOT AUTHORIZED.**
- B-1, B-7, B-10 and B-11 are the only approved B decisions; every other B decision is PROPOSED — NOT APPROVED.
- No live wiring exists, and no real credential or provider behavior has been proven.
- No real credential or certificate was used; there was no Azure, Entra, Key Vault, monday or OAuth contact and no
  deployment.

## Addendum — Stage B3 standalone database and transport preparation

This addendum records a fourth offline slice, implemented on 2026-10-06 on the uncommitted tree at
`3eb24d9e25bc74fd9d505b1664bd2022e3ed0622`. It does not change the status of Gate 3G and is not provider evidence. No
persistent standalone database exists, the seed has never been run, the owner-run tool has run only in its test
sandbox, and the shared local emulator was used only by tests that send to, and remove from, sessions of tenants that
exist only in the test.

### Authorization

- On 2026-10-06 the product owner authorized Stage B3 of `docs/gates/gate-3G-live-enablement-plan.md` ("Gate 3G — Stage
  B Live Readiness Preparation") for offline implementation only, under exactly B-14 (standalone database direction)
  and B-15 (shared-emulator isolation direction), limited to four areas: A, the recursive role-membership safety check;
  B, a reviewed seed script, authored and not run; C, the fresh RLS and R3B-2 evidence procedure; D, the shared-emulator
  isolation checks. Record: `docs/gates/gate-3-scope.md`, section "3G — Stage B3 authorization (2026-10-06)".
- After an unexpected shutdown and a read-only design review, the owner approved the B-14 seed clarification (one fresh
  tenant and one fresh canonical user only) and the owner-run tool structure (one PowerShell entry point,
  `eng/harness-standalone.ps1`, with the two SQL files as payloads only). Record: the same file, section "3G — Stage B3
  owner decisions: B-14 seed clarification and owner-run tool structure (approved 2026-10-06)".
- The owner then approved one temporary isolated validation database, `wce_it_b3_isolated_20261006`, in two phases, with
  `workcontinuity_app` left unaltered. Record: the same file, section "3G — Stage B3 temporary isolated validation
  database (approved 2026-10-06)".
- The owner's texts, verbatim, are kept in the evidence package outside the repository.

### Exact scope

New files:

- `eng/harness-standalone.ps1`: the owner entry point. `seed` runs the seed payload as the local schema owner and only
  with `-Approve` (without it, it shows the plan and the payload's SHA-256 and runs nothing); `evidence` runs the evidence
  procedure as the runtime role, read-only, and succeeds only when each of its 22 checks is reported exactly once, in
  order, as PASS, with the three tenant-context switches answered. `-Database` is mandatory, has no default, is never
  taken from `.env`, must match `wce_harness_` and the payloads' naming rule (case-sensitive, anchored), and is refused
  when it is the shared suite database that `.env` names (or when `.env` or its `POSTGRES_DB` is missing). Another
  Docker engine, context or compose project is refused. psql runs only in the local compose PostgreSQL service, with
  every value as an argument of its own. The tool never creates or drops a database, runs a migration, changes a role or
  a privilege, or prints a secret. Every refusal is one printed line and a non-zero exit. Written in ASCII for Windows
  PowerShell 5.1 and PowerShell 7.
- `eng/harness-standalone-seed.sql`: the seed payload, never run. In one transaction it writes one tenant and one
  canonical user, and nothing else. It refuses unless every value is given and well formed (lower-case, distinct,
  non-nil GUIDs), the connected database is the named target and is named like a standalone harness database, the
  session sees every row (so emptiness can be proven), the database is migrated exactly to the latest of the 14
  migrations, every application table is empty, and `tenants` and `users` keep forced row-level security. It proves
  afterwards one tenant, one user and no connection. It never writes a connection, a connector authorization, an OAuth
  transaction, token material, a PKCE verifier or a delegated provider identity.
- `eng/harness-standalone-evidence.sql`: the evidence procedure, one `BEGIN TRANSACTION READ ONLY` that is rolled back.
  It reports 22 closed checks: 18 of the catalog (the target and identity, the runtime role's attributes, ownership and
  membership through the harness's own recursive statement, forced RLS and the isolation policies of the 16 tenant
  tables, the exact R3B-2 privileges, and no administrative path beyond the approved functions) and 4 of isolation
  (without a tenant nothing is visible; the fresh tenant sees no other tenant's row, is present once with one active
  user, and is seen by no other tenant). It shows no payload, value or identifier.
- `tests/App.Harness.Monday/Composition/TransportSessionPreflight.cs` (area D): the configured tenant's own session on
  the event-processing queue, taken by its id (never the next available one) in peek-lock mode without prefetch, peeked
  once and released; any waiting message refuses the start (`TransportSessionNotEmpty`), and a session that cannot be
  taken within 30 seconds refuses it too (`TransportSessionUnavailable`).
- Tests: `tests/App.Integration.Tests/Harness/HarnessRoleMembershipTests.cs` (10),
  `HarnessStandaloneEvidenceTests.cs` (4) and `HarnessTransportIsolationTests.cs` (5);
  `tests/App.Security.Tests/HarnessStandaloneSeedTests.cs` (5) and `HarnessStandaloneWrapperTests.cs` (52: 12 that read
  the tool, 29 refusal cases and 11 runs, each in a sandbox copy with a stand-in `docker` that records its arguments and
  input).

Changed files:

- `tests/App.Harness.Monday/Evidence/EvidenceReaders.cs` (area A): `DatabaseRoleFacts` gains a fourth fact,
  `MemberOfPrivilegedRole`. A recursive statement follows `pg_auth_members` outwards from the current role through
  `UNION` (so a cycle ends), and a granted role that is a superuser, has BYPASSRLS, owns a table of the schema, or has
  no catalog entry or no stated privilege makes the role privileged. Any answer other than one well-formed row with the
  current role found once fails closed.
- `tests/App.Harness.Monday/Composition/HarnessHost.cs`: the session check runs before anything is published or
  consumed, and the composed consumer's session limit (one session, the configured tenant's) is readable by the tests.
- `tests/App.Harness.Monday/Configuration/HarnessOptions.cs`: the two closed refusals.
- `tests/App.Integration.Tests/Harness/HarnessFailClosedTests.cs`: the fourth role fact in the role-check tests, and
  seven more cases of a transport that is not the local emulator (a non-loopback address, a lookalike host name, the
  compose service name, `UseDevelopmentEmulator=false`, no endpoint, an endpoint that is not an address, and text that is
  not a connection string).
- `tests/App.Architecture.Tests/HarnessBoundaryTests.cs`: the harness statements include the membership catalog, read
  only by the recursive statement; the combination of the four facts is pinned; one new guard pins the empty
  `HarnessStandaloneGate` (no setting, no environment variable, no standalone database name in the harness), the two
  holders of the broker's client, and the check's peek of exactly its own session.
- Documentation: this addendum; `docs/gates/gate-3-scope.md`; `docs/gates/gate-3G-live-enablement-plan.md`; `INIT.md`;
  `docs/canonical/PROJECT-STATUS.md`.

`HarnessStandaloneGate` still approves no database. Nothing changed under `src/`; no package, project reference or
configuration key was added.

### Interruption and recovery

A power loss on 2026-10-06 (about 13:24 local time) interrupted the work during a harness test run, whose result was
lost and is not counted. Before anything else, a read-only recovery check confirmed every changed and new file
byte-identical to a replay of every edit of the interrupted session onto the committed files, `git fsck` clean and the
index untouched. The local stack was restarted container by container, without re-running the one-shot bootstrap or
migrator, so no role or grant statement ran. Every validation in this addendum was run after the recovery.

### Problems found during the work

- **The shared suite database starves the relay.** It holds unrelayable outbox rows left by earlier suite runs (166
  tenants, 215 rows), and the relay discovers at most 100 tenants, oldest first, so five harness relay tests failed
  there, among them the two `HarnessBackboneLoopTests`. On the fresh temporary database all of them passed; the cause
  is the shared database's stale state, not Stage B3. Those rows were not cleaned (the owner's decision of 2026-10-06
  that stale shared rows are not cleaned in 3G stands).
- **Fourteen integration tests alter the runtime role.** A read-only scan found 14 tests that bootstrap the real
  runtime role (and so run `ALTER ROLE workcontinuity_app`): the fresh-database and bootstrap-rerun tests of the
  connector authorizations, the initial sync and the reconciliation targets, and the bootstrap idempotence test. The
  owner's condition forbids that statement, so they were not run in Phase 2. None of them is changed by Stage B3, and
  they ran green in CI on the Stage B2 checkpoint `d1468bb`.
- **The mutation driver's first records were invalid.** It matched test outcomes by xunit test id, which differs on
  every run, so no outcome matched its control and the first six mutants were recorded as incomplete. The records were
  set aside, the driver was changed to match by test name, the control runs were repeated, and every mutant was run
  again. Every file was restored and verified by SHA-256 in both rounds.
- **Error text wrapping.** Windows PowerShell 5.1 wraps error text at about 120 columns on redirected output, so each
  refusal of the tool prints one unwrapped line before it stops, and the tests assert on that line.

### Verification

All local, on the uncommitted tree.

- Clean non-incremental build of the solution: 0 warnings, 0 errors (after the mutation campaign; the rebuilt harness
  and test assemblies are byte-identical to those of the restored tree, as the build is deterministic).
- `eng/verify-architecture.py` PASS; `eng/verify-local-env.py` PASS. `git diff --check` clean, and the new files hold no
  CR byte, no trailing whitespace and a final newline. The six frozen documents unchanged. Nothing under `src/` changed.
- Temporary isolated database `wce_it_b3_isolated_20261006` (all times UTC): created at 13:06:32 by the local schema
  owner with only the database-scoped statements of `infra/local/postgres/init/10-runtime-role.sh` (connect, schema
  usage, default privileges), with no `ALTER ROLE` and no other role statement; migrated from zero by the test fixture;
  Phase 1 at 13:06:48 to 13:07:53; Phase 2 at 13:08:31 to 13:11:48; the mutation campaign's integration runs at
  13:26:14 to 13:53:15; dropped at 14:03:53 by a normal `DROP DATABASE` (no open session, no `FORCE`), after which
  `pg_database` held no database of that name (the server's databases: `postgres`, `template0`, `template1`,
  `workcontinuity`).
- **Phase 1:** the two `HarnessBackboneLoopTests` passed (the emulator round trip in 4.4 s, the flow-isolation test in
  2.5 s), then the 15 harness classes **271 of 271**.
- **Phase 2: 1,931 of 1,931 executed tests passed, none failed, none skipped** (unit 949, security 225, architecture 46,
  integration 711, the Service Bus emulator tests included). The full listing is 1,945; the 14 runtime-role bootstrap
  tests above were not run. The 84 new tests are in it (integration 26, security 57, architecture 1); 1,945 is the
  Stage B2 suite's 1,861 plus those 84.
- Final tree, after the mutation campaign and before these documents were written: unit 949/949, security 225/225,
  architecture 46/46. The only document a test reads, `docs/gates/gate-3-scope.md`, still holds its asserted R3B-2
  grant line exactly once.
- **The runtime role was not altered:** `workcontinuity_app` stayed LOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE,
  NOBYPASSRLS and NOINHERIT, and the version of its catalog row (`xmin` 1215206), which any `ALTER ROLE` rewrites, was
  the same before the temporary database was created, after Phase 2, and after the cleanup. No role was left behind: the
  roles are still exactly `workcontinuity` and `workcontinuity_app`.
- **Shared database unchanged:** a read-only fingerprint (each public table's exact row count and an MD5 over its rows;
  the ACLs of every relation, function, the database and the default privileges; RLS flags and policies; the runtime
  role's attributes and `xmin`; roles, memberships, databases and the migration history) was taken at 13:03:52 before
  the temporary database existed and at 14:05:11 after the cleanup. All 216 compared lines are identical. Only the
  server statistics moved (`xact_commit` +1,091, `tup_updated` +14, `tup_inserted` +0, `tup_deleted` +0), from the running
  local Web and Worker and the server's own maintenance; no table's content changed.
- Secret scan with the CI-pinned gitleaks 8.30.1 (binary SHA-256 verified): the history (53 commits) and each of the 19
  changed or new paths, these documents included: no leaks. The whole working tree was not scanned locally, because it
  holds the git-ignored `.env`.
- **CI: NOT RUN.**

### Mutation evidence

59 targeted mutants of the Stage B3 code and of the tool and payloads, each restored and verified by SHA-256; no test
and nothing under `src/` was mutated. The integration runs used the temporary database. A control run on the unmutated
tree came first: the six detecting integration classes (71 tests, the four that use the local emulator among them,
none skipped),
the harness guards (11) and the four tool and seed classes (57) passed. A mutant counts as CAUGHT only when a test that
passed on the control run fails with an xunit assertion; a timeout or any other exception never counts. After each batch
the restored tree was rebuilt, and its assemblies matched the recorded hashes. **59 CAUGHT, 0 SURVIVED, 0 INVALID.**

- Membership and privilege detection (12 of 12): the fourth fact dropped or its answer ignored, the recursion removed,
  `UNION ALL` (no cycle safety), an unstated privilege counted as safe, each of the three privilege terms removed, and
  each fail-closed reading of the answer (several rows, the current role found never or twice, a negative count, a null
  decision).
- Standalone gate (3 of 3): a database approved, an environment variable that approves one, and a gate that admits
  everything.
- Shared-emulator isolation (7 of 7): no session check, a waiting message accepted, a taken session or a broker fault
  accepted, the next available session checked instead of the tenant's own, the consumer's session limit lifted (run only
  with the test that reads the limit without consuming, so no mutant consumer takes any other session's messages), a
  non-loopback emulator address accepted, and a transport not marked as the development emulator accepted.
- Owner-run tool (22 of 22): the shared database not refused, any name or a name in another case accepted, the target
  taken from `.env`, the seed run without `-Approve`, another Docker engine accepted, evidence established whatever psql
  answers, a FAIL check, an unexpected answer line, a missing tenant-context switch, checks out of order or a failing
  psql run tolerated, the seed run as the runtime role, evidence run as another role, upper-case GUIDs, a subject with
  shell characters, a tenant and a user sharing an id, seed values or `-Approve` given to `evidence`, a missing `.env` or
  `POSTGRES_DB` skipped, and a failing seed reported as complete.
- Seed target protections (10 of 10) and evidence procedure (5 of 5): the connected database not checked against the
  target, any database name, a database that already holds rows, a session that may not see every row, a database not
  migrated to the latest migration, a direct connection insert, a grant, a missing target, row-level security not
  forced, and the no-connection proof dropped; the procedure no longer read-only, another database, another role, rows
  visible without a tenant, and the R3B-2 comparison weakened.

Detection: 42 mutants were caught at run time, most also by a static guard; 17 only by a static pin, each for a stated
reason. A02 (the membership answer ignored) cannot be observed at run time here, because no local role is a member of
another and the Stage B3 tests create, grant and alter no role. A07 to A09 change the role projection, which the
synthetic-catalog tests pin before they run. G01 and G02 concern a compiled-in empty list, which is static by the
owner's decision. S01 to S10 concern the seed, which is never run in Stage B3. On E05 the temporary database's
privileges equal the recorded set, so both forms answer PASS there; the expected value is pinned against the Gate 3
scope record and the migration. In A04 one cycle test also failed by timeout; that failure was not counted, and the
assertion failures of four other tests caught the mutant. The driver, the plan, the control runs and every per-mutant report
are in the evidence package outside the repository.

### Reviews

The three read-only review agents reviewed the change set as an offline slice only, with the repository evidence, the
diffs and the validation evidence above. Each is advisory; none approves or closes anything, and none could run
anything. Their reports are kept verbatim in the evidence package. The main agent checked each factual point they
raised; its checks follow the three verdicts.

- **canonical-auditor: CONFORMS WITH FOLLOW-UP.** No contradiction with a frozen rule or an approved owner decision, and
  no Gate breach: nothing under `src/` changed, the gate is empty, the seed was not run, and no provider contact is
  recorded. The quotations in the new scope sections match the owner's texts, and the dated notes under the stale Stage
  B2 lines of the plan are current-status wording only, as the owner asked. Its findings:
  1. The status records said "ready for owner review" before the reviews were recorded, and `INIT.md` pointed to
     verdicts that did not exist yet. Resolved by this section and by the same wording in the five documents.
  2. No owner text fixes the "approved standalone naming pattern" that Decision 2 requires. `wce_harness_[a-z0-9_]{1,51}`
     follows only the plan's example `wce_harness_live_<date>` (area I). Confirmed; it is an open question below.
  3. The temporary database was kept after Phase 2 for the mutation campaign's integration runs (13:26 to 13:53 UTC) and
     dropped at 14:03:53 UTC. The approval says to drop it "After Phase 1/Phase 2 are complete", and lists the mutation
     campaign among the work that resumes after a green Phase 2. The main agent read that as covering the campaign's use
     of the database; it is a reading, not an explicit approval, and an open question below.
  4. The 14 runtime-role bootstrap tests were not run (disclosed above).
  5. The seed is unproven at run time. Its pins of 14 migrations and of the latest migration fail closed when a migration
     is added, so the payload must then be reviewed again.
  6. B-15 is checked mechanically only in part: the check peeks the session and does not inspect its dead-letter
     subqueue, and the operator preconditions stay manual. It asked whether a standalone run could compose a transport
     without the broker client, which would skip the check; it cannot (below).
  7. Informational notes on the tool's design, with no violation. It also noted that the 3G sentence of `CLAUDE.md` is
     stale current-status text outside this slice; that file was not changed.
- **tenant-security-reviewer: PASS WITH FOLLOW-UP.** No tenant-isolation breach and no Gate breach, checked against
  invariants 44, 45, 50 to 52, 59 and 68, B-14, B-15, R3B-2, D3G-2 and D3G-4. Its follow-ups:
  1. The seed has never run. A mistake fails closed, because the seed writes in one transaction, but its refusals are not
     proven by execution.
  2. A granted role is privileged only as a superuser, with BYPASSRLS or as a table owner, the existing check's meaning;
     membership in a role with CREATEROLE or in a predefined administrative role does not count. It is an open question
     below.
  3. The evidence procedure takes the tenant from an operator setting. It is read-only and shows only counts, but that
     boundary must stay out of product code; and the seed, a write by the schema owner, must be approved as a step of its
     own.
  4. On a database that holds one tenant, "the fresh tenant sees no other tenant's row" cannot fail. There the
     procedure's denial evidence is structural (the policy checks) plus the other-tenant direction.
  5. No test establishes that the R3B-2 checks report PASS on a freshly migrated database, because the tests compare them
     with the catalog. On the temporary database the tests established 17 checks as PASS and the name check as FAIL, by
     design. Of the four R3B-2 checks, E05's run shows the OAuth and reconciliation check as PASS there, two were only
     compared with the catalog, and the update-column check was not asserted. The 22-row output itself was not captured,
     and the database no longer exists.
  6. The membership tests use synthetic catalogs; a real chain through `pg_auth_members` is not exercised.
  7. The session check runs only when a broker client is composed (below).
  8. None of the earlier Stage 10B follow-ups is closed by this slice.
- **gate-verifier: READY WITH FOLLOW-UP**, in the sense "ready for the owner's review", not READY FOR OWNER CLOSURE.
  It mapped every required item of the authorization and of the temporary-database approval to evidence, found the
  hashes tying the mutated files to the candidate, and found no security or architecture defect. Its follow-ups: the 14
  unrun tests and the static-only proofs, as known limitations; the review results and the status wording, resolved by
  this section; the identity of the code tree between Phase 2 and the mutation baseline, shown below; the evidence
  package, built after this record; and the items disclosed above.

The main agent's checks of the points raised:

- **No standalone run skips the session check.** The check is skipped only when no broker client is composed, which
  happens only when a test supplies an in-memory publisher (`HarnessSeams.TransportPublisher`). Then no Service Bus
  consumer is composed either, because `AddServiceBusEventTransport` registers the client together with the consumer
  host (`src/App.Infrastructure/Transport/EventTransportRegistration.cs`), so nothing can be taken from the emulator. The
  standalone entry point supplies no transport seam (`tests/App.Harness.Monday/HarnessProgram.cs`,
  `new HarnessSeams { RunBackboneLoops = true }`), so it always composes the client and always runs the check.
- **The same code throughout.** The last edit of a code path was at 12:11:38 UTC, and a full solution build followed at
  12:23:12 UTC. From then until these documents were written (14:08 UTC), no tool call of the session wrote a repository
  file, except the mutation driver after its baseline at 13:26:14 UTC, and every one of its writes was restored and
  verified by SHA-256. Phase 1, Phase 2 and the mutation baseline therefore ran on the same code. The session's edit
  timeline is in the evidence package.

### Open questions for the owner

1. Is `wce_harness_[a-z0-9_]{1,51}` (case-sensitive and anchored) the "approved standalone naming pattern" of Decision 2,
   or should another one be used? It follows only the plan's example.
2. Was keeping the temporary database after Phase 2 for the mutation campaign's integration runs, and dropping it at
   14:03:53 UTC after them, within the approval?
3. Should "privileged role" extend beyond a superuser, BYPASSRLS and a table owner, for example to membership in a role
   with CREATEROLE or in a predefined administrative role?
4. Is static proof of the seed's protections, of A02 and A07 to A09 and of E05 accepted until the first owner-run seed?
   Or are transactional probe roles, or one seed run on a throwaway, freshly migrated database, wanted first? Each would
   need its own approval.
5. Is the evidence procedure's isolation result on the one-tenant standalone database accepted as structural evidence
   plus one direction of denial?
6. How are the 14 runtime-role bootstrap tests to be covered: by CI after a separately authorized checkpoint commit, or
   otherwise?

*Answered, 2026-10-06:* the owner's six review decisions answer these questions; see "Final delta: the owner's review
decisions" at the end of this addendum. The list above keeps the questions as they were asked.

### Known limitations and follow-ups

- **Nothing here is evidence of the persistent standalone database.** It does not exist; the seed and the evidence
  procedure have never run against it, and the tool has never run outside its sandbox. Each of those is a separately
  approved owner step.
- **The B-15 operator preconditions stay manual:** that the run's tenant GUID was never used before, and that no
  full-suite run shares the emulator during a run. No client of the emulator can observe either.
- **Fourteen runtime-role bootstrap tests were not run locally** in this slice (above). They are unchanged and run in CI.
- **Seventeen mutants are detected only by static pins** (above). The membership-to-privilege link and the projection's
  three privilege terms can be exercised at run time only by a database role that is a member of another, which the
  Stage B3 tests deliberately never create.
- The emulator mutants that let the harness start when it should not have (T01, T02) may have left a synthetic message
  of their own test tenant in that tenant's session or in the queue's dead-letter queue. Each such tenant exists only
  in its test and is never used again. The emulator was never reset, drained or reconfigured.
- The tool's tests ran on Windows PowerShell 5.1 only. On other systems they would use PowerShell 7 and a POSIX stand-in
  for `docker`, which no local run has exercised.
- The shared suite database keeps its stale outbox rows, by the owner's decision; harness relay tests keep failing there
  until that is decided otherwise.
- The seed pins exactly 14 migrations and the latest migration's id, so it refuses to run once a migration is added; it
  must then be reviewed again before any approved run.
- The evidence procedure's full 22-row answer on a freshly migrated database was never captured; the tests assert the
  checks one by one instead (see the tenant-security review, item 5). The first owner-run evidence on the persistent
  database will be the first full answer.
- On a database with one tenant, the procedure's cross-tenant evidence is structural plus one direction of denial (see
  the tenant-security review, item 4).
- The session check peeks the tenant's session only; it does not inspect the dead-letter subqueue, and whether the
  emulator's peek also shows deferred or scheduled messages was not verified.
- `CLAUDE.md` still describes 3G's offline harness as uncommitted and does not mention Stage B; that pre-existing status
  text is outside this slice and was not changed.

Status at the time of this addendum: **Stage B3: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**; the offline
implementation is validated locally, the three reviews are recorded above (CONFORMS WITH FOLLOW-UP, PASS WITH
FOLLOW-UP, READY WITH FOLLOW-UP), and it is ready for owner review, with the open questions above; not PASS and not
closed; uncommitted; CI NOT RUN. The persistent standalone database is NOT authorized and does not exist; the seed
was NOT RUN; `HarnessStandaloneGate` is empty; the emulator was not reset or reconfigured; no real credential or
provider was used; there was no Azure, Entra, Key Vault, monday or OAuth contact and no deployment. The approved B
decisions remain exactly B-1 (authoring only), B-7, B-10, B-11, B-14 (as clarified) and B-15. **Gate 3G: IN PROGRESS —
OFFLINE IMPLEMENTATION ONLY. Real acceptance: NOT PROVEN. Real execution: NOT AUTHORIZED. Gate 3G closure: NOT
AUTHORIZED.**

### Final delta: the owner's review decisions (2026-10-06)

On 2026-10-06 the product owner approved six decisions on the open questions above. The record, in the owner's words,
is `docs/gates/gate-3-scope.md`, section "3G — Stage B3 owner review decisions (approved 2026-10-06)"; the owner's
text, verbatim, is in the evidence package. In short:

1. The standalone naming rule `^wce_harness_[a-z0-9_]{1,51}$`, full-string, lowercase and case-sensitive, with no
   default target and the shared suite database refused in any case. The tool and both payloads already implement it,
   so nothing changed.
2. The temporary database's use by the mutation campaign is accepted, for that evidence run only, as a procedural
   deviation. No rerun; future temporary-database approvals must name mutation use.
3. The runtime-role hardening, implemented below.
4. Static and offline proof of the seed is accepted for Stage B3. The seed is not run; its first execution belongs to the
   separately approved provisioning of the persistent database.
5. One-tenant isolation evidence is accepted for Stage B3 only. Multi-tenant acceptance stays OPEN for Gate 3G, and no
   second tenant is required in the standalone database now.
6. The 14 tests that alter the runtime role stay OWNER-EXCLUDED FROM LOCAL RUN — SAFETY BOUNDARY. Before Stage B3 may
   PASS they must execute and pass in CI on the exact checkpoint commit; if CI skips or omits any of them, Stage B3
   cannot PASS and a separately isolated PostgreSQL environment is required. Their names are in the scope record.

**What changed for decision 3.** Seven files of the Stage B3 tree changed again: `EvidenceReaders.cs`, the evidence
procedure, the tool (its check list only) and four test files. The design of the seed and of the tool is unchanged.

- `DatabaseRoleFacts` now holds eleven facts, each read by itself in one catalog statement: SUPERUSER, BYPASSRLS,
  CREATEROLE, CREATEDB, REPLICATION, INHERIT, the absence of LOGIN, and ownership (directly or through a membership,
  by `pg_has_role(..., 'MEMBER')`) of the current database, of any schema, and of any table, partitioned table, view,
  materialized view, sequence or foreign table outside the temporary schemas; the eleventh is membership in any role.
  Any one of them refuses the start, and a role the catalog does not answer about counts as having all eleven.
- The recursive membership statement is unchanged. Its answer now passes only when the current role is found once and
  is a member of no role at all; it still reports whether a privileged role is reached, and a privileged or unreadable
  answer still refuses. Membership counts whatever its INHERIT, SET or ADMIN option. There is no allowlist.
- The evidence procedure's role checks: `role.login_nosuperuser_nobypassrls_nocreaterole_nocreatedb_noreplication_noinherit`,
  `role.owns_no_table_view_or_sequence`, `role.owns_neither_database_nor_schema` (now every schema) and
  `role.member_of_no_role`. The tool's check list follows the procedure, and the tool's tests compare the two.
- Tests and guards: the decision over all 2,048 combinations of the eleven facts; the real catalog read as both local
  roles (the runtime role has none of the facts; the schema owner has every attribute except the absence of LOGIN, owns
  the database, its schemas and its tables, and is a member of no role); on synthetic catalogs, a direct membership,
  chains, two paths and a cycle through ordinary roles are now refused and only no membership passes; the procedure's
  owner-session negative case now also fails the database-and-schema check; the procedure's new conditions are pinned
  word for word; the harness guard pins the role statement word for word, the eleven-fact decision, the membership rule
  and its composition, and its allowlist of the catalogs the harness reads adds `pg_class`, `pg_database` and
  `pg_namespace`, read only by the role statement.

**Final-delta verification.** All local, on the uncommitted tree.

- Build 0 warnings, 0 errors. Unit 949/949, security 225/225, architecture 46/46.
- The 15 harness integration classes on the shared suite database: 271 tests, 266 passed. The other 5 failed only by
  that database's documented relay starvation: 174 tenants hold unpublished outbox rows, beyond the relay's 100-tenant
  discovery limit, so their waits for a publication time out (`HarnessBackboneLoopTests` (2),
  `HarnessCompositionTests.The_offline_composition_runs_authorization_intake_processing_sync_and_reconciliation_against_fakes`,
  `HarnessLeakPreventionTests.No_credential_callback_secret_cursor_or_provider_text_reaches_a_log_the_console_diagnostics_telemetry_or_the_database`,
  `HarnessTenantIsolationTests.Another_tenants_work_passing_through_the_harness_leaves_none_of_its_identifiers_in_the_log_the_console_or_the_diagnostics`).
  All five got past the hardened role check: no test was refused for its database role. They passed on the isolated
  temporary database before the final delta, and their relay code has not changed since. Four of them run in CI on a
  fresh database; the fifth is an emulator test that CI skips.
- The directly affected classes all passed there: `HarnessRoleMembershipTests` 10/10, `HarnessFailClosedTests` 9/9,
  `HarnessOptionsTests` 41/41, `HarnessStandaloneEvidenceTests` 4/4, `HarnessTransportIsolationTests` 5/5.
- No temporary database was created. The earlier isolated full-suite result (1,931 of 1,931 executed) remains valid for
  everything the final delta did not touch: the delta changes only the harness's role check, the evidence procedure's
  role checks, the tool's check list and their tests, and the harness start path that calls the check is exercised
  again by the 266 passing harness tests.
- The shared database: read-only fingerprints before (16:44:22 UTC) and after (17:09:54 UTC) the final-delta runs show
  every ACL, row-level security flag and policy, the runtime role with the version of its catalog row (`xmin`
  1215206), the roles, memberships, databases and migration history identical. Tables only gained test rows. No role was
  created, granted or altered.
- Both verifiers PASS; `git diff --check` clean; the six frozen documents unchanged; pinned gitleaks 8.30.1: no leaks.

**Final-delta mutation evidence.** 32 targeted mutants of the hardened boundary, on the shared suite database for the
integration runs, each restored and verified by SHA-256, the restored tree rebuilt after each batch and byte-identical
to the baseline build: **32 CAUGHT, 0 SURVIVED, 0 INVALID.** Every fact dropped from the decision (11); every fact the
role statement stops reading (8); the ownership read narrowed to tables or to `public` (2); membership in an ordinary
role accepted, an unanswered catalog not assuming membership, and the membership answer ignored (3); the procedure's
four new conditions narrowed and three of its role checks forced to pass (7); and the tool requiring the old check
name (1). 24 were caught at run time; 8 only by a word-for-word pin, because no local role can show the case without a
role or ownership change the owner forbids: no LOGIN (C05), owning only a view, sequence or non-public schema (N01,
N02), a membership (M03), and the procedure's narrowed conditions (P01 to P04). No mutant could make the schema owner
look like the plain runtime role, so the harness never started as the owner on the shared database.

**Known limitations after the final delta.**

- The 14 OWNER-EXCLUDED tests did not run locally. For the checkpoint commit, the exact-commit CI run must show none of
  them skipped or failed. If nothing else changes, its test summary should read: total 1,945, failed 0, skipped exactly
  13 — the 10 emulator tests CI skips today plus the 3 new emulator tests of `HarnessTransportIsolationTests`.
- The five relay-dependent harness tests stay environment-blocked on the shared suite database, whose stale rows the
  owner decided not to clean in 3G.
- Multi-tenant isolation for Gate 3G acceptance is OPEN (decision 5); the seed has never run (decision 4).
- The checkpoint's CI will be the first run of the tool's sandbox tests under PowerShell 7 on Linux.
- The earlier limitations above still apply.

**Status after the final delta.** **Stage B3: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**, validated locally and ready
for owner checkpoint review; NOT PASS; uncommitted; CI NOT RUN. The final-tree reviews (canonical-auditor,
tenant-security-reviewer and gate-verifier) are recorded in the evidence package outside the repository. Stage B3 may
PASS only after a separately authorized checkpoint commit whose exact-commit CI executes and passes the 14 tests, and
only by the owner's decision. **Gate 3G: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY. Real acceptance: NOT PROVEN. Real
execution: NOT AUTHORIZED. Gate 3G closure: NOT AUTHORIZED.**

### Owner clarification: temporary relations (2026-10-06)

After the final-tree reviews, whose first owner follow-up was the ownership read's exclusion of temporary relations,
the owner approved a clarification. The record, in the owner's words, is `docs/gates/gate-3-scope.md`, section "3G —
Stage B3 owner clarification: temporary relations (approved 2026-10-06)". In short:

- Temporary relations remain outside the Stage B3 runtime-role ownership check, as implemented: the harness's role
  statement and the evidence procedure's `role.owns_no_table_view_or_sequence` leave out relations whose
  `relpersistence` is `'t'`. The owner's reasons: such relations are session-private and are not the persistent
  ownership risk R3B-2 and decision 3 target; a start-up check could not prevent their creation after it; and the
  broader control is PostgreSQL's TEMPORARY privilege.
- The TEMPORARY privilege the runtime role currently has through PUBLIC is accepted as a **NON-BLOCKING FOLLOW-UP**.
  Revoking TEMPORARY from PUBLIC would be a separate privilege-model owner decision, outside Stage B3; no grant or role
  was changed.
- Decision 3 is otherwise unchanged. Documentation only: no code or test changed, and no validation or review was
  rerun. Every code, test and tool path is byte-identical to the final tree of the evidence package
  `FULLY-3G-stage-B3-final-evidence-20261006T172600Z.zip` (SHA-256
  `d40d10d22740ae211c52b9be146c837992e2abce48b6edab75d876b22b280ed9`), whose validation, mutation evidence and
  final-tree reviews remain valid; only the hashes it records for the five status documents predate this
  clarification.

**Status after the clarification.** **Stage B3: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**; NOT PASS; uncommitted; CI
NOT RUN. The next owner boundary is the Stage B3 checkpoint authorization: the owner's checkpoint review of the final
tree and its evidence package, and a checkpoint commit and push only by separate authorization. Stage B3 may PASS only
after that checkpoint's exact-commit CI executes and passes the 14 tests, and only by the owner's decision. **Gate 3G:
IN PROGRESS — OFFLINE IMPLEMENTATION ONLY. Real acceptance: NOT PROVEN. Real execution: NOT AUTHORIZED. Gate 3G
closure: NOT AUTHORIZED.**
