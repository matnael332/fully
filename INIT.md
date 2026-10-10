# INIT — Project Handoff State

Last updated: 2026-10-10 (Gate 1 and Gate 2 PASS / CLOSED; Premium Gate P0 PASS and closed; Gate 3, monday
Connector, IN PROGRESS: 3A to 3F and the PKCE verifier cleanup PASS, 3G IN PROGRESS — OFFLINE IMPLEMENTATION ONLY with
the offline harness and the offline Key Vault adapter integrated at `446fa5a`; Stage B planning complete and committed
at `34e1705`; B-1 (authoring only), B-7, B-10, B-11, B-12, B-13, B-14 and B-15 APPROVED 2026-10-06, B-2, B-3, B-4, B-8
and B-9 APPROVED 2026-10-08 (authoring approvals only), all other B decisions PROPOSED — NOT APPROVED; Stage B1 (IaC
authoring) AUTHORIZED 2026-10-08, IN PROGRESS — OFFLINE IMPLEMENTATION ONLY, implemented and validated locally, its
review decisions approved and its local checkpoint commit authorized 2026-10-10 (no push), CI NOT RUN, NOT PASS,
nothing created, deployed or contacted; Stage B2 PASS — OFFLINE SLICE COMPLETE (checkpoint `d1468bb`, CI run 37434239946 SUCCESS);
four BEFORE-WIRING requirements open; no live wiring; Stage B3 PASS — OFFLINE SLICE COMPLETE (approved by the product
owner on 2026-10-06; checkpoint `736db05`, CI run 37509953860 SUCCESS: 1,945 tests, 1,932 passed, 0 failed, 13 expected
emulator skips, the 14 owner-excluded role-bootstrap tests executed and passed in CI; the temporary-relation
clarification in force, TEMPORARY via PUBLIC a non-blocking follow-up; persistent standalone database NOT created; seed
NOT RUN); Stage B4 (operator identity and loopback TLS) PASS — OFFLINE SLICE COMPLETE (approved by the product owner
on 2026-10-07; checkpoint `ba6a086`, CI run 37638185897 SUCCESS: 1,962 tests, 1,949 passed, 0 failed, 13 expected
emulator skips, all 14 role-altering tests and the Stage B4 tests executed and passed in CI; fixed callback port
`47443`; the B-13 key-container residual recorded; no Stage B2 seam wired; `HarnessStandaloneGate` empty); no other
Stage B slice authorized; next owner boundary: the authorization of the Stage B1 checkpoint's push, then its
exact-commit CI and the Stage B1 PASS decision; real acceptance NOT PROVEN, real execution NOT AUTHORIZED, Gate 3G closure NOT AUTHORIZED).
Read this first when resuming.

## Current state (2026-10-10)

- **Gate 1 (Foundation): PASS / CLOSED. Gate 2 (Event Backbone): PASS / CLOSED.** Premium Gate P0: PASS, architecture
  frozen; Premium Gates P1 to P11 NOT STARTED.
- **Gate 3 (monday Connector): IN PROGRESS.** 3A to 3F: PASS. PKCE verifier cleanup (3B maintenance): PASS.
- **Gate 3G: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** The offline harness (`tests/App.Harness.Monday`, merged as
  `884962f`, PR #1) and the offline Key Vault adapter (`tests/App.Harness.Monday/Secrets/`, owner decisions S-1 to S-5
  approved 2026-10-05 for the offline adapter only, plus the owner's clarification of 2026-10-06 that D3G-1 stays fully
  in force) are both integrated on `main`.
- **Current repository pointer (2026-10-08):** `main` == `origin/main` == `5822698ebfbbe7d9a6552990cb641e71ec2c7714`
  ("docs: record Gate 3G Stage B4 PASS", documentation only, CI run 37747426660 SUCCESS), with the Stage B1 working
  tree uncommitted on top of it on that date; on 2026-10-10 the owner authorized a local checkpoint commit of exactly
  the 16 Stage B1 paths on top of `5822698`, with no push. The **latest pushed code checkpoint** is its parent `ba6a086fae4ee6b1c62ba67bdaf498b7921654f2`
  ("feat: checkpoint Gate 3G Stage B4 offline operator TLS", the 15 Stage B4 paths). **CI run 37638185897: SUCCESS** on
  that exact commit (attempt 1): 1,962 tests with 1,949 passed, 0
  failed, 13 skipped Service Bus emulator tests; all 14 role-altering tests and the Stage B4 tests executed and passed.
  Its parent `519d440` (Stage B3 PASS record) is documentation only. The previous code checkpoint is
  `736db05e912ca9e50c73205124ff8c6b2430d11d` ("feat: checkpoint Gate 3G Stage B3 offline preparation", the 19 reviewed
  Stage B3 paths); **CI run 37509953860: SUCCESS** on that exact commit, all three jobs successful (secret scan, no
  leaks; build, verify and test, 1,945 tests with 1,932 passed, 0 failed, 13 skipped Service Bus emulator tests;
  container images, Release build, no push). Its parent `3eb24d9` (Stage B3 decision set, CI run 37442070826) and
  `2aa9575` (Stage B2 PASS record, CI run 37437521793) are documentation only. The code checkpoint before it is `d1468bb6a6bde130ec24dc72c0571255d1391f8c`
  ("feat: checkpoint Gate 3G Stage B2 offline transport", the 12 reviewed Stage B2 paths, CI run 37434239946 SUCCESS:
  1,861 tests with 1,851 passed, 0 failed, 10 skipped); its parent `56f61a0` (B2 decision set, CI run 37419000306) and
  `b0b7248` (B-1 record) and `34e1705` (Stage B planning) are documentation only; `446fa5a` ("feat: checkpoint Gate 3G
  offline Key Vault adapter", CI run 37409608267) is the code checkpoint before that.
- **Local isolated evidence:** 1,783 of 1,783 tests passed (unit 949, architecture 44, security 168, integration 622) on
  a temporary isolated database, 2026-10-06; the owner's evidence decisions of 2026-10-06 (K05 equivalent, the
  `tup_updated` +7 note, the temporary-database lifecycle, the stale shared rows) are recorded in
  `docs/gates/gate-3-scope.md`.
- **Stage B planning: complete** (2026-10-06), recorded in `docs/gates/gate-3G-live-enablement-plan.md`, section "Gate
  3G — Stage B Live Readiness Preparation".
- **B-1 — Stage B preparation boundary: APPROVED — 2026-10-06** (record: `docs/gates/gate-3-scope.md`, section "3G —
  Stage B owner decision B-1 (approved 2026-10-06)"). Offline Stage B slices may **author** code and IaC capable of using
  a real credential in the future, including the real handler and token source, provided none of it is executed against
  a real endpoint or with a real credential: a narrow supersession of D3G-1 for AUTHORING ONLY. D3G-1 continues to
  prohibit obtaining, storing or using a real credential, any Azure, Key Vault, Entra or identity-provider, or monday
  contact, OAuth execution, deployment and live execution.
- **B-7 (runtime identity), B-10 (token source and transport) and B-11 (guard evolution): APPROVED — 2026-10-06**
  (record: `docs/gates/gate-3-scope.md`, section "3G — Stage B owner decisions B-7, B-10 and B-11 (approved
  2026-10-06)"). B-7: the certificate-credential service principal is the future runtime identity; nothing is created.
  B-10: hand-written certificate assertion, no new package, explicit credential only, a pinned `SocketsHttpHandler` per
  destination, enablement only through reviewed code; `Azure.Identity` / `ClientCertificateCredential` NOT approved.
  B-11: a blanket guard is replaced only by an equal-or-stricter pinned allowance in exactly named files, never simply
  deleted, with targeted mutation evidence.
- **B-14 (standalone database) and B-15 (transport isolation): APPROVED — 2026-10-06** (record: `docs/gates/gate-3-scope.md`,
  section "3G — Stage B owner decisions B-14 and B-15 (approved 2026-10-06)").
  - B-14: a persistent dedicated database on the existing local PostgreSQL server, migrated from zero, with only the
    database-scoped grants of the local runtime-role procedure; one fresh tenant, user and connection seeded by a
    reviewed owner-run script outside the harness; no administrative path in the harness; fresh RLS and privilege
    evidence on that database (the shared suite database is not live evidence); a recursive `pg_auth_members` check
    added to the runtime-role safety check; the cluster-wide `workcontinuity_app` limitation accepted. It supersedes
    the D3G-4 deferral for this Stage B path. **It does not authorize creating the persistent database**, which stays
    a separate local approval.
  - B-15: the shared local emulator, with no concurrent full-suite run, a never-used tenant GUID, the consumer limited
    to its own session, and the session verified empty before the run. No live run is authorized.
- **B-12 (operator identity) and B-13 (loopback TLS): APPROVED — 2026-10-06**, "exactly as proposed in the current
  Gate 3G plan", with the fixed callback port's choice delegated (record: `docs/gates/gate-3-scope.md`, section "3G —
  Stage B owner decisions B-12 and B-13 and Stage B4 authorization (approved 2026-10-06)").
- **B-2 (dedicated vault), B-3 (soft-delete retention), B-4 (purge protection), B-8 (runtime RBAC) and B-9
  (administration and identity creation): APPROVED — 2026-10-08**, as authoring approvals only (record:
  `docs/gates/gate-3-scope.md`, section "3G — Stage B owner decisions B-2, B-3, B-4, B-8 and B-9 and Stage B1
  authorization (approved 2026-10-08)"): one new dedicated DEV/TEST harness vault, Standard, Azure RBAC, in a dedicated
  resource group in North Europe, firewall default deny, a dedicated Log Analytics workspace, a separate Bicep entry
  point; retention 7 days; purge protection OFF; one custom role with exactly `getSecret/action`, `setSecret/action` and
  `delete` on secrets, assignable within the dedicated resource group and assigned at the vault only; the product owner
  as infrastructure administrator, Azure resources in Bicep, the Entra application, service principal and certificate
  credential through one reviewed idempotent `eng/` script using the Azure CLI (accepted for this harness only as
  satisfying invariant 69); the Microsoft Graph Bicep extension NOT approved.
- **Thirteen B decisions are approved: B-1, B-2, B-3, B-4, B-7, B-8, B-9, B-10, B-11, B-12, B-13, B-14, B-15. B-5, B-6
  and B-16 to B-19 remain PROPOSED — NOT APPROVED.**
- **Stage B2 (real transport and token source): PASS — OFFLINE SLICE COMPLETE.** Approved by the product owner on
  2026-10-06 ("Stage B2 — Real Transport and Token Source = PASS"), for the offline slice only.
  - Checkpoint: `d1468bb6a6bde130ec24dc72c0571255d1391f8c`, CI run 37434239946 — SUCCESS.
  - Evidence package: `Desktop/FULLY-EVIDENCE/FULLY-3G-stage-B2-offline-evidence-20261006T071054Z.zip` (SHA-256
    `8556600255054e29677445cbce04c29516ed8c469e3807335753d0733f5eb534`).
  - What it delivered: `tests/App.Harness.Monday/Live/` (a pinned `SocketsHttpHandler` with destination guards, the PS256
    certificate assertion and the Entra certificate token source), its tests, and the evolved and new harness guards.
  - **B2 requires no further work unless separately authorized.**
  - The PASS is not Gate 3G or Gate 3 PASS. It proves no real acceptance, and it authorizes no real execution, no live
    wiring, and no Azure, Entra, Key Vault or monday execution. No live wiring exists, and no real credential or
    provider behavior has been proven.
  - Records: `docs/gates/gate-3-scope.md` (B2 section, "Owner PASS (2026-10-06)") and
    `docs/gates/gate-3G-offline-report.md` ("Addendum — Stage B2 real transport and token source").
- **BEFORE-WIRING requirements (accepted 2026-10-06; OPEN, not resolved).** They are mandatory before any future wiring
  or real execution:
  1. harden Authorization rejection by raw header name, not only the typed Authorization property;
  2. define and enforce destination-specific header allowlists;
  3. resolve telemetry handling for Key Vault URL paths, so that tenant-embedded secret names cannot leak through HTTP
     telemetry;
  4. resolve the trusted Fully tenant context used when this slice is wired.
- **Stage B3 (standalone database and transport preparation): PASS — OFFLINE SLICE COMPLETE.** Approved by the product
  owner on 2026-10-06 ("Stage B3 — PASS, OFFLINE SLICE COMPLETE"): "This PASS applies only to Stage B3 offline
  preparation." It was authorized on 2026-10-06 under B-14 and B-15 for offline implementation only (record:
  `docs/gates/gate-3-scope.md`, section "3G — Stage B3 authorization (2026-10-06)"). Its offline scope was limited to the
  recursive role-membership check, a reviewed seed script (authored, not run), the procedure for fresh RLS and R3B-2
  evidence, and the shared-emulator isolation checks and procedure; `HarnessStandaloneGate` stays empty; the persistent
  database is not created, migrated or seeded without a separate local approval; no live run and no real provider
  execution. The items below record its course.
  - An unexpected shutdown interrupted the B3 work on 2026-10-06; the recovered working tree was verified intact. It was
    then uncommitted and not yet accepted.
  - **B-14 seed clarification: APPROVED — 2026-10-06.** The seed prepares only one fresh tenant and one fresh canonical
    user. It never directly seeds a monday connection, connector authorization, OAuth transaction, token material, PKCE
    verifier or delegated provider identity; the single fresh monday connection is created later through the
    product's existing Connect / `MondayConnectionAuthorizer` lifecycle during a separately approved run. R3B-1, D3B-1
    and all earlier Gate 3 decisions are unchanged.
  - **Owner-run tool structure: APPROVED — 2026-10-06.** One PowerShell owner entry point, `eng/harness-standalone.ps1`
    (`seed` behind `-Approve`, a separate read-only `evidence` command); `eng/harness-standalone-seed.sql` and
    `eng/harness-standalone-evidence.sql` are reviewed payloads only.
  - Record: `docs/gates/gate-3-scope.md`, section "3G — Stage B3 owner decisions: B-14 seed clarification and owner-run
    tool structure (approved 2026-10-06)". The persistent standalone database needs its own owner approval.
  - **Temporary isolated validation database: APPROVED — 2026-10-06**, for Stage B3 validation only
    (`wce_it_b3_isolated_20261006`; record: `docs/gates/gate-3-scope.md`, section "3G — Stage B3 temporary isolated
    validation database (approved 2026-10-06)"). Phase 1: both `HarnessBackboneLoopTests` passed on it, then the 15
    harness classes 271 of 271, so the earlier shared-database failures are explained by that database's stale state.
    Phase 2: 1,931 of 1,931 executed tests passed (the 14 tests that bootstrap the real runtime role not run, by the
    owner's condition). The database was dropped by a normal `DROP DATABASE`; the shared database's fingerprint is
    unchanged apart from server statistics; `workcontinuity_app` was not altered.
  - **Offline implementation validated locally and reviewed (2026-10-06), ready for owner review — not PASS:** 84 new
    tests; clean build 0 warnings, 0 errors; targeted mutation 59 of 59 CAUGHT; both verifiers PASS; pinned gitleaks no
    leaks; reviews canonical-auditor CONFORMS WITH FOLLOW-UP, tenant-security-reviewer PASS WITH FOLLOW-UP, gate-verifier
    READY WITH FOLLOW-UP, none finding a defect. The reviews, their follow-ups and six open questions for the owner are
    in `docs/gates/gate-3G-offline-report.md`, "Addendum — Stage B3 standalone database and transport preparation".
    Uncommitted; CI NOT RUN. The seed has never been run, and `HarnessStandaloneGate` stays empty.
  - **Owner review decisions 1 to 6: APPROVED — 2026-10-06** (record: `docs/gates/gate-3-scope.md`, section "3G —
    Stage B3 owner review decisions (approved 2026-10-06)"): the standalone naming rule; the temporary database's
    mutation use accepted as a procedural deviation; runtime-role hardening (LOGIN and no other attribute, NOINHERIT,
    no ownership of the database, a schema, a table, view or sequence, membership in no role, no allowlist); static
    proof of the seed; one-tenant isolation evidence for Stage B3 only, multi-tenant acceptance OPEN for Gate 3G; the
    14 tests that alter the runtime role OWNER-EXCLUDED FROM LOCAL RUN — SAFETY BOUNDARY, to execute and pass in CI on
    the exact checkpoint commit before Stage B3 may PASS.
  - **Final delta (2026-10-06):** the hardening is implemented and validated locally without a new temporary database
    (build 0/0; unit 949, security 225, architecture 46; the 15 harness classes 266 of 271 on the shared suite database,
    the other 5 failing only by its known relay starvation; mutation 32 of 32 CAUGHT; the shared database's security
    state unchanged; no role altered). Record: `docs/gates/gate-3-scope.md`, section "3G — Stage B3 final delta
    (2026-10-06, offline, uncommitted)". The final-tree reviews are in the evidence package. Ready for owner
    checkpoint review; NOT PASS.
  - **Owner clarification on temporary relations: APPROVED — 2026-10-06** (record: `docs/gates/gate-3-scope.md`,
    section "3G — Stage B3 owner clarification: temporary relations (approved 2026-10-06)"): temporary relations
    remain outside the Stage B3 runtime-role ownership check, as implemented; the TEMPORARY privilege inherited
    through PUBLIC is accepted as a NON-BLOCKING FOLLOW-UP; any future revocation of TEMPORARY from PUBLIC is a
    separate privilege-model owner decision outside Stage B3. Documentation only: no code or test changed, no
    validation or review rerun; the final evidence package `FULLY-3G-stage-B3-final-evidence-20261006T172600Z.zip`
    (SHA-256 `d40d10d2…b280ed9`) remains valid for the unchanged code and tests. Stage B3 was still NOT PASS at that
    point.
  - **Checkpoint, CI and owner PASS (2026-10-06):** committed as `736db05e912ca9e50c73205124ff8c6b2430d11d` (the 19
    reviewed paths) and pushed, each by separate owner authorization; **CI run 37509953860 — SUCCESS** on that exact
    commit (attempt 1, all three jobs): 1,945 total, 1,932 passed, 0 failed, 13 skipped (the expected Service Bus
    emulator tests); the 14 owner-excluded role-bootstrap tests executed and passed in CI, none skipped. The owner then
    approved **Stage B3: PASS — OFFLINE SLICE COMPLETE**. Records: `docs/gates/gate-3-scope.md`, section "3G — Stage B3
    owner PASS (2026-10-06)", and `docs/gates/gate-3G-offline-report.md`, Stage B3 addendum, "Checkpoint, CI and owner
    PASS (2026-10-06)".
  - **B3 requires no further work unless separately authorized.** The PASS is not Gate 3G or Gate 3 PASS. It proves no
    real acceptance, and it authorizes no real execution, no live wiring, no Azure, Entra, Key Vault or monday
    execution, no persistent standalone database and no seed run: the persistent database has not been created, the
    seed has not been run, and `HarnessStandaloneGate` stays empty unless separately authorized. The TEMPORARY privilege
    inherited through PUBLIC remains the approved non-blocking follow-up, and multi-tenant acceptance stays OPEN for
    Gate 3G.
- **Stage B4 (operator identity and loopback TLS): PASS — OFFLINE SLICE COMPLETE.** Approved by the product owner on
  2026-10-07 ("Stage B4 — PASS, OFFLINE SLICE COMPLETE"). It was authorized by the product owner on 2026-10-06 for
  offline implementation only, in a narrowed scope: the honest DEV/TEST operator identity, an ephemeral in-memory
  self-signed loopback certificate per B-13, its SHA-256 fingerprint shown to the operator, one fixed non-default
  loopback callback port, and that certificate supplied to the existing harness host path. The items below record its
  course.
  - Implemented: the operator's identity provider is `wce-dev-harness` (`LocalDemo` named nowhere in the harness); the
    standalone entry point requires the fixed port `47443` before its database gate, and past that gate (still empty)
    creates the certificate for the run (SAN `IP:127.0.0.1` only, one day, not an authority, server authentication
    only), prints its fingerprint, requires the per-run attestation `attest`, and passes the certificate as its one seam.
  - Not done, by the authorization: no Stage B2 seam wiring; the four BEFORE-WIRING requirements stay open;
    `HarnessStandaloneGate` empty; no persistent database or seed run; no provider contact; no trust-store change; no
    change under `src/`; no package.
  - Record and evidence: `docs/gates/gate-3-scope.md`, section "3G — Stage B4 implementation record (2026-10-06,
    offline, uncommitted)", and `docs/gates/gate-3G-offline-report.md`, "Addendum — Stage B4 operator identity and
    loopback TLS". It was then uncommitted, with CI NOT RUN, and **NOT PASS**.
  - Owner decisions, 2026-10-07 (`docs/gates/gate-3-scope.md`, section "3G — Stage B4 owner decisions on the B-13 key
    container and clean verification (approved 2026-10-07)"): the certificate's temporary per-user private-key
    container file, deleted on dispose, is accepted as satisfying B-13, with the residual risk recorded (a hard crash or
    power loss may leave an orphan file); no trust-store change is allowed. The clean verification is exact-commit CI
    after the checkpoint (full suite on a fresh environment, including the 14 role-altering tests); no temporary
    database is required. Stage B4 could PASS only after that CI succeeded, by the owner's decision.
  - **Checkpoint, CI and owner PASS (2026-10-07):** committed as `ba6a086fae4ee6b1c62ba67bdaf498b7921654f2` (parent
    `519d440`, the 15 Stage B4 paths) and pushed, each by separate owner authorization; **CI run 37638185897 —
    SUCCESS** on that exact commit (attempt 1): 1,962 total, 1,949 passed, 0 failed, 13 skipped (the expected Service
    Bus emulator tests); all 14 role-altering tests executed and passed in CI, and the Stage B4-specific tests passed.
    The owner then approved **Stage B4: PASS — OFFLINE SLICE COMPLETE**. Records: `docs/gates/gate-3-scope.md`,
    section "3G — Stage B4 owner PASS (2026-10-07)", and `docs/gates/gate-3G-offline-report.md`, Stage B4 addendum,
    "Checkpoint, CI and owner PASS (2026-10-07)".
  - **B4 requires no further work unless separately authorized.** The PASS is not Gate 3G or Gate 3 PASS. It proves no
    real acceptance, and it authorizes no real execution, no live wiring, no Azure, Entra, Key Vault or monday
    execution, no persistent standalone database, no seed run and no other Stage B slice: no Stage B2 seam is wired,
    the four BEFORE-WIRING requirements stay open, `HarnessStandaloneGate` stays empty, the persistent database has not
    been created and the seed has not been run. The B-13 key-container residual (an orphan private-key file after a
    hard crash or power loss) remains recorded.
- **Stage B1 (IaC authoring): AUTHORIZED — 2026-10-08. IN PROGRESS — OFFLINE IMPLEMENTATION ONLY, NOT PASS.** In the
  owner's words: "Implement Stage B1 — IaC authoring — offline only, under the Product Owner-approved B-2, B-3, B-4, B-8
  and B-9 decisions." It is implemented and validated locally, CI NOT RUN (record:
  `docs/gates/gate-3-scope.md`, section "3G — Stage B1 implementation record (2026-10-08, offline, uncommitted)", and
  `docs/gates/gate-3G-offline-report.md`, "Addendum — Stage B1 IaC authoring").
  - **Owner review decisions and checkpoint authorization: APPROVED — 2026-10-10** (record, in the owner's words:
    `docs/gates/gate-3-scope.md`, section "3G — Stage B1 owner review decisions and checkpoint authorization (approved
    2026-10-10)"). Azure and ARM infrastructure remains Bicep, and for this DEV/TEST harness only the reviewed,
    versioned, idempotent `eng/` identity script is accepted as satisfying invariant 69, amending no frozen document and
    making no project-wide exception; the implementation choices a to h are approved; one Fully tenant per harness run
    is the current operating rule and multi-tenant acceptance remains OPEN; B-18 remains OPEN; the local Integration
    suite omission is accepted for this checkpoint, exact-commit CI being the next clean verification boundary; the
    local approval-guard hook is not changed in B1. The owner authorized a local checkpoint commit of exactly the 16
    Stage B1 paths, with no push. Stage B1 stays **NOT PASS**.
  - New: `infra/bicep/harness-vault.bicep` (its own subscription-scope entry point: the resource group
    `rg-wce-harness-neu` and the custom role) and `infra/bicep/modules/harnessVault.bicep` (the vault, its audit
    workspace and the runtime role's one assignment at the vault); `eng/harness-vault.ps1` (the owner-run preview and
    deployment path, inert without `-Approve`); `eng/harness-identity.ps1` (the Entra application, service principal and
    certificate credential, Azure CLI only, every command behind `-Approve`, verified by read-back); 88 security tests.
  - Changed: `eng/infra.ps1` builds and lints the new entry point (its validate, what-if and deploy are unchanged);
    `infra/bicep/README.md`. No change under `src/`, no package, no Dev or Synthetic template or parameter file changed.
  - Nothing was created, deployed or contacted: no resource group, vault, workspace, role, assignment, Entra
    application, service principal, certificate or secret; no `az login`, validate or what-if; no Stage B2 wiring.
- **No other Stage B slice is currently authorized**; B0 and B5 are not started.
- **Real acceptance: NOT PROVEN. Real execution: NOT AUTHORIZED. Gate 3G closure: NOT AUTHORIZED.**
- **Next owner boundary:** the authorization of the Stage B1 checkpoint's push; the review of its exact-commit CI and
  the Stage B1 PASS decision follow as separate approvals. B5 still needs B-6 and B-17 to B-19
  (PROPOSED — NOT APPROVED); every real step (deploying the vault, creating the identity, the monday app, the runs)
  stays a separate owner approval.

## Where the project is

| Gate | Status |
|---|---|
| 1A Repository & Solution Skeleton | PASS |
| 1B Local Development Environment | PASS |
| 1C Core Platform Skeleton | PASS |
| 1D Database Foundation | PASS |
| 1E Azure Infrastructure as Code | PASS |
| 1F CI/CD & Identity | PASS — closure commit `f4535f7` |
| 1G Foundation Hardening | **PASS** — commits `9651ed3` (implementation), `fc9bb3e` (CI fix), `450c13d` (Dev readiness probe on), plus this closure commit (Commit C) |
| **Gate 1 — Foundation** | **PASS** |
| Gate 2 — Event Backbone | **PASS — closed 2026-09-29.** 2A (event contract, validation, authority table) **PASS**, commit `7ba941a`. 2B (durable ingress) **PASS**, commit `120183b`, CI run 36411854002 green (359 tests: 356 passed, 0 failed, 3 Service Bus emulator tests skipped by design). 2C (outbox relay, publisher, consumer, dead-letter handling) **PASS**, commit `189f173`, CI run 36425641730 green (444 tests: 437 passed, 0 failed, 7 Service Bus emulator tests skipped by design, all of which passed locally). 2D (processing pipeline and event ledger) **PASS**, commit `f97d22d`, CI run 36439994562 green (493 tests: 486 passed, 0 failed, 7 Service Bus emulator tests skipped by design; every 2D test ran and passed in CI). 2E (Snapshot projection) **PASS**, commit `e165852`, CI run 36449846577 green (524 tests: 517 passed, 0 failed, 7 Service Bus emulator tests skipped by design; every 2E test ran and passed in CI). 2F (retention and metrics) **PASS**, commit `259be35`, CI run 36542058355 green (584 tests: 577 passed, 0 failed, 7 Service Bus emulator tests skipped by design; every 2F test ran and passed in CI). 2G (acceptance suite, crash recovery, tenant isolation and the Gate report) **PASS**, commit `59f74e1`, CI run 36552321567 green (613 tests: 604 passed, 0 failed, 9 Service Bus emulator tests skipped by design; every other 2G test ran and passed in CI; 613/613 locally). Acceptance satisfied: duplicate, delayed and out-of-order events do not corrupt the Snapshot and no event is silently lost. Report, risks and deferred items: `docs/gates/gate-2-report.md`. Azure Dev has **none** of the 2B, 2C, 2D, 2E or 2F migrations or schema. Scope and owner decisions: `docs/gates/gate-2-scope.md` |
| Gate 3 — monday Connector | **IN PROGRESS — authorized 2026-09-29.** 3A (connector contract and monday client foundation) **PASS** — approved 2026-09-30, committed as `d06ed66` (773 tests locally, 32/32 mutants caught). 3B (OAuth 2.1 connection lifecycle) **PASS** — approved 2026-09-30 with owner decisions D3B-1 to D3B-3 and review decisions R3B-1 and R3B-2, committed as the Gate 3B commit (986 tests locally, 91/91 mutation and vacuity checks). 3C (webhook intake) **PASS** — approved 2026-09-30 with owner decision D3C-1 (intake only) and the owner-review decisions, committed as the Gate 3C commit (1,167 tests locally, 45/45 mutation checks caught). 3D (source mapping and normalization) **PASS** — approved 2026-10-01 with owner decisions D3D-1 to D3D-8 and the accepted `SourceValueConfirmation` Core extension, committed as the Gate 3D commit (1,291 tests locally, 24/24 mutation checks caught). 3E (API reconciliation and authoritative reread) **PASS** — approved by the product owner on 2026-10-02 (D3E-1 to D3E-11; transient and access failures never held; `AuthorityChanged` for a read overtaken by a reauthorization; 1,420 tests locally; mutation checks 38/38, 32/32 and 13/13), committed after the separate 3B maintenance commit `273faa6`; 3F (initial sync, watermark and buffered replay) **PASS** — approved by the product owner on 2026-10-03 (D3F-1 to D3F-15; a configured connection held from its first enabled scope; versioned scope sets; a current work view of enabled, baselined scopes only; 1,589 tests locally; mutation checks 43/43, 23/23, 15/16 with one equivalent, and 5/5); 3G (real monday end-to-end acceptance) **IN PROGRESS — OFFLINE IMPLEMENTATION ONLY** — authorized by the product owner on 2026-10-05 (D3G-1 to D3G-8: a non-production offline harness at `tests/App.Harness.Monday` and its tests, against deterministic fakes only; real execution NOT AUTHORIZED; Gate 3 closure NOT AUTHORIZED; real acceptance NOT PROVEN); the PKCE verifier cleanup (3B maintenance) is **PASS** — approved 2026-10-03, and the 3G host runs it at start-up and every 5 minutes. On 2026-10-05 the owner approved S-1 to S-5 for an offline Key Vault adapter only; the offline harness and that adapter are integrated on `main` at `446fa5a` (CI run 37409608267 PASS; 1,783 of 1,783 locally on an isolated database). Stage B planning is complete (2026-10-06, committed at `34e1705`, CI run 37414993310 PASS); B-1 (authoring only), B-7, B-10, B-11, B-12, B-13, B-14 and B-15 are APPROVED 2026-10-06, B-2, B-3, B-4, B-8 and B-9 are APPROVED 2026-10-08 (authoring approvals only), B-5, B-6 and B-16 to B-19 are PROPOSED — NOT APPROVED; Stage B1 (IaC authoring) is AUTHORIZED 2026-10-08, IN PROGRESS — OFFLINE IMPLEMENTATION ONLY, implemented and validated locally, its review decisions approved and its local checkpoint commit authorized 2026-10-10 (no push), NOT PASS, nothing created, deployed or contacted; Stage B4 (operator identity and loopback TLS) is PASS — OFFLINE SLICE COMPLETE (owner approval 2026-10-07; checkpoint `ba6a086`, CI run 37638185897 SUCCESS: 1,962 tests, 1,949 passed, 0 failed, 13 expected Service Bus emulator skips, all 14 role-altering tests and the Stage B4 tests executed and passed in CI; authorized 2026-10-06 with B-12 and B-13 in a narrowed scope; fixed callback port `47443`; B-13 key-container deviation owner-accepted on 2026-10-07, its orphan-file residual recorded; no Stage B2 seam wired; `HarnessStandaloneGate` empty); Stage B3 (standalone database and transport preparation) is PASS — OFFLINE SLICE COMPLETE (owner approval 2026-10-06; checkpoint `736db05`, CI run 37509953860 SUCCESS: 1,945 tests, 1,932 passed, 0 failed, 13 expected Service Bus emulator skips, the 14 owner-excluded role-bootstrap tests executed and passed in CI; B-14 seed clarification, owner-run tool structure, one temporary isolated validation database, the six owner review decisions and the temporary-relation clarification approved 2026-10-06; persistent standalone database NOT created; seed NOT RUN), Stage B2 (real transport and token source) is PASS — OFFLINE SLICE COMPLETE (owner approval 2026-10-06; checkpoint `d1468bb`, CI run 37434239946 SUCCESS), with four BEFORE-WIRING requirements open and no live wiring; no Stage B slice other than B1 is authorized (`docs/gates/gate-3G-live-enablement-plan.md`, `docs/gates/gate-3G-offline-report.md`). Scope and Definition of Done: `docs/gates/gate-3-scope.md` |
| Premium P0 — Architecture Freeze | **PASS — ARCHITECTURE FROZEN, closed 2026-09-29.** D1–D4 and C1 approved; invariant 35 COMPATIBLE; audit rerun with no unresolved contradiction. Closure commit `999d2f9` (documentation only), CI run 36619836845 green. `docs/premium/`, `docs/gates/gate-P0-report.md` |
| Premium P1–P11 | Planned; NOT STARTED; not authorized |

**Current repository pointer (2026-10-08):** `main` == `origin/main` == `5822698ebfbbe7d9a6552990cb641e71ec2c7714` (the
Stage B4 PASS record, CI run 37747426660 SUCCESS; the Stage B1 work was uncommitted on top of it on that date, and on
2026-10-10 the owner authorized its local checkpoint commit on top of it, with no push), whose parent is
`ba6a086fae4ee6b1c62ba67bdaf498b7921654f2` (the
Stage B4 offline checkpoint, CI run 37638185897 SUCCESS), whose parent is `519d440` (the Stage B3 PASS record), whose
parent is `736db05` (the Stage B3 offline checkpoint, CI run 37509953860 SUCCESS), whose parent is `3eb24d9` (the Stage B3 decision-set record,
CI run 37442070826 SUCCESS), whose parent is `2aa9575` (the Stage B2 PASS record, CI run
37437521793 PASS), whose parent is `d1468bb` (the Stage B2 offline checkpoint, CI run 37434239946 SUCCESS), whose parent is `56f61a0` (the Stage B2 decision-set record, CI run 37419000306 PASS), whose parent
is `b0b7248` (the
B-1 decision record, CI run 37416642461 PASS), whose parent is `34e1705` (the Stage B planning documentation checkpoint, CI run 37414993310 PASS),
whose parent is `446fa5a` (the offline Key Vault adapter checkpoint, CI run
37409608267 PASS), whose parent is `884962f`, the merge of PR #1 (the offline harness checkpoint,
main CI run 37307089471 green), whose parents are `bc2d7cb` (README redesign) and `4469280`. The paragraph below is the
historical pointer record of 2026-10-04 and is superseded as a statement of the current HEAD.

**Repository pointers (2026-10-04, historical):** `main` == `origin/main` == `f92c94c`, the PKCE verifier cleanup commit (3B
maintenance), whose parent is `aaf2175`, the Gate 3F commit, whose parent is `0dbfe1e`, the Gate 3E commit, whose
parent is `273faa6`, the 3B maintenance commit that keeps the PKCE verifier on a duplicate callback (CI run
36993975259 green), whose parent is `0c2d18e`, the Gate 3D commit (CI run 36819668481 green), whose parent is
`60ded62`, the Gate 3C commit, whose parent is `869e1c7`, the Gate 3B commit
(CI run 36685564292 green), whose parent is `d06ed66`, the Gate 3A commit (CI run 36635615637 green), whose parent is
`999d2f9`, the Premium Gate P0 closure commit (documentation only; CI
run 36619836845 green), whose parent is `9988f54`, the Gate 2 closure commit, whose own parent is
`59f74e16e1332f70aca9924967ee082e84b798e8` (Gate 2G; CI run 36552321567 green on all three jobs: 613 tests,
604 passed, 0 failed, 9 Service Bus emulator tests skipped by design; every other 2G test ran and passed in CI;
gitleaks found no leaks). Earlier: `dcf5496` (Gate 2F status closure), `259be35` (Gate 2F), `2bb480e` (Gate 2E
status closure), `e165852` (Gate 2E), `4b54bec` (the `event-driven-systems` skill aligned with the Gate 2D session
decision), `32e2909` (Gate 2D status closure), `f97d22d` (Gate 2D), `1f59a96` (Gate 2C status closure), `189f173`
(Gate 2C), `3209e57` (Gate 2B status closure), `120183b` (Gate 2B), `23607f9` (project skills), `7ba941a` (Gate
2A). Not committed on that date: the documentation update of 2026-10-04 (this file and `docs/gates/gate-3-scope.md`)
and local Claude tooling under `.claude/`. The local database carries the 2B to 2F migrations (with the 2F follow-up
`RawContentPurgeAge`) and the Gate 3 migrations (`ConnectorAuthorizations`, `ReconciliationTargets`, `InitialSync`,
`InitialSyncScopeVersions`, `InitialSyncActiveScopes` and `ConnectorAuthorizationVerifierCleanup`), applied by the
integration tests; Azure Dev carries none of them.

**Product-owner decisions on record (2026-09-28):** Gate 1G runtime verification approved; Commit B
(readiness probe on in Dev) approved and deployed; the single cold-start Service Bus timeout is a
non-blocking observation carried as a post-Gate-1 hardening follow-up (no readiness timing change, no
token warm-up in Gate 1); Gate 1 / Gate 1G closure committed as `a4bfd18`. Gate 2 was then authorized with
nine design decisions and the 2A–2G decomposition, recorded in `docs/gates/gate-2-scope.md`. The 2C review
approved three more, recorded there under "2C closure": SessionId = TenantId for 2C, to be revisited in 2D;
`outbox_pending_tenants` stays discovery-only; and no runtime activation yet. The 2D review approved three
more, recorded under "2D closure": SessionId remains TenantId, with database ordering and idempotency as the
source of truth; the 5-minute source clock-skew tolerance is approved for now, stays configurable and must be
validated against real connectors; and any authority conflict quarantines the whole event. The 2E review
approved three more, recorded under "2E closure": Snapshot projection stays in the same transaction as the 2D
ledger write; the canonical field-value rule, where NULL means the source cleared the field, invalid values are
quarantined and nothing is truncated or rewritten; and the 1,024-character value limit, kept only in
`EventText.MaxValueLength`, which Gate 3 must validate against real connector data. The 2F reviews (2026-09-29)
approved nine more, recorded in the 2F Definition of Done: no fourth tenant-less function for exact outbox
backlog counts; raw content purged at 28 days with the 30-day hard limit and an hourly future cadence; the
database clock stays the authoritative safety clock, its guard moved from 720 to 672 hours; a separate follow-up
migration, `RawContentPurgeAge`; exit code 3 only for overdue or limit-exceeded content that remains after a run;
a timing model derived from stated assumptions, with no unconditional guarantee during outages; the activation
preconditions recorded, not created; and database time as the authoritative retention clock: PostgreSQL `now()`
decides purge eligibility, the 28-day boundary and the 30-day classification and measures every age, while the
Worker's clock only times runs; and the receipt time itself stamped by PostgreSQL at insert, through a `now()` default
and a trigger that replaces any supplied value and refuses any change, so no ingress clock moves a receipt's age.
Gate 2G needed no new design decision. On 2026-09-29 the product owner approved the 2G change set (commit `59f74e1`,
CI run 36552321567 green) and ordered the formal closure: Gate 2 is PASS and closed, and Gate 3 is next and not
started. The product owner then authorized Premium Gate P0 (Architecture Freeze) as a planning prerequisite to Gate 3:
documentation only, stopping at its owner boundary; Premium Gates P1 to P11 are planned only. The owner's P0 review
(2026-09-29) approved D1, the Premium Autonomous Action Policy (LOW or MEDIUM CHANGE and SEND for verified Premium
tenants only, under a closed, preconfigured Tenant Admin policy with execution-time checks and authoritative
verification; SEND bounded by recipient, channel and content scope; HIGH risk always needs approval); D2, the tenant's
policy plus the user's own revocable delegation, or an explicit Service Identity for service workflows; D3, the Local
Agent extensions for Premium v1; and D4, Support as the first Premium Domain, with end-customer information as Tenant
Work Data. It also corrected the audit: invariant 35 (Approve All) is COMPATIBLE, because Premium's advance policy is
a different mechanism. At closure the owner approved clarification C1 (structured system state is a Premium v1
information source, a separate preferred level above structured UI, with no new Local Agent authority) and closed
Premium Gate P0 as PASS on 2026-09-29: the Premium architecture is frozen, and Gate 3 may begin only after this
closure is committed, on explicit authorization. The closure was committed as `999d2f9` (CI run 36619836845
green), and the same day the product owner authorized Core Gate 3 (monday Connector) with sub-gate 3A; 3B to 3G
each need their own authorization. On 2026-09-30 the product owner approved 3A (PASS) and authorized 3B (OAuth 2.1
connection lifecycle) in SAFE AUTO MODE with decisions D3B-1 to D3B-3; after the review items R3B-1 and R3B-2 the
same day, the product owner gave 3B its final pass (PASS), and then authorized 3C (webhook intake) in SAFE AUTO MODE
with decision D3C-1 (intake only); after the owner's review the same day, 3C passed (PASS), and the product owner then
authorized 3D (source mapping and normalization) in SAFE AUTO MODE with decisions D3D-1 to D3D-5. On 2026-10-01 the
product owner gave 3D its final pass (PASS), accepting the `SourceValueConfirmation` Core extension and recording
decisions D3D-6 to D3D-8. The same day the product owner authorized 3E (API reconciliation and authoritative reread)
in SAFE AUTO MODE with decisions D3E-1 to D3E-9; 3E stopped before code for a durable board id,
and the owner chose option A (decisions D3E-10 and D3E-11). On 2026-10-02 the product owner gave 3E its final pass
(PASS), committed after the separate 3B maintenance commit `273faa6`, and authorized 3F (initial sync, watermark and
buffered replay) in SAFE AUTO MODE with decisions D3F-1 to D3F-15. On 2026-10-03 the product owner gave 3F its final
pass (PASS), then authorized and approved the PKCE verifier cleanup (3B maintenance, PASS) with its 3G precondition.
On 2026-10-04 the product owner ordered the already-approved decisions A to F, and the additional historical Gate 3F
F39 mutation-evidence decision, recorded in `docs/gates/gate-3-scope.md` ("Owner decisions on record before 3G").
On 2026-10-05 the product owner authorized 3G for an offline implementation only, in SAFE AUTO MODE, with decisions
D3G-1 to D3G-8: Gate 3G is IN PROGRESS — OFFLINE IMPLEMENTATION ONLY, real execution is NOT AUTHORIZED and Gate 3
closure is NOT AUTHORIZED. The offline harness was integrated by PR #1 (`884962f`). The same day the owner approved S-1
to S-5 of `docs/gates/gate-3G-live-enablement-plan.md` for an offline Key Vault adapter only; on 2026-10-06 the owner
clarified that S-1 permits only an adapter that holds, uses and obtains no real credential and that D3G-1 stays fully
in force, settled the four evidence questions of that slice, and the adapter was integrated as `446fa5a` (CI run
37409608267 PASS). Stage B (live readiness preparation) was then planned on 2026-10-06, with decisions B-1 to B-19
proposed, and committed and pushed as `34e1705` (CI run 37414993310 PASS). The same day the owner approved B-1 only
(authoring only, a narrow supersession of D3G-1), recorded and pushed as `b0b7248` (CI run 37416642461 PASS), and then
approved B-7, B-10 and B-11, which complete the approval set of slice B2 (the others then remained PROPOSED — NOT
APPROVED). That set was recorded and pushed as `56f61a0` (CI run 37419000306 PASS). The owner then
authorized Stage B2 for offline implementation only; it was implemented offline (uncommitted), verified on an
owner-approved temporary database (1,861 of 1,861), reviewed, and ACCEPTED FOR CHECKPOINT by the owner the same day. It
was committed as `d1468bb` and pushed, CI run 37434239946 succeeded (1,861: 1,851 passed, 0 failed, 10 Service Bus
emulator skips), and the owner approved **Stage B2: PASS — OFFLINE SLICE COMPLETE**, recorded and pushed as `2aa9575`
(CI run 37437521793 PASS). No live wiring exists, and the four BEFORE-WIRING requirements stay open. The owner then
approved B-14 and B-15, which complete the decision prerequisites of Stage B3; B3 itself is not started and not
authorized, and creating the persistent standalone database stays a separate local approval. Recorded and pushed as
`3eb24d9` (CI run 37442070826 SUCCESS), the owner then authorized **Stage B3** on 2026-10-06 for offline implementation
only (B3 IN PROGRESS — OFFLINE IMPLEMENTATION ONLY; persistent database provisioning NOT authorized). An unexpected
shutdown interrupted the B3 work the same day; the recovered working tree was verified intact, and after a read-only
design review the owner approved the B-14 seed clarification (one fresh tenant and one fresh canonical user only; the
connection comes from the product's Connect in a separately approved run) and the owner-run tool structure
(`eng/harness-standalone.ps1` with the two SQL payloads). The owner then approved one temporary isolated validation
database, on which Phase 1 (both `HarnessBackboneLoopTests`, then the 15 harness classes) and Phase 2 (the full local
suite, 1,931 of 1,931 executed) passed; it was dropped, the shared database's fingerprint stayed unchanged, the mutation
campaign caught 59 of 59 mutants, and the Stage B3 offline implementation is ready for owner review (uncommitted, CI
NOT RUN, not PASS). The owner then approved six review decisions; the runtime-role hardening of decision 3 was
implemented and validated (32 of 32 mutants caught), and Stage B3 is ready for owner checkpoint review, still not
PASS: it may PASS only after exact-commit CI executes and passes the 14 tests that alter the runtime role. The owner
then approved a clarification, documentation only: temporary relations remain outside the runtime-role ownership
check, the TEMPORARY privilege inherited through PUBLIC is a non-blocking follow-up, and revoking it would be a
separate owner decision outside Stage B3. No code, test or evidence rerun followed; the next owner boundary was the
Stage B3 checkpoint authorization. The owner then authorized it: the final tree was committed as `736db05` (the 19
reviewed paths) and pushed, CI run 37509953860 succeeded on that exact commit (1,945: 1,932 passed, 0 failed, 13
Service Bus emulator skips; the 14 owner-excluded role-bootstrap tests executed and passed, none skipped), and the
owner approved **Stage B3: PASS — OFFLINE SLICE COMPLETE** (2026-10-06), for Stage B3 offline preparation only. No
other Stage B slice was then authorized; the persistent standalone database had not been created and the seed had not
been run. The owner then approved B-12 and B-13 and authorized **Stage B4** (operator identity and loopback TLS) on
2026-10-06 for offline implementation only, in a narrowed scope (fixed callback port `47443`). On 2026-10-07 the owner
accepted the B-13 key-container deviation, with its orphan-file residual recorded, and chose exact-commit CI after the
checkpoint as Stage B4's clean verification. The 15 Stage B4 paths were committed as `ba6a086` and pushed, each by
separate owner authorization, CI run 37638185897 succeeded on that exact commit (1,962: 1,949 passed, 0 failed, 13
Service Bus emulator skips; all 14 role-altering tests and the Stage B4 tests executed and passed), and the owner
approved **Stage B4: PASS — OFFLINE SLICE COMPLETE** (2026-10-07). No other Stage B slice is authorized; no Stage B2
seam is wired, the persistent standalone database has not been created and the seed has not been run.

## Rules that must not be broken (from CLAUDE.md and the canonical docs)

- Work only on an explicitly authorized Gate. Gate 2 is closed (PASS; `docs/gates/gate-2-scope.md`,
  `docs/gates/gate-2-report.md`), and so is Premium Gate P0 (PASS; `docs/gates/gate-P0-report.md`). Gate 3 is
  authorized and in progress (`docs/gates/gate-3-scope.md`): work only on an explicitly authorized sub-gate; 3G
  is authorized for the offline implementation only (2026-10-05), and any real monday execution and the closure of
  Gate 3 each need their own explicit authorization. Premium Gates P1 to P11 are not started and not authorized. The Premium
  documents in `docs/premium/` are frozen, subordinate to the frozen Core documents, and change only with explicit
  product-owner approval.
- Never modify `docs/canonical/PRD-v1.md`, `SYSTEM-ARCHITECTURE-v1.md`, `ARCHITECTURE-INVARIANTS-v1.md`
  or `DEVELOPMENT-GATES-v1.md`. If a contradiction appears, STOP and report it. (`PROJECT-STATUS.md`
  in the same folder is a status document.)
- Never run `git clean`, `git reset`, `git checkout -- <file>`, `git stash` or anything that discards work.
- Never run `az deployment ... create` without explicit product-owner approval. Use `eng/infra.ps1`,
  which requires `-Environment`, `-SubscriptionId` and `-Approve`; the product owner runs `deploy`
  interactively in their own terminal (it confirms against a live what-if).
- Never dispatch `CD Dev` with `deploy: true`, start a Container Apps job, create Azure resources, change
  RBAC or deploy Synthetic without explicit approval. **Both jobs have exactly one execution; do not
  start either again unless the product owner orders it** (re-running the bootstrap with the same
  password is a no-op; re-running the migration is a no-op while nothing is pending).
- Never grant the GitHub deployment identity anything on the migration or bootstrap jobs.
- One approval boundary at a time; read-only checks need no approval. Scripted edits use two-phase
  verify-then-write with exact occurrence counts and preserve each file's line endings.
- Do not fabricate a PASS. Do not weaken a test or a security rule to make a Gate pass.

## What Gate 1G delivered (all committed on `main`)

- **Azure-aware readiness** (`src/App.Infrastructure/Health/DependencyProbe*.cs`): PostgreSQL proven as
  the runtime role (`SELECT 1`), Service Bus by a side-effect-free operation under the process's own claim
  (Web opens a send link, Worker peeks) on the sessionless `ai-jobs` queue; 5 s budget per dependency;
  reason codes only. Azure: `ServiceBus__FullyQualifiedNamespace` + `ManagedIdentityCredential
  (AZURE_CLIENT_ID)`; local: `ServiceBus__ConnectionString`.
- **Runtime-role bootstrap** (`RuntimeRoleBootstrap.cs`, Worker `--bootstrap-runtime-role`, Bicep job
  `caj-wce-dev-neu-bootstrap`, `bootstrapJobImage` / `WCE_DEV_BOOTSTRAP_JOB_IMAGE`).
- **OpenTelemetry baseline** to Application Insights from both roles, gated on
  `APPLICATIONINSIGHTS_CONNECTION_STRING`; `wce.correlation_id` on request spans; Worker
  `worker.readiness-check` span per heartbeat.
- **Security**: CI `secret-scan` job (gitleaks 8.30.1 pinned by SHA-256, `.gitleaks.toml`); Security
  tests 35 → 58; explicit package allow-list in the Architecture tests.
- **Local parity**: compose `db-bootstrap` before `db-migrator`; `dev.ps1 check` verifies it.
- **CI fix** (`fc9bb3e`): the GitHub Ubuntu runner exports `ACCEPT_EULA=Y` globally; the emulator tests
  now run only when `.env` accepts the licence or `SERVICEBUS_TEST_CONNECTION_STRING` is set.
- **Dev readiness probe on** (`450c13d`): `param webReadinessProbeEnabled = true` in
  `infra/bicep/parameters/main.dev.bicepparam`; Synthetic keeps the default (off).

Full evidence: `docs/gates/gate-1G-report.md`.

## Azure state (verified read-only 2026-09-28, after deployment `wce-dev-20260928-091451`)

- Dev in `rg-wce-dev-neu` (North Europe, token `33f52b`): **21 resources**, **16 role assignments**,
  4 identities, 1 federated credential, subnets `snet-apps 10.10.0.0/23` and `snet-postgres 10.10.2.0/28`.
- Web `ca-wce-dev-neu-web`: revision `ca-wce-dev-neu-web--0000003` Healthy/Running, 1 replica, 0 restarts,
  image `crwcedev33f52b.azurecr.io/wce/web:fc9bb3e9158069064ce5c6e3a2deaac6073e069a`
  (digest `sha256:d97aabf6b1fb8ff8002e73dce2d417fd43bb14a62b560ec114898347ef574b80`); liveness
  `/health/live` (10 s / 30 s / 3) **and readiness `/health/ready` (5 s / 20 s / timeout 15 s / 6)**;
  `GET https://ca-wce-dev-neu-web.wonderfulsmoke-9e138fb2.northeurope.azurecontainerapps.io/health/ready`
  → 200 with `postgres` and `service-bus` healthy.
- Worker `ca-wce-dev-neu-worker`: revision `ca-wce-dev-neu-worker--0000002` Healthy/Running, 0 restarts,
  image `…/wce/worker:fc9bb3e9158069064ce5c6e3a2deaac6073e069a`
  (digest `sha256:aa74781be3ee7ec63933e7dea9973eb15f25ddfd2e2b93bcc1b50330cb6d1153`); no probe by design;
  heartbeat `readiness=ready` every 30 s.
- Jobs (both manual, identity `id-wce-dev-neu-jobs`, image `…/wce/worker:fc9bb3e…`):
  `caj-wce-dev-neu-bootstrap` — **1 execution** `caj-wce-dev-neu-bootstrap-1uur7bk` Succeeded
  2026-09-28 04:10Z; `caj-wce-dev-neu-migrate` — **1 execution** `caj-wce-dev-neu-migrate-9uw56m9`
  Succeeded 04:19Z (migrations `20260926213721_InitialGate1DSchema`,
  `20260926213758_TenantIsolationAndVectorExtension`).
- Runtime role `workcontinuity_app` exists in Azure PostgreSQL (LOGIN, NOSUPERUSER, NOBYPASSRLS, default
  privileges set); the schema is migrated; the apps connect as that role.
- Registry `crwcedev33f52b`: `wce/web` and `wce/worker` each hold exactly two tags (`f4874302…`, `fc9bb3e…`).
- Deployments today: `wce-dev-20260928-065309` (bootstrap job created, migration image) and
  `wce-dev-20260928-091451` (probe on), both Succeeded, 23 nested deployments, 0 failed operations.
- Telemetry: AppRequests (Web); AppDependencies and AppTraces from both roles; AppExceptions 0; console and
  job logs in `ContainerAppConsoleLogs_CL`; sensitive-value sweep 0 hits.
- Synthetic: not deployed. Application Insights: workspace-based, local auth still enabled (follow-up).

## Post-Gate-1 hardening follow-ups (not authorized work; record only)

1. **Managed-identity token warm-up / readiness cold start.** The first `/health/ready` on a fresh Web
   container can spend its 5 s Service Bus budget on the first token acquisition (7.1 s observed once at
   04:26Z on revision `--0000002`; 0.9 s on `--0000003`). Tolerated by failure threshold 6; fix only if it
   becomes operationally significant (warm the token at start-up or size the budget).
2. Web Dockerfile: install `libgssapi-krb5-2` to silence the harmless Npgsql stderr line (Worker already does).
3. Application Insights `DisableLocalAuth` (instrumentation-key ingestion) — hardening option.
4. KEDA `ScaledObjectCheckFailed` warnings: the apps declare no scale rules; cosmetic.
5. Service Bus spans are not emitted for link-open/peek; readiness state is carried as span properties.
6. Synthetic region/subscription decision; Free Trial upgrade before **2026-10-27**.

## Exact next step

1. Gate 3 (monday Connector) is IN PROGRESS: 3A, 3B and 3C are PASS (approved 2026-09-30), 3D (source mapping and
   normalization) is PASS (approved 2026-10-01), and 3E (API reconciliation and authoritative reread) is PASS
   (approved 2026-10-02; decisions D3E-1 to D3E-11; `docs/gates/gate-3-scope.md`), committed after the separate 3B
   maintenance commit `273faa6`. 3F (initial sync, watermark and buffered replay) is PASS, approved on 2026-10-03
   (D3F-1 to D3F-15). 3G (real monday end-to-end acceptance and Gate closure) is IN PROGRESS — OFFLINE IMPLEMENTATION
   ONLY (authorized 2026-10-05, D3G-1 to D3G-8): real execution and Gate 3 closure are NOT AUTHORIZED, and every
   real-execution blocker stays open. The offline harness and the offline Key Vault adapter (S-1 to S-5) are
   integrated at `446fa5a` (CI run 37409608267 PASS); the Stage B planning is committed at `34e1705` (CI run
   37414993310 PASS). **B-1 (authoring only), B-7, B-10, B-11, B-12, B-13, B-14 and B-15 are APPROVED — 2026-10-06**
   (`docs/gates/gate-3-scope.md`, "3G — Stage B owner decision B-1", "3G — Stage B owner decisions B-7, B-10 and
   B-11", "3G — Stage B owner decisions B-14 and B-15" and "3G — Stage B owner decisions B-12 and B-13 and Stage B4
   authorization"). **Stage B2** (real transport and token source) is **PASS — OFFLINE SLICE COMPLETE** (approved by the owner
   on 2026-10-06; checkpoint `d1468bb`, CI run 37434239946 SUCCESS) and needs no further work unless separately
   authorized. No live wiring exists, and four BEFORE-WIRING requirements stay open (Authorization rejection by raw
   header name; destination-specific header allowlists; telemetry handling for Key Vault URL paths; the trusted Fully
   tenant context when wired). **Stage B3** (standalone database and transport preparation) is **PASS — OFFLINE SLICE
   COMPLETE** (approved by the owner on 2026-10-06; checkpoint `736db05`, CI run 37509953860 SUCCESS: 1,945 tests,
   1,932 passed, 0 failed, 13 expected Service Bus emulator skips; the 14 owner-excluded role-bootstrap tests executed
   and passed in CI, none skipped) and needs no further work unless separately authorized; the temporary-relation
   clarification stays in force, with the TEMPORARY privilege inherited through PUBLIC as the approved non-blocking
   follow-up; the persistent standalone database has not been created, the seed has not been run, and
   `HarnessStandaloneGate` stays empty unless separately authorized. **Stage B4** (operator identity and loopback TLS)
   is **PASS — OFFLINE SLICE COMPLETE** (approved by the owner on 2026-10-07; checkpoint `ba6a086`, CI run 37638185897
   SUCCESS: 1,962 tests, 1,949 passed, 0 failed, 13 expected Service Bus emulator skips; all 14 role-altering tests and
   the Stage B4 tests executed and passed in CI) and needs no further work unless separately authorized; no Stage B2
   seam is wired, and the B-13 key-container residual stays recorded. On 2026-10-08 the owner approved **B-2, B-3, B-4,
   B-8 and B-9** (authoring approvals only) and authorized **Stage B1 (IaC authoring)** for offline implementation only;
   it is implemented and validated locally, CI NOT RUN, **NOT PASS**, with nothing created, deployed or contacted; on
   2026-10-10 the owner approved its review decisions and authorized its local checkpoint commit, with no push. **The
   next owner boundary is the authorization of the Stage B1 checkpoint's push**, then its exact-commit CI and the Stage
   B1 PASS decision; no
   other Stage B slice is currently authorized; and B-5, B-6 and B-16 to B-19 are PROPOSED — NOT APPROVED. The pre-existing PKCE verifier cleanup gap is resolved by a 3B maintenance fix, PASS (approved
   2026-10-03); as a 3G precondition, the 3G harness or host runs `CleanUpVerifiersAsync` once at start-up and every 5
   minutes while active, only under a trusted tenant context.
   The 3D decisions stay in force: D3D-1 to D3D-5; D3D-6 (the ledger keeps the provider-neutral
   `processing.normalizer.rejected` for every normalizer refusal); D3D-7 (no new canonical "reread object" concept: 3E
   starts from `ReconciliationRequired` with the trusted tenant and connection and the provider object, board and
   field ids); and D3D-8 (no automatic mapping of a monday date column to `Deadline`). The 3C decisions stay in force:
   D3C-1 (intake only), the monday account bound before any durable write, and HS256 and the raw-or-Bearer
   header forms as compatibility assumptions confirmed in 3G. The 3B decisions stay in force: D3B-1 (delegated
   identity), D3B-2 (secret boundary), D3B-3 (no real monday app yet), R3B-1 (real monday OAuth before Gate 7 only
   through an owner-operated DEV/TEST harness) and R3B-2 (only SELECT, INSERT and UPDATE for the runtime role on the
   two OAuth tables; never rerun the global bootstrap after the 3B migration in Azure without re-asserting them).
   Scope, Definition of Done and the Gate 2 items Gate 3 must resolve: `docs/gates/gate-3-scope.md`.
2. Azure Dev has none of the Gate 2B, 2C, 2D, 2E or 2F migrations. Applying them needs new images, an updated
   migration-job image and one migration-job run, each a separate approval; the migration job keeps exactly
   one execution until the owner orders a rerun. Running the relay, the consumer and the processing pipeline
   in the Worker, including Service Bus send access for the Worker identity, is a further separate approval.
   So is activating the raw-content purge (`--purge-raw-content`), which runs only when invoked explicitly
   until then. Activation needs the hourly job and all five alerts of the 2F Definition of Done (items 13 to 15).
   Content is purged at 28 days; while every hourly run succeeds it is never older than 28 days 1 hour 21
   minutes, and a purge outage of about the 48-hour lead margin or longer can pass the 30-day limit, which the
   alerts report but cannot prevent.
3. No Azure write, job start or `deploy: true` without separate approval. Gate 3 keeps the 1,024-character
   field-value limit (D3D-5) and must confirm it against real monday data in 3G; widening is an explicit proposal,
   never truncation.

Rollback of Gate 1G runtime state, if ever ordered: dispatch `CD Dev` with `f4874302456a50c935a5429deed07699312de37c`
to restore the previous images; redeploy Bicep with `webReadinessProbeEnabled` removed (probe off); the
bootstrap job can be deleted by exact name and the role dropped by name; migrations carry `Down` and the
database holds no data. Tags are immutable and additive.

## Azure facts

- Tenant: the owner's single home tenant ("Default Directory"). MFA is enforced for Azure Resource
  Manager: sign in with `az login --tenant <tenant id>` (plain `az login` fails with AADSTS50076).
- Subscription "Azure subscription 1" (`c1830e72-d806-4cb1-8bff-8627fb01c5ea`), **Free Trial**, spending
  limit on; trial window ends **2026-10-27** unless upgraded. Quota: one Container Apps environment per region.
- Dev names: registry `crwcedev33f52b`, vault `kv-wce-dev-33f52b`, server `psql-wce-dev-neu-33f52b`,
  namespace `sb-wce-dev-neu-33f52b`, storage `stwcedev33f52b`, environment `cae-wce-dev-neu`, apps
  `ca-wce-dev-neu-web|worker`, jobs `caj-wce-dev-neu-migrate|bootstrap`, identities
  `id-wce-dev-neu-web|worker|jobs|github`, Log Analytics `log-wce-dev-neu` (customerId
  `ee16ea70-7f88-4d3f-97f7-ca0baf77b86d`), App Insights `appi-wce-dev-neu`.
- The two Dev passwords (`WCE_DEV_POSTGRES_ADMIN_PASSWORD`, `WCE_DEV_POSTGRES_RUNTIME_ROLE_PASSWORD`) are
  held by the product owner only; every redeployment must reuse the same values. The signed-in owner
  account has no Key Vault data-plane role (by design), so secret metadata cannot be listed.
- Image variables (`WCE_DEV_WEB_IMAGE`, `WCE_DEV_WORKER_IMAGE`, `WCE_DEV_MIGRATION_JOB_IMAGE`,
  `WCE_DEV_BOOTSTRAP_JOB_IMAGE`) may stay unset: `eng/infra.ps1` adopts the live images (all four are the
  `fc9bb3e` images now). An explicitly set variable always wins.
- GitHub: private `matnael332/fully`; CD identity `id-wce-dev-neu-github` (AcrPush on the registry,
  Container Apps Contributor on the two apps only, no access to the jobs; seven repository variables, no secrets).
- Job start (only when approved): `az rest --method post --url "https://management.azure.com/subscriptions/c1830e72-d806-4cb1-8bff-8627fb01c5ea/resourceGroups/rg-wce-dev-neu/providers/Microsoft.App/jobs/<job>/start?api-version=2024-03-01"`;
  poll `.../executions?api-version=2024-03-01`; read the JSON result line from `ContainerAppConsoleLogs_CL`
  where `ContainerGroupName_s startswith '<execution name>'` (ingestion takes 1–5 minutes).

## Technical notes needed to continue safely

- CRLF working copies while git stores LF: `CLAUDE.md`, `README.md`, `docs/canonical/PROJECT-STATUS.md`,
  `infra/bicep/environment.bicep`, `containerRegistry.bicep`, `roleAssignments.bicep`, `serviceBus.bicep`,
  `Directory.Packages.props`, `docs/architecture/dependency-rules.md`, `eng/dev.ps1`,
  `eng/verify-local-env.py`, `tests/App.Architecture.Tests/DependencyBoundaryTests.cs`. Edit scripts must
  detect and preserve each file's line endings (check with `file`, do not assume).
- The tool's Git Bash has **no `jq`**; parse `az` JSON with `--query` (JMESPath) or Python. `az … -o tsv`
  output carries `\r` on Windows: pipe through `tr -d '\r'` before reusing a value (a trailing `\r` in a
  deployment name yields HTTP 400 "Bad Request"). KQL: `first` and `last` are reserved words.
- Through the PowerShell tool, `./eng/dev.ps1 up` reports failure because docker compose writes pull
  progress to stderr; judge the stack by `./eng/dev.ps1 check` and `docker ps`. If the Service Bus
  emulator exits with code 139 after a restart (SQL Server recovery race on a reused container), recreate
  only those two containers: `docker compose --env-file .env -f infra/local/docker-compose.yml up -d
  --force-recreate --no-deps mssql servicebus-emulator`. Never `docker … prune`.
- `dotnet test` under Microsoft.Testing.Platform: `dotnet test --project <csproj> -c Debug` or the
  solution path; never pass `--nologo`. Unit (949), Security (168) and Architecture (45 at `d1468bb`; 44 at `446fa5a`) run without Docker;
  the full suite (1,861 at `d1468bb`, with Integration 699; 1,783 at `446fa5a`, Integration 622)
  needs `./eng/dev.ps1 up`; the Service Bus emulator tests
  skip when `.env` has no `ACCEPT_EULA=Y`, as in CI. The shared local database holds stale, unrelayable outbox rows
  that make five relay-dependent harness tests fail there; full-suite evidence is taken on a temporary isolated
  database by setting `POSTGRES_DB` (see the offline report's addendum). Cleaning those rows is a separate task that
  needs its own authorization.
- The GitHub Ubuntu runner exports `ACCEPT_EULA=Y` globally; never treat a process-level licence flag as
  evidence that a local service exists.
- What-if against the deployed environment reports 16 "modify" noise entries (read-only property echoes,
  unresolved `reference()` expressions, write-only defaults) and 13 "unsupported" role assignments; read
  the property lines before treating any as real. Verified classification: `docs/gates/gate-1G-report.md`.
- Windows PowerShell 5.1 quirks: `ConvertFrom-Json` emits a JSON array as one object unless assigned to
  a variable first; `az role assignment list --all` returns lower-case `/resourcegroups/` scopes; a native
  command writing to stderr under `$ErrorActionPreference = 'Stop'` becomes a terminating error. Do not put
  `Remove-Item` or `cmd /c` in commands run through the tool sandbox.
- On this machine `bash` on PATH is the WSL relay; Git Bash is `C:\Program Files\Git\usr\bin\bash.exe`.
- The Bash tool halves backslashes inside commands, even in quoted heredocs. Write any script that
  contains backslashes (C# character literals, regular expressions, escapes) with the Write tool and run
  it from the file; verify the written bytes before trusting a check that also passed through the shell.
- Working preferences: commit messages ≤10 lines via `-m` flags, `Co-Authored-By` only, no
  `Claude-Session` trailer; never `docker … prune` or `git clean`; remove only resources this session
  created, by exact name.

## Useful commands

```powershell
./eng/dev.ps1 up | check | logs | down          # local stack (bootstrap → migrate → web/worker)
./eng/db.ps1 status | migrate | reset            # local database
./eng/infra.ps1 build | lint                     # Bicep, no Azure
./eng/infra.ps1 validate -Environment dev -SubscriptionId <id>   # read-only, needs az login; prints the image guard
./eng/infra.ps1 whatif   -Environment dev -SubscriptionId <id>   # read-only, needs az login
dotnet test WorkContinuityEngine.sln -c Debug    # needs local stack running
python eng/verify-architecture.py
python eng/verify-local-env.py
gh run list --workflow ci.yml --limit 5          # CI history
gh run list --workflow cd-dev.yml --limit 5      # CD history
```
