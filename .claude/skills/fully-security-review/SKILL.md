---
name: fully-security-review
description: User-invoked only, as /fully-security-review <scope>. Use when a planned FULLY change, a final diff or a gate boundary needs the FULLY-specific security review - tenant isolation and row-level security, OAuth 2.1 and PKCE, token and secret lifecycle, authority change, webhook intake, reconciliation, Snapshot and active work, logs, migrations, and failure or crash paths. It reports findings only. It never changes code, makes a real monday call, takes an Azure action, commits, pushes or starts Gate 3G.
disable-model-invocation: true
user-invocable: true
argument-hint: "<scope>"
---

# Security Review for FULLY

## Overview

This is the security review procedure for Fully gates. It reviews a planned change or a final diff for security
regressions against the rules Fully already has, and it cites the exact invariant, owner decision or implementation
boundary behind every check. It is not a generic checklist, and it decides nothing: CLAUDE.md, the six frozen
documents, `docs/gates/gate-2-scope.md` and `docs/gates/gate-3-scope.md` stay the authority. Where this skill and a
source disagree, the source wins and the drift is reported.

Review the change, not the intent. A finding names a file and line, the rule it breaks and a concrete failure path. A
change that would weaken a canonical rule is stopped and reported with the exact conflict, the rule and the smallest
safe alternatives (CLAUDE.md); it is never quietly fixed. The built-in `/security-review` stays available for a
generic pass; this skill is self-contained without it.

**Violating the letter of these rules is violating their spirit.**

## Authority and invocation

- Invoking the skill is never authorization. `<scope>` names what to review: a plan, the pending diff, a commit range,
  a sub-gate or named files. When it is empty or unclear, ask before reviewing.
- State when this skill was written (2026-10-03): Gate 3 IN PROGRESS; 3A to 3F and the PKCE verifier cleanup PASS;
  **3G NEXT / NOT STARTED**. Read the status table at the top of `docs/gates/gate-3-scope.md` before relying on it.
- The invocation authorizes reading the repository and reporting by the output contract. Report only: the review
  changes no file, stages nothing and fixes nothing. A remediation is described, never applied.
- It never authorizes, and a review never proposes doing without the owner's own approval:
  - any real monday call: the API, OAuth, webhook registration or a monday MCP tool, reads included;
  - Azure, Key Vault, RBAC, `eng/infra.ps1`, `CD Dev`, a job start, or applying a migration outside the local stack;
  - a commit or a push;
  - starting Gate 3G: implementing or scaffolding it in code, or any real-provider execution (decision A).
- This is not a full repository scan. Review the stated scope and the code paths it reaches; a wider scan is its own
  owner order.
- The local hooks `fully_approval_guard.py` and `fully_hygiene_check.py` guard commands, protected files and file
  hygiene. A deny is a stop, never a cue to rephrase. These rules hold in full when the hooks are absent.
- Never print a secret value. A finding about a secret names the file, the line and the kind of secret, and nothing of
  its value, not even a prefix, a suffix or a length.

## Owner decisions (2026-10-03)

The authority for these four decisions is `docs/gates/gate-3-scope.md`, "Owner decisions on record before 3G (recorded
2026-10-04)": item 1 below is its decision A, item 2 its decision C, item 3 its decision D and item 4 its decision B.
This skill is not their source: cite the record and apply it as written. The text under each number is this skill's
operational interpretation of that decision: guidance that bounds the rules above and below. Where it and the record
differ, the record wins and the drift is reported.

1. **3G planning boundary.** Planning or documenting the future Gate 3G host, harness, acceptance cases or first real
   monday run does not by itself start Gate 3G and is not a finding. Documentation and planning alone may occur while
   Gate 3G remains NOT STARTED. Gate 3G is started only when the owner explicitly authorizes it and work crosses into
   execution or implementation: a 3G-specific harness implementation; production-code changes made specifically for
   3G; real OAuth or provider interaction; webhook registration; a real monday API or MCP write or connector call; any
   other real-provider execution. A scope that does one of these without that authorization is reported as outside
   the approved gate (CLAUDE.md, scope discipline). A planned design is still reviewed against every domain: planning
   is allowed, a rejected design stays rejected.
2. **Tests-only scope.** A tests-only scope does not require a full security re-review of unchanged production code
   when the production baseline is unchanged. The review must still: confirm that the referenced production baseline
   (the commit or the hash manifest it names) has not changed, with read-only git; test the changed tests for vacuity;
   and re-check the specific security invariant when a changed test is material evidence for it. When the test is the
   only evidence for a high-risk security rule, perform a targeted re-check of that rule's production boundary. A
   baseline that changed, or that cannot be confirmed, makes the changed production code part of the scope.
3. **monday client id.** The monday OAuth client id is an identifier, not a secret, and is never treated as
   credential material. It may appear in structured logs only when operationally useful; by default it is not logged
   unnecessarily. Its presence in a log is not by itself a finding. The client secret, an access token, a refresh
   token, an authorization code, a PKCE verifier and a raw state value are never logged.
4. **Real monday call boundary.** Adding code or a design capable of calling a monday endpoint is not itself a real
   monday call. Execution is the boundary: any actual call from a development, test or runtime environment to a real
   monday OAuth, API, webhook or other provider endpoint requires the owner's explicit authorization. Before Gate 3G
   is authorized, the code and the design may exist; real provider execution may not occur. New calling code is still
   reviewed against every domain (fixed endpoints, read-only operations, access-time credentials).

## Scope first

1. Collect the change: for a diff, `git status`, `git diff`, `git diff --cached` and every new file; for a plan, the
   plan text and every file it names. Use read-only git only, with `--no-optional-locks`.
2. List the domains below that the change touches. A domain it does not touch is reported NO FINDING with the reason,
   never left out.
3. Run the reject table before anything else.
4. For each touched domain, walk its checks against the code, not against the description of the change. Read the
   boundary named in the check; a check answered from memory is not a check.
5. Inspect every security test the change adds, edits or relies on for vacuity.
6. Report by the output contract.

## Labels

Every statement about monday carries exactly one label, as `/monday-connector` defines them.

- **FACT**: monday behaviour that `docs/gates/gate-3-scope.md` records as verified, or an exact code constant.
- **PROJECT DECISION**: a Fully rule: an invariant, a Gate 2 decision, an owner decision such as D3E-1, a review
  decision such as R3B-2, or the code that implements one.
- **MUST CONFIRM IN 3G**: provider behaviour that is assumed, undocumented or only modelled by a test fake.

An unverified monday behaviour is never turned into a security fact, in either direction: it neither justifies
relaxing a guard nor counts as a finding against a change that fails closed. A forum answer, general knowledge of
monday and a fake's behaviour are not sources. The facts and the open items live in `/monday-connector`; do not
restate them from memory.

## Security domains

Each check: what to verify, the authority for it, and the boundary in the code with the proof that pins it.

### 1. Tenant isolation

| # | Verify | Authority | Boundary and proof |
|---|---|---|---|
| 1.1 | The tenant is resolved server-side; no route, query, header, body, form, token claim or provider payload names it | invariants 44 and 45 | `TenantContext.Resolved` takes a `TenantResolutionSource`, which has no caller-asserted value; `TenantTrustTests` |
| 1.2 | Only the ingress, the transport and retention assign a tenant context, each as `TrustedServiceIdentity`; no endpoint or helper calls `SetResolved` | invariant 45; Gate 2 decision 1 | `TenantTrustTests` |
| 1.3 | Where the server already knows the tenant, a tenant argument is compared with the trusted context and never trusted by itself; authentication of the caller does not replace that | invariants 46 and 48 | `ConnectorSourceScopes` answers `TenantMismatch`; `DatabaseCredentialTests` |
| 1.4 | Every database access runs through `ITenantScopedDatabase`, which sets `app.tenant_id` for the transaction only | invariant 52 | `TenantScopedDatabase`; `ConnectionPoolIsolationTests` |
| 1.5 | Every tenant-owned table is listed in `WorkContinuityModel.TenantOwnedTables` and has row-level security both enabled and forced, with its `tenant_isolation_{table}` policy carrying USING and WITH CHECK | invariants 44 and 59 | `TenantIsolationTests`, `TenantIsolationAcceptanceTests`, `PersistenceSecurityTests` |
| 1.6 | The runtime role `workcontinuity_app` owns nothing, is no superuser and cannot bypass row-level security; the schema owner is used for migrations only | CLAUDE.md; Gate 1D | `RuntimeRoleBootstrap`; `RuntimeRoleBootstrapTests` |
| 1.7 | No new tenant-less function, query or job. The approved exceptions are exactly `resolve_ingress_connection`, `outbox_pending_tenants` and `retention_expired_tenants`; each returns ids, status or counts only | Gate 2 decision 1; System Architecture v1 §14 | `IngressDatabaseSecurityTests` pins the exact allowlist and each definition |
| 1.8 | No cross-tenant path through a direct table, a view, a function, a reconciliation target, the Snapshot, the active-work views or a cleanup job: a view is `security_invoker`, a function is not a definer, a cleanup runs inside one trusted tenant | invariants 48, 52 and 59 | `InitialSyncDatabaseSecurityTests`, `ReconciliationTargetDatabaseSecurityTests`, `CurrentWorkViewTests` |
| 1.9 | Every message carries its tenant, the consumer re-establishes it, and no transaction spans tenants | invariants 1 and 52 | `TransportSecurityTests` |

### 2. OAuth 2.1 and PKCE

| # | Verify | Authority | Boundary and proof |
|---|---|---|---|
| 2.1 | The state is single-use: consumed atomically, so a replay or a concurrent callback loses | 3B Definition of Done | `IConnectorAuthorizationStore.ConsumeTransactionAsync`; `ConnectorAuthorizationStoreTests` |
| 2.2 | Only a hash of the state is stored (`state_hash`); the state itself is never stored or logged | D3B-2 | `MondayAuthorizationState`; `ConnectorAuthorizationSecurityTests` |
| 2.3 | The authorization code is exchanged at most once and the exchange is never retried | 3B Definition of Done | `MondayConnectionAuthorizer.CompleteAsync` |
| 2.4 | PKCE is `S256` only | 3B; `/monday-connector` (FACT) | `MondayPkce.ChallengeMethod`; `MondayOAuthPrimitivesTests` |
| 2.5 | The verifier lives only in secret storage, under any column name or encoding | D3B-2; invariant 53 | `IMondaySecretStore`, `MondaySecretName.ForVerifier` |
| 2.6 | A duplicate or replayed callback never deletes the winning completion's verifier: the `AlreadyConsumed` branch answers `Replayed` and touches nothing | 3B maintenance `273faa6` | `MondayConnectionAuthorizer.CompleteAsync`; `MondayConnectionAuthorizerTests` |
| 2.7 | Cleanup of a consumed transaction is crash-recoverable: each confirmed deletion is recorded durably (`verifier_deleted_at_utc`), and a completion whose process died leaves the transaction due | 3B maintenance `f92c94c` | `ListVerifierCleanupAsync`, `RecordVerifierDeletedAsync` |
| 2.8 | Verifier cleanup is idempotent, and a failed deletion stays due and is retried: the deletion is recorded only after the storage confirms it | 3B maintenance `f92c94c` | `DeleteConfirmedAsync`; `MondayConnectionLifecycleTests` |
| 2.9 | Cleanup never resurrects authorization state: it never changes whether a transaction was consumed and never reopens one | 3B maintenance `f92c94c` | `MondayConnectionAuthorizer.CleanUpVerifiersAsync` |
| 2.10 | The transaction stays bound to tenant, connection, user and state, and the grant to the delegated account identity; a mismatch fails closed (`BindingMismatch`, `StateMismatch`, `IdentityMismatch`) | D3B-1; invariants 19 and 47 | `ConnectorTransactionOutcome`, `ConnectorAuthorizationErrors` |
| 2.11 | An expired or consumed transaction can never be used again; the lifetime is at most 10 minutes | 3B Definition of Done | `ConnectorAuthorizationTransaction.MaxLifetime`, `CloseExpiredTransactionsAsync` |
| 2.12 | No anonymous production OAuth-start endpoint; real OAuth runs only through the owner-operated harness | R3B-1 | `docs/gates/gate-3-scope.md` |
| 2.13 | Cleanup runs only under a trusted tenant context, with no tenant-less cleanup or discovery function | 3G precondition (CLAUDE.md) | `MondayConnectionAuthorizer.CleanUpVerifiersAsync` answers `TenantUnresolved` |

### 3. Token and credential lifecycle

| # | Verify | Authority | Boundary and proof |
|---|---|---|---|
| 3.1 | Access tokens, refresh tokens, the client secret and the Signing Secret never enter an ordinary database field, an AI prompt, business state, an event, a log or a test snapshot | D3B-2; invariant 53; Premium §20 constraint 1 | `IMondaySecretStore`, `MondayTokenKeeper`; `ConnectorAuthorizationSecurityTests` |
| 3.2 | Refresh-token rotation is preserved: the latest refresh token replaces the old one, and an old one is never reused | 3B; `/monday-connector` (FACT) | `MondayConnectionCredentialSource` |
| 3.3 | Renewal stays single-flight and crash-safe: one claim per generation renews, and the others wait or read the result | 3B Definition of Done | `ClaimRefreshAsync`, `CompleteRefreshAsync`, `ReleaseRefreshAsync` |
| 3.4 | A stale credential is never accepted after a reauthorization: the generation and `authorized_at_utc` decide, never a cached token | invariant 58 | `MondayConnectionCredentialSource.AcquireAsync` |
| 3.5 | Every provider call records the authority it used | Premium §20 constraint 2 | `ConnectorCallResult` |
| 3.6 | Revocation is enforced at access time, before every attempt, not only at connection setup | invariant 58; Premium §20 constraint 7 | `MondayCredentialResult.Revoked`; `MondayConnectionCredentialSourceTests` |
| 3.7 | The runtime role keeps only SELECT, INSERT and UPDATE on the two OAuth tables; DELETE stays revoked, and a bootstrap rerun re-asserts and verifies that in the same approved step | R3B-2 | `ConnectorAuthorizationFreshDatabaseTests` |
| 3.8 | Only read scopes are granted; a grant holding a write scope is refused and revoked | D3C-1; Premium §20 constraint 5 | `MondayScopePolicy` |

### 4. Authority change and reauthorization

| # | Verify | Authority | Boundary and proof |
|---|---|---|---|
| 4.1 | A provider read started under an old authority is discarded when a reauthorization happens before it is applied | 3E final owner review | `SourceReconciler`: `StandingAsync` runs before the read and again inside the apply transaction, whatever the elapsed time |
| 4.2 | `AuthorityChanged` discards the read, applies nothing from it, and the next attempt reads again under the current authority | 3E final owner review | `SameAuthorizationAs` compares the subject and `authorized_at_utc`; `MondayReconciliationTests` |
| 4.3 | Stale API evidence never becomes `Confirmed` | D3E-1; invariant 22 | `SourceReconciler.ConfirmAsync` |
| 4.4 | A stale claim, target generation or sync generation writes nothing into current state | D3E-11; D3F-5 | `LockClaimedAsync`; compare-and-swap on `generation` |
| 4.5 | An authorization or account identity mismatch fails closed and is held, with nothing remapped | D3E-8; D3B-1 | `IdentityMismatch`; `ISourceVerifier.ConfirmAsync` |
| 4.6 | A sync generation captures its authority at start; a token renewal changes nothing and a reauthorization restarts it | D3F-5 | `InitialSyncEngine`; `InitialSyncRecoveryTests` |

### 5. Webhook security

| # | Verify | Label | Authority | Boundary and proof |
|---|---|---|---|---|
| 5.1 | The delivery is authenticated before any provider evidence is accepted, and the account is bound to the 3B connection before any durable write | PROJECT DECISION | 3C Definition of Done; invariant 13 | `MondayWebhookAuthenticator`, `MondayWebhookVerifier`, `ISourceVerifier.ConfirmAsync` |
| 5.2 | The challenge is answered only for an active monday connection; it is never stored, never reaches ingress and grants nothing | PROJECT DECISION | 3C Definition of Done | `MondayWebhookEndpoint`, `IIngressConnectionCheck` |
| 5.3 | Payload values stay untrusted hints: the token authenticates the sender, not the fields | PROJECT DECISION | D3D-1; invariant 25 | `MondayWebhookNormalizer` declares `Unconfirmed` |
| 5.4 | Webhook data never becomes `Confirmed` directly; only the authenticated API reread confirms | PROJECT DECISION | D3D-1; D3E-1; invariants 13 and 17 | `SourceValueConfirmation`; `UnconfirmedSourceValueTests` |
| 5.5 | A replay or duplicate delivery stays idempotent | PROJECT DECISION | invariant 64 | Gate 2 deduplication; `MondayWebhookSecurityTests` |
| 5.6 | Ordering is never assumed from arrival, a UUID or receipt time | PROJECT DECISION | invariants 12 and 18; D3F-2 | `EventProcessor.DecideAndProjectAsync` |
| 5.7 | The raw payload is no shadow source of truth: raw receipts are a buffer and provenance under the 30-day retention | PROJECT DECISION | Gate 2 decision 5; invariant 55 | `RetentionSecurityTests` |
| 5.8 | The tenant comes from the connection, never from the route, the body or the token | PROJECT DECISION | invariant 45 | `EventIngressService`; `MondayWebhookIntakeSecurityTests` |
| 5.9 | monday signs deliveries with the app's Signing Secret and sends a challenge when a webhook is created; ordering, duplicates and idempotency are not documented | FACT | `/monday-connector` | `docs/gates/gate-3-scope.md` |
| 5.10 | HS256 as the token algorithm, the bare or `Bearer` header forms, the `aud` form, whether the challenge carries a token, and the token lifetime | MUST CONFIRM IN 3G | 3C compatibility assumptions | Fail closed today: an exact HS256 allowlist, expiry and a per-connection audience are enforced |
| 5.11 | No webhook registration, no `webhooks:write` and no monday write | PROJECT DECISION | D3C-1 | `MondayQueryGuard` |

### 6. Reconciliation security

| # | Verify | Authority | Boundary and proof |
|---|---|---|---|
| 6.1 | A target binds the tenant, the source, the connection, the object, the field, the scope and the generation: `tenant_id`, `object_system`, `object_connection`, `object_type`, `object_id`, `field`, `source_scope`, `generation` | D3E-11; D3E-10 | `reconciliation_targets`; `ReconciliationTargetTests` |
| 6.2 | No work value, display label, raw provider response, credential or secret is stored in the target | D3E-11; invariant 55 | `ReconciliationTargetRecord`; `ReconciliationSecurityTests` |
| 6.3 | A retry path never bypasses the access and authority checks: access is rechecked locally, with no provider call while it does not stand | 3E Definition of Done; invariant 58 | `AwaitAccess` |
| 6.4 | A held or retried target neither leaks nor confirms stale data: resolution is a compare-and-swap on the generation, and a stale worker records nothing | D3E-11 | `ReconciliationTargets`; `claimed_generation`, `resolved_generation` |
| 6.5 | A transient failure never silently becomes success: a credential, provider or rate-limit failure is recorded as its own outcome (`CredentialUnavailable`, `ProviderUnavailable`, `RateLimited`) and never as `Current` or `Repaired` | 3E Definition of Done; invariant 10 | `ReconciliationOutcomes` |
| 6.6 | Missing and unsupported semantic states follow the fixed policy: `ObjectMissing` and `UnsupportedResponse` are bounded, semantic cases are held, absence is never deletion | D3E-9 | `ReconciliationOutcomes`; `ReconciliationContractTests` |
| 6.7 | The result is applied through processing, never by monday-specific Snapshot SQL, and the work is resolved only after that commit | D3E-6; D3E-7 | `SourceReconciler.ConfirmAsync` |
| 6.8 | The table keeps forced row-level security, its restrictive no-delete policy and SELECT, INSERT and UPDATE only | D3E-11 | `ReconciliationTargetDatabaseSecurityTests` |

### 7. Active work and Snapshot security

| # | Verify | Authority | Boundary and proof |
|---|---|---|---|
| 7.1 | The raw Snapshot stays provenance and internal state; only the projection and its persistence name it | 3F final owner review; invariant 14 | `CurrentWorkViewTests` |
| 7.2 | Current active work is read only through `active_snapshot_fields` and `active_snapshot_objects` | 3F final owner review | both views are `security_invoker`; `InitialSyncDatabaseSecurityTests` |
| 7.3 | A disabled SourceScope leaves current work at once: `baseline_generation` is cleared in the disabling transaction | 3F final owner review | `ConnectorSourceScopeRecord.SetEnabled`; `InitialSyncActiveStateTests` |
| 7.4 | A scope enabled again is current only after a new authoritative baseline reaches Live; no time window, cache or flag restores it sooner | 3F final owner review; D3F-6 | `awaiting_baseline_version`, `ReconciliationTargets.AwaitBaselineAsync` |
| 7.5 | Historical and audit evidence may stay retained without becoming active state: Snapshot rows, the ledger, receipts and targets are kept and are not current work | 3F final owner review; invariant 55 | `InitialSyncActiveStateTests` |
| 7.6 | A future Work Memory, brief or UI reader cannot read the raw Snapshot as current work | 3F final owner review; invariant 2 | the architecture guard `CurrentWorkViewTests` |
| 7.7 | Every change of the enabled scope set invalidates Live, and no connection is partly Live | D3F-15 | `connection_syncs.scope_version`; `InitialSyncConfigurationTests` |

### 8. Secret and log hygiene

Look for any client secret, Signing Secret, PKCE verifier, authorization code, state, access token, refresh token or
other provider credential material, whole or in part, in each place below.

| # | Place | Authority | Boundary and proof |
|---|---|---|---|
| 8.1 | Structured logs and their arguments: ids, reason codes and counts only; no payload body, field value, actor name, token or response body | invariant 53; Gate 2 decision 6 | leakage tests capture every log category (`CapturingLoggerProvider`) |
| 8.2 | Exception messages and client errors: fixed text; no exception detail reaches a client | invariant 53 | `PlatformExceptionMiddleware` |
| 8.3 | Tracing and telemetry attributes: a closed set of tag keys, with no tenant, connection, object or payload value | invariants 52 and 53 | `EventBackboneMetrics.TagKeys` |
| 8.4 | Test output, fixtures and snapshots: nothing realistic-looking; the secret scan reads the whole tree | invariant 53 | gitleaks in `ci.yml`; `fully_hygiene_check.py` |
| 8.5 | Migrations | invariant 53 | `DatabaseCredentialTests` |
| 8.6 | Database text, JSON and binary columns, under any name | D3B-2; invariant 53 | `ConnectorAuthorizationSecurityTests` |
| 8.7 | Documents, reports and review output produced from runtime values | invariant 53; Premium §20 constraint 1 | this review prints no secret value |

A partial value is a value: the last characters of a token, a hash of a low-entropy secret and a provider error body
that may echo a credential are all findings. A log level that is off in production is still a finding. The monday
OAuth client id is an identifier, not credential material (decision D).

### 9. Database and migration security

| # | Verify | Authority | Boundary and proof |
|---|---|---|---|
| 9.1 | Row-level security still applies after the migration: enabled and forced, the tenant policy intact, no policy dropped or made permissive | invariants 44 and 59 | `TenantIsolationAcceptanceTests`; `PersistenceSecurityTests` |
| 9.2 | No new `SECURITY DEFINER` function, and none modelled on an approved one, without the owner's explicit approval | Gate 2 decision 1; System Architecture v1 §14 | `IngressDatabaseSecurityTests` |
| 9.3 | Every function fixes its `search_path` to `pg_catalog, pg_temp` and schema-qualifies what it uses; a definer function runs as the migrating owner, which forced row-level security binds too | Gate 2 decision 1 | the three approved functions' migrations |
| 9.4 | Runtime grants are least privilege: revoke all, then grant exactly what the code needs, column-level where possible; never DELETE or ALL by default | R3B-2; D3E-11 | the fresh-database security tests |
| 9.5 | The migration introduces no PASSWORD, SECRET, CREATE ROLE or CREATE USER, unless an explicitly approved architecture change requires it | invariant 53 | `DatabaseCredentialTests`; `InitialSyncSecurityTests` |
| 9.6 | Migration security tests are not vacuous: they assert the catalog state of the migrated database, not only the migration text | this skill, Security test quality | `ConnectorAuthorizationFreshDatabaseTests` |
| 9.7 | Schema, grant, policy, function and view changes are proven on a fresh database, because the shared test database reruns the runtime-role bootstrap | `/mutation-evidence` section 6 | `InitialSyncDatabaseSecurityTests` |
| 9.8 | Persistence stays in `App.Infrastructure`; `App.Core` has no package and no persistence | CLAUDE.md | `DependencyBoundaryTests` |
| 9.9 | Applying a migration outside the local stack, a job start and a bootstrap rerun are owner approval boundaries | CLAUDE.md | `eng/infra.ps1` |

### 10. Failure, retry and crash security

Walk each failure through the changed path and state what the system holds afterwards.

| Failure | Must stay true | Authority |
|---|---|---|
| Process crash | Every step is restartable and idempotent; nothing half-applied becomes current; a consumed OAuth transaction's verifier is still cleaned up | D3F-13; 3B maintenance `f92c94c` |
| Duplicate delivery | Applied once; a duplicate callback loses and deletes nothing | invariant 64; 3B maintenance `273faa6` |
| Partial commit | The outcome and its target are written in one transaction; success is answered only after the durable commit | D3E-11; 3C Definition of Done |
| Secret-store failure | No token, code or verifier falls back to another store; a failed deletion stays due | D3B-2 |
| Provider outage | Retried on a capped backoff; never held as a semantic failure and never success | 3E Definition of Done |
| Stale lease or claim | A worker whose claim lapsed records nothing | D3E-11; D3F-4 |
| Reauthorization | The read in flight is discarded and retried; the sync generation restarts | D3F-5 |
| Retry exhaustion | Bounded cases are held and visible; exhaustion never resolves, confirms or deletes | D3E-9 |

A failure must never broaden authority, reuse a code, confirm stale provider data, bypass tenant isolation, leak a
secret, expose disabled work or create an unauthorized external action (invariant 10).

## Reject on sight

Run this table first. A match is a finding at the severity shown and makes the verdict NOT READY FOR OWNER REVIEW.
The mitigations a proposal offers (an internal route, a static key, encryption at rest, a short window, a Debug level,
a later gate) do not change the verdict.

| Design | Verdict and rule | Severity | Do instead |
|---|---|---|---|
| A tenant id taken from a client request, route, header, body or token and trusted, on any route, internal ones included | REJECT. invariants 45 and 46; `TenantTrustTests` | BLOCKER | Resolve the tenant server-side from the authenticated principal or the connection |
| Disabling, omitting or dropping FORCE ROW LEVEL SECURITY, or the tenant policy, on a tenant-owned table | REJECT. invariants 44 and 59; Gate 1D | BLOCKER | Enable and force row-level security with the `tenant_isolation_{table}` policy and list the table in `WorkContinuityModel.TenantOwnedTables` |
| The runtime role owning a tenant table, or made a superuser or a row-level-security bypass role | REJECT. CLAUDE.md; Gate 1D | BLOCKER | Keep `workcontinuity_app` a non-owner with exact grants |
| A tenant-less helper, function, query, sweep, cleanup or tenant-discovery added for convenience, even one modelled exactly on an approved function | REJECT. Gate 2 decision 1 approves three operations only; the 3G precondition forbids a tenant-less cleanup | BLOCKER | Stay inside one trusted tenant; a new tenant-less operation needs the owner's explicit approval first |
| A refresh token, access token or client secret written to an ordinary database field | REJECT. D3B-2; invariant 53 | BLOCKER | Secret storage only (`IMondaySecretStore`); ids, hashes and versions in the database |
| The PKCE verifier stored in the transaction table or any other column, under any name | REJECT. D3B-2; invariant 53 | BLOCKER | Keep it in secret storage and keep the durable cleanup |
| A duplicate or replayed callback deleting the verifier | REJECT. 3B maintenance `273faa6`: the winning completion may still need it | BLOCKER | Leave the replay branch untouched; cleanup removes the verifier |
| A provider response accepted after a reauthorization, or the authority recheck in the apply transaction skipped, cached or time-boxed | REJECT. `AuthorityChanged`; D3F-5; invariant 58 | BLOCKER | Recheck inside the apply transaction, discard the read and retry under the current authority |
| A webhook payload value marked `Confirmed`, or reaching the Snapshot, because its token verified | REJECT. D3D-1; D3E-1; invariants 13 and 17 | BLOCKER | Keep it `Unconfirmed`; the API reread confirms |
| The raw Snapshot read as current work | REJECT. 3F final owner review; `CurrentWorkViewTests` | BLOCKER | Read `active_snapshot_fields` and `active_snapshot_objects` |
| A disabled SourceScope still visible as current work, for any grace period, or re-enabled without a new baseline | REJECT. 3F final owner review; D3F-6 | BLOCKER | Hide it in the disabling transaction; show it again only after a new baseline reaches Live |
| A provider credential, token, code, verifier, state, secret or response body written to a log, an exception, a trace or test output, whole or in part | REJECT. invariant 53; Gate 2 decision 6; D3B-2 | BLOCKER | Log ids, reason codes and counts only |
| A reconciliation target storing a provider work value, label or raw response | REJECT. D3E-11; invariant 55 | BLOCKER | Store identity, scope, generation and outcome only |
| A retry branch treating a credential, access or provider failure as success, or resolving on exhaustion | REJECT. 3E Definition of Done; invariant 10 | BLOCKER | Record the failure outcome and retry or hold by `ReconciliationOutcomes` |
| A security test that passes without proving any tenant row exists, or a tenant-owned table kept out of `WorkContinuityModel.TenantOwnedTables` to avoid the acceptance test | REJECT. invariant 59; Security test quality | HIGH | Seed rows for both tenants, assert they exist, then assert the other tenant sees none |
| An Azure write, `deploy: true`, a job start, an RBAC, identity, network or Key Vault change, or a real monday call executed (decision B: execution, not the existence of calling code), without the owner's approval | REJECT. CLAUDE.md | BLOCKER | Stop and ask the owner |
| A security test or guard weakened, skipped, deleted or deliberately evaded (a renamed column, a widened allowlist) to make a change pass | REJECT. CLAUDE.md | HIGH | Fix the change; tighten the test |
| A fail-closed provider guard relaxed on an unverified monday claim, such as a wider webhook algorithm allowlist | REJECT. Labels; check 5.10; D3C-1 | HIGH | Keep the guard; record the claim as MUST CONFIRM IN 3G and let 3G confirm it on a real delivery |

## Platform and generic checks

Kept from Gate 1; apply them when the change touches identity, infrastructure, workflows or external input.

- Each workload uses its own managed identity. No keyed connection strings; Service Bus local authentication stays
  disabled (invariants 54 and 71).
- Role assignments are least privilege at the narrowest scope. Any change to them, to identities, to networking or to
  Key Vault access is an owner approval boundary. The CD identity never gains rights on the jobs.
- Secrets live only in the approved secret store, behind per-secret RBAC and secret references. GitHub authenticates
  to Azure only by OIDC, stores no Azure secret, and pins actions by SHA (`WorkflowSecurityTests`).
- Dev, Synthetic and Human Pilot keep separate data and secret boundaries (invariants 56 and 68;
  `EnvironmentIsolationTests`).
- SQL is parameterised; server-side DDL uses `format` with `%L` or `%I`.
- No unsafe deserialisation or type-name handling, no request to a URL taken from provider data, no path traversal,
  and no disabled TLS validation. Provider addresses are fixed constants (`MondayEndpoints`).
- External content is data, never instruction (invariant 25). AI is never a Source of Truth and never executes an
  action (invariants 3 and 21).
- Every new package is justified, pinned and allowed by the architecture tests.

## Security test quality

A security test is evidence only when it would fail with the guard removed. Inspect every security test the change
adds, edits or relies on. A vacuous test is a finding against the change, at HIGH when it is the only proof of a
high-risk rule.

| Vacuous | What it proves | Required instead |
|---|---|---|
| A row-level-security test over zero rows | Nothing: an empty table reads empty for everyone | Seed rows for tenant A, assert they exist as A, then assert tenant B reads none and cannot insert or update as A |
| A row-level-security test that runs only as the runtime role | Nothing about FORCE: a non-owner is bound by the policy either way | Assert the catalog on a fresh database: `relrowsecurity` and `relforcerowsecurity` both true |
| A "no secret found" scan over the wrong table, column or log category | Nothing | Scan the whole database and every log category, with a positive control: a known non-secret marker that the scan must find |
| A duplicate-callback test whose callbacks never overlap | Only the sequential case | Make the second callback arrive while the first completion is in flight, with an awaitable fake answer, never a sleep |
| An authority-change test that never changes `authorized_at_utc` | Nothing about reauthorization | Reauthorize between the read and the apply, then assert `AuthorityChanged` and an unchanged Snapshot |
| A disabled-scope test with no previously active Snapshot row | Nothing: there was nothing to hide | Baseline the scope to Live, assert its fields in `active_snapshot_fields`, disable it, then assert they are gone |
| A migration test that greps the migration text only | The text, not the catalog | Assert the migrated catalog on a fresh database |
| A grant or policy test on the shared test database | Possibly the bootstrap's grants, not the migration's | A fresh-database test |
| A test run that executed zero tests | Nothing: `dotnet test` exit code 8 | Rerun with a filter that selects the tests; never combine `--filter-class` with `--filter-method` |
| A negative assertion with no positive control | Nothing | Pair every "nothing leaked" or "nothing visible" with proof that the thing existed |

- A new tenant-owned table is listed in `WorkContinuityModel.TenantOwnedTables`, so `TenantIsolationAcceptanceTests`
  attacks it from both tenants and requires rows of its own.
- A race is made deterministic with awaitable fakes and interceptors, never with sleeps or elapsed time.
- For a high-risk rule (tenant isolation, a secret boundary, replay, authority, confirmation, active work) recommend
  targeted mutation evidence: remove the guard and show the test fail. That procedure is `/mutation-evidence`.

## Review orchestration

The main agent reviews directly by default. Reviewer subagents are an option for a wide scope, never a default: do
not spawn all four automatically, use the minimum the scope needs, and use none for a search or a read the main agent
can do faster itself.

| Reviewer | Domains | Use it when the scope touches |
|---|---|---|
| Tenant and row-level security | 1, 9 | persistence, migrations, grants, policies, functions, views, tenant context |
| OAuth, PKCE, token and secret | 2, 3, 8 | the 3B lifecycle, secret storage, credential acquisition, logging of connector calls |
| Webhook, authority and reconciliation | 4, 5, 6, 10 | intake, normalization, rereads, retries, replay and crash paths |
| Snapshot, active work and vacuity | 7, Security test quality | the projection, the active views, source scopes, any changed security test |

- Reviewers are read-only. A reviewer never modifies a file, never calls monday, never touches Azure, never commits
  or pushes and never starts Gate 3G; its brief says so.
- A reviewer gets the scope, its domains from this skill and the files to read. It returns candidate findings with
  file, line, rule and failure path, and what it could not check.
- The main agent stays responsible. It resolves contradictions between reviewers, checks every candidate finding
  against the actual repository, rejects any finding the code does not support, sets the severity and the verdict,
  and writes the report. A reviewer's finding that the main agent has not verified is not reported as a finding.
- A reviewer's silence is not a NO FINDING: the main agent confirms what was actually read.

## Integration with other skills

Use each for its own procedure; none is repeated here.

- `/mutation-evidence`: targeted security mutants for a changed or new guard, with its verdicts, its restoration
  rules and its fresh-database rule for schema mutants. This review names the mutants a change needs (output part 7);
  it does not run them.
- `/gate-closure`: the final closure evidence, the full suite, staging, commit, push and CI. A security review is an
  input to a closure, never a closure and never an approval.
- `/monday-connector`: the verified monday facts, the Fully connector rules and the MUST CONFIRM IN 3G list. Take
  every provider claim and its label from there.

## Severity and verdict

| Severity | Meaning |
|---|---|
| BLOCKER | The change breaks a canonical safety rule, an invariant or an owner decision, with a concrete path: cross-tenant exposure, a credential outside secret storage, an untrusted or stale value confirmed, authority broadened, disabled work exposed, an approval boundary bypassed |
| HIGH | A guard is weakened, or the only proof of a high-risk rule is missing or vacuous, without a demonstrated exploit path yet |
| MEDIUM | A bounded loss of defence in depth: another guard still holds, and the finding names it |
| LOW | Hygiene or drift with no security effect by itself |
| NO FINDING | The domain was checked; the report says what was read |

Do not inflate severity. A BLOCKER or HIGH needs the failure path traced in the code or the plan under review. A
suspicion that cannot be traced goes under residual unknowns, not under findings. A MUST CONFIRM IN 3G item is never
by itself a finding against a change that fails closed. Do not deflate either: a reject-table match keeps its
severity whatever mitigations the proposal lists.

| Verdict | When |
|---|---|
| PASS | No BLOCKER, HIGH or MEDIUM finding; every touched domain checked; nothing owed |
| PASS WITH FOLLOW-UP | No BLOCKER or HIGH finding; MEDIUM or LOW findings, required evidence or MUST CONFIRM IN 3G items are listed with an owner for each |
| NOT READY FOR OWNER REVIEW | Any BLOCKER or HIGH finding, any reject-table match, or a touched domain that could not be checked |

- When several rows match one change, report each finding; the highest severity decides the verdict.
- For a plan, evidence the plan itself commits to (named tests, a fresh database, mutation evidence) is not owed and
  does not prevent PASS; evidence this review requires and the plan lacks makes it PASS WITH FOLLOW-UP.
- An owner decision cited by a plan covers only what it states. Approval of a neighbouring thing is not approval of
  this one; when in doubt the answer is NOT READY FOR OWNER REVIEW and the question goes to the owner.

A verdict is the review's result, not an approval: only the product owner approves a gate.

## Output contract

```
FULLY SECURITY REVIEW - <scope in a few words>
1. Scope reviewed: <plan or diff, files, commit range; domains touched; what was not reviewed>
2. Findings: BLOCKER <n> | HIGH <n> | MEDIUM <n> | LOW <n>; NO FINDING: <domains checked clean, with what was read>
   For each finding, most severe first:
   [<severity>] <title>
3. Rule: <the exact invariant, decision id or implementation boundary>
4. Path: <file:line, or the plan step>
5. Failure: <the concrete exploit or failure path>
6. Remediation: <the smallest safe change; described, not applied>
7. Evidence required: <targeted tests; mutation evidence for a changed guard; fresh database where schema changes>
8. Residual unknowns: <what could not be traced; MUST CONFIRM IN 3G items> | none
9. Verdict: PASS | PASS WITH FOLLOW-UP | NOT READY FOR OWNER REVIEW
```

- Parts 3 to 7 repeat for every finding. Parts 1, 2, 8 and 9 appear once.
- Label every statement about monday FACT, PROJECT DECISION or MUST CONFIRM IN 3G.
- Quote no secret value anywhere in the report.

## Rationalizations

| Excuse | Reality |
|---|---|
| "The route is internal and has its own key" | Authentication is never authorization (invariant 46); the tenant is still resolved server-side |
| "It follows the approved function exactly" | Gate 2 decision 1 approved three operations, not a pattern; a fourth is an owner decision |
| "The column is encrypted at rest and has forced row-level security" | An ordinary database field is not secret storage (invariant 53; D3B-2) |
| "Deleting is idempotent, so deleting on replay is safe" | The winner may not have read the verifier yet (`273faa6`) |
| "The check ran a moment ago" | A reauthorization can land in that moment; the recheck belongs inside the apply transaction |
| "The token verified, so the value is trustworthy" | The token authenticates the sender, not the fields (D3D-1) |
| "It is only visible for a short while" | A disabled scope leaves current work at once |
| "Debug logging is off in production" | The value is still written wherever Debug is on |
| "The test is green" | Green with zero rows, zero overlap or zero tests is no evidence |
| "ENABLE is enough; FORCE is a detail" | Without FORCE the table owner bypasses the policy |
| "monday surely behaves this way" | Unverified provider behaviour is MUST CONFIRM IN 3G, never a fact |
| "I will just fix it while I am here" | The review reports; a fix is its own authorized change |

## Never

- Change, stage, commit or push anything during a review, or apply a remediation.
- Call real monday (the API, OAuth, webhook registration or a monday MCP tool), touch Azure, or start Gate 3G by
  implementing or scaffolding it in code.
- Report planning or documentation of Gate 3G, calling code that was never executed against real monday, or a logged
  client id as a finding by itself (decisions A, D and B).
- Print a secret value, whole or in part, or copy one into the report.
- State a MUST CONFIRM IN 3G item, a compatibility assumption or a fake's behaviour as a FACT.
- Report a reviewer's finding that the main agent has not verified against the repository.
- Inflate a severity without a traced failure path, or lower one because of a mitigation the reject table excludes.
- Weaken, skip or delete a test or a guard, or accept a vacuous test as proof.
- Run a full repository scan, or widen the scope, without the owner's order.
