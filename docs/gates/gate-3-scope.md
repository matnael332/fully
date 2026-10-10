# Gate 3 Scope — monday Connector

## Status

| Item | Status |
|---|---|
| Gate 3 — monday Connector | **AUTHORIZED 2026-09-29, IN PROGRESS** |
| 3A — Connector contract and monday client foundation | **PASS** — approved by the product owner on 2026-09-30 |
| 3B — OAuth 2.1 connection lifecycle | **PASS** — approved by the product owner on 2026-09-30 |
| 3C — Webhook intake | **PASS** — approved by the product owner on 2026-09-30 |
| 3D — Source mapping and normalization | **PASS** — approved by the product owner on 2026-10-01 |
| 3E — API reconciliation and authoritative reread | **PASS** — approved by the product owner on 2026-10-02 |
| 3F — Initial sync, watermark and buffered replay | **PASS** — approved by the product owner on 2026-10-03 |
| 3B maintenance before 3G — PKCE verifier cleanup | **PASS** — approved by the product owner on 2026-10-03 |
| 3G — Real monday end-to-end acceptance and Gate closure | **IN PROGRESS — OFFLINE IMPLEMENTATION ONLY** (authorized by the product owner on 2026-10-05); real execution NOT AUTHORIZED; Gate 3 closure NOT AUTHORIZED; real acceptance NOT PROVEN; Stage B decisions B-1 (authoring only), B-7, B-10, B-11, B-12, B-13, B-14 and B-15 APPROVED 2026-10-06, B-2, B-3, B-4, B-8 and B-9 APPROVED 2026-10-08 (authoring approvals only), all other B decisions PROPOSED — NOT APPROVED; Stage B2 (real transport and token source) **PASS — OFFLINE SLICE COMPLETE** (approved by the product owner on 2026-10-06; checkpoint `d1468bb`, CI run 37434239946 SUCCESS), four BEFORE-WIRING requirements open, no live wiring, Stage B3 (standalone database and transport preparation) **PASS — OFFLINE SLICE COMPLETE** (approved by the product owner on 2026-10-06; checkpoint `736db05`, exact-commit CI run 37509953860 SUCCESS: 1,945 tests, 1,932 passed, 0 failed, 13 expected Service Bus emulator skips; the 14 owner-excluded role-bootstrap tests executed and passed in CI, none skipped) under B-14 and B-15, with the B-14 seed clarification (the seed prepares only one fresh tenant and one fresh canonical user), the owner-run tool structure (`eng/harness-standalone.ps1`), one temporary isolated validation database, the owner's six review decisions (one-tenant isolation evidence for Stage B3 only, multi-tenant acceptance OPEN for Gate 3G) and the temporary-relation clarification (the TEMPORARY privilege inherited through PUBLIC a NON-BLOCKING FOLLOW-UP; revoking it a separate owner decision outside Stage B3), all APPROVED 2026-10-06; no further work unless separately authorized (the temporary database was used for validation only and dropped; persistent standalone database NOT created; seed NOT RUN; `HarnessStandaloneGate` empty; no live run); Stage B4 (operator identity and loopback TLS) **PASS — OFFLINE SLICE COMPLETE** (approved by the product owner on 2026-10-07; checkpoint `ba6a086`, exact-commit CI run 37638185897 SUCCESS: 1,962 tests, 1,949 passed, 0 failed, 13 expected Service Bus emulator skips; all 14 role-altering tests and the Stage B4 tests executed and passed in CI) under B-12 and B-13, authorized by the product owner on 2026-10-06 in a narrowed scope (no Stage B2 seam wiring, no BEFORE-WIRING work, `HarnessStandaloneGate` empty; fixed callback port `47443`), with the per-user key-container file of the ephemeral certificate accepted on 2026-10-07 as satisfying B-13 and the orphan-file residual recorded, and exact-commit CI after the checkpoint as the clean verification, no temporary database; no further work unless separately authorized; Stage B1 (IaC authoring) **PASS — OFFLINE SLICE COMPLETE** (approved by the product owner on 2026-10-10; checkpoint `310e3ff`, exact-commit CI run 38044980337 SUCCESS: 2,050 tests, 2,037 passed, 0 failed, 13 expected Service Bus emulator skips; Bicep build and lint passed on Linux) under B-2, B-3, B-4, B-8 and B-9 (authorized 2026-10-08; authoring only: nothing created, deployed or contacted), with the owner review decisions of 2026-10-10 (one Fully tenant per harness run the current operating rule, multi-tenant acceptance OPEN, B-18 OPEN); no further offline work unless separately authorized; every other Stage B slice not authorized or started; precondition kept: its host runs the PKCE verifier cleanup at start-up and every 5 minutes |
| Premium Gates P1 to P11 | Not started; not authorized |

Baseline: `main` == `origin/main` == `999d2f9` (the Premium Gate P0 closure), working tree clean, the frozen Core
documents and the frozen Premium documents byte-identical to their recorded hashes.

Gate 3 builds the real monday source pieces that Gate 2 deliberately left out: the production source verifier, the
production normalizer, the connector lifecycle, the webhook adapter, the reconciliation reader and the initial-sync
path. It plugs into the Event Backbone of Gate 2 and rebuilds none of it. It implements no Premium capability, but
its connector contract follows the constraints of Premium architecture §20.

## Canonical scope

Development Gates v1: **build real authentication, webhooks, source mapping, API reconciliation and initial sync.
Acceptance: a real monday change reaches canonical event and Snapshot end-to-end; reconciliation repairs missed
event scenarios.**

| Item | Requirement | Source |
|---|---|---|
| Connector adapter | One adapter per SaaS: authenticates with the provider, receives webhooks, fetches current state for reconciliation, maps provider objects and events to canonical ones, and leaks no provider semantics into Core | System Architecture v1 §2; PRD v1 §10 |
| Two paths | Webhooks are the fast path; provider API reconciliation is the correctness path. They stay separate mechanisms | System Architecture v1 §2; invariant 17 |
| Initial connection | Establish connector → activate or buffer realtime intake → read current authorized source state → build initial Snapshot → record sync watermark → replay buffered newer events → normal realtime mode | System Architecture v1 §2 |
| Ingress | Source verification, server-side tenant resolution and a durable receipt before acknowledgement, through the Gate 2 ingress | System Architecture v1 §3, §9; invariants 1, 45 |
| Ordering and dedupe | Provider revision, then source update time, then occurrence time; ambiguity is not overwritten but reconciled; stable provider event id per tenant and connection | System Architecture v1 §3; invariants 12, 16, 18, 64 |
| Identity | User identity mapping is never guessed | invariant 19; Gate 2 decision 7 |
| Secrets | Secrets only in the secret store, never in code, ordinary database fields or logs; SaaS and device credentials are separate trust domains | PRD v1 §17; invariants 53, 54 |
| Revocation | Permission revocation takes effect at access time, not only at ingestion | invariant 58 |
| Untrusted content | External content is data, never instruction | invariant 25 |
| Testing | The deterministic simulator and the real monday integration are tested separately; the real connector passes end-to-end testing before the Human Pilot | invariants 74, 82 |

**Out of scope:** Work Memory and AI (Gate 4); UI (Gate 5); actions, the Action Engine and the Windows Agent
(Gate 6); SSO and authorization hardening (Gate 7); every Premium Gate; and any monday write or mutation feature.
Gate 3 reads and ingests only.

## Reuse of the Event Backbone

Gate 3 plugs into what Gate 2 built and rebuilds none of it: the canonical event contract, tenant resolution through
`resolve_ingress_connection`, durable ingress (`IEventIngress`, `ISourceVerifier`), the inbox and outbox, the Service
Bus transport, the processing ledger and ordering engine (`ISourceEventNormalizer`, `NormalizedSourceEvent`), the
Snapshot, retention and tenant isolation. Gate 3 supplies the monday implementations of the source-specific ports Gate
2 left unregistered in production: `ISourceVerifier` (3C) and `ISourceEventNormalizer` (3D), plus the connector
lifecycle, reconciliation and initial sync.

## Constraints from Premium architecture §20

Gate 3 implements no Agent, but the connector contract must let Premium extend it without rebuilding it.

| # | Constraint | Where Gate 3 meets it |
|---|---|---|
| 1 | Credentials stay inside the connector and security layer; never in Core events, AI, prompts, Agents or logs | 3A: no credential-bearing type in Core; an opaque, redacted monday credential acquired per call. 3B: secret storage |
| 2 | Every provider call records whose authority it uses: an explicit Service Identity or a named user's delegation | 3A: a connector identity bound to tenant and connection, returned with every call result. 3B: established at connection time, never invented |
| 3 | Operations are typed and declared: purpose, input and output schema, action type, risk, resource scope, side-effect and recovery classification, timeout, retry policy | 3A: every operation has a complete, validated descriptor |
| 4 | Reconciliation exposes an authoritative per-object reread with the provider's revision where one exists | 3A: the typed per-item reread. 3E: reconciliation on top of it. monday exposes no item revision; `updated_at` is its source update time |
| 5 | Gate 3 is read and ingest only; no write because the API allows one | 3A: only read operations exist; the client refuses any GraphQL document that is not a query |
| 6 | Provider text is untrusted data | 3A: provider data types never render content. 3D: trust classification in normalization |
| 7 | Disabling a connection or revoking a token takes effect at the next call | 3A: the credential is acquired before every attempt and fails closed. 3B: the real revocation path |

## Carried forward from Gate 2

| # | Decision or deferred item | Resolved in |
|---|---|---|
| 1 | Reconciliation is Gate 3 work; events recorded as `ReconciliationRequired` wait for it | 3E |
| 2 | Duplicate candidates without a stable provider event id are never silently discarded; reconciliation decides current truth | 3D (provider event id), 3E |
| 3 | Ambiguous, equal or incomparable ordering does not overwrite the Snapshot; reconciliation is the correctness mechanism | 3E |
| 4 | The 5-minute source clock-skew tolerance (`EventProcessingOptions.ClockSkewTolerance`) must be validated against real monday behavior; it is not changed silently | 3E, confirmed in 3G |
| 5 | `EventText.MaxValueLength` (1,024 characters) must be validated against real monday data; widening is an explicit proposal, never silent truncation | 3D, confirmed in 3G |
| 6 | Unmapped actors stay unresolved; employee identity is never guessed from display names or email | 3D |
| 7 | Azure Dev has none of the Gate 2B to 2F migrations; Azure is not Gate-3-ready | Owner boundary |
| 8 | The Event Backbone is not activated in Azure; runtime activation is a separate owner boundary | Owner boundary |

## monday platform baseline (verified 2026-09-29)

Verified against the official monday developer documentation only. No material contradiction with the baseline in
the Gate 3 authorization was found.

| Topic | Verified fact | Consequence |
|---|---|---|
| API versions ([versioning](https://developer.monday.com/api-reference/docs/api-versioning)) | `2026-07` is Current and the default; `2026-10` is the Release Candidate; `2026-04` is in maintenance. Quarterly releases; a version is stable for at least six months. Header `API-Version: YYYY-MM`; monday strongly encourages passing it on every call | Pin `2026-07` in one constant; send it on every request; upgrades are deliberate and tested |
| Endpoint and authentication ([authentication](https://developer.monday.com/api-reference/docs/authentication)) | `https://api.monday.com/v2`, JSON over POST; the token goes in the `Authorization` header, shown as the raw token | One fixed endpoint, never taken from configuration or provider data |
| OAuth 2.1 ([new flow](https://developer.monday.com/apps/docs/migrating-to-the-new-oauth-flow), [OAuth](https://developer.monday.com/apps/docs/oauth)) | Authorize `https://auth.monday.com/oauth2/authorize`; token `https://auth.monday.com/oauth_ms/oauth/token`; revoke `https://auth.monday.com/oauth_ms/oauth/revoke`. PKCE is required, `S256` only, 43–128 characters. Access tokens expire (`expires_in`, JWT `exp`; refresh when under 5 minutes remain). Each refresh returns a new refresh token: keep the latest, discard the old. The client secret is required. Opt-in through the app's "New OAuth Flow" setting. The legacy flow issues non-expiring tokens and no refresh token | 3B uses only the OAuth 2.1 flow. A personal API token is never the production architecture |
| Rate limits ([rate limits](https://developer.monday.com/api-reference/docs/rate-limits)) | Complexity (5M per query; per-minute budgets by token type; `ComplexityException`, `COMPLEXITY_BUDGET_EXHAUSTED`), daily calls (by plan, reset at midnight UTC; `DAILY_LIMIT_EXCEEDED`), minute rate (by plan; 429 with `Retry-After`), concurrency (by plan; `maxConcurrencyExceeded`), IP (5,000 per 10 s; `IP_RATE_LIMIT_EXCEEDED`). "Every rate limit error returns a `retry_in_seconds` field." `RateLimit` and `RateLimit-Policy` headers report remaining quota and reset seconds (`t`). Failed requests count too | Bounded, provider-aware retry that honours the provider's timing and never hammers; waits beyond a cap are returned to the caller instead of slept |
| Errors ([errors](https://developer.monday.com/api-reference/docs/errors)) | Application errors can return HTTP 200 with `errors[].extensions.code` and `status_code`; transport errors use 400, 401, 403, 404, 409, 422, 423, 429 and 500. Retryable: `API_TEMPORARILY_BLOCKED`, 409 `IDEMPOTENCY_CONFLICT`, 423 locked, the 429 family, 500. Scope failures: `missingRequiredPermissions` | A classifier maps status, codes and headers onto the stable connector taxonomy |
| Items ([items](https://developer.monday.com/api-reference/reference/items), [items_page](https://developer.monday.com/api-reference/reference/items-page), [column values](https://developer.monday.com/api-reference/reference/column-values-v2)) | `items(ids:)` returns at most 100 items, default `limit` 25; items expose `state` (active, archived, deleted), `created_at`, `updated_at`, `board`, `group`, `column_values { id type text value }`; **no revision or version field**. `items_page` returns up to 500 items with an opaque cursor; `next_items_page(cursor:)` continues. `text` may be null; `value` is raw JSON; no size limit is documented | The reread passes `limit` equal to the id count. `updated_at` is monday's ordering evidence (source update time), not a revision |
| Webhooks ([webhooks](https://developer.monday.com/api-reference/reference/webhooks)) | Verification challenge: echo `{"challenge": …}`. Requests of webhooks created with an app token carry a JWT in `Authorization`, verified against the app's Signing Secret. Payload fields include `triggerUuid`, `originalTriggerUuid`, `changedAt`, `triggerTime`, `userId`, `previousValue`. Failed deliveries retry once a minute for 30 minutes. Ordering and duplicate delivery are not documented. `create_webhook` needs `webhooks:write` | 3C verifies the JWT, handles the challenge, acknowledges only after the durable receipt, and treats ordering and duplicates as unguaranteed |

**Open verification items** (not contradictions; each is confirmed in the sub-gate named):

1. The JSON path of `retry_in_seconds` is not documented; the client reads it from the error extensions and their
   `error_data`, as well as the `Retry-After` and `RateLimit` headers (3A; confirmed in 3G).
2. The documentation shows the raw token in `Authorization`, while OAuth 2.1 token responses say `token_type: Bearer`;
   the client uses the documented form, in one place (3A; confirmed in 3B and 3G).
3. The claims of webhook JWTs and whether an OAuth-created webhook counts as created "with an app token" (3C).
4. The lifetime of an `items_page` cursor is not documented; an expired cursor restarts the read (3F).

## Sub-gates

The proposed decomposition holds after inspecting the repository. `App.Connectors.Monday` exists as an empty project
that may reference only `App.Core`, and `App.Infrastructure` may not reference a connector; so provider-neutral
contracts go into Core, monday specifics into the connector, and anything that needs both persistence and the
connector (the credential store of 3B, the lifecycle of 3F) is bridged in the composition roots, Web and Worker.

- **3A — Connector contract and monday client foundation.** Provider-neutral contract; typed read operations;
  explicit API version; GraphQL-over-HTTP client; error taxonomy; provider-aware bounded retry; identity metadata; a
  credential port; no live credentials.
- **3B — OAuth 2.1 connection lifecycle.** Authorization start and callback; state and PKCE S256; code exchange;
  expiry and refresh-token rotation; revocation; connection enable and disable; the secret-storage boundary; fail
  closed; no token in ordinary database fields or logs.
- **3C — Webhook intake.** Public endpoint; challenge; JWT verification per current documentation;
  connection-to-tenant resolution through the Gate 2 contract; provider event identity; acknowledge only after the
  durable receipt;
  replay and duplicate safety; no unauthenticated payload persistence.
- **3D — Source mapping and normalization.** The production monday normalizer; monday objects and changes mapped to
  generic Core objects and field changes; actors never guessed; source update times as evidence; untrusted content;
  schema versions; the 1,024-character value limit validated against representative monday data.
- **3E — API reconciliation and authoritative reread.** Current provider state; per-object reread; repair of missed
  webhooks and of `ReconciliationRequired` events; provider rate limits; validation of the clock-skew tolerance; no
  blind last-write-wins.
- **3F — Initial sync, watermark and buffered replay.** The frozen initial-connection sequence, crash-safe and
  idempotent, with no gap between the initial state and realtime intake.
- **3G — Real monday end-to-end acceptance and Gate closure.** Real OAuth against an owner-approved test workspace; a
  real change reaching Snapshot; a missed webhook repaired; initial sync; revocation failing closed; rate-limit
  behavior where practical; contradiction audit, security review, report and owner review.
  **Precondition (owner decision, 2026-10-03):** the 3G harness or host runs
  `MondayConnectionAuthorizer.CleanUpVerifiersAsync` once at start-up and then every 5 minutes while active, only
  under a trusted tenant context, and never enumerates tenants through a new unrestricted path ("3B maintenance before
  3G — PKCE verifier cleanup").

One sub-gate at a time; no later functionality enters an earlier sub-gate.

## Gate 3 Definition of Done

1. Sub-gates 3A to 3G each PASS by product-owner review.
2. A real monday OAuth 2.1 connection (PKCE S256, expiring access tokens, rotating refresh tokens, revocation) against
   an owner-approved test workspace, with every secret only in approved secret storage and never in an ordinary
   database field, event, log, prompt or AI context.
3. Webhook intake that answers the challenge, verifies authenticity per the current documentation, resolves the
   tenant through the Gate 2 connection contract, stores the durable receipt before acknowledging, and is safe under
   replay and duplicates.
4. A production monday normalizer producing canonical events, leaving unmapped actors unresolved, respecting the
   `EventText` limits or an explicitly approved widening, and treating monday text as untrusted data.
5. Reconciliation that rereads authoritative per-object state, repairs missed webhooks and `ReconciliationRequired`
   events without blind last-write-wins, and respects the provider's limits.
6. Initial sync following the frozen sequence: crash-safe, idempotent and gap-free.
7. Acceptance proven with a real monday workspace: a real change reaches canonical event and Snapshot end to end; a
   missed webhook is repaired by reconciliation; initial sync; revocation fails closed.
8. Every Gate 2 carry-forward item resolved or explicitly decided by the product owner.
9. Every Premium architecture §20 constraint met.
10. No monday write feature, no AI, no Premium capability.
11. Unit, security, architecture and integration tests; mutation and vacuity checks for every new guard; the full suite
    green locally and in CI.
12. Contradiction audit, security review, Gate report and product-owner approval.
13. Azure unchanged unless separately approved; running the connector in Azure is its own owner boundary.

## 3A Definition of Done

1. **Scope first.** This document and the status lines recording Gate 3 IN PROGRESS with 3A authorized.
2. **Provider-neutral contract in Core** (`src/App.Core/Connectors/`): an operation descriptor carrying every field of
   §20 constraint 3 and refusing an incomplete one; a connector identity that is either a Service Identity or a
   delegated user, bound to tenant and connection, never defaulted, never an email address; a failure taxonomy with
   stable codes and fixed messages; a bounded, provider-aware retry policy; a call result carrying the identity used
   and the attempts made. Nothing in Core mentions monday, and no Core type carries a credential.
3. **monday foundation** (`src/App.Connectors.Monday/`): the API version in one constant, sent on every request; one
   fixed endpoint; a GraphQL-over-HTTP client that sends only read queries with variables (never interpolated provider
   data), bounds the response size and the operation time, classifies every outcome into the taxonomy, and retries
   only transient and rate-limited reads, honouring `Retry-After`, `retry_in_seconds` and the `RateLimit` reset up to a
   cap. A credential port acquires the credential before every attempt and fails closed when the connection is
   disabled, the credential is revoked or unavailable, or the credential belongs to another tenant or connection. The
   credential type is opaque: no public accessor, a redacted string form, and no serialization.
4. **Typed read operations:** items by id (at most 100, `limit` equal to the id count, every state visible), the
   first page of a board's items (at most 500), and the next page by cursor. Each has a complete descriptor: READ, LOW
   risk, no side effect, no recovery needed, a timeout and a retry policy. Provider data types never render content;
   an unknown shape is an unsupported response, never a guess.
5. **Nothing more:** no OAuth, webhook, normalizer, reconciliation or sync logic; nothing registered in any host; no
   live credential; no schema change; no package added; no Azure change.
6. **Tests:** unit tests for the contract and the client over a deterministic fake monday server; security tests for
   credential and content leakage, fail-closed access and read-only enforcement; architecture tests for the single
   version constant, the single endpoint and the provider-neutral Core; mutation checks for the key guards.
7. **Verification:** build with 0 warnings and 0 errors; the full suite green against the local stack; the
   verifiers pass.
8. **Review:** security review, contradiction audit and a report at the 3A owner boundary; no commit or push.

## Owner boundaries

- Creating the monday app in the owner's account, enabling its New OAuth Flow, choosing its scopes and redirect URLs,
  and holding its client id, client secret and signing secret.
- Where secrets are stored (3B). Any Key Vault, RBAC, identity or network change is a separate approval.
- Registering a webhook against a real board, and how a real webhook reaches the system (a public Azure Dev endpoint
  or another approved route) for 3C and 3G.
- Azure Dev has none of the Gate 2B to 2F migrations and runs none of the Event Backbone; deploying, migrating and
  activating are separate approvals.
- Decided for 3B on 2026-09-30: the identity a connection acts under (D3B-1) and the secret boundary (D3B-2); see
  *3B owner decisions*. Decisions expected later: widening `EventText.MaxValueLength` if monday data needs it (3D),
  the clock-skew tolerance (3E), and rate budgets and scheduling (3E, 3F).
- Recorded on 2026-10-04 (see *Owner decisions on record before 3G*): planning or documenting 3G does not start it
  (decision A), and any real monday execution, a read as much as a write, needs the owner's explicit approval
  (decision B).
- Recorded on 2026-10-05 (see *3G — Offline harness implementation*): Gate 3G is authorized for the offline
  implementation only. Real execution and Gate 3 closure are not authorized, and every real-execution blocker stays
  open.

## Contradiction audit before implementation

Every element of this scope and of the 3A design was compared with PRD v1, System Architecture v1, the 83
Architecture Invariants, Development Gates v1 and the frozen Premium documents.

| Element | Core rule | Result |
|---|---|---|
| Connector in its own project; Core provider-neutral | System Architecture v1 §2 ("does not leak provider-specific semantics into Core"); dependency rules | Compatible |
| Webhooks and reconciliation as separate mechanisms | System Architecture v1 §2; invariant 17 | Compatible: 3C and 3E are separate sub-gates |
| Initial sync sequence | System Architecture v1 §2 | Compatible: 3F implements the frozen sequence as written |
| Reusing Gate 2 ingress, ordering and Snapshot | System Architecture v1 §3, §9; Gate 2 decisions | Compatible: no Core service duplicated |
| monday has no item revision; `updated_at` as evidence | System Architecture v1 §3 ordering preference | Compatible: revision absent, source update time used, ambiguity reconciled |
| Secrets outside Core and outside ordinary database fields | PRD v1 §17; invariants 53, 54 | Compatible: no Core credential type; storage decided in 3B |
| Identity carried, never invented; no email identity | invariants 19, 45, 46, 47; Premium D2 | Compatible |
| Read and ingest only | Development Gates v1 (Gate 3 builds no action); invariant 37; Premium §20 constraint 5 | Compatible: no mutation exists; the client refuses non-query documents |
| Revocation at access time | invariant 58; Premium §20 constraint 7 | Compatible |
| Untrusted provider text | invariant 25; Premium §20 constraint 6 | Compatible |
| Tokens, payloads and provider bodies out of logs | PRD v1 §17; System Architecture v1 §10 | Compatible: the 3A client does not log, and no failure carries provider content |
| No AI, no Premium, no Gate 4 or 6 work | Development Gates v1; P0 closure | Compatible |
| One real monday connector | PRD v1 §10, §19 (multiple production connectors deferred) | Compatible: monday only |

**Result: no contradiction, no blocker.** Nothing in 3A needs a new product-owner decision before implementation.

## 3A implementation record

Implemented 2026-09-29 on top of `999d2f9`; approved by the product owner on 2026-09-30.

**Core, provider-neutral** (`src/App.Core/Connectors/`): `ConnectorOperationDescriptor` carries every field of §20
constraint 3 and refuses an incomplete or inconsistent declaration; only a read-only operation may retry.
`ConnectorIdentity` is a Service Identity or a delegated user, bound to tenant and connection, never an email address.
`ConnectorFailure`, `ConnectorFailureKind` and `ConnectorErrors` give 12 failure kinds and 17 fixed errors.
`ConnectorRetryPolicy`'s default for reads is 4 attempts, backoff from 1 second doubling to 30 seconds with jitter, and
provider-requested waits of up to 60 seconds honoured in place; a longer wait is returned to the caller.
`ConnectorCallResult<T>` carries the value or the failure, the identity used and the attempts made. No Core type
carries a credential or mentions monday.

**monday** (`src/App.Connectors.Monday/`): `MondayApiVersion` pins `2026-07` and names the `API-Version` header;
`MondayEndpoints` fixes `https://api.monday.com/v2`. `MondayAccessCredential` is opaque: visible-ASCII token only, a
redacted string form, and serialization refused. `IMondayCredentialSource` is the per-attempt, fail-closed port that
3B implements. `MondayQueryGuard` admits a single read query only. `MondayOperations` and `MondayQueries` declare three
reads: items by id, a board's first page of items, and the next page. `MondayReadClient` acquires and checks the
credential before every attempt, bounds each attempt to 30 seconds and each response to 16 MiB, refuses redirected
answers, classifies every outcome, retries within the policy and writes no log. `MondayResponseClassifier` recognizes
only documented codes and messages; `MondayResponseParser` is strict; the item types never render content. Nothing is
registered in a host, and no package, schema, workflow, configuration or Azure change was made.

**Tests:** unit 390 (+139), security 118 (+17), architecture 18 (+4), integration 247 (unchanged): 773 in all, and
773/773 locally, the 9 Service Bus emulator tests included. A mutation run of 32 mutants over the 3A guards caught all
32, after two tests were strengthened (disagreeing errors in both orders; a second anonymous operation), with every
file restored and verified by hash.

**Verification:** `dotnet build --no-incremental` with 0 warnings and 0 errors; the full suite 773/773;
`eng/verify-architecture.py` and `eng/verify-local-env.py` PASS; the frozen Core and Premium documents byte-identical.

**Security review:** no finding. Carried to later sub-gates: register the HTTP client without automatic redirects
(3B); token storage, refresh and the identity a connection acts under (3B); a rate and concurrency budget across calls
(3E, 3F).

## 3A closure (2026-09-30)

The product owner approved 3A on 2026-09-30: **3A is PASS**. Before the closing commit the clean build, the full
suite and both verifiers were repeated on the tree being committed. The repeated mutation run caught all 31 mutants it
completed before Claude Code stopped it for low system memory during M32; M32, rerun alone in the foreground on the
restored source, was caught too. **Final mutation result: 32/32 caught**, with every file restored and verified by
hash. **3B (OAuth 2.1 connection lifecycle) is NEXT / NOT STARTED** and begins only on its own authorization; it needs
the owner decisions listed under *Owner boundaries*. Gate 3 stays IN PROGRESS.

## 3B — OAuth 2.1 connection lifecycle

Authorized by the product owner on 2026-09-30 in SAFE AUTO MODE. Baseline: `main` == `origin/main` == `d06ed66` (the
Gate 3A commit), working tree clean, the frozen Core and Premium documents byte-identical to their recorded hashes.
3B stops at its owner boundary: no commit, no push, no 3C.

### 3B owner decisions (2026-09-30)

- **D3B-1 — Delegated identity: APPROVED.** A normal monday connection acts through the explicit delegated identity
  of the user who authorized that connection. Required chain: Tenant → canonical user → monday connection → delegated
  monday identity. Email is never the security identity, no user is invented, and a Service Identity is not the
  default for a normal monday connection.
- **D3B-2 — Secret boundary: APPROVED.** The monday client secret, the OAuth access token, the OAuth refresh token and
  the PKCE code verifier are secrets. They never appear in ordinary PostgreSQL fields, source, logs, telemetry, events
  or Core contracts. Ordinary database fields may hold only: the tenant; the connection id; the canonical user id; the
  OAuth transaction id; the state hash; timestamps and expiry; granted scope names; connection status; an opaque secret
  reference or version; the token generation or version; and non-secret provider and account identifiers. 3B needs no
  Azure change and creates or updates no Key Vault resource: it defines the narrow secret-storage abstraction, tests
  it with deterministic in-memory storage, and adds no Azure deployment, RBAC or IaC work. The lack of a deployed Key
  Vault adapter is not a 3B blocker.
- **D3B-3 — No real monday app in 3B.** No monday app is created or configured. The code is ready for the New OAuth
  Flow, a server-controlled redirect URI, minimum scopes, a client id and a client secret. A personal API token is
  never the architecture.

### monday OAuth 2.1, reverified (2026-09-30)

Reverified against monday's own sources only: the authorization server metadata
(`https://auth.monday.com/oauth_ms/.well-known/oauth-authorization-server`), the
[OAuth 2.1 migration guide](https://developer.monday.com/apps/docs/migrating-to-the-new-oauth-flow) and the
[OAuth reference](https://developer.monday.com/apps/docs/oauth). Nothing contradicts a material point of the 3B
authorization.

| Topic | Verified fact |
|---|---|
| Metadata | Issuer `https://auth.monday.com`; authorization `https://auth.monday.com/oauth2/authorize`; token `https://auth.monday.com/oauth_ms/oauth/token`; revocation `https://auth.monday.com/oauth_ms/oauth/revoke`; response type `code` only; grants `authorization_code` and `refresh_token`; PKCE method `S256` only; client authentication `client_secret_post` or `client_secret_basic`; `authorization_response_iss_parameter_supported: true`. 26 scopes: 14 end in `:read`, 11 in `:write`, plus `ai:consume` |
| Authorization request | PKCE is required: `code_challenge` (base64url SHA-256 of the verifier) and `code_challenge_method=S256`; the verifier is 43–128 unreserved characters. `redirect_uri` must be one of the app's configured redirect URLs, and the requested scopes must match the scopes configured in the app |
| Callback | "Same parameters — `code`, `state`, and `status`. Check `status` to confirm the user approved the authorization." The code is valid for 10 minutes |
| Code exchange | `POST` with a JSON body (`Content-Type: application/json`): `grant_type=authorization_code`, `client_id`, `client_secret`, `code`, `redirect_uri` (required when the authorization request carried one) and `code_verifier` |
| Token response | `access_token` (a JWT whose `exp` claim is its expiry), `refresh_token`, `token_type` `Bearer`, `scope` (space-separated). `expires_in` is documented only for the legacy-token migration endpoint, which 3B does not use |
| Refresh | `POST` JSON `grant_type=refresh_token`, `client_id`, `client_secret`, `refresh_token`. Refresh when under 5 minutes remain; after a `401 Unauthorized`, refresh and retry. "Each refresh returns a new refresh_token. Always store the latest refresh token and discard the old one." |
| Revocation | `POST` JSON `token`, `client_id`, `client_secret`, `token_type_hint`; success is `{"success": true}` |

**Open verification items** (continuing the list above; none is a contradiction, and none is guessed):

5. The values of the callback's `status` parameter are not documented. 3B accepts an authorization response only when
   it carries a code and no `error`; exchanging the code then proves the approval. 3G records the real value and adds
   a positive check (3G).
6. The metadata advertises the `iss` authorization-response parameter, but the callback documentation names only
   `code`, `state` and `status`. 3B refuses a present `iss` that is not exactly `https://auth.monday.com` and accepts
   its absence: with one fixed authorization server there is no second server for the mix-up attack `iss` prevents.
   3G confirms, and makes `iss` mandatory if monday sends it (3G).
7. The token endpoint's error codes, the access-token and refresh-token lifetimes and the treatment of a reused
   refresh token are not documented. 3B decides by HTTP status alone and never reuses a refresh token (3G).
8. Item 2 stays open: the only documented `Authorization` form is the raw personal token, and OAuth responses say
   `token_type: Bearer`. The form stays in one place and is confirmed in 3G.

### 3B Definition of Done

1. The New monday OAuth 2.1 flow only.
2. PKCE S256 and anti-CSRF, anti-replay state.
3. Strong tenant, user and connection binding.
4. Secure authorization-code exchange.
5. An expiring access-token lifecycle.
6. Safe refresh-token rotation.
7. Durable concurrency protection.
8. Fail-closed handling of ambiguous refresh persistence.
9. Local disconnect and provider revocation.
10. Access-time revocation enforcement.
11. No secret in an ordinary database field, Core contract, log or event.
12. Read-only scopes and policy; no Gate 3 write capability.
13. No live monday account and no Azure needed by any test.
14. Full test, security and mutation evidence.
15. The frozen documents unchanged.
16. The full suite and the repository verifiers green.
17. No contradiction with Core or Premium P0.

### 3B design

**Where things live.** The dependency rules decide the split. Core holds the provider-neutral authorization port, its
records and its errors; the port carries hashes, ids, times, scope names and an opaque material version, never a secret.
Infrastructure implements the port over PostgreSQL through `ITenantScopedDatabase` only. The monday project holds
everything that touches a secret or monday's protocol: options, the scope policy, PKCE and state, the OAuth client,
the secret-storage port, the authorizer (start, callback, disconnect), the credential source that implements the 3A
port, and an HTTP handler that never follows redirects. Nothing is registered in a host.

**Persistence.** Two tenant-owned tables under forced row-level security; no tenant-less function is added.
`connector_authorization_transactions` holds one row per started authorization: tenant, transaction id, canonical
user, intended connection, source system, purpose (connect or reauthorize), the SHA-256 of the state secret, the
SHA-256 of the redirect URI, the requested scope names, and the created, expiry and consumed times, stamped by the
database clock; a check constraint caps the lifetime at 10 minutes. `connector_authorizations` holds one row per
authorized connection: tenant, connection, canonical user, state (Active, ReauthorizationRequired, Revoked), monday
user id and account id, generation, material version, access-token expiry, granted scope names, the refresh claim and
its time, and lifecycle times. No column can hold a token, a secret or a verifier. Every foreign key includes the
tenant, so neither table can name another tenant's user or connection. A connection's Gate 2 `connections` row is
created Active when it is first authorized and set Disabled when it is disconnected; nothing re-enables it.

**Secret storage.** `IMondaySecretStore` sets, gets and deletes one value by a `MondaySecretName` that only the
connector can build: tenant, connection and material version for a token pair; tenant and transaction for a verifier;
one fixed name for the client secret. It has no list or search, so there is no tenant-less secret query. A stored
token pair carries its tenant, connection, version and monday identity, and is refused on read unless all of them
match. Tests use in-memory storage; the deployed adapter (Key Vault) is a later owner decision.

**State and PKCE.** The server generates everything from the operating system's CSPRNG: a transaction id, a 256-bit
state secret and a 256-bit verifier (43 characters). The `state` sent to monday is the transaction id and the state
secret; the database keeps only the secret's SHA-256. The verifier lives only in secret storage; the challenge is its
S256. The redirect URI and the scopes come from server configuration, never from the browser.

**Callback order**, each failure stopping before any exchange: the state parses; the transaction exists in the trusted
tenant; it has not expired (database clock); it is bound to the calling canonical user; the state secret matches (in
constant time); it has not been consumed, and is consumed atomically under a row lock, so a replay or a concurrent
second callback loses; the response is approved (no `error`); an `iss`, when present, is monday's issuer; a code is
present; the configured redirect URI still matches the transaction. A wrong user or a wrong state does not consume the
transaction. Once the transaction is consumed, found expired or replayed, its verifier is deleted.

**Exchange.** One attempt, never retried: JSON to the token endpoint with the code, the verifier, the redirect URI and
the client credentials. The response must carry an access token, a refresh token, `token_type` Bearer and scopes. The
expiry is the access token's JWT `exp`, which must be in the future and within a sanity bound; granted scopes must all
be allowlisted read scopes and include the required ones. The identity is then verified by the read
`me { id account { id } }`; for a reauthorization it must equal the connection's recorded monday user and account. The
pair is stored under a new material version before any database row points at it. Any failure after tokens were issued
revokes them, best effort, and fails closed.

**Refresh: single-flight and crash-safe.** A credential with more than 5 minutes left is used as it is. Otherwise the
caller claims the connection's refresh by a compare-and-swap on its generation, and only the winner calls monday. The
new pair is written first, under the claim's id; then a compare-and-swap on generation and claim advances the
generation and points the row at the new version; then the old version is deleted. A loser keeps using the current
token while it is still valid, and otherwise waits briefly for the winner. A 429, or a request that provably never
left, releases the claim as a transient failure. Any other rejection, an ambiguous outcome (a timeout after sending, a
5xx, an unreadable success) or a failure to store the new pair moves the connection to ReauthorizationRequired: the
old refresh token is never used again. A claim older than its timeout is resolved by the next caller: the claimant's
new pair is adopted if it was stored, and otherwise the connection requires reauthorization. After a provider 401 the
read client asks for one renewal and retries once.

**Access-time checks.** Before every attempt the credential source rereads the row and fails closed, before anything
is sent, when the connection is disabled or revoked, when reauthorization is required, when the delegating user is no
longer active, when the row or the stored pair belongs to another tenant, connection or identity, or when the secret
cannot be read.

**Disconnect.** One database transaction first marks the authorization Revoked and the connection Disabled; only then
are the refresh token and the access token revoked at monday, one attempt each, and every stored version deleted. A
monday outage still leaves the connection unusable, and the revocation is recorded as unconfirmed. In 3B only the
connection's canonical user may disconnect it; a tenant administrator's disconnect arrives with authorization
hardening (Gate 7).

**Scopes.** Server configuration chooses the requested scopes from an allowlist of monday's documented `:read`
scopes; the default is `me:read boards:read`. No `:write` scope, `ai:consume` or unknown scope can be configured, and a
grant that contains one is refused and revoked.

**OAuth calls are not connector operations.** Exchange, refresh and revocation are the credential mechanism, like the
3A credential port, not operations on monday work data. They carry no operation descriptor and never enter the action
taxonomy, so the SEND/CHANGE approval policy does not apply to them; none is ever retried automatically. The identity
probe is a work-data read and is declared like the other reads.

**Not in 3B.** No webhook, normalization, reconciliation or initial-sync logic; no monday write; no host endpoint and
no registration. The Web host has no authenticated user session yet, so the start and callback entry points arrive with
the sub-gate that can authenticate the canonical user. No real monday app, account or secret; no Azure, Key Vault,
RBAC, network or IaC change.

### 3B contradiction audit (before code)

Every element of the 3B design was compared with PRD v1, System Architecture v1, the 83 Architecture Invariants,
Development Gates v1, the frozen Premium documents and the Gate 2 decisions.

| Element | Canonical rule | Result |
|---|---|---|
| Delegated identity chain; never email; no invented user | D3B-1; invariants 19, 45, 46, 47; Premium D2 | Compatible: the identity is the monday user verified by `me`, bound to the authorizing canonical user |
| Secrets only in secret storage; ordinary fields limited to the D3B-2 list | D3B-2; PRD v1 §17; invariants 53, 54 | Compatible: no token, secret or verifier column; hashes, ids, times, scope names and an opaque version only |
| Tenant from trusted server context; forced row-level security; no tenant-less secret access | invariants 1, 44, 45; Gate 2 decision 1 | Compatible: both tables tenant-owned; no new tenant-less function; secret names derive from trusted ids |
| Revocation at access time | invariant 58; Premium §20 constraint 7 | Compatible: the row is reread before every attempt |
| Read and ingest only | Development Gates v1; invariant 37; Premium §20 constraint 5 | Compatible: read scopes only; the identity probe is a read query |
| Credentials only in the connector layer | Premium §20 constraint 1 | Compatible: the Core port carries no secret |
| Every provider call records its authority | Premium §20 constraint 2 | Compatible: every read result carries the verified delegated identity |
| SEND/CHANGE need approval by policy | CLAUDE.md; PRD v1 approval rules | Compatible: OAuth protocol calls are the credential mechanism, not work actions; nothing changes monday data |
| AI is never Source of Truth and never executes | invariants on AI | Compatible: no AI |
| Webhooks and reconciliation stay separate and later | System Architecture v1 §2; invariant 17 | Compatible: none in 3B |
| No Azure change | CLAUDE.md; D3B-2 | Compatible |

**Result: no contradiction, no blocker.** The design needs no further product-owner decision before implementation.

### 3B implementation record

Implemented 2026-09-30 on top of `d06ed66`; approved by the product owner on 2026-09-30.

**Core** (`src/App.Core/Connectors/`, provider-neutral and secret-free): `IConnectorAuthorizationStore`, eleven
operations, each a compare-and-swap in the trusted tenant and none with a tenant parameter; the records
`ConnectorAuthorizationTransaction`, `ConnectorAuthorizationGrant`, `ConnectorAuthorization`,
`ConnectorTransactionConsumption` and `ConnectorDisconnection` with their states, purposes and outcomes;
`ConnectorScopes`; 23 fixed `ConnectorAuthorizationErrors`; and `ConnectorErrors.ReauthorizationRequired` (18 connector
errors).

**Infrastructure:** the migration `20260930013407_ConnectorAuthorizations` adds the two tenant-owned tables under
forced row-level security with the standard tenant policy, foreign keys that include the tenant (through two new
unique constraints on `users` and `connections`), check constraints including the 10-minute lifetime, and runtime
grants of read, insert and update; it adds no function. `ConnectorAuthorizationStore` implements the port through
`ITenantScopedDatabase` only, with parameterized SQL and the database clock: a row lock for consuming and for
disconnecting, and a CTE for the bounded close of expired transactions.

**monday** (`src/App.Connectors.Monday/`): `MondayEndpoints` adds the issuer and the authorization, token and
revocation endpoints; `MondayOAuthOptions` is server configuration without the client secret; `MondayScopePolicy`
allows monday's 14 read scopes with the default `boards:read me:read`; `MondayPkce` and `MondayAuthorizationState`;
`IMondaySecretStore`, `MondaySecretName`, `MondaySecretLookup` and stored values bound to tenant, connection, version
and identity; `MondayOAuthClient` makes JSON token and revocation calls, one attempt each, bounded and never
redirected, and reports Issued, Unusable, Rejected, Transient or Ambiguous; `MondayConnectionAuthorizer` starts,
completes and disconnects; `MondayConnectionCredentialSource` implements the 3A port with access-time checks,
single-flight crash-safe renewal and renewal after a rejection; `MondayHttp.CreateHandler()` follows no redirect and
keeps no cookie. The 3A read client gains the single renewal and retry after a 401 and the identity read
`monday.me.read`, and its bounded body read moved into a shared helper. Nothing is registered in a host, and nothing
logs.

**Refined during implementation** (no owner decision changed):

- The stored-credential reference is the column `material_version`. A Gate 1D security test forbids the word
  "secret" anywhere in a migration, and that guard was kept rather than weakened.
- When the last compare-and-swap of a renewal fails with an unknown outcome and a fresh read shows the claim is still
  the renewal's own, the compare-and-swap is tried once more; failing that, the new pair is kept for adoption rather
  than destroyed.
- The runtime role holds exactly SELECT, INSERT and UPDATE on both tables: the migration revokes every privilege,
  DELETE included, and grants those three back (review item R3B-2). The unchanged global bootstrap re-grants DELETE
  whenever it reruns; *3B owner review* records the rule for the Azure rollout. No store path deletes.
- A verifier is deleted once its transaction is consumed, found expired or replayed. A wrong user or a wrong state
  leaves the transaction and its verifier to their owner.
- `ConnectorAuthorization.PermitsUse` was dropped as unused: the credential source reports each refusal separately.

**Definition of Done evidence:**

| # | Item | Evidence |
|---|---|---|
| 1 | New OAuth 2.1 flow only | Only the metadata's authorization, token and revocation endpoints; PKCE on every start; JSON bodies as documented; no legacy endpoint |
| 2 | PKCE S256 and state | RFC 7636 vector; 43-character CSPRNG verifiers, unique over 50 starts; a 256-bit state secret stored as SHA-256 only; strict parsing |
| 3 | Binding | The transaction is bound to the trusted tenant, the canonical user, the connection, the redirect hash and the scopes; foreign tenant, other user, wrong state, expiry and replay refused in the documented order |
| 4 | Code exchange | Exactly one request, never retried (400, 401, 429, 5xx, redirect, network failure, timeout); verifier and client secret from secret storage only |
| 5 | Expiring access | Expiry from the JWT `exp`, sane bounds, `expires_in` only shortens it; renewal inside the 5-minute margin |
| 6 | Rotation | Each renewal presents the latest refresh token and stores the new pair before the row points at it; old versions destroyed |
| 7 | Concurrency | Compare-and-swap claims: 8 concurrent claims yield one winner; 8 concurrent callbacks, one exchange; two instances, one refresh |
| 8 | Ambiguous persistence | Rejected, ambiguous or unstorable outcomes require reauthorization; an abandoned claim's stored pair is adopted, otherwise reauthorization; the old refresh token is never used again |
| 9 | Disconnect and revocation | Revoked and disabled in one transaction before any provider call; refresh and access tokens revoked, versions destroyed; an outage is recorded as unconfirmed |
| 10 | Access-time revocation | Disabled, revoked, reauthorization required, inactive user, foreign tenant, and tampered stored pairs all refused before anything is sent |
| 11 | No secret in ordinary fields, Core, logs or events | Pinned columns and Core members; a full database scan after the lifecycle finds no token, code, verifier, state or client secret, with a positive control; the connector references no logging |
| 12 | Read-only scopes | The allowlist holds only read scopes; a write scope, `ai:consume` or an unknown scope is refused and revoked |
| 13 | No live monday or Azure | Fake monday endpoints and in-memory secret storage only |
| 14 | Test, security and mutation evidence | See below |
| 15 | Frozen documents | Byte-identical to the recorded hashes |
| 16 | Suite and verifiers | See below |
| 17 | No contradiction | The pre-code audit holds after implementation |

**Tests:** unit 568 (+178), security 129 (+11), architecture 23 (+5), integration 266 (+19): 986 in all, 986/986
locally, the 9 Service Bus emulator tests included. The Gate 2 acceptance test that attacks every tenant-owned table
now seeds and covers the two new tables.

**Mutation and vacuity:** 59 new mutants over Core, the connector, the store and the migration, 14 of them run
against real databases: the store's against the shared test database, the migration's (review item R3B-2) against
fresh ones. All 59 were caught, after two tests were strengthened (the connection binding and the identity check of a
stored pair) and two mutants were rewritten to compile. The 32 Gate 3A mutants, rerun on the refactored client, were
all caught: 91/91. With row-level security disabled on the two new tables, 4 isolation tests failed; it was restored
and verified. An empty database was migrated from zero (9 migrations), a re-run was a no-op, and it was dropped.

**Verification:** `dotnet build --no-incremental` with 0 warnings and 0 errors; the full suite 986/986;
`eng/verify-architecture.py` and `eng/verify-local-env.py` PASS; the six frozen documents byte-identical.

**Security review:** no blocking finding. For later sub-gates: the authorization address carries the state, so the
host that serves it must not log it (its string form is redacted); the deployed secret-store adapter needs its own
timeouts (a store that hangs past the claim timeout ends in reauthorization, failing closed); abandoned verifiers are
cleaned when the tenant next starts an authorization, and a scheduled sweep would need tenant discovery, a Gate 2
decision 1 boundary; in 3B only the authorizing user can disconnect (a tenant administrator's disconnect comes with
Gate 7); start requests are not rate-limited (Gate 7).

**Still to decide before real monday use (3G and the owner):** the monday app (New OAuth Flow, read-only scopes since
any write scope is refused, the redirect URL, the client id, and the client secret placed in the approved secret store
as `wce-monday-client-secret`); the deployed secret store (a Key Vault adapter and access for the Web and Worker
identities); the owner-operated DEV/TEST harness for real OAuth before Gate 7 (R3B-1), while production entry points
for start and callback wait for the authenticated identity layer; the Azure rollout of the 3B migration under the
bootstrap-rerun rule (R3B-2); how
webhooks are registered in 3C, since `webhooks:write` is excluded; and verification items 2, 5 and 6.

### 3B owner review (2026-09-30)

The product owner reviewed 3B and ordered two items. Both were applied before the final 3B decision; nothing was
committed.

**R3B-1 — Real monday OAuth before Gate 7: approved boundary.**

- Real monday OAuth testing before Gate 7 uses an owner-operated DEV/TEST harness.
- The harness supplies the trusted chain itself: Tenant → canonical user → monday connection. It calls the same
  `MondayConnectionAuthorizer` with a resolved tenant context and an authenticated principal for the owner's test user;
  nothing of that chain is taken from a browser or a request.
- No anonymous production OAuth-start endpoint may accept an arbitrary tenant or user.
- Production, user-facing OAuth initiation waits for the appropriate authenticated identity layer.
- The callback, state and PKCE binding stay unchanged: the transaction still binds the tenant, the canonical user, the
  connection, the redirect address and the scopes, and the callback is validated in the same documented order.
- Gate 7 authentication is not implemented, and 3B adds no harness and no endpoint. Building and running the harness
  belongs to the owner-approved real-monday testing (3G) and needs its own approval.

**R3B-2 — The runtime role's privileges on the 3B tables: applied.**

- The migration revokes every privilege of `workcontinuity_app` on `connector_authorizations` and
  `connector_authorization_transactions`, DELETE and TRUNCATE included, and grants back only SELECT, INSERT and UPDATE,
  which is what the store uses. The global bootstrap mechanism is unchanged.
- Evidence on fresh databases, bootstrapped and then migrated from zero in the rollout order
  (`ConnectorAuthorizationFreshDatabaseTests`): the runtime role holds exactly INSERT, SELECT and UPDATE on both tables;
  reading, inserting and updating work through the store (opening and consuming a transaction, activating, renewing,
  reading, disconnecting); DELETE and TRUNCATE by the runtime role fail with permission denied (SQLSTATE 42501);
  another tenant reads nothing, updates nothing and cannot insert rows labelled with the first tenant; and both tables
  keep row-level security enabled and forced, with their tenant policy. Each database is created and dropped by its
  exact name.
- **Azure rollout rule.** The global bootstrap grants SELECT, INSERT, UPDATE and DELETE on every table each time it
  runs. Run before the migration, as the rollout order requires (bootstrap, then migrate), it is overridden by the
  migration. Rerun after it, it silently re-grants DELETE on both 3B tables, as a fresh-database test demonstrates.
  During the Azure rollout, the bootstrap must therefore not be rerun after the 3B migration unless the same
  owner-approved step immediately re-asserts the 3B privileges and verifies them; any result other than
  `INSERT,SELECT,UPDATE` for both tables stops the rollout:

  ```sql
  REVOKE ALL PRIVILEGES ON connector_authorizations, connector_authorization_transactions FROM workcontinuity_app;
  GRANT SELECT, INSERT, UPDATE ON connector_authorizations, connector_authorization_transactions TO workcontinuity_app;

  SELECT t.table_name, string_agg(p.privilege, ',' ORDER BY p.privilege)
  FROM (VALUES ('connector_authorizations'), ('connector_authorization_transactions')) AS t(table_name)
  CROSS JOIN (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE'), ('REFERENCES'), ('TRIGGER')) AS p(privilege)
  WHERE has_table_privilege('workcontinuity_app', 'public.' || t.table_name, p.privilege)
  GROUP BY t.table_name;
  ```

  The same test proves that this re-assertion restores exactly read, insert and update. Making the bootstrap preserve
  per-table revocations, or adding a restrictive no-delete policy so that a re-grant stays inert, would make the rule
  durable; either is a separate owner decision.
- On the shared local test database the suite reruns the bootstrap on every run, so that database shows DELETE on
  these tables afterwards. That is the bootstrap's documented behaviour, not the migration's result; the fresh-database
  tests are the evidence.

### 3B closure (2026-09-30)

The product owner gave 3B its final pass on 2026-09-30: **3B is PASS**. The owner decisions D3B-1 to D3B-3 and the two
review decisions stay in force:

- **R3B-1:** real monday OAuth before Gate 7 runs only through an owner-operated DEV/TEST harness that supplies the
  trusted Tenant → canonical user → monday connection chain. No anonymous production OAuth-start endpoint may accept an
  arbitrary tenant or user, production initiation waits for the authenticated identity layer, and the callback, state
  and PKCE binding are unchanged.
- **R3B-2:** the runtime role holds only SELECT, INSERT and UPDATE on the two OAuth tables, with DELETE and every other
  privilege explicitly revoked. The unchanged global bootstrap must never be rerun after the 3B migration in Azure
  without re-asserting and verifying those privileges in the same approved step.

Before the closing commit, the clean build, the full suite (986/986) and both verifiers were repeated on the tree being
committed. The mutation and vacuity evidence (91/91) stands, because only status documentation changed after it.
**3C (webhook intake) is NEXT / NOT STARTED** and begins only on its own authorization. Gate 3 stays IN PROGRESS.

## 3C — Webhook intake

Authorized by the product owner on 2026-09-30 in SAFE AUTO MODE. Baseline: `main` == `origin/main` == `869e1c7` (the
Gate 3B commit), working tree clean, the frozen Core and Premium documents byte-identical to their recorded hashes.
3C stops at its owner boundary: no commit, no push, no 3D.

### 3C owner decision (2026-09-30)

- **D3C-1 — Intake only.** 3C implements webhook intake: the receiving endpoint, the verification challenge, JWT
  verification, tenant and connection resolution, durable Gate 2 ingress, and duplicate and retry safety. It creates and
  deletes no real monday webhook subscription and does not add `webhooks:write` to the 3B scope policy: monday's
  `create_webhook` and `delete_webhook` need that scope, and that connector control-plane setup is authorized
  separately, when the real monday app and a real workspace are exercised. Real webhook creation and registration stay
  an owner boundary, and 3C introduces no monday business-data mutation.

### monday webhooks, reverified (2026-09-30)

Reverified against monday's own documentation only: the
[webhooks reference](https://developer.monday.com/api-reference/reference/webhooks) and the
[integration authorization](https://developer.monday.com/apps/docs/integration-authorization) page. Nothing contradicts
the 3C authorization: board webhook requests are verified with the app's Signing Secret, and only app lifecycle
webhooks use the Client Secret.

| Topic | Verified fact |
|---|---|
| Verification challenge | When a webhook is created, monday POSTs JSON `{"challenge": "…"}` to its URL; the endpoint must answer HTTP 200 with the same JSON. The documentation does not say that this request carries a JWT |
| Authentication | "Some webhook requests contain a JWT in the Authorization header": those of webhooks created with an integration app token. It is verified with the app's **Signing Secret**. The integration authorization page makes the split explicit: board webhooks use the Signing Secret, app lifecycle webhooks the Client Secret |
| JWT claims | `accountId`, `userId`, `aud` ("your app's endpoint URL"), `exp`, `iat` and `shortLivedToken`, an API token valid for five minutes |
| Payload | `{"event": {…}}` with, among others, `userId`, `originalTriggerUuid` (null or text), `boardId`, `pulseId`, `pulseName`, `groupId`, `columnId`, `columnType`, `value`, `previousValue`, `changedAt` (epoch seconds, fractional), `isTopGroup`, `app`, `type`, `triggerTime` (ISO 8601), `subscriptionId` and `triggerUuid` (hexadecimal text) |
| Delivery | Failed deliveries are retried once a minute for 30 minutes. Ordering, duplicate delivery and idempotency are not documented |
| Management | `create_webhook` and `delete_webhook` need `webhooks:write`. Twenty-one event types cover items, subitems, column values, names, group moves, columns and updates |

**Open verification items** (continuing the numbering above; none is a contradiction, and none is guessed). Item 3 is
partly resolved: the claims are documented as listed.

9. **HS256 — 3C compatibility assumption, which must be confirmed against a real monday board webhook in 3G.** The
   board-webhook documentation verifies that the token is signed with the app's Signing Secret and carried in the
   `Authorization` header, that `aud` must match the endpoint and that `exp` must be valid; it does not name the JOSE
   algorithm. 3C verifies HS256 only, because the credential is a symmetric secret and monday's related examples sign
   with it as a standard JWT. The algorithm allowlist is exactly `HS256`: `none`, every asymmetric algorithm (RS, PS,
   ES, EdDSA) and every other HMAC are refused. HS256 is not recorded as a documented monday fact.
10. **Raw token or `Bearer` — 3C compatibility decision, to be confirmed in 3G.** The documentation does not say
    whether the `Authorization` value is always the bare JWT or `Bearer <JWT>`. 3C accepts exactly those two forms,
    with a strict parser: the value is exactly the token, or exactly `Bearer`, one space and the token, in that case.
    Any other scheme or case, extra whitespace, a list of values or a header given more than once is refused.
11. `aud` is described only as "your app's endpoint URL". 3C requires it to equal, character for character, the
    address it would register for the connection: the configured public base address followed by
    `/api/connectors/monday/webhooks/{connectionId}`. 3G confirms the real form; a different one is an owner decision,
    never a silent relaxation.
12. The challenge request's `Authorization` header is not documented; the challenge path neither requires nor reads
    it.
13. The token's lifetime is not documented. 3C enforces `exp` with a 30-second clock tolerance, refuses a token that is
    not yet valid by `nbf` or issued in the future by `iat`, and invents no maximum (3G records the real lifetime).
14. Whether a webhook created with a connection's OAuth 2.1 token counts as created "with an app token", and so
    carries the JWT, stays open until real registration (3G). A delivery without a valid JWT is always refused.
15. Which responses monday treats as failures, whether a retry carries a fresh JWT, and whether monday disables a
    failing webhook are not documented. 3C answers every delivery it has not durably stored with a non-2xx status, and
    reconciliation (3E) repairs whatever never arrives (invariant 17).

### 3C Definition of Done

Recorded before coding, exactly as the product owner set it. 3C is READY FOR OWNER REVIEW only when:

1. Public monday webhook endpoint exists locally.
2. URL challenge works without entering Event Backbone.
3. Event requests require valid monday board-webhook authentication.
4. Signing Secret stays only inside the secret boundary.
5. JWT signature, expiry and audience are verified.
6. Verified monday account is bound to the 3B connection.
7. Tenant is resolved only server-side.
8. Real Gate 2 durable ingress is reused.
9. Success is returned only after durable ingress commit.
10. Duplicate/retry behavior is safe.
11. Unauthenticated/malformed input persists nothing.
12. No webhook arrival ordering is trusted.
13. triggerUuid is used only when actually supplied.
14. Provider text remains untrusted.
15. No secret/payload leakage in logs.
16. No webhooks:write, registration mutation or business write exists.
17. No 3D normalization/reconciliation/sync exists.
18. Full security/integration/mutation evidence is green.
19. Frozen documents remain unchanged.
20. No Azure or real monday account is required.
21. No contradiction with Core or Premium P0.

### 3C design

**Where things live.** monday's protocol stays in the connector: reading a webhook body, the challenge, the JWT check,
the Signing Secret lookup, a connection's audience and the production monday `ISourceVerifier`, none of which logs. The Web host owns HTTP only: the route, the size, media-type and encoding limits, the status codes, the logs and
the composition. Storage is reached only through Gate 2 ports: `IEventIngress` for events, and a new provider-neutral
connection check for the challenge. Nothing monday-specific enters Core, and no monday or Web code touches a table.

**Endpoint.** `POST /api/connectors/monday/webhooks/{connectionId}`. The connection id is the only request input that
selects anything, and only in its canonical lowercase form; the tenant never comes from the route, the query, the body,
the JWT or a header. A request is refused before its body is read when it is not `application/json` in UTF-8, is
compressed, or declares more than 256 KiB. The body is read to at most 256 KiB, never decompressed and never truncated:
a larger one is refused and nothing is stored. It must be well-formed UTF-8 JSON without a byte-order mark or duplicate
keys, nested at most 64 levels. A challenge and an event are told apart by the body's shape alone and take separate
code paths; a body that is both, or neither, is malformed.

**Challenge.** A challenge is a JSON object with exactly one property, `challenge`, holding 1 to 256 visible ASCII
characters; anything else, an extra field included, is malformed and refused. The connection id must name an active
monday connection. A new provider-neutral port, `IIngressConnectionCheck`, answers that through the approved Gate 2
resolver `resolve_ingress_connection`: it returns only active, unknown or unavailable, never the tenant or any other
data, and adds no database function or query. The response is `{"challenge": …}`, serialized from the parsed value. A
challenge is never stored and never reaches the ingress, the outbox or the Snapshot. Because monday does not document a
JWT on the challenge, the challenge path neither requires nor reads `Authorization` (item 12).

**Event authentication.** An event requires exactly one `Authorization` header of at most 8 KiB holding a JWT, as the
exact bare token or exactly `Bearer` and the token (a 3C compatibility decision, item 10). The token must be three
canonical base64url segments without padding. Its header must be a JSON object whose `alg` is on the one-entry
allowlist, `HS256` (a 3C compatibility assumption, item 9), with no `crit`, and whose `typ`, when present, is `JWT`. The
signature is recomputed with HMAC-SHA256 over the first two segments, keyed by the Signing Secret, and compared in
constant time; no claim is read before it matches. Then `exp` is required and must not have passed (30-second tolerance); `nbf` and
`iat`, when present, must not lie in the future; `aud`, a string or an array of strings, must contain exactly the
connection's audience; and `accountId`, a number or a string of digits, is required. `userId` is provider actor
information and is never read as an identity. `shortLivedToken` is credential material: it is never read, stored,
logged or passed on.

**Signing Secret.** It is read through `IMondaySecretStore` under the fixed name `wce-monday-signing-secret`, which the
product owner fills, at most once per request, and is used only as the HMAC key. It is never in configuration, Core, a
database field, a log, an error, telemetry, an event or the Snapshot, and a caller can never supply it. It must be 1 to
1,024 visible ASCII characters. When it is missing, malformed or unreadable, or the public base address is not
configured, no delivery can be verified, and the event is refused with a retryable 503. The deployed secret store is
still an owner decision (3B), so the Web host registers a store that refuses every read unless an approved one is
registered: as composed today, the endpoint answers challenges and refuses every event, failing closed.

**Account, connection and tenant binding.** The owner's flow validates the monday account before the delivery reaches
ingress. But the account a connection was authorized for is tenant-owned data under forced row-level security,
recorded by 3B in `connector_authorizations`, so it can be read only after the tenant is resolved; reading it earlier
would need a tenant-less query, which 3C may not create. The binding therefore runs inside ingress, after server-side
tenant resolution and before anything is stored, as the smallest provider-neutral extension of the Gate 2 verifier
contract:

1. The Web endpoint verifies the JWT before calling ingress, so an unauthenticated request never reaches the database:
   an invalid token is refused with 401, and an unreadable secret with 503.
2. Ingress runs its unchanged Gate 2 order: shape, the approved resolver, the source match, then
   `ISourceVerifier.VerifyAsync`. The monday verifier verifies the proof again against the named connection's own
   audience, which binds the proof to that connection, before any tenant is set.
3. Ingress sets the tenant from the connection, server-side, and then calls the new member
   `ISourceVerifier.ConfirmAsync`. The monday verifier verifies the JWT once more and reads the connection's
   authorization through the tenant-scoped 3B store. It confirms only when that authorization exists in the trusted
   tenant for this connection and source; it is Active, its connection enabled and its authorizing user active (the 3B
   access-time rule); and the token's `accountId` equals the recorded monday account. Any other answer is a permanent
   `SourceNotVerified` rejection, except a store that cannot be read, which fails the delivery retryably.
4. Only then does ingress write the raw receipt and its outbox marker, in one transaction with the tenant-status check,
   exactly as in Gate 2.

`ConfirmAsync` is mandatory, so no source can skip the binding by omission: every verifier states what it confirms. Its
answer is `Confirmed`, `Refused` or `Unavailable`; an exception counts as a refusal and is logged by type only, as for
verification. A verifier whose proof already binds everything, such as the synthetic Gate 2 source, confirms. The step
reads only the tenant's own data and writes nothing, so a refused delivery still leaves nothing behind (Gate 2 owner
decision 6). A delivery while the connection requires reauthorization is therefore refused; reconciliation (3E)
repairs that gap, and the product owner may decide otherwise.

**Submission.** An authenticated event becomes one `IngressSubmission`: the connection id; source `monday`; schema
version `monday.webhook.v1`; as the raw payload, the body exactly as received; the request's correlation id; and the
token as the proof, which ingress never stores or logs. `event.triggerUuid` becomes the provider event id only when
monday supplies it as text that is a valid identifier. When it is absent or null there is none and none is
synthesized, so the Gate 2 fingerprint and duplicate-candidate path applies; any other `triggerUuid` is malformed and
refused. `originalTriggerUuid`, `changedAt` and `triggerTime` stay in the raw payload for 3D and decide nothing in 3C.
No order is inferred from arrival or from any UUID, and the ordering engine is unchanged.

**Acknowledgement.** The response follows the ingress outcome and is sent only after it:

| Outcome | Status |
|---|---|
| Challenge answered | 200 with `{"challenge": …}` |
| Event stored now, or already stored by an earlier delivery with the same `triggerUuid` | 200, empty |
| Not JSON in UTF-8, or compressed | 415 |
| Body over 256 KiB | 413 |
| Malformed body, challenge, event or `triggerUuid`; a payload ingress cannot store | 400 |
| Missing, malformed or invalid token; wrong signature or audience; expired; account or authorization mismatch | 401 |
| Connection id malformed, unknown, inactive or of another source; its tenant not active | 404 |
| Signing Secret or base address unavailable; ingress failed; database unavailable | 503 |

A 404 does not say whether a tenant exists. Error bodies carry only a fixed code, a fixed message and the correlation
id, never provider content. Every non-2xx answer leaves nothing stored, so monday's once-a-minute retries are safe, and
a delivery with a `triggerUuid` converges on one durable receipt however often it arrives.

**Logs.** The endpoint logs connection ids, correlation ids, fixed reason codes and outcome statuses only: never the
body, a header, the token, a claim, the Signing Secret or provider text. The connector does not log.

**Web host.** It registers only what intake needs: the Gate 2 ingress and the connection check, the 3B authorization
store for the binding, the webhook options (the public base address, from configuration key
`Connectors:Monday:WebhookBaseAddress`), the connector's authenticator and verifier, and the fail-closed secret store.
It activates no relay, consumer, processing, purge, reconciliation or initial sync, and changes nothing in Azure.

**Not in 3C.** No webhook registration or deletion, no `webhooks:write`, no monday write, no normalizer (3D), no
reconciliation (3E), no initial sync (3F); no real monday app, secret, JWT or account; no Key Vault adapter; and no
Azure, RBAC, network, IaC or workflow change.

### 3C contradiction audit (before code)

Every element of the 3C design was compared with PRD v1, System Architecture v1, the 83 Architecture Invariants,
Development Gates v1, the frozen Premium documents and the Gate 2 decisions.

| Element | Canonical rule | Result |
|---|---|---|
| Durable receipt before acknowledgement; the challenge is no work event | System Architecture v1 §3 ("ACK occurs only after durable receipt") | Compatible: 200 only for an acknowledged ingress outcome; a challenge stores nothing |
| Verification before tenant resolution; the tenant resolved server-side | System Architecture v1 §3 lifecycle; invariants 1, 44, 45; Gate 2 decisions 1 and 6 | Compatible: the proof is verified twice before any tenant is set, and the tenant comes only from the connection. The account binding adds a tenant-scoped check after resolution and before the receipt, like the Gate 2 tenant-status check; nothing is stored or acknowledged unless every check passes |
| No new tenant-less query | Gate 2 decision 1 | Compatible: the challenge and ingress reuse the approved resolver only; the new check returns a status, never a tenant |
| Webhooks give speed; reconciliation gives correctness | Invariant 17 | Compatible: nothing about ordering or completeness is inferred; missed and refused deliveries are left to reconciliation (3E) |
| Dedupe by a stable provider event id, otherwise fingerprint candidates | System Architecture v1 §3 (deduplication) | Compatible: `triggerUuid` only when supplied; none synthesized |
| No blind receipt order | System Architecture v1 §3 (ordering) | Compatible: arrival order and UUIDs decide nothing; provider times stay in the payload for 3D |
| Secrets only in the connector's secret storage | PRD v1 §17; invariants 53, 54; D3B-2; Premium §20 constraint 1 | Compatible: the Signing Secret is read only through `IMondaySecretStore` |
| External content is data | Invariant 25; Premium §20 constraint 6 | Compatible: the body is parsed only to tell a challenge from an event and to read `triggerUuid`; nothing in it selects a tenant, a user or a policy |
| A provider actor is no identity; no guessed employee | Invariants 19, 45, 46, 47; D3B-1 | Compatible: `userId` is never read as an identity |
| Revocation at access time | Invariant 58; Premium §20 constraint 7 | Compatible: a disabled connection, or a revoked, reauthorization-required or ownerless authorization, is refused on every delivery |
| Read and ingest only | Development Gates v1; invariant 37; Premium §20 constraint 5; D3C-1 | Compatible: no `webhooks:write`, no mutation, no registration |
| Payloads, tokens and secrets out of logs | PRD v1 §17; System Architecture v1 §10 | Compatible: ids and fixed codes only |
| No AI, no Premium | Development Gates v1; P0 closure | Compatible |
| No Azure change | CLAUDE.md; D3C-1 | Compatible |

**Result: no contradiction, no blocker.** One point is reported for the owner's review rather than decided silently:
the owner's flow validates the monday account before ingress, which is impossible without a tenant-less read, so the
binding runs inside ingress after server-side tenant resolution and before the durable receipt (see *Account,
connection and tenant binding*). The order and guarantees are otherwise the owner's.

### 3C owner clarification (2026-09-30): HS256 and the `Authorization` form

Before the JWT implementation was final, the product owner clarified that neither the JOSE algorithm nor the
`Authorization` form is a documented monday fact. Applied as follows, without weakening any signature, audience,
expiry or account-binding test:

- HS256 is used as a **3C compatibility assumption, which must be confirmed against a real monday board webhook in
  3G** (item 9). The algorithm allowlist is exactly `HS256`; `none`, the asymmetric families (RS, PS, ES, EdDSA),
  other HMACs, other spellings and an empty value are refused, and tests pin each.
- Accepting the bare token and `Bearer <JWT>` is a **3C compatibility decision, to be confirmed in 3G** (item 10). The
  parser was tightened to the exact forms: the scheme must be `Bearer` in that case, followed by one space and the
  token. Lowercase or uppercase schemes, extra whitespace, lists and repeated headers are refused.

### 3C implementation record

Implemented on `869e1c7`, uncommitted. No schema change, migration, package, workflow, IaC, Key Vault or Azure change.

**Core, provider-neutral** (`src/App.Core/Ingress/`): `ISourceVerifier.ConfirmAsync` with `SourceConfirmation`
(`Confirmed`, `Refused`, `Unavailable`; `Unspecified` counts as a refusal), the mandatory post-tenant confirmation;
`IIngressConnectionCheck` with `IngressConnectionStatus` (`Active`, `Unknown`, `Unavailable`), the status-only check a
challenge needs. Neither names a provider or a token format.

**Infrastructure** (`src/App.Infrastructure/Ingress/`): `EventIngressService` calls the verifier's `ConfirmAsync` after
`SetResolved` and before the write transaction; `Unavailable` fails the delivery retryably, anything but `Confirmed` is
a `SourceNotVerified` rejection, and an exception is a refusal logged by type only. Nothing else in the Gate 2 order
changed. `IngressConnectionCheck` (internal) reduces the approved resolver's answer to a status, reports another
source's connection as unknown, and never exposes the tenant; `AddEventIngress` registers it.

**Connector** (`src/App.Connectors.Monday/`): `MondayWebhooks` (the route, written once; the limits; the schema version;
the canonical connection-id parse), `MondayWebhookOptions` (the configured public base address and each connection's
audience), `MondayWebhookBody` (the strict reader, the challenge answer and the ingress submission),
`MondayWebhookToken` (the JWT check, internal), `MondayWebhookAuthenticator` (the Signing Secret through
`IMondaySecretStore`, at most once per request; the exact header forms) and `MondayWebhookVerifier` (the production
`ISourceVerifier`: verification against the connection's audience, then confirmation against the 3B authorization).
`MondaySecretName.SigningSecret` is `wce-monday-signing-secret`. The connector does not log.

**Web** (`src/App.Web/`): `Connectors/MondayWebhookEndpoint.cs` (HTTP only: limits, the two paths, the answers, the
logs), `Connectors/MondayWebhookRegistration.cs` (`AddMondayWebhookIntake`, and `UnconfiguredMondaySecretStore`, the
fail-closed default), and `Program.cs`, which composes intake and maps the endpoint.

**Tests:** unit 692 (+124), security 138 (+9), architecture 29 (+6), integration 308 (+42): 1,167 in all,
1,167/1,167 locally, the 9 Service Bus emulator tests included. The end-to-end tests run the real endpoint in a
real Kestrel server on a loopback port against the local PostgreSQL through the runtime role, with fake tokens and a
fake Signing Secret, and capture every log record of every category at Trace. Three existing pins were updated
deliberately, each still pinning an exact set: the 3B rule that no webhook code may precede 3C became the 3C file rule;
the Gate 2 rule that no production verifier exists became "the monday webhook verifier is the only one and the only
proof reader"; and the 3B pin of fixed secret names gained `SigningSecret`. The three Gate 2 test verifiers implement
`ConfirmAsync` (the throwing one throws), so every Gate 2 test runs unchanged.

**Mutation and vacuity:** 45 mutants, C01 to C45 (`mutate-3c.py`), cover every guard the owner named: the JWT signature and its algorithm
allowlist, expiry, not-before and issued-at, the audience, the account binding and the access-time state, the
post-tenant confirmation in ingress, the connection check, durable-before-acknowledgement, the size, media-type and
encoding limits, `triggerUuid` handling, and the exact `Bearer` form. **All 45 were caught.**

- *First run.* Started in the background, it was stopped by Claude Code at C07 because the machine ran low on
  memory. C01 to C06 had been caught. The kill skipped C07's restore: the one removed line was put back and the file
  verified against the pre-run hash manifest of all 15 changed production files, and the clean build and the full
  suite were repeated.
- *Completion.* On the product owner's go, C07 to C45 ran in the foreground in five batches, each mutant restored and
  hash-checked by the driver, and all 29 changed production and test files hash-checked after every batch.
- *One real test gap, closed test-only.* C37, which lets the endpoint read four times its bound, first survived:
  under Kestrel, the server's per-request body limit, which the endpoint also sets, refused the oversized body first,
  so the endpoint's own bound, the only guard where that server feature is absent or read-only, was never
  exercised. A test-host option now serves requests as a server without that feature, and an end-to-end test proves
  that a chunked body one byte over the bound is refused whole with 413 and stores nothing, while a body at the bound
  is stored whole. C37 was then caught. No production code changed; C38 and C39, the neighbouring limits caught by
  the same test class, were rerun and caught again.
- *Final verification.* Every production file matches the original pre-mutation manifest, except `Program.cs`, whose
  only difference is a comment-only correction made after the restore, proven by reversal; the clean build, the full
  suite, both verifiers and the frozen-document hashes were repeated afterwards.

**Vacuity controls:** the log-leakage test proves the capture includes ASP.NET Core hosting and routing, Entity
Framework Core commands and the ingress; the database-wide scan finds the delivered body in `raw_receipts.payload` as a
positive control while no token, short-lived credential or Signing Secret appears in any table; the "unauthenticated
requests never reach the database" test relies on the same Entity Framework Core categories that the leakage test
proves are captured.

**Local endpoint:** the real Web host, composed by `Program.cs` exactly as shipped, was run locally in Development, which
validates its dependency graph on build, against the local PostgreSQL as the runtime role. A challenge for an active
monday connection was answered 200 with the echo; an unknown connection 404; an event without a token 401; an event
with a token 503, because no approved secret store is registered; `text/plain` 415; and a GET 405. Its console held
connection ids, correlation ids and fixed codes only.

**Security review** (`fully-security-review` checklist; no finding requires a change before review):

1. *The token authenticates the sender, not the body.* monday's JWT carries no digest of the body, so body integrity
   rests on TLS: a token captured in transit or from a log could be replayed with a different body to the same
   connection until `exp` plus 30 seconds. The audience binds it to one connection and the account binding to one
   monday account. 3D and 3E should treat webhook content as a hint that authoritative reread confirms before it can
   change the Snapshot (invariant 17).
2. *No rate limiting on the public endpoint.* Unauthenticated events never reach the database, but a challenge performs
   one approved tenant-less lookup per request, because monday does not sign challenges. Rate limiting or a WAF in
   front of the endpoint is an owner decision (infrastructure or Gate 7).
3. *The challenge reveals whether an id names an active monday connection* (200 or 404), never the tenant. Connection
   ids are version 7 GUIDs with 74 random bits, so enumeration is infeasible.
4. *Deliveries while a connection requires reauthorization are refused* (401); monday retries for 30 minutes, and
   reconciliation (3E) repairs anything after that. Accepting them instead is an owner decision.
5. *When `CD Dev` next deploys the Web image* (an owner approval), the endpoint becomes reachable in Azure Dev. It fails
   closed there: no approved secret store and no base address, so every event is 503; and without the Gate 2B to 2F
   migrations, the challenge's lookup fails, so every challenge is 503 too, logged with the connection id only.

**Still to decide before real webhook registration (3G and the owner):** the monday app and its Signing Secret, placed
in the approved secret store as `wce-monday-signing-secret`; the deployed secret store (a Key Vault adapter and access
for the Web identity); the public base address (`Connectors:Monday:WebhookBaseAddress`) and how monday reaches it; how
webhooks are created and deleted (`create_webhook` and `delete_webhook` need `webhooks:write`, which D3C-1 defers), on
which boards and events, and their removal on disconnect; the Azure rollout of the Gate 2B to 2F and 3B migrations
(with the R3B-2 rule); rate limiting for the public endpoint; and verification items 9 to 15.

### 3C owner review and closure (2026-09-30)

The product owner reviewed 3C and gave it the final pass on 2026-09-30: **3C is PASS**. Decision D3C-1 stays in force
(intake only: no real webhook registration, no `webhooks:write`, no monday write), and the review approved:

1. **Account binding placement.** The monday account is bound in this order: server-side connection resolution →
   trusted tenant context → `ISourceVerifier.ConfirmAsync` → account and connection validation → durable receipt. The
   binding must stay before any durable write of a webhook payload.
2. **HS256** remains a 3C compatibility assumption, not a documented monday fact, and must be confirmed against a real
   monday board webhook in 3G.
3. **`Authorization` parsing** supports only the exact raw JWT and the exact `Bearer <JWT>`; confirming it is a 3G
   compatibility-verification item.
4. **Security follow-ups.** The recorded findings (body integrity beyond TLS, rate limiting for the public endpoint,
   the challenge revealing whether a connection id is active, refusing deliveries while reauthorization is required,
   and the fail-closed behaviour after a future Azure Dev deployment) are accepted as later-Gate work and do not block
   3C.

**Final evidence:** 45/45 mutation checks caught, C37 having exposed a real test gap that was closed test-only;
1,167/1,167 local tests (unit 692, security 138, architecture 29, integration 308); build 0 warnings and 0 errors; both
repository verifiers PASS; the frozen Core and Premium documents byte-identical. Before the closing commit, the clean
build, the full suite, both verifiers, the frozen-document hashes and `git diff --check` were repeated on the tree
being committed. The mutation evidence stands, because only status documentation changed after it.

**3D (source mapping and normalization) is NEXT / NOT STARTED** and begins only on its own authorization. Gate 3
stays IN PROGRESS.

## 3D — Source mapping and normalization

Authorized by the product owner on 2026-09-30 in SAFE AUTO MODE. Baseline: `main` == `origin/main` == remote `main` ==
`60ded62` (the Gate 3C commit), working tree clean, the frozen Core and Premium documents byte-identical to their
recorded hashes. 3D stops at its owner boundary: no commit, no push, no 3E.

### 3D owner decisions (2026-09-30)

- **D3D-1 — A webhook is a change signal, not authoritative state.** The monday JWT authenticates the sender, not each
  work-data field of the body, so a webhook payload alone never becomes authoritative Snapshot state. From a webhook,
  3D may take the object and field that appear to have changed, the event type, the provider actor id, the provider
  timestamps, the provider event id and provenance, and previous and current values as untrusted change hints. A value
  that would become Snapshot truth must be confirmed by the authoritative reread of 3E, so 3D routes the state-changing
  result to `ReconciliationRequired`; it implements no reread and does not start 3E. If the contract cannot express
  this safely without a material Core architecture change, 3D stops and reports the blocker.
- **D3D-2 — Canonical ids, not display labels.** monday's item (pulse) id identifies the source object, the board id
  its board context, the column id the field, the parent item id (where monday supplies it) the parent relationship,
  and `triggerUuid` the provider event id as 3C preserved it. Item names, column titles, group names, user names and
  emails are untrusted display data and never keys, and no id is derived from text.
- **D3D-3 — Actor identity.** monday's `userId` is a provider actor id: kept as provenance and left unresolved. No
  mapping by email or name, no assumption that the connection's OAuth user acted, and no new identity-mapping system.
- **D3D-4 — Ordering evidence.** monday items have no revision, and none is fabricated. `changedAt`, where present and
  valid, is source-update-time evidence; `triggerTime` is occurrence evidence; receipt time is never provider order.
  Nothing is manufactured from receipt time or clamped to "now", and conflicting or malformed timestamps follow the
  existing Gate 2 paths.
- **D3D-5 — Value length.** `EventText.MaxValueLength` stays 1,024 characters and is not widened without owner
  approval. monday data is never truncated or cut, the raw receipt is never replaced, and a value beyond the bound gets
  the existing explicit outcome.

### monday webhook payloads, reverified (2026-09-30)

Reverified against monday's own [webhooks reference](https://developer.monday.com/api-reference/reference/webhooks)
only: the legacy documentation host no longer resolves, and the changelog entries carry no payloads. Nothing
contradicts a material point of the 3D authorization.

| Topic | Verified fact |
|---|---|
| Event types | 21 subscription events: `change_column_value`, `change_status_column_value`, `change_subitem_column_value`, `change_specific_column_value`, `change_name`, `create_item`, `item_archived`, `item_deleted`, `item_moved_to_any_group`, `item_moved_to_specific_group`, `item_restored`, `create_subitem`, `change_subitem_name`, `move_subitem`, `subitem_archived`, `subitem_deleted`, `create_column`, `create_update`, `edit_update`, `delete_update`, `create_subitem_update` |
| Documented payloads | Five only: `create_item` and `create_subitem` (payload `type` `create_pulse`), `change_column_value` and `change_status_column_value` (payload `type` `update_column_value`), and `create_update` (payload `type` `create_update`). A payload's `type` is not its subscription name |
| Column change | `userId`, `originalTriggerUuid`, `boardId`, `groupId`, `pulseId`, `pulseName`, `columnId`, `columnType`, `columnTitle`, `value`, `previousValue`, `changedAt`, `isTopGroup`, `app`, `type`, `triggerTime`, `subscriptionId`, `triggerUuid`. A status column is `columnType` `color` with `value` `{"label": {"index", "text", "style"}, "post_id"}`; a date column is `date` with `{"date", "icon", "time"}` |
| Subitem creation | Adds `itemId` (equal to `pulseId`), `parentItemId` and `parentItemBoardId` (both strings), `groupName`, `groupColor` and `columnValues`; `boardId` is the subitems board |
| Update creation | `body` (HTML), `textBody`, `updateId` and `replyId`; no column and no value |
| Types and times | `userId`, `boardId`, `pulseId` and `subscriptionId` are JSON numbers. `changedAt` is epoch seconds with a fraction (`1633943701.9457765`); `triggerTime` is ISO 8601 UTC with milliseconds (`2021-10-11T09:15:03.429Z`). No item revision or version field exists |
| Specific column | `change_specific_column_value` takes `{"columnId": …}` and does not support subitem columns |

**Open verification items** (continuing the numbering above; none is a contradiction, and none is guessed):

16. The payloads of the other 16 event types are not documented. 3D infers none of them, and 3G records real samples
    before any of them is registered.
17. Whether a subitem column change carries `parentItemId`, and whether its `pulseId` is the subitem's own id, is not
    documented (3G).
18. The status column type appears in webhooks only as `color`. A real payload with another type string is refused
    until it is confirmed (3G).
19. Whether `userId` can take special values, for example for automations, is not documented. Anything but a positive
    numeric id is refused (3G).
20. Status label limits are not documented. A label that is not a valid canonical value is refused, never cut (3G).
21. `changedAt` precision: 3D accepts up to seven decimal places, the resolution a timestamp holds, and refuses more
    rather than rounding (3G).

### 3D event support matrix

The three classes:

- `SUPPORTED_CANONICAL`: normalized to an existing canonical event with canonical field semantics; its values are
  carried only as untrusted hints and the event is held for reconciliation (D3D-1).
- `SUPPORTED_RECONCILIATION_HINT`: normalized to a reconciliation signal without canonical field values. **This class
  is empty in 3D.** A canonical event must change at least one canonical field (Gate 2), and "this object changed,
  reread it" without one would be a new canonical concept, which 3D does not invent.
- `EXPLICITLY_UNSUPPORTED_IN_GATE_3`: refused by the normalizer with a fixed, content-free code, then quarantined by
  processing with its raw receipt kept until its retention purge. Never guessed, never silently dropped, and nothing
  reaches the Snapshot. Missed changes are left to 3E's reconciliation.

Payloads are classified by their documented shape (`type`, `columnType` and the members present), never by a
subscription name.

**Supported (`SUPPORTED_CANONICAL`)**

| Event | Required provider ids | Canonical object | Canonical field and meaning | Actor | Timestamp evidence | Webhook values | 3E reread |
|---|---|---|---|---|---|---|---|
| `change_status_column_value`, and `change_column_value` or `change_specific_column_value` on a status column: payload `update_column_value`, `columnType` `color`, no subitem parent fields | `pulseId`, `boardId`, `columnId`; `triggerUuid` as 3C stored it, when present | Object type `item`, id = the monday item id, homed at the receipt's connection | Field = the column id; data type `TaskStatus`; after = the label text of `value` (null when `value` is null: cleared); before = the label text of `previousValue` (null when null or absent: not reported) | `userId` as an unresolved source actor; none when absent or null; never mapped | `changedAt` = source update time; `triggerTime` = occurrence time; each only when present and valid; no revision; receipt time never | Untrusted hints only; the event declares its values unconfirmed | Required: held as `ReconciliationRequired`, reason `processing.source.unconfirmed` |

**Explicitly unsupported in Gate 3 (`EXPLICITLY_UNSUPPORTED_IN_GATE_3`)**

| Event | Documented payload | Why it is not mapped |
|---|---|---|
| `change_column_value`, `change_specific_column_value` on any other column (date, text, numbers, people, …) | Yes (a date column is shown) | No PRD §5 data type fits. Treating a date column as a `Deadline` would be a guess; under D3D-8, date columns stay unsupported until a later owner-approved mapping exists |
| `change_subitem_column_value` | No | Shape undocumented (item 17). A payload with `parentItemId` or `parentItemBoardId` is refused. One indistinguishable from an item column change is handled as that change of its own `pulseId`, which cannot collapse into the parent. Not to be registered in 3G before a real sample is verified |
| `change_name`, `change_subitem_name` | No | Item names are display data; no canonical concept |
| `create_item`, `create_subitem` | Yes (`create_pulse`) | No canonical creation or lifecycle concept, and `columnValues` beyond `{}` is undocumented. A subitem's own id and its parent's id are distinct and never collapse |
| `item_archived`, `item_deleted`, `item_restored`, `subitem_archived`, `subitem_deleted` | No | No canonical lifecycle concept. Archive and delete are not collapsed into one outcome: both are refused explicitly |
| `item_moved_to_any_group`, `item_moved_to_specific_group`, `move_subitem` | No | Groups are board context; no canonical concept |
| `create_column` | No | A board-schema change, not work-object state |
| `create_update`, `edit_update`, `delete_update`, `create_subitem_update` | `create_update` only | No canonical update or comment concept. The body (HTML) is never logged, never sent to AI and never copied |

Any other payload `type`, and any shape the matrix does not name, is refused as unsupported; 3G may register only the
supported row.

### 3D Definition of Done

Recorded before coding, exactly as the product owner set it. Gate 3D is READY FOR OWNER REVIEW only when:

1. Production monday `ISourceEventNormalizer` exists.
2. Core remains provider-neutral.
3. An explicit webhook event support matrix exists.
4. Stable provider IDs, not display text, drive source mapping.
5. Actors are never guessed.
6. monday item revision is not fabricated.
7. `changedAt` / `triggerTime` have correct distinct semantics.
8. Arrival/receipt order is not provider order.
9. Unknown/malformed events are explicit, never guessed or silently dropped.
10. Item/subitem identities cannot collapse.
11. Provider content remains untrusted data.
12. No silent value truncation occurs.
13. The 1,024-character Core bound is not widened without owner approval.
14. Webhook-only values cannot become authoritative Snapshot state.
15. Reconciliation-required events preserve sufficient bounded identifiers for 3E.
16. No raw-data shadow store is introduced.
17. No API reconciliation is implemented.
18. No initial sync is implemented.
19. No monday write capability is introduced.
20. Full unit/security/architecture/integration evidence is green.
21. Critical mutation/vacuity guards are proven.
22. Gate 2 and 3C regressions remain green.
23. All six frozen documents remain byte-identical.
24. No Azure or real monday account is required.
25. No contradiction exists with Core or Premium P0.

### 3D design

**The smallest provider-neutral extension (D3D-1).** Gate 2's normalizer contract cannot say that a value is
unconfirmed: processing projects an evidenced first change straight into the Snapshot. Its only route to
`ReconciliationRequired` for such an event would be to drop the event's timestamps, which D3D-4 forbids and which would
record a false reason. 3D therefore adds one provider-neutral member to `NormalizedSourceEvent`: `ValueConfirmation`,
either `Confirmed` (the values are the source's state as read from it) or `Unconfirmed` (a change signal whose values
are untrusted hints).

- It is a required constructor argument, so no normalizer can omit it, and `Unspecified` counts as unconfirmed.
- Processing keeps its authority and impossibility checks unchanged. After them, and before any ordering state is read
  or written, it records every unconfirmed event as `ReconciliationRequired` with reason code
  `processing.source.unconfirmed` and every field `Withheld`. Nothing advances and nothing is projected.
- No outcome, stage, table, column or migration is added: the ledger's reason code is free text, and the ledger
  already keeps each event's object, ordering evidence and field names, without values.
- Invariant 13 ("untrusted events do not reach Snapshot") and invariant 17 ask for exactly this. The Gate 2 synthetic
  normalizer declares `Confirmed`, so every Gate 2 behaviour stays as it was.

**Where things live.** The normalizer, `MondayWebhookNormalizer`, and its parsing are in `App.Connectors.Monday`, which
references Core only. It calls no API, reads no credential, writes nothing, does not log and invokes no AI. Core gains
only the provider-neutral member and its reason code.

**Parsing.** The stored body is parsed again as hostile input:

- strict JSON, without comments, trailing commas or duplicate properties, nested at most 64 levels;
- a root object with an `event` object and no `challenge`;
- ids read from the JSON token's raw text, never as floating-point numbers: `pulseId`, `boardId` and `userId` must be
  JSON integers of 1 to 20 digits with no sign, fraction or exponent, and `columnId` must be a canonical token;
- the provider event id the receipt carries must equal the body's `triggerUuid`, or both must be absent. The
  normalizer checks it and never regenerates it;
- `originalTriggerUuid`, `pulseName`, `columnTitle`, `groupId` and a label's index and style are never read as
  identity, order or value;
- numbers and times are parsed culture-invariantly, by exact formats.

**Mapping.** Only the supported row of the matrix is mapped:

- the object is an `item` whose id is the monday item id, homed at the receipt's connection;
- the field token is the column id, and the data type `TaskStatus`;
- after is the label text of `value`, or null when `value` is null (cleared); before is the label text of
  `previousValue`, or null when it is null or absent (not reported);
- the board id is kept, with the item id, in the event's source reference (`board/{boardId}/item/{pulseId}`), which
  expires with the content; the item id alone suffices for the 3E reread;
- the actor is `userId` as an unresolved source actor, or none when it is absent or null;
- `changedAt` becomes the source update time and `triggerTime` the occurrence time, each only when present, well
  formed and in range. There is no revision, and receipt time is not read;
- the event declares `Unconfirmed`.

**Timestamps in conflict.** Malformed timestamps are refused. Impossible ones (a source time after the receipt, or an
occurrence after the update, beyond the tolerated clock skew) are quarantined by Gate 2's unchanged impossibility
check. Every other supported event is held for reconciliation, so no timestamp can make a webhook value current.

**Refusals.** Everything else is refused with one of four fixed, content-free codes, and processing quarantines the
receipt as bad schema:

- `monday.webhook.malformed`: not the documented shape, such as bad JSON, a missing or mistyped member, an invalid id
  or timestamp, or a provider event id that does not match;
- `monday.webhook.unsupported_event`: a payload type or shape the matrix does not support;
- `monday.webhook.unsupported_column`: a column type without a canonical mapping;
- `monday.webhook.unsupported_value`: a status label that is not a valid canonical value, for example longer than 1,024
  characters, multi-line or empty.

**Reconciliation handoff.** For every held event the ledger keeps the tenant (trusted), the connection, the source
system, the provider event id, the object type and monday item id, the field tokens (column ids) and data types, the
source update and occurrence times, and the reason code. No value, text or credential is copied anywhere: the raw
receipt stays the only copy of the body.

**Not in 3D.** No API reread (3E), no initial sync (3F), no monday write or scope, no widened value limit, no host
registration of the normalizer (running processing in any host is an owner boundary), no AI, and no Azure change.

### 3D contradiction audit (before code)

Every element of the 3D design was compared with PRD v1, System Architecture v1, the 83 Architecture Invariants,
Development Gates v1, the frozen Premium documents and the Gate 2 decisions.

| Element | Canonical rule | Result |
|---|---|---|
| Webhook values never become Snapshot state; held for reconciliation | Invariants 13, 14 and 17; System Architecture v1 §2 ("webhooks are fast path; provider API reconciliation is correctness path") and §3 ("if ambiguous, do not overwrite; reconcile") | Compatible: nothing of an unconfirmed event advances or is projected |
| The `ValueConfirmation` extension of the Gate 2 normalizer contract | System Architecture v1 §3 lifecycle; Gate 2 decisions | Compatible: one provider-neutral member and one reason for the existing `ReconciliationRequired` outcome; no new stage, outcome, table or migration, and every Gate 2 behaviour unchanged |
| Only the existing canonical type and PRD §5 data types | PRD v1 §5; `CanonicalEventSchemas`; `FactDataType` | Compatible: status columns map to `TaskStatus`; no event type, data type or concept is added |
| Stable provider ids, never display text | D3D-2; invariant 47 | Compatible |
| Actor left unresolved | Invariants 19 and 47; D3D-3 | Compatible |
| No fabricated revision; `changedAt` as update time, `triggerTime` as occurrence, receipt time never | System Architecture v1 §3 (ordering); invariants 12 and 18 | Compatible |
| 1,024-character bound, no truncation | Gate 2 decision on `EventText`; D3D-5 | Compatible: a value beyond it is refused, never cut |
| Provider content is data | Invariant 25; Premium §20 constraint 6 | Compatible |
| No shadow store; raw content expires | PRD v1 §12; Gate 2 decision 5 | Compatible: the ledger keeps ids, field tokens and evidence only |
| Read and ingest only | Development Gates v1; invariant 37; D3C-1 | Compatible |
| No AI, no Premium, no Azure | Development Gates v1; P0 closure; CLAUDE.md | Compatible |

**Result: no contradiction, no blocker.** One point is reported for the owner's review rather than decided silently:
D3D-1 cannot be met within the Gate 2 normalizer contract without stripping ordering evidence. The smallest
provider-neutral extension above is used instead. It is judged not to be a material architecture change: it adds one
reason to an existing outcome and enforces invariant 13.

### 3D implementation record

Implemented on `60ded62`, uncommitted. No schema change, migration, package, workflow, IaC, Key Vault or Azure change,
and no host registration.

**Core, provider-neutral** (`src/App.Core/Processing/`): `SourceValueConfirmation` and
`NormalizedSourceEvent.ValueConfirmation`, a required constructor argument; `ProcessingErrors.UnconfirmedSourceValues`
(`processing.source.unconfirmed`). Neither names a provider.

**Infrastructure** (`EventProcessor`): after the unchanged authority and impossibility checks, every event not declared
`Confirmed` is recorded `ReconciliationRequired` with the new reason and every field `Withheld`, before any ordering
state is read or written. No other Gate 2 behaviour changed.

**Connector** (`src/App.Connectors.Monday/MondayWebhookNormalizer.cs`): `MondayWebhookNormalizer`, the production
`ISourceEventNormalizer` for schema `monday.webhook.v1`, and `MondayWebhookNormalizationErrors`, its four fixed codes,
as designed above. It is registered in no host.

**Tests:** unit 797 (+105), security 144 (+6), architecture 30 (+1), integration 320 (+12): 1,291 in all, 1,291/1,291
locally, the 9 Service Bus emulator tests included.

- The unit tests are built on monday's documented payloads.
- The provider-neutral processing tests drive the Gate 2 pipeline with the synthetic source.
- The monday end-to-end tests deliver a signed webhook through the real 3C endpoint, then run the real processor with the
  monday normalizer against its durable receipt.
- Four existing pins were updated deliberately, each still exact. Gate 2's "no production normalizer yet" became "the
  monday webhook normalizer is the only production normalizer"; the pinned members of `NormalizedSourceEvent` gained
  `ValueConfirmation`; the processing error count went from 13 to 14, with the new code named; and the 3C rule that no
  normalization code precedes 3D became the rule that normalization lives only in the normalizer file.
- The Gate 2 synthetic normalizer declares `Confirmed`, unless a new provider-neutral test asks otherwise.

**Mutation and vacuity:** 24 mutants, D01 to D24 (`mutate-3d.py`), all caught. They ran in the foreground, a few at a
time, in seven short runs. After every mutant the driver restores the file and checks its hash, and all eleven changed
code files still match the hashes taken before the first run. D11 was first written with a syntax error, which the
driver reports as invalid, never as caught; corrected, it was caught. The mutants cover:

- the object id (D01), and the column id against the column title (D02, D03);
- the unresolved actor (D04, D05);
- the meanings of `changedAt` and `triggerTime` (D06, D07, D09, D10), and the absence of a revision (D08);
- truncation (D11), and previous and cleared values (D12, D13);
- unsupported events and columns, and malformed input (D14 to D20);
- subitem separation (D21);
- D3D-1 on both sides: the normalizer's declaration (D22) and processing's hold (D23, D24).

D22 and D23 made the end-to-end tests fail because a webhook value reached the Snapshot, which also proves those
Snapshot assertions are not vacuous. Two further vacuity controls: the log test proves processing's own records are
captured, and the database-wide scan finds the delivered label in `raw_receipts.payload`, its positive control, and
nowhere else.

**Verification:** clean non-incremental build with 0 warnings and 0 errors; full suite 1,291/1,291; both repository
verifiers PASS; the six frozen documents byte-identical; `git diff --check` clean.

**Security review** (no finding needs a change before review):

1. *monday documents few payloads.* Only status changes are mapped. Every other shape is refused and quarantined with
   its receipt kept, so a real payload that differs from the documented one fails closed until 3G confirms it.
2. *The ledger does not say why a normalizer refused.* Every normalizer refusal is recorded under Gate 2's single code,
   `processing.normalizer.rejected`; the connector's four codes are visible to its tests but not in the ledger.
   Recording them would need a small Gate 2 change, an owner decision.
3. *Held events wait for 3E.* Until the reread exists, no monday webhook changes the Snapshot at all; that is D3D-1's
   intent.
4. *Status labels are provider text.* They are carried as hints in memory only: never persisted outside the raw
   receipt, never logged and never interpreted.

**Deferred to 3E and 3G, and decisions for the owner:**

- 3E: the authoritative reread of each held object, whose item and status columns the ledger names; reconciliation
  sweeps for quarantined and missed events; and object lifecycle (archive, delete, restore), which Core cannot yet
  represent.
- 3G: real payloads for the 16 undocumented event types and for subitem column changes (items 16 and 17); the status
  column type string (18); special user ids (19); status label limits (20); `changedAt` precision (21); and which
  webhook events to register, the supported row only.
- The owner: whether a date column may be treated as a deadline, and which one; whether the ledger should record the
  connector's refusal codes; and whether "this object changed, reread it" should become a canonical concept, which
  `SUPPORTED_RECONCILIATION_HINT` would need.

### 3D owner review and closure (2026-10-01)

The product owner reviewed 3D and gave it the final pass on 2026-10-01: **3D is PASS**. Decisions D3D-1 to D3D-5 stay
in force. The owner explicitly accepted the provider-neutral Core extension `SourceValueConfirmation` (`Unspecified`,
`Confirmed`, `Unconfirmed`) and its processing rule: an `Unspecified` or `Unconfirmed` event is recorded
`ReconciliationRequired`, and no normalized field of it reaches the Snapshot. It is an approved Core implementation
change and needs no change to a frozen document. The review also recorded three decisions, which settle the three
questions the implementation record left to the owner:

- **D3D-6 — Normalizer refusal codes.** The Gate 2 processing ledger is not changed in 3D merely to persist monday's
  four provider-specific refusal codes. The provider-neutral `processing.normalizer.rejected` remains sufficient for
  3D, and the raw durable receipt remains available, under its retention, for investigation. A richer structured
  observability mechanism may be proposed later if needed.
- **D3D-7 — No new canonical "reread object" concept.** No canonical event or outcome is added merely to mean "reread
  this provider object". The handoff to 3E is the existing `ReconciliationRequired` outcome with the trusted tenant
  and connection context, the provider object id, the board (resource) id, and the field id and change provenance. A
  new Core concept is proposed only if implementing 3E proves the existing contract insufficient.
- **D3D-8 — Date columns.** A monday date column is not mapped automatically to the canonical `Deadline`: no approved
  semantic rule says that every monday date is a deadline. Date-column events stay unsupported / reconciliation-only
  until a later owner-approved mapping exists, and no such mapping is added in 3D. The closure message labels this
  decision D3D-5, a number the 3D authorization already gave the value-length decision; it is recorded as D3D-8 so
  that each number names one decision.

**Final evidence:** the production monday `ISourceEventNormalizer` and its explicit event support matrix; only
status-column changes on items are mapped; every monday webhook value is declared `Unconfirmed`; unconfirmed and
unspecified events are recorded `ReconciliationRequired` before any ordering or Snapshot state changes; 24/24 mutation
checks caught; 1,291/1,291 local tests (unit 797, security 144, architecture 30, integration 320); build 0 warnings
and 0 errors; both repository verifiers PASS; the frozen Core and Premium documents byte-identical; no Azure and no
real monday activity. Before the closing commit, the clean build, the full suite, both verifiers, the frozen-document
hashes and `git diff --check` were repeated on the tree being committed. The mutation evidence stands, because only
status documentation changed after it.

**3E (API reconciliation and authoritative reread) is NEXT / NOT STARTED** and begins only on its own authorization.
Gate 3 stays IN PROGRESS.

## 3E — API reconciliation and authoritative reread

Authorized by the product owner on 2026-10-01 in SAFE AUTO MODE. Baseline: `main` == `origin/main` == remote `main` ==
`0c2d18e` (the Gate 3D commit), working tree clean, the frozen Core and Premium documents byte-identical to their
recorded hashes, CI run 36819668481 green, and no CD run since 2026-09-28. 3E stops at its owner boundary: no commit,
no push, no 3F.

### 3E owner decisions (2026-10-01)

- **D3E-1 — The API reread is the authoritative path.** A webhook value stays `Unconfirmed` and is held as
  `ReconciliationRequired`; only a successful, authenticated monday API reread may produce a `Confirmed` value, and
  only then may the Snapshot change. The reread uses the 3A read client and the 3B access-time credential path: no
  second monday HTTP client, and no bypass of credential renewal or revocation checks.
- **D3E-2 — Authoritative status field.** 3E rereads the surface 3D supports, an item's status column, through
  monday's typed `StatusValue` (`id`, `type`, `label`, `index`, `text`, `updated_at`). The canonical value follows
  3D's mapping, preferring the typed `label`, and an empty status follows 3D's clear-value semantics. `index` is kept
  as bounded provenance only where the existing model has a place for it. The field identity stays the column id.
- **D3E-3 — Field timestamp authority.** The status value's own `updated_at` is the source-update-time evidence: never
  receipt time, reconciliation time, a local or database clock, or the item's `updated_at`. When it is unavailable,
  malformed or not safely representable, no timestamp is fabricated: the work stays on the ambiguity /
  `ReconciliationRequired` path and is reported. A fallback to the item's `updated_at` needs owner approval. monday
  has no item revision, and a column-settings revision is never used as one.
- **D3E-4 — Current state beats the webhook value.** The reread's current provider state is authoritative even when it
  differs from the webhook that triggered it; no intermediate webhook value is reproduced.
- **D3E-5 — Known-object reconciliation is not initial sync.** 3E reconciles events already recorded
  `ReconciliationRequired` and source objects Fully already knows. It discovers no workspace, ingests no board,
  creates no initial Snapshot, sets no initial-sync watermark and implements no buffered replay. If known objects
  cannot be enumerated safely and provider-neutrally, 3E stops and reports.
- **D3E-6 — Crash-safe completion.** Work is resolved only after the confirmed state was applied through processing
  and the result recorded durably. A crash at any point leaves the work safely retryable or idempotently repairable,
  never resolved while its Snapshot repair is missing.
- **D3E-7 — No direct Snapshot bypass.** The reread produces the equivalent of a `Confirmed` normalized source event,
  and the existing ordering and Snapshot logic decides the write. No monday-specific Snapshot SQL, and
  `App.Connectors.Monday` never references Infrastructure.
- **D3E-8 — Response binding.** A response is authoritative only when the returned item id, board id, column id and
  column type equal the requested item, the expected board, the expected field and the supported status type; the
  connection, tenant and source are still the trusted ones; and the identity used is still the expected 3B delegated
  identity. Any mismatch fails closed, and nothing is remapped to what monday returned.
- **D3E-9 — Item state.** Absence is never read as deletion. A reported `archived` or `deleted` state is provider
  evidence: the item's status value is not applied as an active task's, and nothing archived or deleted is
  resurrected. A lifecycle is reconciled only if the canonical model already represents it; otherwise the work stays
  unresolved with an explicit outcome and the Core gap is reported.

### monday item and status reads, reverified (2026-10-01)

Reverified against monday's own API reference: items, the status column, column values, columns, multi-level boards,
errors and rate limits. Nothing contradicts a material point of the 3E authorization; new points are marked.

| Topic | Verified fact |
|---|---|
| `items(ids:)` | Up to 100 ids "using the `limit` argument"; `limit` defaults to 25, at most 100. `exclude_nonactive` excludes inactive and deleted items, and items of deleted items, only with `ids`. `state` is `active`, `archived` or `deleted` (`all` is a filter value). Whether an inaccessible or unknown id is omitted or reported as an error is not documented |
| The 3A query | `items(ids: $ids, limit: $limit)` with `limit` set to the number of ids and without `exclude_nonactive`, so archived and deleted items come back with their state. It reads `id name state created_at updated_at board { id } group { id } column_values { id type text value }`, none of `StatusValue`'s typed fields; the parser refuses an unrequested id, and a missing id is simply absent |
| `StatusValue` | `id`, `type`, `label` and `index` (both null when the status is empty), `text` (null when empty), `updated_at` ("the column's last updated date"), `value` (`{"index": N}`), `is_done`, `label_style`, `update_id`, `column` and `is_leaf`. New: at most 40 labels per column, each at most 30 characters |
| Timestamps | `updated_at` is a `Date`, documented as `2026-03-21T08:36:10+00:00` |
| Column identity | New: the API names a status column's type `status`, while webhooks report `color` (3D). Column ids are unique only within a board |
| Multi-level boards | New: a status column with rollup resolves to `BatteryValue` for every item, leaves included, and `... on StatusValue` does not match it; `column_values` omits rollup columns unless `capabilities: [CALCULATED]` is passed. `hierarchy_type` is `multi_level` or `classic` |
| Errors and limits | Application errors usually return HTTP 200 with an `errors` array, possibly with partial data: `ResourceNotFoundException`, `UserUnauthorizedException` (403), `missingRequiredPermissions`. Every rate-limit error carries `retry_in_seconds`; an app's reads have a budget of 5,000,000 complexity points per minute |

### 3E Definition of Done

Recorded before coding, exactly as the product owner set it. Gate 3E is READY FOR OWNER REVIEW only when:

1. A production provider-neutral reconciliation path exists.
2. monday authoritative reread uses the existing 3A client.
3. Every provider call uses 3B access-time credentials.
4. Gate 3D webhook values remain Unconfirmed.
5. Only API-reread values may become Confirmed.
6. Supported status values are reread using stable item/board/column identifiers.
7. `StatusValue.updated_at` is the preferred field ordering evidence.
8. No monday item revision is fabricated.
9. No local/receipt timestamp is substituted for provider ordering evidence.
10. Returned item/board/column/type are validated against the requested source reference.
11. Current API state wins over stale webhook hints.
12. Reconciliation applies through the existing ordering/Snapshot path.
13. Snapshot is never written by monday-specific SQL.
14. Reconciliation completion is crash-safe and idempotent.
15. Duplicate reconciliation converges.
16. Provider transient/rate-limit failures leave work safely retryable.
17. Disabled/revoked/reauthorization-required connections fail closed.
18. Tenant suspension fails closed.
19. Known-object reconciliation can detect a missed status webhook.
20. Reconciliation does not discover new provider objects.
21. Gate 3F initial sync is not implemented.
22. Unsupported/missing/archived/deleted/provider-shape cases are handled explicitly, never guessed.
23. The 5-minute Gate 2 clock-skew rule is tested and unchanged unless separately approved.
24. EventText 1,024 limit remains unchanged and nothing is silently truncated.
25. No provider text/secrets/raw responses leak to logs or shadow storage.
26. Full unit/security/architecture/integration evidence is green.
27. Critical mutation/vacuity guards are proven.
28. Gate 2, 3C and 3D regressions remain green.
29. All six frozen documents remain byte-identical.
30. No Azure or real monday activity is required.
31. No contradiction exists with Core or Premium P0.

### 3E pre-code inspection

**What a 3D `ReconciliationRequired` record holds.** `event_ledger`: the tenant, the receipt, the source system and
connection, the provider event id (`triggerUuid`), the event type and schema, the object's system, connection, type
(`item`) and id (the monday item id), `changedAt` as source update time, `triggerTime` as occurrence time, the receipt
and processing times, the correlation, the outcome and the reason `processing.source.unconfirmed`.
`event_ledger_fields`: each field token (the column id), its data type (`TaskStatus`) and its decision (`Withheld`).
No value and no reference.

**The board id is held nowhere durable.** 3D validated it but placed it only in the event's source reference
(`board/{boardId}/item/{itemId}`), which lives in memory and is stored nowhere. The ledger, the ordering state and the
Snapshot have no column for it, and no Snapshot object knows its board. Its only durable copy is inside the
authenticated webhook body in `raw_receipts.payload`, which the retention purge clears at 28 days.

**Everything else 3E needs exists:**

- the tenant, connection, source, item id and column id are durable, and Snapshot objects can be enumerated per
  tenant, connection, source system and object type, so known objects can be listed without discovering anything;
- the 3A client rereads up to 100 items by id in every state and records the identity it used;
- the 3B credential source refuses a revoked, disabled, reauthorization-required or deactivated connection before
  every attempt;
- ingress already refuses a suspended tenant inside its write transaction (`TenantStatus.Suspended`), and
  reconciliation can do the same;
- 3A's retry policy honours monday's waits within its bound and returns longer ones as failures.

### 3E blocker (stopped before code)

D3E-8 requires the returned board to equal the expected board, and the 3E inputs name the board id as a required
stable identifier. Column ids are unique only within a board, and an item can move to another board, so without the
expected board a reread could apply another board's column of the same id to the canonical field. The existing
reconciliation record lacks the board id, and the owner's instruction for that case is to stop and report the smallest
provider-neutral extension, never to parse an identity out of text. 3E therefore stopped before any code was written.

The options for the owner:

- **A (recommended): a typed scope on the normalized event, kept in 3E's own reconciliation state.**
  `NormalizedSourceEvent` gains one optional provider-neutral member, the source scope: a bounded identifier of the
  provider resource that scopes the object's field identities (for monday, the board). The monday normalizer sets it
  from the `boardId` it already validates. One tenant-owned table with forced row-level security holds a
  reconciliation target per object and field: its identity, the field and data type, the expected scope, and the
  claim, attempt, retry and outcome state. Processing writes or refreshes the target in the same transaction that
  records a `ReconciliationRequired` outcome, so the expected board is durable before the work exists, and
  reconciliation updates it in the transaction that applies the confirmed state. The targets are also the known
  objects a sweep may reread, each with its expected board, so nothing is discovered. The canonical event, the ledger
  and the Snapshot do not change.
- **B: the scope becomes part of the canonical object.** `ObjectReference`, and with it the canonical event, the
  ledger, the ordering state and the Snapshot, gains the scope. Equally strict, but larger: it changes the canonical
  object model and every Gate 2 table that keys objects.
- **C: no expected-board check in 3E.** Item, column, type and state are bound, and the returned board is only
  provenance. The smallest change, but weaker than D3E-8: a column of another board with the same id would not be
  detected.

With option A or B, the rest of the Definition of Done is reachable within SAFE AUTO MODE. The planned design, which
needs no further decision:

- each authoritative read becomes a durable receipt in `raw_receipts`, holding a minimal normalized read document
  rather than the raw API response; the same processing core decides it, in one transaction with the target's
  resolution, and the existing retention purges it;
- the 3A item read gains a narrow status read: `column_values(ids:)` with a `StatusValue` fragment reading `label`,
  `index` and `updated_at`, the API's `status` type, and strict parsing of `updated_at`;
- a rollup column, which monday omits by default, is reported as unsupported, never as a cleared value;
- reconciliation runs only when invoked: no hosted scheduler and nothing activated in Azure.

### 3E contradiction audit (before code)

Every 3E decision was compared with PRD v1, System Architecture v1, the 83 Architecture Invariants, Development Gates
v1, the frozen Premium documents and the Gate 2 decisions.

| Element | Canonical rule | Result |
|---|---|---|
| Only an authenticated API reread confirms a value; webhook values stay unconfirmed | Invariants 13 and 17; System Architecture v1 §2 and §3 | Compatible |
| Confirmed state applied through processing; no Snapshot bypass | System Architecture v1 §3 lifecycle; Gate 2 decisions | Compatible |
| The status value's `updated_at` as update-time evidence; no fabricated revision; read and receipt time never order | System Architecture v1 §3 ordering; invariants 12 and 18 | Compatible |
| Known objects only; no discovery, initial sync or watermark | System Architecture v1 §2 initial-connection sequence (3F); Development Gates v1 | Compatible |
| Archived and deleted items left unresolved while Core has no lifecycle | PRD v1 §5; invariants 13 and 22 | Compatible: the gap is reported, not invented |
| Read only; no write scope | Development Gates v1; invariant 37; D3C-1 | Compatible |
| Board binding with no durable board id | D3E-8 and the 3E inputs | Not a contradiction with a frozen document: an owner-instruction stop, needing the smallest provider-neutral extension (blocker above) |

**Result: no contradiction with the frozen documents.** One owner-instruction stop: the board id. Nothing was
implemented.

### 3E owner decisions, continued (2026-10-01)

The product owner chose option A and continued 3E from the stopped state, keeping its documentation:

- **D3E-10 — Provider-neutral source scope.** `NormalizedSourceEvent` may carry an optional, stable source scope: a
  bounded opaque identifier of the provider resource that disambiguates the source field. Core does not interpret it,
  has no board semantics and stores no display name or title. For the supported monday status event the scope is
  required and holds the validated `boardId`; its absence fails closed; it is never parsed from `SourceReference` text
  or recovered from display data. `ObjectReference`, Snapshot identity and the frozen canonical event architecture do
  not change.
- **D3E-11 — Reconciliation-target persistence.** One provider-neutral, tenant-owned table holds only the operational
  metadata reconciliation needs: no copy of a work value, no status label, no raw API body, no OAuth material, no
  signing secret and no user or display text. It has forced row-level security, tenant-inclusive foreign keys where
  they apply, least privilege for the runtime role and no tenant-less lookup, and it is scoped by connection and
  source. Processing writes or updates the target in the same transaction as each `ReconciliationRequired` outcome, so
  no such outcome commits without the identifiers that execute it.
- **Generations.** A target is versioned. A newer request raises its generation, and a worker that claimed generation
  N can never close N+1: resolution is a compare-and-swap on the generation, so neither a crash nor a race loses a
  later request. Applying a current read may still advance the Snapshot through normal ordering. No process-local lock
  decides correctness.
- **Known objects.** A target reconciled successfully keeps its stable source identity as the known-object metadata
  the missed-webhook sweep may enumerate. The sweep enumerates only those targets and never queries a board or
  workspace; discovery stays 3F.
- **Board mismatch.** A reread binds the account and connection authority, the item id, the expected scope, the column
  id and the supported status type. The same item returned on another board fails closed and stays unresolved: the
  expected board is never replaced by what the API returned, and item moves are left to later real-provider and
  lifecycle work.
- **Read-evidence receipts.** No normalized or canonical document is stored as raw evidence. `raw_receipts` may be
  reused only if it can honestly represent an internally initiated provider observation: a distinct receipt holding
  only the exact provider evidence consumed, clearly distinguishable from a webhook delivery, under the existing
  retention, with no full response, credential or unrelated field. Otherwise 3E stops and reports the smallest
  provider-neutral schema extension; no webhook receipt is faked and ledger causation is not weakened.
- **Renamed labels.** A different label whose `updated_at` does not let the existing ordering engine order it newer is
  never forced into the Snapshot: no reconciliation time or item `updated_at` is substituted, the work stays
  unresolved, and the case is a 3G verification item. The Gate 2 ordering algorithm does not change.

### 3E design

**Source scope.** `NormalizedSourceEvent` gains `SourceScope`, a constructor argument every normalizer must state,
null when its source has no scope, and validated as a bounded opaque identifier (`EventText.IsIdentifier`). The monday
normalizer passes the `boardId` it already validates; the Gate 2 synthetic normalizer passes none unless a test asks.
It is provider-neutral: neither the name, the type nor the validation says "board", Core never parses or interprets
it, and only the monday connector knows that a monday scope is a board.

**Reconciliation targets.** Table `reconciliation_targets`, one row per tenant, source system, connection, object
type, object id and field, the key of the ordering state and of a Snapshot field. It holds:

- identity: the field's canonical data type and the source scope;
- lifecycle: the state (`Pending`, `Resolved` or `Held`), the generation, the resolved generation, the kind of the
  latest request (`Event` or `Sweep`), when it was requested and the ledger event that requested it;
- claim: the claim id, the claimed generation and the lease expiry, all present or all absent;
- retries: the attempts at the current generation, the next and the last attempt, and the last outcome code;
- results: when it was resolved, the ledger event that resolved it, and when it was last verified current.

No column can hold a value, a label, a response, a credential or display text. Forced row-level security with the
standard tenant policy, a restrictive no-delete policy, foreign keys to the ledger that include the tenant, and
SELECT, INSERT and UPDATE only for the runtime role: rows are never deleted. The connection is named by its connection
reference, as the ledger and the Snapshot name it.

**Lifecycle.**

- *Request.* Processing upserts the target in the transaction that records `ReconciliationRequired`. A new target
  starts `Pending` at generation 1. An existing target's generation rises by one, its attempts reset and it is
  `Pending` again, unless the request names another scope or data type: the stored identity is then never replaced,
  and the target is `Held` with a scope-conflict outcome.
- *Claim.* A worker claims due `Pending` targets of one tenant, source and connection with `FOR UPDATE SKIP LOCKED`,
  recording its claim id, the claimed generation and a lease; a target whose lease expired can be claimed again.
- *Read.* Outside any transaction, through the source's authoritative reader.
- *Apply.* One transaction per target. The target is locked and the claim checked. The tenant must be active, the
  connection active and its authorization active and held by the delegated identity the read used. A read equal to the
  Snapshot, the same value with the same update time, resolves the target with no event. Any other read is stored as
  an evidence receipt and decided by the unchanged Gate 2 processing core as a `Confirmed` event, projected by the
  Snapshot projection, and the target's outcome is written in the same transaction.
- *Resolve.* Compare-and-swap: the target becomes `Resolved` only while its generation still equals the claimed one;
  otherwise it stays `Pending` for the newer request. No failure resolves a target.
- *Retry or hold* (revised after the owner's review; see "3E owner review" below). A transient failure (a rate
  limit, monday unavailable, a credential that cannot be obtained just now) is retried on the capped backoff for as
  long as it lasts and is never held. A refusal of access (the tenant inactive; the connection, its authorization or
  its user not standing) makes no provider call: every later attempt checks access locally first, on the same backoff,
  and the target resumes by itself once access stands. An unusable answer (the item missing, a response the reader does
  not understand) is retried and held after eight consecutive such answers. What retrying cannot change holds at once:
  an archived or deleted item, a binding mismatch, a missing or unsupported column, an invalid value, ambiguous,
  unevidenced or impossible ordering, a quarantine, a missing scope, a scope conflict, or a read under another identity
  while the authorization did not change. A read overtaken by a new authorization is discarded and retried (see "3E
  final owner review" below). A held target reopens only on a new request.

**Sweep.** It requests the least recently verified `Resolved` targets of one tenant, source and connection, a bounded
number per call, by raising their generation. It never inserts a target, so it can only reread objects Fully has
already reconciled.

**Read-evidence receipts, verified against the receipt and ledger semantics.** `raw_receipts` can represent the
observation honestly, so 3E does not stop here:

- `schema_version` exists to say what a payload is: an observation gets its own version, `monday.api-read.v1`,
  distinct from `monday.webhook.v1`; it has no provider event id, and the database stamps its receipt time;
- the payload holds only the provider values consumed, each exactly as the response held it: the item's `id`,
  `state` and `board.id`, and the column value's `id`, `type`, `label`, `index` and `updated_at`, beside the operation
  name and the pinned API version. No other provider field, no full response and no credential;
- it is created and settled inside the apply transaction, with no outbox message, so the transport never processes it;
  the webhook normalizer does not support its schema, so it could never be read as a webhook delivery; and nothing
  reads a receipt to decide confirmation: only a live, bound read produces `Confirmed`;
- the ledger's causation is unchanged: its entry names that receipt; and the existing retention purges the content
  like any raw work data, so no second store exists.

**monday reader.** A new read-only operation of the 3A client: `items(ids:, limit:)` with `id state board { id }` and
`column_values(ids:)` returning `id`, `type` and, through a `StatusValue` fragment, `label`, `index` and `updated_at`;
`limit` is the number of ids. It uses the same credential source, query guard, bounds, classification and retry
policy, and strict parsing: an unrequested or repeated item, an unknown state, a missing board, a column of another
type, a fragment that did not match or a label without its index each fail closed. `updated_at` is parsed strictly;
when it is absent or unreadable, the read carries no update time, and processing records `ReconciliationRequired` for
want of ordering evidence.

**Ordering.** The status value's `updated_at` is the source update time; there is no revision and no occurrence time,
and the read time orders nothing. The database's receipt time feeds the unchanged 5-minute plausibility check.

**Running.** Reconciliation runs when invoked, for one tenant and connection; no hosted scheduler, and nothing
registered in the Web or Worker host or activated in Azure.

**Audit additions (before code).**

| Element | Canonical rule | Result |
|---|---|---|
| Optional source scope on the normalized event | System Architecture v1 §3 normalization; D3E-10 | Compatible: provider-neutral, opaque and bounded; the canonical object, event and Snapshot unchanged |
| Reconciliation targets written with each `ReconciliationRequired` outcome | Gate 2 decisions 1 and 5; invariants 44 and 45 | Compatible: tenant-owned with forced row-level security and no tenant-less function; metadata only, no content |
| Read evidence in `raw_receipts` | PRD v1 §12; Gate 2 decision 5; invariant 20 | Compatible: one raw store, the existing retention, only the consumed provider values |
| A generation compare-and-swap decides resolution | System Architecture v1 §3 lifecycle; D3E-6 | Compatible: idempotent and crash-safe in the database, no process-local lock |

**Result: no contradiction, and the receipt semantics need no schema extension.** Implementation proceeds.

### 3E implementation record

Implemented on `0c2d18e`, uncommitted. One additive migration, `20261001073251_ReconciliationTargets`, applied only to
the local database, by the tests' fixture. No package, workflow, IaC, Key Vault or Azure change, and no registration
in any host.

**Core, provider-neutral.** `NormalizedSourceEvent.SourceScope` (D3E-10): a constructor argument every normalizer
states, null or a bounded opaque identifier. `src/App.Core/Reconciliation/`: the target (`ReconciliationTarget`); the
read contract (`AuthoritativeReadRequest`, `AuthoritativeReadResult`, `TargetReading`, `AuthoritativeFieldState`,
`ObservationEvidence`); the reader port `IAuthoritativeSourceReader`; the runner port `ISourceReconciler` with
`ReconciliationRun` and `ReconciliationRunSummary`; `ReconciliationErrors`; and the closed `ReconciliationOutcome` set
with fixed codes and dispositions (resolve, retry or hold).

**Infrastructure.**

- `reconciliation_targets` (entity, model, migration): forced row-level security, the tenant policy, a restrictive
  no-delete policy, SELECT, INSERT and UPDATE only for the runtime role, and foreign keys to the ledger that include
  the tenant.
- Processing: the decision after normalization is shared (`DecideNormalizedAsync`). A delivery's
  `ReconciliationRequired` writes its fields' targets in the same transaction, through an atomic upsert
  (`ReconciliationTargets.RequestAsync`). Reconciliation enters only through `DecideAndProjectAsync`, so the processor
  stays the only caller of the Snapshot projection; `SnapshotProjection.HoldsAsync` is the read behind the
  already-current check.
- `SourceReconciler`, `ReconciliationOptions` and `AddSourceReconciliation`, which only registers.
  `EventIngressService.Fingerprint` is shared, so evidence receipts are fingerprinted like deliveries.

**Connector.** The 3A read model gained the read-only operation `monday.items.read-status-by-id`, its query,
`MondayReadClient.ReadItemStatusesByIdAsync` and its strict parser (`MondayItemStatuses.cs`); `MondayStatusReader` is
the monday `IAuthoritativeSourceReader`; and the 3D normalizer carries the board as the source scope.

**Tests:** unit 870 (+73), security 152 (+8), architecture 31 (+1), integration 352 (+32): 1,405 in all, 1,405/1,405
locally, the 9 Service Bus emulator tests included.

- Unit: the Core contracts, the closed outcome set, the reader against a deterministic fake of monday (binding,
  refusals, strict times, exact evidence, failure mapping, identity), and the 5-minute rule at its exact bound on
  monday-shaped times.
- Integration: the whole path end to end (23), from a signed webhook through the real 3C endpoint, the real processor
  and the real reader and client to the Snapshot, including the real 3B credential lifecycle; provider-neutral
  requests (6), including a forced crash between a hold and its target; and the table on databases created for the
  test (3).
- Security and architecture: Confirmed constructed only by the reconciler after binding; no fabricated revision,
  occurrence or local time; the Snapshot untouched by reconciliation itself; no value column; the migration's grants
  and policies; no host registration; and provider-neutral reconciliation code.
- Deliberate pin updates, each still exact: monday's documents and operations (4 to 5), the pinned members of
  `NormalizedSourceEvent` (`SourceScope`, a source statement), the connector files that mention webhooks (the reader,
  which reads the surface the normalizer maps), the database policy list (the two target policies), and the
  cross-tenant acceptance test, which now also attacks the target table and seeds a hold per tenant.
- Test infrastructure: `FakeMonday` gained an awaitable API answer and an answer built from the request;
  `SyntheticEvent` can state a source scope.

**Mutation and vacuity:** 38 mutants, E01 to E37 and E39 (`mutate-3e.py`), all caught, in nine short foreground runs.
After every mutant the driver restores each changed file and checks its hash, and all 37 changed code files matched
their recorded hashes after every run; the two test files strengthened between runs were recorded again.

- E03, a repeated item accepted by the parser, first survived. With one requested item the response's count bound
  already refused a repeat, so no test exercised the repeat check. A test-only fix asks for two items and returns one
  twice; E03 was then caught.
- E37, reconciliation requesting reconciliation again, was first caught only by a static pin. The end-to-end test now
  also pins the held target's generation and request kind, and catches it.
- The mutants cover every guard the owner listed: item, board, column and type binding (E01 to E07); archived and
  deleted items (E08); the status value's own time, strict parsing, no local or receipt time, no revision and no
  occurrence (E09, E10, E12 to E15); no truncation (E11); Confirmed only from a reread, with the board as scope (E16,
  E39); completion only with the Snapshot commit, the generation compare-and-swap and lost claims (E17 to E19);
  identical and current reads (E23, E24); standing, suspension and identity (E20 to E22); requests (E25 to E27, E37);
  retries (E28, E29); known objects only (E30); logs and evidence (E31, E32); and the table's row-level security,
  no-delete policy, grants and tenant isolation (E33 to E36).
- Vacuity: with row-level security disabled, the isolation check sees the other tenant's target; the log test proves
  the run's own summary is captured; and the database-wide scan finds the label in the receipts and the Snapshot, its
  positive controls, and never in a target.

**Verification:** clean non-incremental build with 0 warnings and 0 errors; full suite 1,405/1,405; both repository
verifiers PASS; the six frozen documents byte-identical; `git diff --check` clean; and no changed file mixes line
endings.

**Security review** (no finding needs a change before review):

1. *Identity at apply time* (corrected in the final owner review). A read must have used the connection's current
   delegated identity, under the authorization that access found before the read. A renewal keeps the authorization.
   3B lets only the connection's own monday user authorize it again, so an authorization by another monday user cannot
   happen; an authorization again during the read discards and retries the read, and a read under another identity
   with the authorization unchanged is held.
2. *Duplicates.* The parser refuses a repeated or unrequested item, and the reader relies on it: the parser is the
   only way into the reader's binding.
3. *Evidence.* Each consumed value is copied exactly as monday returned it; PostgreSQL stores the document as jsonb,
   which keeps every value but not its whitespace or key order. It is purged with all raw content, and the ledger
   keeps its ids.
4. *Held targets* never reopen on their own: a sweep verifies only resolved targets, and only a new request reopens a
   held one. An item that stays archived, deleted or moved stays held, visible by its outcome code, until lifecycle
   work decides it.
5. *The bootstrap rerun hazard (R3B-2)* now also covers `reconciliation_targets`: a rerun re-grants DELETE, which the
   restrictive policy still refuses, and the Azure rollout's re-assertion must include the table.
6. *Leases.* Workers whose clocks disagree by more than a lease could both claim a target, but the claim check in the
   apply transaction lets only the current holder record anything.

**Deferred to 3F and 3G, and owner items:**

- 3F: initial sync, the watermark, buffered replay and discovery.
- 3G, on a real workspace: what `items(ids:)` returns for an unknown or inaccessible id (retried, then held); the
  precision and format of `updated_at`; whether renaming a status label changes `updated_at` (held, as approved); the
  5-minute rule against real data; the `status` and `color` types; and multi-level boards, whose rollup columns monday
  omits (held).
- The owner: running reconciliation in a host and in Azure, including how a worker finds the tenants and connections
  with due targets, which needs a tenant-less lookup that Gate 2 decision 1 does not allow today; applying the
  migration in Azure; and lifecycle semantics for archived, deleted, restored and moved items.

### 3E owner review (2026-10-01): transient failures are never held

The owner accepted the 3E implementation except for one blocker. Transient failures (a rate limit, a monday outage, a
secret store that cannot be read) and conditions of access (authorization needed again, a suspended tenant) were
retried eight times and then held, and a held target never reopens on its own, so a condition that outlasted eight
attempts disabled reconciliation of the field until a new webhook arrived. Retrying is now split by what retrying can
change. Nothing else changed: the source scope, the evidence receipts, the item, board, column and type binding, the
`StatusValue.updated_at` ordering evidence, the confirmation rules, the Snapshot processing path, the 5-minute rule,
the 1,024-character bound, the schema and the frozen documents.

**Classification.** Every outcome has exactly one disposition, fixed in Core (`ReconciliationOutcomes`):

| Outcome | Code | Disposition | Behaviour |
|---|---|---|---|
| `Current`, `Repaired`, `Superseded` | `reconciliation.current`, `reconciliation.repaired`, `reconciliation.superseded` | Resolve | Resolved, by compare-and-swap on the claimed generation. |
| `RateLimited` | `reconciliation.provider.rate_limited` | Retry (transient) | Retried on the capped backoff for as long as it lasts; never held. |
| `ProviderUnavailable` | `reconciliation.provider.unavailable` | Retry (transient) | As above: monday failed or could not be reached, or the reader failed. |
| `CredentialUnavailable` | `reconciliation.credential.unavailable` | Retry (transient) | As above: the credential's state or secret could not be read, a renewal is undecided, or what was read did not match the connection. Nothing was sent. |
| `ConnectionUnavailable` | `reconciliation.connection.unavailable` | AwaitAccess | The connection disabled or disconnected, unknown or of another source; its authorization revoked, needing authorization again or of an inactive user; or monday rejected the credential or the identity. No provider call while it lasts; resumes by itself; never held. |
| `TenantInactive` | `reconciliation.tenant.inactive` | AwaitAccess | As above, for a tenant that is not active. |
| `AuthorityChanged` | `reconciliation.authority.changed` | AwaitAccess | The authorization changed while monday answered: the read is discarded, never applied, and the next attempt reads again under the current authorization. Added in the final owner review. |
| `ObjectMissing` | `reconciliation.object.missing` | RetryThenHold | Held after 8 consecutive such answers; what monday returns for a missing or inaccessible id is a 3G item. |
| `UnsupportedResponse` | `reconciliation.provider.unsupported_response` | RetryThenHold | Held after 8 consecutive such answers. |
| `ObjectInactive`, `BindingMismatch`, `FieldMissing`, `FieldUnsupported`, `ValueInvalid`, `OrderingAmbiguous`, `OrderingUnevidenced`, `OrderingImpossible`, `Quarantined`, `ScopeMissing`, `ScopeConflict`, `IdentityMismatch` | unchanged | Hold | Held at once: retrying the same evidence cannot change the answer. Only a new request reopens it. |

`IdentityMismatch` stays a hold for a stable mismatch only: the read was not made under the connection's current
delegated identity although the authorization did not change during the read, and D3E-8 fails closed. A read overtaken
by a new authorization is `AuthorityChanged`, discarded and retried (see "3E final owner review"); the example given
here before, another monday user authorizing the connection during the read, cannot happen under 3B. Work waiting for
access is read under whatever authorization stands when access returns, so it is not affected.

**Retry and hold behaviour.**

- `attempts` counts the current run of like failures: every claim counts one, and a failure of another disposition
  than the last starts the count again, so an outage never spends the bound of a missing item. While a transient or
  access failure lasts, the count keeps rising, for telemetry.
- Only `RetryThenHold` outcomes are held for their attempts, after `MaxAttempts` (8) consecutive ones.
- The backoff is 30 seconds doubling over the current run, capped at 1 hour, so no attempt comes sooner than 30
  seconds. A longer wait the provider asks for wins, up to `MaxProviderWait` (1 day, the bound 3A already puts on
  monday's waits), so no answer parks a target indefinitely.
- The reader reports a credential that monday or 3B refuses (revoked, needing authorization again, rejected after a
  renewal) as `ConnectionUnavailable`; only a credential that cannot be obtained just now is `CredentialUnavailable`.
  The local access check reports an inactive authorization or user as `ConnectionUnavailable` (it was
  `CredentialUnavailable`).

**Automatic recovery.**

- Every run checks access locally, after its claim and before any read: the tenant active, the connection active and
  of the run's source, its authorization active, and its user active. While any of them does not stand, no credential
  is asked for and nothing is sent to monday; each claimed target records the refusal and waits on the backoff.
- Its next due attempt checks again. Once 3B restores the authorization (the connection's user authorizes it again),
  the user is active again, or the tenant is active again, the target is read at that attempt, at most an hour later,
  with no new webhook, no manual database change and no discovery.
- The check reads only the run's own tenant, under its row-level security: no cross-tenant path. The apply transaction
  checks access again, as before, for a change during the read.
- 3B never re-enables a disconnected connection. Its targets keep waiting, checked locally at most hourly when a run is
  invoked for that connection, and are never held; whether to retire them belongs with the host activation decision.
- Semantic holds are unaffected: a sweep verifies only resolved targets and never reopens a held one.

**Tests**, written first: 3 unit and 10 integration tests failed before the change, each for the intended reason.

- Unit: the classification table and the parsing of recorded codes; the reader's mapping of refused credentials, and
  a 401 that a renewal does not cure.
- Integration (`MondayReconciliationTests`, now 33):
  1. to 3. A rate limit, a monday outage and a credential that cannot be obtained each stay pending for twelve attempts,
     past the bound of eight, and repair the Snapshot once they end.
  4. A two-day outage: 54 attempts, never more than an hour apart, then repaired when monday returns.
  5. Ten attempts with the connection needing authorization again, and ten with it disconnected: no credential
     acquired and nothing sent to monday.
  6. Work waiting for reauthorization resumes after the real 3B reauthorization, and work waiting for a suspended
     tenant resumes once it is active, both with no new webhook.
  7. Archived, moved and unevidenced holds stay held after three days, a run and a sweep.
  8. A missing item is held after eight consecutive missing answers, which ten failed attempts before them did not
     shorten.
  9. An unsupported answer is held after eight.
  10. Delays of 30, 60, 120, 240, 480, 960 and 1,920 seconds, then 3,600; a run a second early claims and sends
      nothing; a 2-hour wait from monday is kept, and a 12-hour one is cut to the configured bound.
- Tests 7 and 9 passed before the change: they pin behaviour that already held, and mutants R09 and R08 prove they can
  fail.
- Deliberate pin updates, each still exact: the outcome table; the reader's refusal mapping; the access test's
  reauthorization case (now `ConnectionUnavailable`, with no call to monday); the count of log calls, now 5 (the access
  check's failure, ids and a code only); and the removed test that asserted a transient failure held at the bound.

**Mutation:** `mutate-3e-retry.py`, 32 mutants, all caught, in six short foreground runs; all 37 changed code files
matched their pre-mutation hashes after every run.

- The owner's three: a transient failure held at the bound (R01); a read while access does not stand (R02); and
  recovery prevented, by holding access refusals at once (R03), at the bound (R04), or by never claiming them again
  (R05).
- Classification and bounds, R06 to R14: a rate limit bounded; a missing item and an unsupported response never held;
  an archived item retried; the bound on unusable answers removed; the run of like failures never restarted; recorded
  codes never parsed; and a refusal reported as transient, by the reader (R13, caught by the reader's unit tests, since
  the apply transaction's access check also refuses) or by the access check (R14).
- Scheduling: no floor on the backoff (R15), no cap (R16), the provider's longer wait ignored (R17) or unbounded
  (R18); and a lost claim still recording (R19).
- R10, R17 and R19 re-anchor E28, E29 and E19, whose code moved. E17, E18, E20 to E27, E30, E31 and E37, whose code or
  detecting tests this correction touched, were run again and caught. The other 3E mutants touch code and tests this
  correction did not change.

**Verification:** clean non-incremental build with 0 warnings and 0 errors; full suite 1,416/1,416 (unit 871, security
152, architecture 31, integration 362, the 9 Service Bus emulator tests included); both repository verifiers PASS; the
six frozen documents byte-identical; `git diff --check` clean.

**Later operational hardening, not a 3E item (owner decision, final review):** the backoff has no random jitter.
Each run claims a connection's due targets as one batch, so retries that fall due together become one read.

### 3E final owner review (2026-10-02): a read overtaken by a reauthorization

The owner accepted the retry and access correction and named one last blocker: `IdentityMismatch` was held, so a
legitimate reauthorization while monday answered would strand reconciliation of the field. 3B's data model showed the
gap was different and wider than reported. 3B lets only the connection's own monday user, with the same subject and
account, authorize a connection again (`ConnectorAuthorizationStore.ReauthorizeAsync`), so a reauthorization never
changed the identity the apply step compared, and a read made under the earlier authorization was applied. A run of the
new race test before this correction confirmed it: the Snapshot took the stale read's value and the target resolved as
`reconciliation.repaired`. The example in the first review section, another monday user authorizing the connection
during the read, cannot happen and is corrected there.

**Detection, from existing evidence.** No schema, migration, Core connector contract or 3B change. 3B stamps
`connector_authorizations.authorized_at_utc` only when a connection is authorized or authorized again; a renewal
records `refreshed_at_utc` and leaves it unchanged, while `generation` and `material_version` change on both. Every
run's access check, after its claim and before any read, also records the authorization it found: the delegated
identity's subject and `authorized_at_utc`. Every apply, after its own access check, compares them with what it finds:

- Different: the authorization changed while monday answered, and the read is stale whatever it reports.
  `AuthorityChanged` (`reconciliation.authority.changed`, AwaitAccess): nothing is applied, confirmed or kept as
  evidence, the target stays pending, and its next due attempt checks access, acquires the current 3B credential and
  reads again, with no webhook, manual change or discovery.
- The same, and the read's identity is not the connection's current delegated identity: a stable mismatch,
  `IdentityMismatch`, held as before, and monday is not asked again.
- The same, and the identity matches: applied as before.

The comparison is sound: the record is taken before the credential is acquired, and 3B only ever moves
`authorized_at_utc` to a later authorization, so an unchanged value at apply means no authorization happened in
between, and the read's credential belongs to the authorization the apply step finds. An authorization between the
record and the acquisition only discards a read that was in fact current, and the next attempt repeats it. A renewal,
by this run or by another worker, is no change: the existing real-3B test renews before a reread and still finds
`Current`.

**Tests**, written first: the contract test and the two race tests failed before the change, for the intended reasons.

- `A_read_overtaken_by_a_reauthorization_is_discarded_and_the_next_attempt_rereads_under_the_new_authorization`
  (owner tests 1 to 7 and 10), through the real 3B lifecycle: the reread starts with authorization A's token; while
  monday answers, the connection needs authorizing again and its user authorizes it again (B); A's result is discarded,
  the Snapshot unchanged, no receipt kept and A's value found nowhere in the database, the target pending, no webhook
  delivered; the next due attempt sends B's token, and B's result repairs the Snapshot.
- `A_wrong_identity_is_retried_only_when_the_authorization_changed_during_the_read_and_held_once_it_did_not` (test 8): a
  read under a wrong identity during a reauthorization through the real 3B store is discarded and retried; the next
  read, with nothing changed, reports the same wrong identity and is held; three days later a run and a sweep do
  nothing and monday is not asked again. The earlier wrong-identity test still holds at once.
- `A_credential_bound_to_another_connection_or_tenant_sends_nothing_and_confirms_nothing` (test 9): the 3A client
  refuses a credential for another connection, of another monday account, or for another tenant before anything is
  sent, at every attempt, and nothing is confirmed. 3B itself refuses stored material of another account, as its own
  tests show. This test passed before the change: it pins fail-closed behaviour that already held.
- The contract test pins the new outcome, its code and its disposition.

**Mutation:** `mutate-3e-authority.py`, 13 mutants, all caught, in three short foreground runs (and A04 once more, to
name its failing tests); all 37 changed code files matched their pre-mutation hashes after every run.

- The owner's four: the race held (A01); the stale read accepted, with the authorization not compared (A02); a stable
  mismatch retried (A03); and the reread made with the old credential, the 3A client keeping its first one (A04, caught
  by the race test among ten others).
- The comparison: by identity alone, as before this correction (A05); a stable mismatch reported as a change (A06); a
  renewal counted as a change (A07); and the authorization at read start never recorded (A08).
- Reruns in the code this correction touched: E20, E21, E22, R02 and R14.

**Verification:** clean non-incremental build with 0 warnings and 0 errors; full suite 1,419/1,419 (unit 871, security
152, architecture 31, integration 365, the 9 Service Bus emulator tests included); both repository verifiers PASS; the
six frozen documents byte-identical; `git diff --check` clean.

**A pre-existing 3B defect found during this verification (fixed separately in `273faa6`; see the closure).** The 3B unit test
`Concurrent_copies_of_one_callback_exchange_the_code_exactly_once` failed once in a full run here, passed in the other
full runs, and fails every time when run alone, on the committed `0c2d18e` as well. Root cause: when concurrent copies
of one OAuth callback arrive, the first consumes the transaction and goes on to read its PKCE verifier, while every copy
refused as replayed deletes that verifier (`MondayConnectionAuthorizer.CompleteAsync`, the `AlreadyConsumed` branch).
If a copy deletes it first, the genuine completion fails with `connector.authorization.storage_failed` before any code
exchange. It fails closed (no code is exchanged twice and nothing leaks), but a duplicate callback can defeat a genuine
authorization, which the user must then start again. The smallest fix leaves the verifier to the completion that
consumed the transaction, which already deletes it when it finishes, and to the existing expiry cleanup, by removing
the delete from the replay branch. The owner approved it as a separate 3B maintenance commit.

### 3E owner review and closure (2026-10-02)

The product owner gave 3E the final pass on 2026-10-02: **3E is PASS**. The `AuthorityChanged` design is approved: an
authorization change during a provider read discards the stale result, creates no `Confirmed` state and writes no
Snapshot, and the work is retried under the current 3B authorization; a stable `IdentityMismatch`, with no
authorization change, stays held. No 3E design blocker remains, and decisions D3E-1 to D3E-11 stay in force.

**Intervening 3B maintenance commit.** As the owner directed, the pre-existing 3B defect recorded above was fixed on
its own before 3E was committed: `273faa6` ("fix: preserve PKCE verifier during duplicate OAuth callback") removes the
verifier deletion from the replay path, so a copy of a callback refused as a replay never touches the verifier of the
completion that owns the transaction. That completion still removes it when it finishes, and expiry cleanup still
removes the verifiers of abandoned transactions. A deterministic regression test holds the winning completion at its
verifier read while a copy is refused, and a mutant that restores the deletion fails it. CI run 36993975259 was green
(1,292 tests: 1,283 passed, 0 failed, 9 Service Bus emulator tests skipped by design; gitleaks found no leaks in 39
commits), with no CD run and no Azure action. 3E's baseline moved from `0c2d18e` to `273faa6`; no 3E file changed.

A residual, pre-existing 3B gap is recorded for later: expiry cleanup closes only transactions that were never
consumed, so a completion whose process dies, or whose final verifier deletion fails, after it consumed its
transaction leaves that verifier behind. The removed replay-side deletion covered that case only when the same
callback happened to arrive again.

**Final evidence:** a provider-neutral reconciliation target model (a tenant-owned `reconciliation_targets` table with
forced row-level security, written in the same transaction as each `ReconciliationRequired`); `SourceScope` binding
the monday board; the authoritative monday API reread through the 3A client and 3B credentials, with API-read evidence
receipts; `Confirmed` only after a bound reread; `StatusValue.updated_at` as ordering evidence; generation
compare-and-swap for crash-safe completion; current provider state over stale webhook hints; transient failures never
held, access refusals rechecked locally with no provider call and resumed without a new webhook, unusable answers
bounded, semantic cases held; `AuthorityChanged` for a read overtaken by a reauthorization; and a known-object-only
missed-webhook sweep. 1,420/1,420 local tests on the tree being committed (unit 872, security 152, architecture 31,
integration 365), including the 3B regression test; mutation checks 38/38 for the implementation, 32/32 for the retry
and access correction, and 13/13 for the authorization race; build with 0 warnings and 0 errors; both repository
verifiers PASS; the six frozen documents byte-identical; no initial sync, no Azure and no real monday activity. Before
the closing commit, the clean build, the full suite, both verifiers, the frozen-document hashes and `git diff --check`
were repeated on the tree being committed; every mutation-tested 3E code file is byte-identical to the version its
mutants ran against, so the mutation evidence stands.

**3F (initial sync, watermark and buffered replay) is NEXT / NOT STARTED** and begins only on its own authorization.
3G is NOT STARTED. Gate 3 stays IN PROGRESS. Premium Gates P1 to P11 are NOT STARTED.

## 3F — Initial sync, watermark and buffered replay

### 3F authorization and owner decisions (2026-10-02)

The product owner authorized 3F on 2026-10-02 in SAFE AUTO MODE, on `main` == `origin/main` == `0dbfe1e` with a clean
tree. It implements the frozen initial-connection flow (System Architecture v1 §2): establish the connector,
activate or buffer realtime intake, read the current authorized source state, build the initial Snapshot, record the
sync watermark, replay buffered newer events and enter normal realtime mode. It is never simplified to "scan first,
then enable webhooks". Real webhook registration stays in 3G (D3C-1).

- **D3F-1 — Explicit source scope only.** A connection syncs only an explicit, enabled allowlist of provider
  resources: for monday, board ids. A provider-neutral, tenant-owned, connection-owned source scope holds an opaque
  bounded resource id and an enabled flag; no display text is identity, and nothing discovers boards.
- **D3F-2 — A local ingress fence is the watermark.** monday offers no global revision or event sequence, so none is
  invented. The cursor, `updated_at`, `changedAt`, `triggerTime`, the receipt time and the local clock are never the
  watermark. The watermark is a local, durable, database-generated ingress sequence that only decides which receipts
  belong before, inside or after a cutover; it is never business ordering.
- **D3F-3 — Raw receipts are the buffer.** 3C keeps accepting and acknowledging webhooks only after the durable
  receipt; nothing copies a webhook body. New sync tables hold metadata, fences and progress only.
- **D3F-4 — A durable sync state machine per connection,** with generations; a stale generation never completes a
  newer one, and process memory is never the source of truth.
- **D3F-5 — Authority snapshot.** The monday subject and the 3B `authorized_at_utc` are captured at start, as 3E
  proved: a token renewal never restarts a sync, a reauthorization invalidates the generation, and access that does
  not stand fails closed with no provider call.
- **D3F-6 — Initial values are authoritative API reads,** `Confirmed` only after binding and shape validation, through
  the same processing, ordering and Snapshot path as 3E; no monday-specific Snapshot SQL.
- **D3F-7 — The 3E evidence model is reused** for initial-sync reads, identifying their purpose truthfully, under the
  raw-content retention, with no raw response, credential or unrelated field.
- **D3F-8 — Known objects.** Every synchronized supported field becomes a resolved 3E reconciliation target, available
  to the known-object sweep, through the existing target model.
- **D3F-9 — Consistency model.** monday pages are not a frozen snapshot. Correctness comes from buffering before the
  scan, authoritative reads, normal ordering, replay of buffered receipts as unconfirmed hints, and 3E rereads.
- **D3F-10 and D3F-11 — Gap-free buffer start and cutover,** decided by durable fences and locks, never by time or
  sleeps.
- **D3F-12 — Buffered receipts never reach the Snapshot path ahead of the baseline,** with no redelivery loop, no
  memory queue and no second raw-data buffer.
- **D3F-13 — Restartable and idempotent;** **D3F-14 — failures classified as in 3E;** **D3F-15 — no partial Live
  connection:** every enabled configured scope completes before Live.

### Reverified monday facts (2026-10-02)

- `items_page`: default 25, maximum 500 items per page; returns active items only, with no state filter.
- `next_items_page(cursor:, limit:)`: limit at most 500; the cursor is opaque; it is cached for 60 minutes and "will
  expire 60 minutes after the first request"; a `null` cursor means no more items. What an expired cursor returns is
  not documented, and no cursor error code is documented.
- `boards(ids:, state:)`: the state filter defaults to `active` (`active`, `archived`, `deleted`, `all`);
  `hierarchy_type` is `classic` or `multi_level`; `type` is `board`, `custom_object`, `document` or `sub_items_board`.
  What `boards(ids:)` returns for a missing or inaccessible id is not documented.
- Status columns have type `status`; their values are `StatusValue` (`label`, `index`, `updated_at`), with `label`,
  `index` and `text` all `null` when the status is empty. On multi-level boards status columns resolve to
  `BatteryValue` and need `capabilities: [CALCULATED]`.
- Errors: application errors return HTTP 200 with an `errors` array (`InvalidBoardIdException`,
  `ResourceNotFoundException`, `InvalidColumnIdException`); rate limits return 429.
- Not documented, to confirm in 3G: item order and consistency across pages, the expired-cursor error, and what an
  unknown board id returns.

### 3F Definition of Done

Recorded before coding, exactly as the product owner set it. Gate 3F is READY FOR OWNER REVIEW only when:

1. A provider-neutral initial-sync state machine exists.
2. Initial sync operates only on explicitly configured SourceScopes.
3. No automatic all-workspace ingestion occurs.
4. Realtime intake can be durably buffered before baseline read begins.
5. Buffering reuses existing durable raw receipts; no shadow payload buffer exists.
6. A durable non-time-based ingress fence defines buffer membership.
7. The ingress fence is never used as business ordering.
8. monday pagination cursor is never used as the sync watermark.
9. Cursor expiry/restart converges safely.
10. Initial baseline values come only from authoritative monday API reads.
11. Initial values become Confirmed only after provider binding validation.
12. Initial values use the existing processing/ordering/Snapshot path.
13. No monday-specific Snapshot write exists.
14. Successfully initialized fields become known to 3E reconciliation.
15. Every enabled configured board scope must complete before Live.
16. No partial connection is silently marked Live.
17. Buffer start is gap-free.
18. Replay through cutover fence is gap-free.
19. Receipts after cutover transition to normal realtime behavior.
20. Buffered receipts cannot mutate Snapshot before baseline completion.
21. Current provider truth wins over stale buffered hints.
22. Failed sync generations never lose buffered receipts.
23. Sync generation/CAS prevents stale-worker completion.
24. Reauthorization invalidates/restarts a stale generation.
25. Ordinary token refresh does not restart the generation.
26. Initial sync is crash-safe and idempotent.
27. Provider transient failures remain recoverable.
28. Access revocation fails closed.
29. Unsupported board/item/value shapes are explicit, never guessed.
30. monday cursor expiry cannot permanently strand a sync.
31. No webhook registration/write scope is added.
32. Gate 3G is not started.
33. No initial-sync work touches Azure.
34. No real monday workspace is required.
35. New persistence is tenant-isolated with forced RLS and least privilege.
36. No credentials/raw API bodies/provider text leak to logs or sync metadata.
37. Full unit/security/architecture/integration evidence is green.
38. Critical mutation/vacuity guards are proven.
39. Gate 2 and Gate 3A–3E regressions remain green.
40. All six frozen documents remain byte-identical.
41. No contradiction exists with Core or Premium P0.

### 3F pre-code inspection

- **No ingress sequence exists.** `raw_receipts.receipt_id` is an application UUID v7, time-ordered, and
  `received_at_utc` is a timestamp; D3F-2 rules out both. 3F adds a database-generated identity, used only for fences.
- **Receipt states** are `Received`, `Processed` and `Quarantined`. No sweeper republishes a `Received` receipt; the
  only re-drive is the manual dead-letter tool. The transport handler completes a message on success, retries on
  `processing.unavailable` and dead-letters every other refusal.
- **A processing result always names a ledger event,** which a held receipt does not have, so a held receipt is
  reported by a fixed processing code that the handler completes, and Gate 2's result contract stays unchanged.
- **The ingress write** is one transaction: tenant status, then the receipt and its outbox marker, committed before
  the acknowledgement.
- **3E targets** know the request kinds `Event` (a `ReconciliationRequired` outcome asked) and `Sweep`. A target that
  initial sync establishes was asked for by neither; recording it honestly needs one more kind, `Sync`, on the same
  table: the smallest extension under D3F-8, with no second registry.
- **3B evidence:** `authorized_at_utc` is set only when a connection is authorized or authorized again;
  `generation` and `material_version` also change on every renewal. Connections have the tenant-inclusive key
  `ak_connections_tenant_connection`.

### 3F design

**Source scopes (D3F-1).** `connector_source_scopes`: tenant, connection, source system, an opaque resource id (a
bounded identifier) and an enabled flag, keyed by tenant, connection and resource id, with a tenant-inclusive foreign
key to the connection. The runtime role may read and insert, and may update only `enabled` and `updated_at_utc`, so a
scope can never move to another connection or change its resource; deletes are refused. A provider-neutral application
service configures scopes after checking that the connection belongs to the tenant and serves the source.

**Ingress fence (D3F-2).** `raw_receipts.ingress_sequence`: a `bigint` identity generated by the database at insert,
made immutable by a trigger, and indexed with the tenant and connection. It never orders business state.

**Sync state (D3F-4).** `connection_syncs`, one row per connection that has started initial sync: the current
generation; its state (`Buffering`, `Reading`, `Replaying`, `Live`, `Failed`, `RequiresRestart`; a connection with no
row is NotStarted and processes realtime receipts as before, which "3F owner review" supersedes); the authority snapshot (the delegated subject and
`authorized_at_utc`); the replay floor; the generation's buffer-start, replay and cutover fences; a worker lease;
retry state; and timestamps. `connection_sync_scopes` records each generation's scope set and progress (state, attempt
streak, last outcome, pages and items read, fields applied). No value, label, cursor, token or response is stored.

**Buffer start (D3F-10).** One transaction locks the connection's sync row, checks the tenant, the connection and its
authorization locally, snapshots the authority, raises the generation, records the buffer-start fence (the highest
ingress sequence of the connection's committed receipts) and copies the enabled scopes; only after it commits does any
provider call happen. The replay floor is kept from the earliest generation since the last Live, so a failed or
restarted generation never drops what an earlier one buffered (DoD 22).

**Holding and fencing (D3F-11, D3F-12).** The ingress transaction takes a share lock on the connection's sync row
before it inserts a receipt, and the processor takes the same share lock before it decides one. A receipt of a
connection that is not Live and whose ingress sequence lies above the replay floor is held: nothing is decided, the
receipt stays `Received`, and the transport message completes with the fixed code
`processing.receipt.held_for_initial_sync`. The cutover takes the row's exclusive lock, so no ingress or processing
decision for the connection is in flight; it captures the cutover fence, replays every unprocessed receipt above the
floor up to it, and marks the generation Live in the same transaction. A receipt committed afterwards has a higher
sequence and meets a Live connection, so it is processed normally. Duplicates stay no-ops through the ledger.

**Baseline (D3F-6, D3F-7, D3F-8).** For each scope of the generation, the reader validates the board through a typed
read-only operation (it must be the requested board, active, of type `board` and `classic` hierarchy, with at least
one `status` column), then pages its active items 500 at a time with `items_page` and `next_items_page`, reading only
item id, item state, board id and the status columns' `StatusValue`. Each field is applied as 3E applies a target: a
local access check, the authority comparison, the already-current check, an evidence receipt (`monday.api-read.v1`,
the consumed values only, its `operation` naming the initial-sync read), and the processing core's
`DecideAndProjectAsync` with a `Confirmed` event. The field then becomes a known 3E target, resolved, or held with its
outcome, inserted only when absent, with the request kind `Sync`. A cleared status is a valid value; a missing column,
an unsupported type and a malformed response are distinct, explicit outcomes.

**Pagination (D3F-9; DoD 9 and 30).** The cursor lives only in memory for one scan and is never stored, logged or used as
a watermark. A continuation monday refuses, an expired cursor and a resumed scan restart that board from its first
page; reapplying a page is idempotent.

**Authority (D3F-5).** Before every provider call the access check runs locally, and each apply transaction compares
the authority with the generation's snapshot. A change discards the read, marks the generation `RequiresRestart`, and
the next run starts a new generation under the current authority; a renewal never changes `authorized_at_utc`.

**Failures (D3F-14).** Per scope, as in 3E: transient failures are retried on the capped backoff and never held;
access refusals wait with no provider call and resume; a missing or inaccessible board and an unsupported response are
retried up to the bound and then held; an inactive board, an unsupported type or hierarchy, a board without a status
column and a binding or identity mismatch are held. A held scope fails the generation, which never reaches Live
(D3F-15).

**Concurrency (D3F-13).** A run claims the generation with a lease; every transition compares the generation and the
claim, so a stale worker records nothing; the lease is renewed per page.

### 3F contradiction audit (before code)

| Rule | Source | Result |
|---|---|---|
| Initial connection flow, in order | System Architecture v1 §2 | Compatible: buffer start, baseline, watermark (replay and cutover fences), replay, Live |
| Webhooks give speed, reconciliation correctness | Invariant 17 | Compatible: buffered webhooks are replayed as unconfirmed hints |
| Receipt time never decides business order | Invariant 12 | Compatible: the ingress sequence only fences; Gate 2 ordering decides |
| No blind last-write-wins | Invariant 18 | Compatible: every value passes the processing core's ordering |
| Unknown or untrusted data never reaches the Snapshot | Invariants 13, 25 | Compatible: binding and shape validation before `Confirmed` |
| Provenance | Invariant 14 | Compatible: evidence receipts and ledger entries |
| Tenant isolation | Invariants 44, 45, 48, 52, 59 | Compatible: forced row-level security, tenant-inclusive keys, no enumeration |
| Secrets and retention | Invariants 20, 53, 55 | Compatible: the buffer is the existing receipts; sync metadata holds no content |
| Revocation at access time | Invariant 58; Premium §20.7 | Compatible: a local check before every provider call |
| Service Bus is transport; application idempotency | Invariants 63, 64 | Compatible: held messages complete; the replay is idempotent |
| Typed operations | Premium §20.3 | Compatible: each read is declared with its descriptor |
| No webhook registration or write | D3C-1 | Compatible: none added |
| Tenant-less access | Gate 2 decision 1 | Compatible: no new tenant-less function |
| Least privilege | R3B-2 | Compatible: explicit grants, no delete, column-level updates |
| No AI, no Premium, no Azure | Development Gates v1; P0 closure; CLAUDE.md | Compatible |

No contradiction found.

**For the owner's attention:** (1) a connection with no sync row keeps processing realtime receipts as before, so a
host must start initial sync right after it activates a connection (host activation is already an owner item;
resolved in "3F owner review"); (2)
the `Sync` request kind is the one extension of the 3E target model; (3) initial-sync evidence reuses
`monday.api-read.v1`, its `operation` naming the read; (4) a held receipt whose content expires before replay is
superseded by the baseline read.

### 3F implementation record

Implemented on `0dbfe1e`, uncommitted. One additive migration, `20261002140325_InitialSync`, applied only to the local
database by the tests' fixture and to the databases the tests create. No package, workflow, IaC, Key Vault or Azure
change; no registration in any host; no real monday call, credential or webhook registration; no monday write; 3G not
started.

**Core, provider-neutral** (`src/App.Core/InitialSync/`). The configured scope (`ConnectorSourceScope`) and its port
`IConnectorSourceScopes`; the reader port `IInitialSyncReader` with its requests and answers (`ScopeReadRequest`,
`ScopeValidation`, `ScopePageRequest`, `ScopePage`, `SyncedObject`, `SyncField`) and `ScopeContinuation`, an opaque,
bounded, never-rendered position that nothing stores; the runner port `IInitialSync` with `InitialSyncRun`,
`InitialSyncSummary`, the state machine `InitialSyncState` and `InitialSyncErrors`; and the closed `SyncScopeOutcome`
set with fixed `initial_sync.*` codes and the 3E dispositions. Processing gained one fixed code,
`processing.receipt.held_for_initial_sync`.

**Infrastructure.**

- *Ingress fence (D3F-2).* `raw_receipts.ingress_sequence`: an identity the database generates at insert, existing rows
  included, one value at a time (sequence cache 1, so positions follow insert order across sessions), refused if
  supplied, and kept from any change by a trigger whose function has no definer rights, a fixed search path and no
  caller. A unique index orders a connection's receipts by it. It decides fence membership and nothing else.
- *Tables (D3F-1, D3F-4).* `connector_source_scopes`, `connection_syncs` and `connection_sync_scopes`: forced row-level
  security with the tenant policy, a restrictive policy refusing every delete, foreign keys that include the tenant,
  and exact grants: read, insert, and update only of the columns that may change. A scope never moves to another
  connection or resource, a sync row never changes its identity, and its replay floor, set once when the connection's
  first generation starts, is not updatable at all. `reconciliation_targets` gained the request kind `Sync`, and its
  requester rule is restated as `(request_kind = 'Event') = (requested_by_event_id IS NOT NULL)`, equivalent for every
  existing row.
- *Gate (D3F-10 to D3F-12).* Ingress takes a key-share lock on its connection's row before it stores a receipt.
  Processing takes a share lock on the connection's sync row before it locks a receipt, and holds the receipt when the
  row exists, is not Live and the receipt lies above the replay floor: nothing is decided, the receipt stays
  `Received`, and the transport handler completes the message. Buffer start and cutover take the exclusive lock of the
  connection row, then of the sync row, so both fences are exact, the first generation's included.
- *Engine.* `InitialSyncEngine` (buffer start, claim and lease, a local access and authority check before every
  provider call, one transaction per object, failure recording, replay passes and the locked cutover, as designed
  above); `ConnectorSourceScopes`; and `AddInitialSync`, which only registers.
- *Shared with 3E, its behaviour unchanged.* The 3E apply path is one method, `SourceReconciler.ConfirmAsync`, which the
  reconciler and initial sync both call, so a value becomes `Confirmed` in one place only; the 3E access check
  (`StandingAsync`) is shared the same way. `ReconciliationTargets` gained the three writes initial sync needs: lock a
  known target, establish a field, and hold a scope conflict.

**Connector.** Three read-only operations, each with its document, client method and strict parser: the board
validation (`monday.board.read-sync-scope`: id, state in every state, type, hierarchy, and column ids and types), the
first status page (`monday.board.read-sync-status-page`: the board's id, and its items' ids, states and boards with the
named status columns' typed `StatusValue`) and the next page (`monday.items.read-next-sync-status-page`). No name,
title, text, value or item update time is read. `MondayInitialSyncReader` is the monday `IInitialSyncReader`. The 3E
reader's per-column binding is now one shared rule, `MondayStatusReader.ReadStatus`, used by both readers, its evidence
naming the operation that read it.

**Behaviour, as tested.**

- Only the enabled configured scopes of the run's own connection and source are read; nothing is discovered, and a Live
  connection is never scanned again.
- Every field goes through the 3E path: a bound read, the access and authority check in the object's transaction, the
  already-current check, an evidence receipt (`monday.api-read.v1`, its `operation` naming the initial-sync read) and
  the processing core with a `Confirmed` event. Each field becomes a known 3E target with the request kind `Sync`:
  resolved, or held with its outcome. A field already known in another scope is held as a scope conflict and never
  overwritten; an object monday places on another board is neither applied nor made known in this one.
- A cleared status is a confirmed empty value; a missing column, an unsupported type or shape, an invalid label, an
  inactive item and a misbound item are distinct held outcomes, and a malformed or ambiguous page binds nothing.
- Pages hold up to 500 items; a cursor monday refuses, or answers with no next page, restarts that board from its first
  page once per run and then waits on the backoff, never held; an item answered twice is applied once; no cursor is
  stored anywhere, not even during the scan.
- Every enabled scope must complete: a held scope fails the generation, which never reaches Live; transient failures
  and missing access wait on the capped backoff and are never held; an unusable answer is held after the bound.
- A reauthorization during the scan discards the read and makes the generation `RequiresRestart`; the next run starts
  a new generation under the current authority, keeping the replay floor. A token renewal changes nothing.
- A stale worker records nothing: a live claim excludes every other worker, a lapsed one is taken over, and every write
  compares the generation and the claim, the cutover's included.
- Every receipt above the floor and up to the cutover fence is replayed, in ingress order, as the unconfirmed hint it
  is; one being stored during the buffer start or the cutover is waited for; every later receipt is processed live;
  duplicates are no-ops; and business order comes from monday's evidence and the 3E reread, never from ingress order.

**Tests:** unit 940 (+68), security 161 (+9), architecture 32 (+1), integration 421 (+56): 1,554 in all, 1,554/1,554
locally, the Service Bus emulator tests included.

- Unit: the monday reader against a deterministic fake of monday (68): validation and its refusals, pages and their
  binding, the exact evidence, cursor handling and the classification of every failure.
- Integration (56), against the real local PostgreSQL, the real 3C endpoint, the real processor behind the real
  transport consumer, the real reader and client, and one stateful fake monday account: scope, baseline and pagination
  (23); fences and ordering races (8), including a receipt held mid-write at the buffer start and at the cutover;
  failure and recovery (14), including a database failure after the evidence receipt was written and two workers at
  the cutover; the records' transitions (7); and the new tables on databases created for the test (4).
- Security and architecture: the trigger function, the grants and policies, no content-bearing member, fences that are
  positions, the hold and complete path, the one confirmation path, log hygiene, the reader's boundaries, no host
  registration, and provider-neutral 3F code.
- Deliberate pin updates, each still exact: monday's documents and operations (5 to 8); the processing errors (14 to
  15); the connector files that mention webhooks (the initial-sync reader, which reads the surface the normalizer maps),
  tightened so that the connector never runs a sync or configures a scope; the migration routine allowlist (the trigger
  function); the database policy and no-delete lists (the three tables); the raw-receipt triggers (two); and the
  cross-tenant acceptance test, which inserts with `OVERRIDING SYSTEM VALUE` and seeds a configured scope and a started
  generation per tenant through the real application path.
- Test infrastructure: `FakeMonday` gained an awaitable answer built from the request; `FakeMondayWorkspace`,
  `InitialSyncStack` and `InitialSyncData` are new; the 3B seeding helper returns its grant.

**Mutation and vacuity:** 43 mutants, F01 to F43 (`mutate-3f.py`, on the 3E runner), all caught. After every mutant
the runner restored each changed file and checked its hash, and all 311 source and test files matched their recorded
hashes after every batch.

- F12, the apply-time claim check ignoring the claim, first survived: the stale worker woke only after the other had
  reached Live, so the state check alone stopped it. The test now wakes the stale worker while the other still holds
  the generation mid-read; F12 was then caught.
- F04, the cursor kept as sync progress, was first caught only by a static pin, because the cursor was cleared before
  the end-to-end test looked. The test now looks during the scan and catches it too.
- The mutants cover every guard the owner listed: the scope filters (F01, F02); receipt time and the cursor as the
  watermark (F03, F04); processing before the baseline (F05); the buffer-start lock and fence (F06, F07); a skipped
  replay and the cutover gap (F08 to F10); stale workers (F11 to F14); reauthorization and renewal (F15, F16); board,
  item and column binding (F17 to F21); the confirmation of the baseline and of webhooks (F22 to F24); a partial scope
  set (F25); cross-tenant configuration (F26); rediscovery (F27). And the guards 3F added: scope conflicts and
  misbound objects (F28, F29); identity, authority and access checks (F30 to F32); the replay floor, the transient
  bound and cursor refusals (F33 to F35); the held message (F36); the ingress and gate locks (F37, F38); and the
  tables' grants, no-delete policies, tenant isolation and the ingress-position trigger (F39 to F43).
- Vacuity: with row-level security disabled, each table's isolation check sees the other tenant's rows; the acceptance
  test requires every tenant-owned table to hold rows of its own; the log test requires the run's own Live record; and
  the cursor checks require the fake to have issued the cursor they look for.

**Verification:** clean non-incremental build with 0 warnings and 0 errors; full suite 1,554/1,554; both repository
verifiers PASS; the six frozen documents byte-identical; `git diff --check` clean; no changed file mixes line endings;
and the migration's idempotent SQL read in full.

**Runtime privileges (R3B-2).** Superseded by the block in "3F owner review", which adds the two scope-set version
columns. The unchanged global bootstrap, if rerun after this migration, re-grants DELETE and
full UPDATE on the three new tables; the restrictive policies still refuse every delete. The Azure rollout must run,
in the same approved step as any bootstrap rerun, the re-assertion for the 3B and 3E tables and this one, which the
tests prove restores the exact grants:

```sql
REVOKE ALL PRIVILEGES ON connector_source_scopes FROM workcontinuity_app;
GRANT SELECT, INSERT ON connector_source_scopes TO workcontinuity_app;
GRANT UPDATE (enabled, updated_at_utc) ON connector_source_scopes TO workcontinuity_app;
REVOKE ALL PRIVILEGES ON connection_syncs FROM workcontinuity_app;
GRANT SELECT, INSERT ON connection_syncs TO workcontinuity_app;
GRANT UPDATE (generation, state, provider_subject, authorized_at_utc, buffer_start_fence, replay_fence, cutover_fence,
    claim_id, claimed_until_utc, attempts, next_attempt_at_utc, last_outcome, started_at_utc, live_at_utc, updated_at_utc)
    ON connection_syncs TO workcontinuity_app;
REVOKE ALL PRIVILEGES ON connection_sync_scopes FROM workcontinuity_app;
GRANT SELECT, INSERT ON connection_sync_scopes TO workcontinuity_app;
GRANT UPDATE (state, attempts, last_outcome, pages_read, objects_read, fields_applied, completed_at_utc)
    ON connection_sync_scopes TO workcontinuity_app;
```

**Security review** (no finding needs a change before review):

1. *Locks on the connection row.* Every ingress now takes a key-share lock on its connection's row. Such locks never
   wait for each other or for an ordinary update; only a buffer start or a cutover makes ingress for that one
   connection wait, briefly, and the cutover replays only what arrived after the last replay pass.
2. *Held receipts.* While a generation has failed, its connection's receipts stay held until a new generation starts.
   A held receipt whose content expires first is skipped by the replay, and the baseline read supersedes it.
3. *A connection with no sync row* processes webhooks as before; the host must start initial sync right after it
   activates the connection. Superseded by "3F owner review": a configured connection holds from its first enabled
   scope.
4. *Correlation.* A replay is recorded under the sync run's correlation id; its causation stays the receipt id.
5. *Leases.* As in 3E: workers whose clocks disagree by more than a lease could both run, but every write compares the
   generation and the claim, so only the holder records anything, which the tests prove for the page and the cutover.
6. *Evidence.* Initial-sync evidence receipts hold exactly the provider values consumed, as raw work data under the
   30-day retention; no other new column can hold a value, a label, a cursor or a credential.
7. *Rollout.* Adding the identity column numbers the existing receipts, which rewrites `raw_receipts` under an exclusive
   lock; the Azure migration step should be scheduled with that in mind.

**Deferred to 3G** (to confirm on a real workspace): what monday answers for an expired cursor (an error and a missing
next page are both handled as a refusal); what `boards(ids:)` answers for an unknown or inaccessible id (an empty list
is retried, then held); the `hierarchy_type` of classic boards (anything but `classic`, null included, fails closed as
unsupported); item order and consistency across pages and the cursor's lifetime (rereads converge); and deliveries of a
real subscription buffered through 3C during a real initial sync.

**For the owner's attention:**

- Host wiring: which component starts initial sync right after a connection is activated and runs it until Live. No
  host registers 3F, and finding due work across tenants raises the same tenant-less lookup question as 3E.
- A failed generation waits for an explicit start after its configuration is fixed; the connection's receipts stay held
  meanwhile. A change of its enabled scope set now makes it restart at the next run ("3F owner review").
- A scope enabled after Live is not read: syncing an added board, or a Live connection again, is a future decision;
  `StartAsync` refuses a Live connection. Superseded by "3F owner review": any change of the enabled scope set
  invalidates Live, and the next run restarts over the new set.
- A field the read cannot apply is held as its own target with its outcome and does not fail its board; board-level
  problems fail the generation.
- Azure: applying the migration and the re-assertion above.

### 3F owner review (2026-10-02): a configured connection holds, and its scope set has a version

The owner accepted the core 3F implementation for review and named two related lifecycle blockers, both fixed here.
Nothing the owner listed as settled changed: the ingress sequence and fences, the connection-row locking design (scope
configuration now takes the same locks, in the same order), raw receipts as the buffer, the replay and cutover
algorithm, monday pagination, 3E `ConfirmAsync`, the evidence receipts, Known Object seeding, authority generations,
transient retries, the `EventText` limits and the frozen documents. Host activation stays deferred to 3G (owner
decision): 3F exposes the provider-neutral start and resume operations, and correctness never depends on a host
invoking them at once.

**Blocker 1: a configured connection that has not started never processes realtime data.** A connection had no sync
row until its first generation started, and a connection with no row processed realtime receipts as before 3F, even
with enabled scopes.

- A connection now takes part in initial sync from the transaction that first enables one of its scopes through
  `IConnectorSourceScopes`: trusted local configuration, never a payload. That transaction takes the connection row's
  exclusive lock, so every receipt ingress was storing for the connection has committed and none is stored until it
  commits, then the sync row's lock, and creates the sync row: `NotStarted`, generation 0, no authority, start or
  generation scope-set version, scope-set version 1, and the replay floor and buffer-start fence both at the exact
  ingress fence.
- The gate keeps its lock (processing takes the sync row's share lock before it locks a receipt) and decides:

  | Connection | A realtime receipt above the replay floor |
  |---|---|
  | No sync row and no enabled scope (never configured, or disabled scopes only) | Processed as before 3F |
  | No sync row and an enabled scope (written outside the configuration service) | Held; the first generation replays from floor 0 |
  | `NotStarted`, `Buffering`, `Reading`, `Failed` or `RequiresRestart` | Held |
  | `Replaying` | Held; only the engine's controlled replay decides it |
  | `Live` | Processed by the ordinary realtime path |

  A receipt at or below the floor was stored before the connection took part and is decided as it would have been.
  A held receipt stays `Received` with no ledger entry, Snapshot change or reconciliation request, and its message
  completes with `processing.receipt.held_for_initial_sync` at every delivery: no redelivery loop and no poison message.
  Nothing is inferred from a payload, and another tenant's or another connection's scopes never count.
- `IInitialSync.StartAsync` starts the first generation of a `NotStarted` connection, which replays everything held
  above the floor before it goes Live. A run never starts a `NotStarted` connection by itself: starting stays the
  host's decision, and until then the connection holds safely.
- Found while writing the tests, and fixed: the gate locked the sync row, found none, and only then read the
  configuration, so a first configuration committing between the two reads made it hold a receipt at or below the new
  floor, which no generation would ever replay. Whether a sync row exists and whether a scope is enabled are now read in
  one snapshot, and a row created since decides, under its share lock. A test reproduces the interleaving
  deterministically; it failed before the fix and passes after it.
- Disabling, through the service, the last enabled scope written outside it also creates the row, at floor 0, so what
  those scopes held is replayed instead of stranded.

**Blocker 2: the scope set never changes while Live without invalidating the baseline.**

- *Version model.* `connection_syncs.scope_version` is the version of the connection's enabled scope set: 1 when the
  connection first takes part, raised by one by every effective change: a scope added enabled, a disabled scope
  enabled, an enabled scope disabled. A call that leaves the enabled set as it was changes nothing.
  `connection_syncs.generation_scope_version` is the version the current generation captured when it started, in the
  same buffer-start transaction that copies exactly the enabled scopes into the generation's own
  `connection_sync_scopes` rows; it is null only while `NotStarted`.
- *In the database itself.* `ck_connection_syncs_scope_version`: `scope_version >= 1`, a generation's version between
  1 and the current one, and a `Live` row only on the current version. `ck_connection_syncs_not_started`: a
  `NotStarted` row has no authority (subject and authorization time), start or generation version, and every other row
  has all of them. `ck_connection_syncs_generation`: generation 0 exactly when `NotStarted`. `GoLive` also refuses a
  generation whose version is not current.
- *Atomic invalidation.* The configuration transaction takes the connection row's exclusive lock, then the sync row's,
  writes the scope and, when the enabled set changed, raises the version in the same transaction. A `Live`,
  `Buffering`, `Reading`, `Replaying` or `Failed` row becomes `RequiresRestart` with its claim released, its cutover
  fence and Live time cleared and its attempt streak reset, and records `initial_sync.scope_set.changed`; `NotStarted`
  and `RequiresRestart` keep their state. The new scope set and the invalidation commit together, so no transaction
  sees one without the other, and the check refuses a Live row on an earlier version. No time and no process memory
  takes part.
- *After the change.* Realtime receipts above the floor are held; the stale worker's next write finds no claim and
  records nothing; the next run restarts a generation under the current authority, with no new webhook, capturing the
  new version and exactly the enabled scopes, disabled ones excluded; and what was held is replayed before Live. A
  stale generation never becomes Live: its claim is gone, `GoLive` refuses its version, and the check refuses the row.
  Disabling every scope restarts an empty generation, which reads nothing, replays what was held and goes Live.
- *Frozen membership.* A configuration change never touches `connection_sync_scopes`: a generation's scope rows stay as
  they were, as historical evidence. The runtime role cannot change a scope row's generation or resource, nor delete
  one.
- *Replay floor.* Set once, when the connection first takes part: the exact ingress fence then, or 0 when enabled scopes
  had no row. It never moves, not by a scope change, a failure, a restart or Live, and the runtime role still cannot
  update it. A restart replays every receipt above it that is still unresolved, so a failed generation restarted by a
  scope change keeps everything held since the first.
- *Concurrency at the boundary.* A receipt being stored when a change begins holds the connection row's key-share lock,
  so the change waits for it; delivered afterwards, it meets the new state and is held for the new generation. A
  receipt stored after the change began waits for it and is held. A processing decision in flight when the change
  begins holds the sync row's share lock, so the change waits for it: that receipt is decided under the old Live
  generation, before the boundary. Nothing falls between, and nothing is lost.
- *Tenants.* Configuration finds the connection under the trusted tenant's row-level security; another tenant can
  neither name nor change this connection's scopes or sync row, and the gate reads only this connection's own scopes.

**Schema.** One additive migration, `20261002193124_InitialSyncScopeVersions`, after `20261002140325_InitialSync`,
which stays as it was: `NotStarted` joins the state check; the generation check allows generation 0 exactly when
`NotStarted`; `provider_subject`, `authorized_at_utc` and `started_at_utc` become nullable (only a `NotStarted` row
lacks them); `scope_version` (`bigint`, not null) and `generation_scope_version` (`bigint`) are added, both 1 on
existing rows, with no default left on the column; the two new checks; and the runtime role's grants on
`connection_syncs`, re-asserted exactly, add update of the two version columns and still never of the replay floor or
an identity column. `Down` is refused while a `NotStarted` row exists, which the earlier shape cannot represent; a
forward fix is preferred. Applied only to the local database by the tests' fixture and to the databases the tests
create; its idempotent SQL was read in full.

**Runtime privileges (R3B-2).** Superseded in turn by the block in "3F final owner review". This block superseded the
one in the implementation record: the Azure rollout was to run it, in the same approved step as any bootstrap rerun,
after both migrations. The tests prove it restores the exact
grants:

```sql
REVOKE ALL PRIVILEGES ON connector_source_scopes FROM workcontinuity_app;
GRANT SELECT, INSERT ON connector_source_scopes TO workcontinuity_app;
GRANT UPDATE (enabled, updated_at_utc) ON connector_source_scopes TO workcontinuity_app;
REVOKE ALL PRIVILEGES ON connection_syncs FROM workcontinuity_app;
GRANT SELECT, INSERT ON connection_syncs TO workcontinuity_app;
GRANT UPDATE (generation, state, provider_subject, authorized_at_utc, scope_version, generation_scope_version,
    buffer_start_fence, replay_fence, cutover_fence, claim_id, claimed_until_utc, attempts, next_attempt_at_utc,
    last_outcome, started_at_utc, live_at_utc, updated_at_utc)
    ON connection_syncs TO workcontinuity_app;
REVOKE ALL PRIVILEGES ON connection_sync_scopes FROM workcontinuity_app;
GRANT SELECT, INSERT ON connection_sync_scopes TO workcontinuity_app;
GRANT UPDATE (state, attempts, last_outcome, pages_read, objects_read, fields_applied, completed_at_utc)
    ON connection_sync_scopes TO workcontinuity_app;
```

**Tests.** The race test and the stranded-scope test were written before their fixes and failed first, each for the
intended reason; the others were written with the fix.

- `InitialSyncConfigurationTests` (new, 18), on the real stack:
  1. A configured connection holds a realtime receipt before its first generation (stored and acknowledged, no ledger
     entry, no Snapshot change, its message completed at three redeliveries) and replays it once Live; realtime
     receipts are then processed.
  2. Enabled scopes with no sync row hold, and the first generation replays everything unresolved.
  3. A receipt being stored while the first scope is configured is waited for and lies exactly at the boundary.
  4. A receipt being decided while the first scope is configured is decided by the new row, never stranded.
  5. Disabling the last scope written without a sync row keeps what it held for the next generation.
  6. No scopes, or disabled scopes only: processing as before, and starting is refused with no scopes.
  7. and 8. Another tenant's scope, and another connection's scope in the same tenant, never gate this connection.
  9. and 10. Adding an enabled board, or enabling a disabled one, while Live invalidates Live atomically; the next run
     restarts over the new set with no new webhook.
  11. Disabling an enabled board while Live invalidates Live, and the next baseline excludes it.
  12. Disabling every scope starts an empty generation that replays what was held and goes Live.
  13. A call that changes no enabled scope leaves Live and the version alone.
  14. A receipt being stored during a scope change is waited for and held for the new generation.
  15. A receipt being decided during a scope change is waited for and decided under the old generation, once.
  16. A stale generation stalled mid-read never returns to Live after the scope set changes; its frozen scope rows stay
      as they were, and the next generation captures version 2.
  17. A failed generation restarted by a scope change keeps the earliest replay floor and replays everything held.
  18. Another tenant's scope change cannot invalidate this connection.
- `InitialSyncRecordTests` (now 10): the `NotStarted` row; a first generation with no configuration row; a scope-set
  change from every state, keeping the floor and refusing the stale worker; and a generation on an earlier version
  never becoming Live.
- `InitialSyncDatabaseSecurityTests` (now 5): the shape and version checks refuse a `NotStarted` row with a
  generation's data, a scope-set change that leaves Live in place, a stale generation becoming Live and a generation
  ahead of the scope set, and another tenant's statements change nothing.
- `InitialSyncSecurityTests` (now 10): the version migration only relaxes and adds, has the database refuse a Live row
  on an earlier version, and re-asserts exactly the grants.
- Deliberate updates, each still exact: two fence tests now deliver the "before" receipt before the connection takes
  part, and assert that receipts stored after it are held; the database grant lists, the insert helper and the
  re-assertion add the two version columns; the record's member list adds them; the gate pin follows the renamed
  local; the provider-neutral file count is 11 with the new migration; and the test helper that holds an ingress
  transaction open now queues its outbox marker too, as ingress does.

**Mutation and vacuity:** `mutate-3f-config.py`, 23 mutants, G01 to G23, on the 3E runner, all caught: 22 in one
run, and G23 once the processing-in-flight test existed. After every mutant the runner restored each changed file and
checked its hash, and all 56 changed files matched their pre-mutation hashes after each run. The 43 F mutants of the
original run were run again against the new hold semantics, all caught; F05 and F14 were re-anchored because their code
moved. Two comments changed afterwards, and no code.

- Blocker 1: the configured hold removed (G01); enabled scopes with no row not holding (G02); no `NotStarted` row at the
  first enabled scope (G03); an inexact boundary, the floor at 0 (G04); configuration without the connection lock
  (G05); the gate's no-row decision read from two snapshots (G06); another connection's scope, or a disabled one,
  counting (G07, G08); and a last scope written outside the service stranding what it held (G09).
- Blocker 2: a change that invalidates nothing (G10); the version raised but Live or running generations kept (G11,
  G12); a stale version becoming Live past the record (G13) or the database (G14); the `NotStarted` shape unchecked
  (G15); a generation not capturing its version (G16); frozen membership extended in place (G17) or made updatable
  (G18); the replay floor advanced by a configuration restart (G19) or made updatable in the effective grant (G20); the
  stale worker keeping its claim (G21, caught by the record test, since the state check alone already stops the worker);
  a call that changes nothing treated as a change (G22); and a decision in flight not holding the sync row (G23, F38's
  mutation, which only a static pin caught before and the processing-in-flight test now catches end to end).
- F39 still mutates the original migration's grant line; the version migration re-asserts the grants, so only the
  static pin catches it there, and G20 covers the effective grant in the database too.
- Vacuity: the race test and the stranded-scope test failed before their fixes; the database checks are proven by
  statements the database refuses, and G14 and G15 show those tests fail without them; the processing-in-flight test
  requires the change to be blocked by the paused decision's own backend.

**Verification:** clean non-incremental build with 0 warnings and 0 errors; full suite 1,577/1,577 (unit 940,
security 162, architecture 32, integration 443, the 9 Service Bus emulator tests included); both repository verifiers
PASS; the six frozen documents byte-identical; `git diff --check` clean; no changed file mixes line endings; and the new
migration's idempotent SQL read in full.

**For the owner's attention:**

- The replay floor never moves, so a restart after a long Live period scans the connection's receipts above its first
  floor for the unresolved ones; the scan is indexed by connection and ingress position.
- Snapshot state that a generation applied for a board later disabled stays: 3F defines no removal, and none was
  invented. As in 3C to 3E, a Live connection's realtime receipts are processed whichever board they name. Superseded by
  "3F final owner review": that state stays as history and leaves the current work view.
- Host wiring stays a 3G item: a `NotStarted` connection holds until a host starts it, and a `RequiresRestart` one until
  a run.
- The earlier PKCE consumed-transaction verifier cleanup stays deferred, to be handled before real monday or the 3G
  acceptance (owner decision).

### 3F final owner review (2026-10-03): a disabled scope's state is never current work

The owner accepted the two lifecycle fixes and approved 3F but for one blocker: state already read for a scope later
disabled stayed in the Snapshot as if it were current. If scope B was part of generation N and generation N+1 reached
Live without it, Fully must no longer treat B's old projected state as current work, and a scope enabled again must be
read authoritatively before its state is current again. History must stay: the ledger, the receipts and evidence under
their retention, provenance and reconciliation evidence. Nothing the owner listed as settled changed: the ingress fence,
the replay floor, pagination, the authority model, the 3E confirmation path, the retry model, the scope-set version
model and the frozen documents.

**Provenance, checked before any change.** Snapshot rows carry the tenant, the source system and the connection, but not
the scope. The scope is carried by the field's reconciliation target, whose key is the Snapshot field's own key (tenant,
system, connection, type, object, field) and whose `source_scope` is provider-neutral (D3E-10, D3E-11, D3F-8): stored
from the normalized event or the configured scope being read, never replaced, never deleted. Every confirmed value of a
scoped source reaches the Snapshot through its target, read bound to that scope (3E `ConfirmAsync`, and the 3F apply
that establishes or locks the target in the same transaction), and a field seen in another scope is held, never
applied. So the originating scope is identified exactly, with no display text and no parsing of a reference, and no
Gate 2 Snapshot table needed a change. Where a participating connection's field cannot be attributed to such a scope,
it is treated as not current: attribution fails closed.

**Semantics.**

- A scope's state is current only while the scope is enabled and its baseline is established.
  `connector_source_scopes.baseline_generation` is the generation whose transition to Live established it: set for every
  enabled scope the generation read in full, in the cutover transaction itself; cleared in the transaction that
  disables the scope; never restored by enabling it again. The database refuses a baseline on a disabled scope.
- A field known before its scope was enabled again, or before it was first configured, awaits a baseline:
  `reconciliation_targets.awaiting_baseline_version`, set to the connection's scope-set version for every field of the
  scope, in the transaction that enables it. Only a baseline read of the scope that resolves the field clears it; a read
  that holds the field, and a field the new baseline never finds, keep it.
- The current work view: `active_snapshot_fields` and `active_snapshot_objects`, provider-neutral views over the
  Snapshot, by identifiers only. A connection that never took part in initial sync shows all its state, as before 3F.
  A participating connection's field is current only when its target names an enabled scope of that connection with an
  established baseline and no baseline is awaited for the field; an object is current when one of its fields is. The
  views run as their caller (`security_invoker`), so every table's row-level security applies to whoever reads them, and
  the runtime role may only read them.

| Moment | Scope B's state in the current view |
|---|---|
| B disabled (the configuration transaction) | Gone at once: B's baseline is cleared as the connection leaves Live, before any new generation |
| The restart and the new generation without B | Gone; A, still enabled, keeps its baseline and stays current throughout |
| The new generation reaches Live | Gone; A's baseline moves to the new generation |
| B enabled again | Still gone: no baseline, and every field B held awaits one |
| The next generation reads B | Still gone while it reads, even for fields already read again |
| That generation reaches Live | Current again, but only the fields its baseline read and confirmed |

Realtime changes read for B while it is disabled still reach the Snapshot through reconciliation, as history, and never
the view. A board that is no configured scope of a participating connection is not current either: its state becomes
current only through its own baseline once configured. Nothing is deleted or rewritten: the Snapshot rows, the ledger,
the evidence receipts and the targets all stay exactly as they were.

**Atomicity.** B's baseline is cleared in the very transaction that disables it, under the connection's and the sync
row's exclusive locks, together with the scope-set change that ends Live; a new generation's baselines are set in the
transaction that makes it Live, under the same locks. The view reads committed state only. There is no window in which B
is disabled, a generation reports Live, and B's old state is presented as current; no time and no process memory takes
part.

**Schema.** One additive migration, `20261002212331_InitialSyncActiveScopes`: the two nullable columns, a check on each
(`baseline_generation IS NULL OR (enabled AND baseline_generation >= 1)`; `awaiting_baseline_version` at least 1), the
two views, and the grants: the runtime role's privileges on `connector_source_scopes` re-asserted exactly with update of
the baseline, and the views read-only to it and closed to everyone else. No existing row is rewritten: a scope's
baseline is set by its connection's next transition to Live, and no deployed environment holds initial-sync data.
`Down` drops the views and the columns, losing only which state is current, never history. A later change to a column
the views read must recreate them. Applied only to the local database by the tests' fixture and to the databases the
tests create; its idempotent SQL was read in full.

**Runtime privileges (R3B-2).** This block supersedes the earlier ones: the Azure rollout must run it, in the same
approved step as any bootstrap rerun, after all three initial-sync migrations. The tests prove it restores the exact
grants:

```sql
REVOKE ALL PRIVILEGES ON connector_source_scopes FROM workcontinuity_app;
GRANT SELECT, INSERT ON connector_source_scopes TO workcontinuity_app;
GRANT UPDATE (enabled, baseline_generation, updated_at_utc) ON connector_source_scopes TO workcontinuity_app;
REVOKE ALL PRIVILEGES ON connection_syncs FROM workcontinuity_app;
GRANT SELECT, INSERT ON connection_syncs TO workcontinuity_app;
GRANT UPDATE (generation, state, provider_subject, authorized_at_utc, scope_version, generation_scope_version,
    buffer_start_fence, replay_fence, cutover_fence, claim_id, claimed_until_utc, attempts, next_attempt_at_utc,
    last_outcome, started_at_utc, live_at_utc, updated_at_utc)
    ON connection_syncs TO workcontinuity_app;
REVOKE ALL PRIVILEGES ON connection_sync_scopes FROM workcontinuity_app;
GRANT SELECT, INSERT ON connection_sync_scopes TO workcontinuity_app;
GRANT UPDATE (state, attempts, last_outcome, pages_read, objects_read, fields_applied, completed_at_utc)
    ON connection_sync_scopes TO workcontinuity_app;
REVOKE ALL PRIVILEGES ON active_snapshot_objects, active_snapshot_fields FROM workcontinuity_app;
GRANT SELECT ON active_snapshot_objects, active_snapshot_fields TO workcontinuity_app;
```

**Tests.**

- `InitialSyncActiveStateTests` (new, 6), on the real stack:
  1. The owner's sequence: A and B synced and current; B disabled, Live ended, and B gone from the view at once while A
     stays; the new generation excludes B and, once Live, B stays gone and A current; B's Snapshot rows, targets, ledger
     entries and receipts unchanged; B enabled again shows nothing, not even while the next generation reads it; once
     that generation is Live, B is current again with monday's state as read then.
  2. Items B lost or cannot confirm while disabled (deleted, archived, a value that cannot be confirmed) never come
     back, though the Snapshot keeps them as history.
  3. Realtime changes read for B while disabled reach the Snapshot but not the view, until B's baseline is established
     again.
  4. A board that is no configured scope is not current for a participating connection, and becomes current only through
     its own baseline.
  5. A connection that never took part keeps all its state current.
  6. Another tenant's scope changes, on a board of the same id, never hide or reveal this tenant's state, and it reads
     only its own state through the view.
- `InitialSyncDatabaseSecurityTests` (now 7): the views' rules on a database created for the test, row by row (current,
  awaiting, enabled without a baseline, disabled, unconfigured, unattributed, never taking part, another tenant), with a
  vacuity step: the same views run as their owner would show the other tenant's state; the baseline and awaiting
  checks; the views read-only to the runtime role and run as the caller, after the migration and after the re-assertion.
- `InitialSyncSecurityTests` (now 11): the migration's views run as their caller, the visibility rule, the exact grants,
  and nothing dropped, deleted, inserted or rewritten going up. `InitialSyncRecordTests`: disabling drops the baseline,
  enabling again restores none, and only an enabled scope establishes one.
- `CurrentWorkViewTests` (architecture, new, 3), added in the final verification: only the projection and its
  persistence plumbing name the raw Snapshot; the projection reads it only to write it and for reconciliation's
  already-current check; and current work is presented by exactly the two views, run as their caller.
- Deliberate updates, each still exact: the target and scope record member lists, the database grant lists and the
  re-assertion, the cutover's pinned line, the provider-neutral file count (12, with the new migration), and 3E's exact
  list of the reconciliation targets' columns, now with `awaiting_baseline_version`, a scope-set version and no value.

**Mutation and vacuity:** `mutate-3f-active.py`, 16 mutants, H01 to H16, on the 3E runner: 15 caught and one
equivalent.

- A disabled scope's state still current: the view ignoring the scope (H01) or whether the connection takes part (H02);
  disabling keeping the baseline (H03, which the database's check also refuses) and that check removed (H04); a disabled
  scope establishing a baseline (H05).
- Stale state current without a new baseline: enabling again marking nothing (H06); the view ignoring an awaited
  baseline (H07); a read that holds a field clearing its mark (H08); enabling again establishing the baseline at once
  (H09); and Live establishing none (H10).
- History deleted or rewritten instead of only leaving the view: values wiped (H11) and targets detached from their
  scope (H12), both caught by the history check. H13, which deletes the scope's Snapshot rows, survived as an equivalent
  mutant: the database never lets the runtime role delete them, by grant and, where a bootstrap rerun re-granted DELETE
  as on the shared test database, by the restrictive no-delete policy, which made its DELETE remove nothing; the same
  history check confirmed every row intact.
- The views' own guards: run as their owner (H14), writable by the runtime role (H15), and an object shown though none
  of its fields is current (H16).
- The 23 G mutants were run again, since the scope configuration changed: all caught. G03 and G04 were re-anchored, and
  G09, which no longer compiled, was restated (the row created but persisted only while an enabled scope remains) and
  caught. F09, F10, F11, F14 and F26, around the cutover and the configuration, were run again and caught. After every
  run, all 60 changed files matched their pre-mutation hashes.

**Current work readers (final verification, 2026-10-03).** No product reader of current work exists yet: no host,
Core port or connector reads the Snapshot. The raw Snapshot tables are read in two places only, both internal: the
projection, which reads the rows it writes, and `SnapshotProjection.HoldsAsync`, reconciliation's check that a confirmed
value is already held, which is provenance, not a work view. The ledger, the evidence receipts and the targets stay
readable internally for provenance and audit. The architecture guard `CurrentWorkViewTests` keeps it so: a later reader
(Work Memory, an interface) that names the raw tables fails it and must read the current work view. Five vacuity
mutants, V01 to V05 (`mutate-3f-guard.py`), were all caught: a host or a worker naming the raw tables, a new reader on
the projection, reconciliation writing through it, and a view that runs as its owner.

**Verification:** the first complete run was stopped by Claude Code for low memory during the integration tests. On the
owner's go it was run again on the final tree, in sequential batches of one test process each, with nothing skipped,
excluded or weakened: clean non-incremental build with 0 warnings and 0 errors; unit 940/940, security 163/163,
architecture 35/35, and integration 451/451 in twelve batches whose sum equals the project's own listing, the 9 Service
Bus emulator tests included: 1,589/1,589. The batches found one exact pin to update, 3E's list of the targets' columns
(above). Both repository verifiers PASS; the six frozen documents byte-identical; `git diff --check` clean; no changed
file mixes line endings; and the new migration's idempotent SQL read in full.

**For the owner's attention:**

- Reconciliation still rereads a disabled scope's known fields when a webhook or a sweep asks for them, as 3E does for
  any known target; what it reads stays history and never reaches the view. Excluding disabled scopes from those reads
  would change the 3E retry and sweep model, so it was not done here.
- The view hides more than disabled scopes: for a participating connection, state of a board that is no configured
  scope, and state no target attributes to a scope, is not current either (fail closed).
- Fully's later readers (Work Memory, interfaces) must read the current work view, not the Snapshot tables; the
  architecture guard `CurrentWorkViewTests` now enforces it.

### 3F owner review and closure (2026-10-03)

The product owner gave 3F the final pass on 2026-10-03: **3F is PASS**. The design and the implementation record above
stand as approved, with the three owner reviews: a configured connection holds realtime receipts from its first enabled
scope; every change of its enabled scope set has a version and invalidates Live; and a disabled scope's state leaves the
current work view and returns only after a new baseline reaches Live. Decisions D3F-1 to D3F-15 stay in force.

**Final evidence:** provider-neutral `ConnectorSourceScope` configuration and explicit configured-board sync only; an
initial-sync state machine with generations, claims and leases; a database-generated ingress sequence as the fence, with
raw receipts as the buffer and no shadow payload store; authoritative monday baseline reads with pagination and a safe
cursor restart, confirmed through the 3E path with its evidence receipts, each field seeded as a known reconciliation
target; buffered replay and a gap-free cutover; restart on an authorization change; scope-set versions; safe NotStarted
gating; and the current work views `active_snapshot_fields` and `active_snapshot_objects`, with the architecture guard
that keeps raw Snapshot reads out of current work. Three additive migrations, applied only locally:
`20261002140325_InitialSync`, `20261002193124_InitialSyncScopeVersions` and `20261002212331_InitialSyncActiveScopes`.
1,589/1,589 local tests on the tree being committed (unit 940, security 163, architecture 35, integration 451, the 9
Service Bus emulator tests included); mutation and vacuity checks 43/43, 23/23, 15/16 with one equivalent mutant, and
5/5; build with 0 warnings and 0 errors; both repository verifiers PASS; the six frozen documents byte-identical; no
Azure action and no real monday activity. Before the closing commit, the clean build, the full suite in sequential
batches, both verifiers, the frozen-document hashes, `git diff --check` and the line-ending check were repeated on the
tree being committed; no production file changed after the last mutation runs, so the mutation evidence stands.

**Rollout, when approved separately.** Azure Dev has none of the Gate 2B to 2F or Gate 3 migrations. Applying the three
initial-sync migrations, and running the R3B-2 re-assertion recorded in "3F final owner review" after any bootstrap
rerun, are owner approvals of their own.

**Before 3G.** The pre-existing 3B gap recorded in "3E owner review and closure" stays open: a completion whose process
dies, or whose final verifier deletion fails, after it consumed its OAuth transaction leaves that verifier behind. It
must be resolved and verified, as its own maintenance item, before real monday or the 3G acceptance.

**3G (real monday end-to-end acceptance and Gate closure) is NEXT / NOT STARTED** and begins only on its own
authorization. Gate 3 stays IN PROGRESS. Premium Gates P1 to P11 are NOT STARTED.

## 3B maintenance before 3G — PKCE verifier cleanup (2026-10-03)

### Authorization and scope

On 2026-10-03 the product owner authorized, in SAFE AUTO MODE, the fix of the remaining 3B PKCE verifier lifecycle
debt recorded in "3E owner review and closure" and in "Before 3G" of "3F owner review and closure". It is Gate 3B
maintenance only, on the baseline `main` == `origin/main` == `aaf2175` (the 3F closure) with a clean working tree. 3G
is not started and 3F is unchanged; no frozen document changes; the OAuth 2.1 flow and the monday connector's
semantics are unchanged; and nothing touches webhook registration, real monday, Azure or the infrastructure
deployment. The duplicate-callback fix `273faa6` stays: a callback refused as a replay still never touches the
verifier.

### Root cause

The winning completion deleted its verifier as a best-effort side effect with no durable trace (`DeleteQuietlyAsync`
in the `finally` of `MondayConnectionAuthorizer.CompleteAsync`). The only durable sweep,
`CloseExpiredTransactionsAsync`, selected only transactions that were never consumed; it marked each one closed before
it attempted the deletion, so a failed deletion there was never retried; and it ran only when an authorization started
in the same tenant. A completion whose process died after it consumed its transaction, or whose final deletion failed,
therefore left the verifier in secret storage with nothing in the database to find it again.

### Lifecycle

- **Durable record.** A nullable `verifier_deleted_at_utc` on `connector_authorization_transactions` holds the
  database time at which the verifier was confirmed gone, never the verifier or its name. A check refuses it before
  the transaction is consumed or closed, and a partial index covers the transactions whose deletion is not yet
  recorded.
- **Due.** A transaction with no recorded deletion is due once its verifier can no longer be used: closed unconsumed
  (`consumed_at_utc >= expires_at_utc`; consuming refuses an expired transaction, so no completion ever owned it), or
  consumed at least `ConnectorAuthorizationTransaction.MaxLifetime` ago, the 10-minute validity of a monday
  authorization code, after which no code issued for it can be exchanged. A transaction consumed more recently may
  belong to a completion that is still running and is never listed.
- **Delete, then record.** Each due verifier is deleted under its own transaction's name
  (`MondaySecretName.ForVerifier(tenant, transactionId)`), and only a deletion the storage confirms (`Succeeded` or
  `NotFound`) is recorded, by a compare-and-set on that transaction id that requires a consumed or closed transaction
  and no earlier record. A refusal, an exception or a failed record leaves the transaction due. Whether a transaction
  was consumed never changes.
- **Triggers.** The winning completion deletes and records in its `finally`; every authorization start cleans up to 20
  due transactions of its tenant, after closing expired ones as before; and the new public
  `MondayConnectionAuthorizer.CleanUpVerifiersAsync`, for the tenant of the trusted context, cleans up to 100 and
  reports how many verifiers it deleted and how many remain due. None of them needs process memory, another callback
  or a user's retry.

### Crash, restart and failure

- A completion that dies after it consumed its transaction leaves the transaction due in the database. Once the code
  validity has passed, any later cleanup, in any process, deletes the verifier and records it.
- A deletion the storage refuses, or raises an exception on, is not recorded: the transaction stays due and the next
  cleanup retries it until the storage confirms it. A deletion confirmed but not recorded is confirmed again
  (`NotFound`) by the next cleanup and then recorded. Deletion is idempotent, and a cleanup run twice finds nothing
  the second time.
- A cleanup changes no consumption, state, user, connection, account or authorization, so no state or code becomes
  usable again; a callback delivered again is refused with no exchange.

### Hosting

No host constructs `MondayConnectionAuthorizer` today: under R3B-1, monday OAuth runs before Gate 7 only through an
owner-operated DEV/TEST harness, and the deployed secret store is an owner decision. So no verifier can exist outside
such a harness. The host that runs authorizations, the 3G harness first, must also run `CleanUpVerifiersAsync` for its
tenant at start-up and periodically. This maintenance does not wire it. A background sweep across tenants would need a
new tenant-less database function, which Gate 2 decision 1 does not allow without approval, so none is added.

### Schema and privileges

One additive migration, `20261003073534_ConnectorAuthorizationVerifierCleanup`, applied only locally: the nullable
column, the check `ck_connector_authorization_transactions_verifier_deleted` and the partial index
`ix_connector_authorization_transactions_verifier_pending`. It grants, revokes and executes nothing, so the runtime
role keeps exactly SELECT, INSERT and UPDATE on the table (R3B-2), and the R3B-2 re-assertion recorded for the Azure
rollout is unchanged. The column is metadata, as D3B-2 allows. `Down` drops the index, the check and the column, and
loses only the record of confirmed deletions.

### Tests

Nine new unit tests and one extended test in `MondayConnectionAuthorizerTests`; a store test, a restart lifecycle test
and a failure lifecycle test against the real database; the column pin and a fresh-database pin of the new check; and
two security pins (the record's members, and a migration that changes no privilege). The owner's twelve required
behaviours:

1. A normal completion deletes its verifier and records it:
   `A_successful_completion_deletes_its_verifier_and_records_it_so_no_cleanup_is_due`.
2. A duplicate callback leaves the active winner's verifier:
   `A_copy_refused_as_a_replay_while_the_winner_is_completing_leaves_the_winners_verifier` (from `273faa6`), and a
   cleanup does too: `A_cleanup_while_the_winner_is_completing_leaves_the_winners_verifier`.
3. The transaction becomes and stays consumed: asserted by the completion, restart and binding tests.
4. A process failure before the final deletion:
   `A_verifier_left_by_a_completion_that_died_is_removed_once_its_code_can_no_longer_be_exchanged` and both restart
   tests.
5. After a restart the orphan is deleted:
   `After_a_restart_a_cleanup_with_no_callback_deletes_an_orphaned_verifier_once_and_keeps_the_transaction_consumed`
   and, against the real database,
   `A_verifier_orphaned_by_a_completion_that_died_is_deleted_after_a_restart_and_never_reaches_the_database`.
6. Running cleanup again is safe: both restart tests run it twice, and the store records a deletion once.
7. A temporary storage failure is retried: `A_verifier_whose_deletion_fails_stays_due_and_a_later_cleanup_deletes_it`
   (a refused and a raised deletion),
   `A_deletion_the_database_failed_to_record_stays_due_and_is_confirmed_by_a_later_cleanup`, and
   `A_verifier_the_secret_store_failed_to_delete_stays_due_and_a_later_cleanup_deletes_it`.
8. No code becomes reusable: `A_cleanup_never_makes_a_code_or_a_state_usable_again`, the replay after the restart
   cleanup with no token request, and the store's `AlreadyConsumed` after a recorded deletion.
9. No binding changes: `A_cleanup_changes_no_authorization_or_transaction_binding`, and the failure lifecycle test
   reads the same authorization before and after.
10. No other transaction's verifier is deleted:
    `A_cleanup_deletes_only_the_verifiers_of_due_transactions_each_by_its_own_name` (two orphans, an open and an
    active transaction, and the exact list of deletions), and the store's record bound to its transaction id and
    tenant.
11. Expired unconsumed transactions still close and lose their verifiers:
    `Starting_closes_expired_transactions_and_deletes_their_verifiers` (now asserting the record),
    `An_expired_transaction_is_refused_before_anything_else_and_its_verifier_destroyed`, and
    `Verifier_cleanup_lists_only_verifiers_that_can_no_longer_be_used_and_records_each_deletion_once`.
12. No verifier in logs or database fields: the connector writes no log (existing security test); after the restart
    cleanup every text and binary column of the schema is searched for the verifier, the state secret and the code,
    and none holds them; and the new column and record member are pinned as an instant.

### Mutation checks

19/19 caught on the final text, each file restored and verified by hash. Terminal cleanup removed: P01 consumed
transactions never listed, P02 nothing deleted, P03 no cleanup on start. Deleted too early: P04 no grace, P05 half the
grace, P06 a replay deletes the winner's verifier (`273faa6` reversed), P07 the winner deletes before it completes.
Transaction binding removed: P08 the record not bound to its transaction id, P09 the name not bound to the
transaction, P10 every verifier deleted by the first one's name. Failure taken as success: P11 a refused deletion, P12
a raised deletion, P13 a record with no confirmation, P14 a failed record counted. Extras: P15 the winner deletes
without recording, P16 recorded transactions listed again, P17 a record for an open transaction, P18 the check removed
from the migration, P19 the migration granting DELETE.

### Verification

Clean non-incremental build with 0 warnings and 0 errors; 1,602/1,602 local tests (unit 949, security 164,
architecture 35, integration 454, the 9 Service Bus emulator tests included), in sequential batches; both repository
verifiers PASS; the six frozen documents byte-identical; `git diff --check` clean; line endings unchanged. The first
full run caught one defect: `DatabaseCredentialTests.No_migration_contains_a_credential_or_a_role_password` failed
because the new migration's comments used a word that guard forbids in any migration. The comments were reworded with
the operations unchanged, and every mutant was run again on the final text.

### Owner review and closure (2026-10-03)

The product owner approved the fix on 2026-10-03: **the PKCE verifier cleanup is PASS**, closed by the commit "fix:
make PKCE verifier cleanup crash-recoverable". The hosting decision:

1. No new tenant-less cleanup or discovery database function.
2. No production-wide scheduling in this maintenance fix.
3. `CleanUpVerifiersAsync` stays tenant-scoped.
4. **Gate 3G precondition.** The real monday 3G harness or host must run `CleanUpVerifiersAsync` once at start-up; run
   it periodically while active, initially every 5 minutes; run it only under a trusted tenant context; and never
   enumerate tenants through a new unrestricted path.
5. The start-up run provides restart recovery.
6. The periodic run provides retry after a temporary secret-store deletion failure.
7. Production multi-tenant scheduling stays a later runtime and hosting decision, and must reuse an approved
   tenant-discovery mechanism rather than create an ad-hoc bypass.

**Final evidence**, repeated on the tree being committed: clean non-incremental build with 0 warnings and 0 errors;
1,602/1,602 local tests; 19/19 mutation checks; both repository verifiers PASS; the six frozen documents
byte-identical; `git diff --check` clean; line endings unchanged. Recording the decision changed documentation only,
so every mutation-tested file is byte-identical to the version its mutants ran against. Applying the migration in
Azure is a separate owner approval; Azure Dev has none of the Gate 2B to 2F or Gate 3 migrations. No Azure and no real
monday activity.

**3G (real monday end-to-end acceptance and Gate closure) stays NEXT / NOT STARTED** and begins only on its own
authorization, with the precondition above. Gate 3 stays IN PROGRESS. Premium Gates P1 to P11 are NOT STARTED.

## Owner decisions on record before 3G (recorded 2026-10-04)

On 2026-10-04 the product owner ordered the already-approved decisions below recorded here: first the six decisions A
to F, then the historical decision on Gate 3F mutant F39. Until then they existed only in local Claude tooling; this
section is their authoritative record. Recording them is documentation only: it authorizes nothing, starts nothing,
and changes no frozen document, no code and no earlier decision.

- **A — Gate 3G planning boundary.** Planning or documenting Gate 3G does not start Gate 3G. Gate 3G formally starts
  only on the product owner's explicit authorization. Until that authorization, 3G implementation and every
  real-provider execution stay forbidden.
- **B — Real monday execution boundary.** Code that is capable of calling monday is not itself a real monday call. Any
  actual execution against a real monday OAuth, API, webhook or other provider endpoint needs the product owner's
  explicit approval, a real read as much as a real write.
- **C — Tests-only security review.** A tests-only change does not require a full security re-review of unchanged
  production code. The review still confirms the production baseline, checks the changed tests for vacuity, and gives
  the affected invariant a targeted re-check where a test is material evidence for it.
- **D — monday client id.** The monday OAuth client id is an identifier, not credential material. It may be logged
  only when that is operationally useful. The client secret, tokens, an authorization code, a PKCE verifier and a raw
  state value stay forbidden from logs.
- **E — To confirm in 3G (MUST CONFIRM IN 3G).** Two provider behaviors are unknown and must be confirmed in 3G:
  - what moving or copying an item between boards does to its identity (today's handling: D3E-8 and "3E owner
    decisions, continued", Board mismatch);
  - whether an `items_page` or `next_items_page` cursor is bound to the authority, the token, the account or the
    session that created it (today's handling: "3F design", Pagination, and D3F-5).
- **F — Premium "Core Gate 3" status row.** A Gate 3 sub-gate closure does not change the "Core Gate 3" row of
  `docs/premium/PREMIUM-DEVELOPMENT-GATES-v1.md`. That row changes only when Gate 3 as a whole changes state. Any edit
  under `docs/premium/` still needs the product owner's explicit approval.
- **Historical Gate 3F mutation evidence — F39.** Under the mutation taxonomy adopted after Gate 3F, the historical
  mutant F39 would not qualify as CAUGHT. Gate 3F remains PASS / CLOSED. This is an evidence-classification exception,
  not a production defect, and no retroactive mutation campaign is required. Future migration and schema mutation
  evidence follows the fresh-database rule.

**3G (real monday end-to-end acceptance and Gate closure) stays NEXT / NOT STARTED** and begins only on its own
authorization, with the precondition of "3B maintenance before 3G — PKCE verifier cleanup (2026-10-03)". Gate 3 stays
IN PROGRESS. Premium Gates P1 to P11 are NOT STARTED.

## 3G — Offline harness implementation (authorized 2026-10-05)

### 3G authorization and owner decisions (2026-10-05)

On 2026-10-05 the product owner authorized, in SAFE AUTO MODE, the start of Gate 3G for a narrowly bounded offline
implementation only. The starting point was verified before any change: branch `main`, HEAD
`cc1602dafb1db3a4b2ddadd091fe1306f3eda7ae`, equal to the local `origin/main` reference, a clean working tree and
nothing staged. The authorization is self-contained: it does not approve every recommendation of the 3G preparation
amendment, and it neither approves nor incorporates missing or truncated text. The status it orders recorded:

- **Gate 3G: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.**
- **Real execution: NOT AUTHORIZED.**
- **Gate 3 closure: NOT AUTHORIZED.**

**Authorized:** a non-production offline harness; its targeted architecture and security guards and tests; the
necessary solution registration; non-frozen scope, status and implementation-report documentation; and local build,
tests and targeted mutation verification within the boundaries below.

**Preserved:** every existing owner decision, including Gate 3F PASS / CLOSED and the historical F39 exception; D3C-1
and Fully's read-only scope; R3B-1, R3B-2 and the PKCE cleanup hosting requirement; and decisions A to F. No frozen
Core or Premium document changes. Gate 4 and every Premium implementation stay not started.

The decisions below are the owner's. The labels D3G-1 to D3G-8 are this record's own, added for reference, and are
not the owner's numbering. The authorization has nine numbered sections, and D3G-1 to D3G-8 restate its sections 2 to
9 in order: D3G-1 is section 2, "Hard exclusions", and D3G-8 is section 9, "Documentation and final output". Section
1, "Starting point", is the verification recorded in the first paragraph above; its remaining clauses are that a
different HEAD or any unrelated change is reported exactly, and that work is never reset, cleaned, stashed,
overwritten or automatically synchronized. The owner's full text was given in the working session of 2026-10-05 and
is not stored verbatim in this repository: this section is the repository's record of it.

- **D3G-1 — Hard exclusions.**
  - No real monday contact: no API, OAuth, MCP, webhook registration, browser authorization, account discovery or
    live endpoint probing; no real credentials, credential import or real-credential adapter; no real monday app or
    test-workspace change.
  - No Azure, Key Vault, RBAC, cloud deployment, tunnel, public exposure, system certificate installation, or host or
    network configuration change.
  - No modification under `src/`. If a production change proves necessary, the affected part stops and the exact
    dependency and the smallest alternative are reported. A production fix is never hidden in the harness.
  - `MondayScopePolicy`, `MondayQueryGuard`, JWT verification, tenant isolation, the current-work rules and the
    existing production guards are not weakened.
  - No plugin, hook, permission or local Claude configuration change. A stale review-tool expectation is a reported
    issue, not permission to modify the tool.
  - No stage, commit, push, workflow dispatch or deployment, and no destructive Git operation.
- **D3G-2 — Project placement and guards.**
  - The non-production project is created at `tests/App.Harness.Monday`. It reuses existing production components
    through their public interfaces, and uses `App.Web` only where necessary to reuse its existing webhook
    registration and endpoint surface, without duplicating that implementation.
  - Nothing in production may reference the harness, and it is not added to production images or deployment paths.
  - The existing production `SetResolved` allowlist stays unchanged. Harness-scoped guards are added that cover:
    exactly one reviewed trusted tenant-assignment call site; tenant parsing only at the reviewed configuration
    boundary; no tenant or user authority taken from HTTP input; no raw Snapshot access for current-work reads; no
    monday mutations or webhook-management implementation; no administrative database connection or provisioning
    operation inside the harness; and no fallback to a real provider transport.
  - Existing dependencies are used where possible. A new third-party dependency outside the current approved set
    needs a separate decision and is never added silently.
  - The `tests/` location is not a security exemption: the harness is reviewed as new executable code with its own
    trust boundaries.
- **D3G-3 — Offline composition.**
  - The smallest coherent composition that exercises: the authorization lifecycle through the existing authorizer;
    webhook intake through the existing endpoint and verifier; ingress, relay, consumer and processing;
    reconciliation and initial sync; explicit SourceScope configuration; PKCE verifier cleanup; and tenant-scoped
    evidence and current-work reading.
  - Tests use deterministic fake monday responses and synthetic credentials, reusing existing fakes where practical
    without coupling the harness to a test assembly in a circular dependency.
  - The default executable configuration fails closed: no usable real credential store, no usable real-provider HTTP
    transport, no configuration flag that enables live execution in this slice, and no automatic browser launch.
  - Missing fake dependencies fail and never fall back to network access, proven by negative tests that use
    intercepted requests, not live calls.
  - Provider traffic stays fake even when a test uses the real local database or the Service Bus emulator.
  - Each operator action and each cleanup tick has its own trusted execution flow and dependency scope. Existing
    ingress and transport flows keep their own tenant-resolution behavior.
  - PKCE cleanup runs at start-up and every five minutes under the trusted tenant context. The cadence is tested
    deterministically without weakening the runtime requirement.
  - Initial sync, reconciliation, sweep and scope configuration stay manual and bounded for this slice.
- **D3G-4 — Database and transport boundary.**
  - A new persistent standalone harness database is not authorized now, and neither is a standalone bootstrap or
    provisioning run.
  - Existing local test fixtures may manage their own test resources only through the established test lifecycle,
    inspected before tests are added. Its authority is not broadened, and shared passwords, roles, grants, emulator
    configuration and unrelated data are not changed.
  - The standalone harness is not run against the shared suite database.
  - Where fresh-database evidence is required, the existing isolated test mechanism is used and the fresh-database
    rule preserved. If that cannot be done within this authorization, the affected verification is marked BLOCKED,
    with the reason.
  - Standalone harness consumers are not run concurrently with the suite. Tests that use the emulator restrict
    consumption to their own explicit test sessions, with no unrestricted queue or dead-letter draining.
  - The later standalone database decision stays deferred. A standalone smoke run is never marked PASS when it was
    not performed.
- **D3G-5 — HTTP and operator safety.**
  - Operator and callback behavior is exercised only with synthetic test flows.
  - Operator-start and callback handling stay separate from webhook handling. Two ports alone are not assumed to
    enforce the separation: tests show that routes are unavailable on the wrong listener.
  - The fixed operator identity must not be attachable through an unbound browser callback. The proposed safeguards
    are implemented and tested with fake flows: a bounded, single-use operator-start capability; explicit Host and
    origin checks; a browser binding in addition to the existing OAuth state checks; no caching or referrer leakage
    from callback handling; and a parameter-free terminal page.
  - No public listener or tunnel is authorized. Existing in-process or local test mechanisms are used, without
    changing machine trust or browser settings.
  - If the existing public interfaces cannot support a necessary safeguard without changing `src/`, that boundary is
    reported and the safeguard is not relaxed.
- **D3G-6 — Minimum evidence and leak prevention.**
  - Current-work values come through `active_snapshot_fields` and `active_snapshot_objects` under tenant isolation.
  - Other evidence readers may inspect approved operational metadata: receipt ids, ledger outcomes, reconciliation
    targets, sync state, authorization state, counts, timestamps and fixed outcome codes. Raw Snapshot reads are never
    substituted for current-work access.
  - New diagnostics stay bounded and in memory: closed enums, booleans, counts and bounded numeric measurements. No
    arbitrary callback values, unknown parameter names, claim values, provider messages or raw error strings. Unknown
    categories become "Other" or a count.
  - Tokens, codes, state, verifiers, Signing Secrets, Authorization headers, callback query strings, cursors, raw
    payload copies and uncontrolled provider text are never exposed in diagnostics or reports.
  - No shadow raw-data store is created.
  - Logging, telemetry, exceptions and operator output are tested with synthetic sentinels and positive controls. A
    quiet log level alone is not evidence.
  - A business-body sentinel may legitimately appear in its authorized raw receipt; credential and callback-secret
    sentinels may not. The two assertions are kept distinct.
  - A diagnostic observation that cannot be captured safely without a production change is deferred explicitly.
- **D3G-7 — Verification.**
  - Baseline and final local checks: build; the relevant unit, integration, security and architecture tests; the
    repository verifiers; the full local suite where the environment supports it; the frozen-document hash
    comparison; and `git diff --check`. Exact results are recorded, including failed, skipped, blocked and not-run
    checks. Previous test totals are not assumed to still apply.
  - Targeted mutation and vacuity evidence is added for the new critical guards: provider egress denial, tenant
    assignment and isolation, browser binding, current-work access, logging and diagnostic restrictions, and PKCE
    hosting. Historical mutation campaigns are not reopened and closed production code is not mutated for this task.
    Every temporary mutation is restored and verified by hash. The fresh-database requirement for schema-dependent
    evidence is preserved.
  - The canonical-auditor, the tenant-security-reviewer and the gate-verifier are used for the affected change set,
    and no agent review is claimed that did not run. Decision C limits unnecessary re-review of unchanged production
    code; it does not waive the security review of the new executable harness.
  - Work stops before commit or push. CI for the uncommitted changes is reported as NOT RUN; historical green CI
    proves only the checkpoint it actually tested.
- **D3G-8 — Documentation and final output.**
  - The authorized offline scope and its acceptance criteria are recorded before coding, by the project's existing
    decision-recording conventions.
  - Every real-execution blocker stays open: approved durable real-secret storage; the monday app and workspace and
    the exact approved runs; compatible webhook provisioning and public ingress; remote-revocation behavior; decision
    E and the other MUST CONFIRM IN 3G items; and dedicated standalone runtime resources.
  - No local-secret-store exception is approved. Neither Azure nor a particular provisioning path is approved. Real
    authenticated webhook evidence is not waived. Fake observations are never classified as confirmed provider
    behavior.
  - A complete English implementation report is written to `docs/gates/gate-3G-offline-report.md`. It is an offline
    implementation report, not a Gate 3 closure report. It uses normal Markdown with complete sentences, no
    terminal-width tables, no clipping, and no secrets or raw runtime evidence.
  - The final response provides what was implemented, the exact changed-file inventory, the actual tests and mutation
    outcomes, the review findings and any remaining blockers, what was deliberately not implemented, and the report
    path. It concludes with accurate values for: the offline implementation (READY FOR OWNER REVIEW or BLOCKED), Gate
    3G, real acceptance, real execution, CI for the current changes, real monday calls, Azure actions, standalone
    database provisioning, and commit or push.
  - Completing this slice is not permission for real execution: the work stops at the owner-review boundary.

### 3G offline Definition of Done

Written before any code, from the authorization above, and amended after the canonical audit of 2026-10-05, when code
already existed. Each item names the decision it serves.

Added after the canonical audit of 2026-10-05. These acceptance criteria are this record's reading of the
authorization. Where an item is stricter or more specific than the owner's wording, it is a design choice of this
slice and not an owner decision, and it stays open to the owner's confirmation or change. Those items are: the guard
against a telemetry exporter and against HTTP request logging, and for loopback-only listeners (item 5.8); the refusal
to start as a privileged or owning role, which reads the `pg_roles` and `pg_tables` catalogs about the current role
and nothing else (items 7 and 11); and, in item 12, TLS on the operator listener, the `__Host-` binding cookie, the
fetch-metadata checks, the exact `127.0.0.1` Host, the redirect address restricted to loopback HTTPS, and the fixed
two-minute and ten-minute lifetimes. The five safeguards the owner named are the single-use start capability, the
Host and origin checks, the browser binding, no caching or referrer leakage, and the parameter-free terminal page.

1. **Scope first.** This section and the status lines of the repository record Gate 3G IN PROGRESS — OFFLINE
   IMPLEMENTATION ONLY, real execution NOT AUTHORIZED and Gate 3 closure NOT AUTHORIZED, before any code (D3G-8).
2. **Baseline.** The branch, HEAD, working tree and staged state are verified against the checkpoint; the six frozen
   documents are hash-checked; and the build, both verifiers and the full local suite are run on the unchanged
   checkpoint, with exact results recorded (D3G-7).
3. **Placement.** One non-production project, `tests/App.Harness.Monday`, registered in the solution. It references
   only `App.Core`, `App.Infrastructure`, `App.Connectors.Monday` and `App.Web`, takes no package reference, is not a
   test project and is not publishable. `App.Web` is used only for its existing webhook registration, endpoint and
   request pipeline, which are reused and never duplicated. Nothing under `src/`, no Dockerfile, workflow, compose
   file, infrastructure file or `eng/` script names the harness (D3G-2).
4. **No production change.** No file under `src/` changes. No existing guard or test is weakened or edited to admit
   the harness; the production `SetResolved` allowlist is unchanged (D3G-1, D3G-2).
5. **Harness-scoped static guards**, each shown to fail when its rule is broken (D3G-2):
   1. exactly one trusted tenant-assignment call site, naming the trusted-service source once;
   2. the tenant id and the canonical user id are parsed only at the reviewed configuration boundary;
   3. no tenant or user authority is taken from HTTP input, and the HTTP handlers name no tenant, user or principal;
   4. no raw Snapshot name appears, and current work is read only through `active_snapshot_fields` and
      `active_snapshot_objects`;
   5. no monday mutation, no webhook-management call and no GraphQL document;
   6. no administrative connection, migrator, role bootstrap, provisioning or data-changing SQL;
   7. no real provider transport, no fallback to one, no automatic browser launch;
   8. no telemetry exporter and no HTTP request logging, and listeners bound to loopback only.
6. **Offline composition.** The harness exercises, against deterministic fakes and synthetic credentials: the
   authorization lifecycle through `MondayConnectionAuthorizer`; webhook intake through the existing endpoint and
   verifier; ingress, relay, consumer and processing; reconciliation and initial sync; explicit SourceScope
   configuration; PKCE verifier cleanup; and tenant-scoped evidence and current-work reading (D3G-3).
7. **Fail-closed default.** The default executable has no usable secret store, no usable provider transport, no flag
   that enables live execution and no browser launch, and it refuses to start because no standalone database is
   approved. Negative tests show that a missing fake ends at an intercepting transport and never reaches the network
   (D3G-3, D3G-4).
8. **Trusted flows.** Every operator action and every cleanup tick runs in its own execution flow and dependency
   scope, under the configured tenant and canonical user only. The relay, the consumer and webhook requests keep
   their own tenant resolution. Tests show that no flow observes another flow's tenant and that a second tenant's
   connection is refused with no provider call (D3G-3).
9. **PKCE hosting.** `CleanUpVerifiersAsync` runs once at start-up and then every five minutes under the trusted
   tenant context. The interval is fixed in code, not configurable, and tested deterministically (D3G-3; the hosting
   decision of 2026-10-03).
10. **Manual and bounded.** Initial sync, reconciliation, sweep and scope configuration are single, bounded operator
    actions; nothing schedules them (D3G-3).
11. **Database and transport boundary.** The harness holds only the runtime-role connection and refuses to start as a
    privileged or owning role. It creates, bootstraps, migrates and provisions nothing. Its tests use the existing
    fixtures through their established lifecycle. Its consumer is restricted to the configured tenant's session, and
    it has no dead-letter access. No standalone run is performed in this slice (D3G-4).
12. **Operator safety**, proven with synthetic flows (D3G-5): operator and webhook routes on separate listeners, each
    route unavailable on the other; a bounded, single-use, short-lived start capability; exact Host and origin
    checks; a browser binding in addition to the OAuth state checks; no caching and no referrer leakage from start
    and callback handling; a parameter-free terminal page; loopback listeners only; and TLS on the operator listener
    from a certificate the tests create in memory, with no change to machine trust or browser settings.
13. **Evidence.** Current-work values are read only through the two views under tenant isolation; the other readers
    return approved operational metadata only (D3G-6).
14. **Diagnostics.** In memory only; closed enums, booleans, counts and bounded numbers only; unknown categories
    counted as "Other". Every observation that would need a production change, or a copy of a payload, is deferred
    explicitly (D3G-6).
15. **Leak prevention.** Synthetic sentinels and positive controls over logs, activities and metrics, exceptions,
    operator output, diagnostics and every text column of the database: credential and callback-secret sentinels
    appear nowhere, and the business-body sentinel appears only in its authorized raw receipt (D3G-6).
16. **Tests and mutation evidence.** Unit-level, integration, security and architecture tests for the above, and
    targeted mutation and vacuity evidence for the new critical guards: provider egress denial, tenant assignment and
    isolation, browser binding, current-work access, logging and diagnostic restrictions, and PKCE hosting. Every
    mutation is restored and verified by hash; no historical campaign is reopened; no closed production code is
    mutated (D3G-7).
17. **Final verification** on the final tree: a clean build with 0 warnings and 0 errors; the full local suite; both
    repository verifiers; the six frozen documents unchanged; `git diff --check` clean; exact results recorded,
    including failed, skipped, blocked and not-run checks (D3G-7).
18. **Reviews.** The canonical-auditor, the tenant-security-reviewer and the gate-verifier review the change set, and
    their findings are reported (D3G-7).
19. **Report and status.** `docs/gates/gate-3G-offline-report.md` and the status documents; every real-execution
    blocker kept open; no fake observation presented as provider behavior (D3G-8).
20. **Stop.** No stage, commit or push. CI for the uncommitted change is NOT RUN (D3G-1, D3G-7).

### 3G offline design

- **One project, three boundaries.** `tests/App.Harness.Monday` is an executable that composes a worker container
  (persistence as the runtime role, the transport's relay and consumer, processing with the monday normalizer,
  reconciliation and initial sync with the monday readers, the authorization store, the credential source and the
  authorizer), a webhook listener that maps only the existing monday webhook endpoint through
  `AddMondayWebhookIntake` and `MapMondayWebhooks`, and an operator listener that maps only the operator start,
  callback and terminal routes. The two listeners are separate applications with separate route tables.
- **Trust.** The tenant id and the canonical user id are read once, from server-side configuration, at one reviewed
  boundary. One type holds the only `SetResolved` call: it starts a fresh execution flow and a fresh dependency scope
  for each operator action and each cleanup tick, as a trusted service. No route, query, header, cookie, body or
  console argument can supply a tenant or a user.
- **Fail-closed seams.** The secret store and the provider transport are seams. Their defaults refuse every
  operation: the default store answers unavailable, and the default transport ends every request without sending it.
  Only a test can supply a fake. The harness constructs no network handler, launches no browser and has no setting
  that enables live execution. The standalone entry point also requires an approved database, and none is approved in
  this slice.
- **Operator safety.** The start route needs a single-use capability minted at the console, valid for two minutes.
  Start and callback are served only over TLS on loopback, only for the exact configured Host, and never for a
  foreign origin. Starting sets a host-only, HttpOnly, Secure binding cookie; the callback needs it in addition to the
  existing OAuth state checks, and a callback without it never reaches the authorizer, so it consumes nothing. Both
  answer `no-store` and `no-referrer`, and the callback redirects to a static page that takes no parameter.
- **Hosting.** PKCE verifier cleanup runs at start-up and every five minutes through the trusted flow. The relay and
  the consumer run on flows with no resolved tenant and resolve each tenant themselves, as in production; the consumer
  takes only the configured tenant's session. Initial sync, reconciliation, sweep and scope configuration are
  explicit operator actions.
- **Evidence and diagnostics.** A current-work reader selects from the two active views inside the tenant-scoped
  transaction. An operational reader returns counts, states, times and fixed outcome codes. Diagnostics are counters
  keyed by closed enums, kept in memory.
- **Logging.** The harness owns its logger factory: only the product's and the harness's own categories reach the
  sink, and an exception is logged by its type name only. Framework request logging therefore cannot be re-enabled by
  configuration, and no exporter is registered.

### 3G offline contradiction audit (before code)

The table was written before the code. Its last two rows were added after the canonical audit of 2026-10-05, when code
already existed, and each says so.

Every element of the offline slice was compared with PRD v1, System Architecture v1, the 83 Architecture Invariants,
Development Gates v1, the frozen Premium documents and the owner decisions on record.

| Element | Rule | Result |
|---|---|---|
| Harness under `tests/`, referencing Core, Infrastructure, the connector and Web; nothing references it | `docs/architecture/dependency-rules.md` (test projects may reference production projects, never the reverse); invariants 60, 72 | Compatible: the seven production projects and their references are unchanged |
| Trusted tenant and canonical user from server-side harness configuration; one new `SetResolved` call site outside `src/` | PRD v1 §17; invariants 44, 45, 46; R3B-1 | Compatible: R3B-1 names an owner-operated DEV/TEST harness that supplies the trusted Tenant → canonical user → connection chain; the production allowlist is unchanged and the harness has its own guard |
| OAuth start only on the harness's loopback operator listener, behind a single-use capability | R3B-1 (no anonymous production OAuth-start endpoint) | Compatible: nothing is added to `App.Web` or `App.Worker` |
| Fail-closed default secret store; in-memory fakes in tests only | PRD v1 §17; invariants 53, 54; D3B-2; Premium architecture §20 constraint 1 | Compatible: no real secret exists anywhere, and no storage decision is taken |
| No real provider call; fake transport only | Decisions A and B; D3G-1 | Compatible |
| Webhook intake reused through the existing registration and endpoint; no webhook registered | D3C-1; System Architecture v1 §3, §9; invariants 1, 45 | Compatible: intake only, tenant resolved from the connection inside ingress |
| Read and ingest only; no mutation, no write scope | Premium architecture §20 constraint 5; Gate 3 scope | Compatible: `MondayScopePolicy` and `MondayQueryGuard` are untouched and the harness sends no document of its own |
| Reconciliation and initial sync invoked by explicit operator actions through the existing ports | D3E-1 to D3E-11; D3F-1 to D3F-15; invariant 17 | Compatible: nothing is scheduled, and scopes are explicit |
| PKCE cleanup at start-up and every five minutes under the trusted tenant | Hosting decision of 2026-10-03, points 1 to 7 | Compatible: tenant-scoped, no tenant enumeration, no new database function |
| Current work only through the two active views | 3F final owner review; invariants 2, 13, 14 | Compatible |
| Diagnostics in memory, closed values only; logs carry no payload or credential | PRD v1 §17; System Architecture v1 §10; invariants 52, 53, 55 | Compatible: no shadow raw-data store |
| Runtime-role connection only; forced row-level security applies | Invariants 44, 48, 52, 59; R3B-2 | Compatible: no administrative path, no grant change, no bootstrap |
| Local development boundary only; no Azure | Invariants 68, 69; Gate 3 Definition of Done item 13 | Compatible |
| Offline fakes are not real monday evidence | Invariants 74, 82; Development Gates v1 (Gate 3 acceptance) | Compatible: real acceptance stays NOT PROVEN and Gate 3 closure is not claimed |
| No AI, no Premium capability, no Gate 4 work, no new package | Development Gates v1; P0 closure; CLAUDE.md scope discipline | Compatible |
| Added after the canonical audit of 2026-10-05: a fixed operator identity, built from the configured canonical user with the identity provider key `LocalDemo` and a fixed subject | Invariants 46, 47, 56, 68; Premium architecture §20 constraint 2; R3B-1 | Compatible for the offline slice: the identity is configured server-side, is not derived from an email address, and every call of the authorizer names it. Which identity provider key an owner-operated DEV/TEST harness uses in a real run is not decided here and stays open for the owner before any real execution |
| Added after the canonical audit of 2026-10-05: disconnect and revocation through the existing authorizer | Invariant 58; Premium architecture §20 constraint 7 | Compatible: the harness keeps no copy of a connection's authorization state, so a disabled connection is refused at its next call by the product itself. A fake provider's answer to a revocation is not evidence of monday's behavior |

**Result: no contradiction, no blocker for the offline slice.** Real execution stays blocked by the open items of
D3G-8, and this slice decides none of them.

### 3G offline implementation record (2026-10-05)

The offline slice is implemented and locally verified, and it waits for the owner's review. The full record is
`docs/gates/gate-3G-offline-report.md`. In short, all local and on the uncommitted tree: a clean build with 0 warnings
and 0 errors; both repository verifiers PASS; 1,694 of 1,694 tests passed with none skipped (unit 949, architecture
43, security 168, integration 534), of which 92 are new; 97 of 97 targeted mutants of the new harness guards caught,
each restored and verified by hash; no file under `src/` changed; and the six frozen documents unchanged. The
canonical-auditor, the tenant-security-reviewer and the gate-verifier reviewed the change set, and their findings are
in the report. CI for the change set is NOT RUN. No standalone run was performed.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real execution is NOT AUTHORIZED, Gate 3 closure is NOT
AUTHORIZED, real acceptance is NOT PROVEN, and every real-execution blocker of D3G-8 stays open.

## 3G — Offline Key Vault secret-store adapter (S-1 to S-5, approved 2026-10-05)

### Owner decisions S-1 to S-5 (2026-10-05)

On 2026-10-05 the product owner approved S-1 to S-5 exactly as defined in the corrected
`docs/gates/gate-3G-live-enablement-plan.md` (SHA-256 `8a9ade1c5b1e4db227bc790f892268b48b859a16e88bfcdf5d06cebf1da19dc9`)
and authorized only the smallest offline adapter slice that plan defines. The owner's full text was given in the working
session of 2026-10-05 and is not stored verbatim in this repository; the list below is the repository's record of it,
restated closely from that text:

- **S-1 — Direction.** A Key Vault-backed `IMondaySecretStore` is the approved direction for real monday secrets in the
  Gate 3G DEV/TEST harness. This approval authorizes the OFFLINE adapter and its tests only. No real vault, no Azure
  resource, no permission grant and no real secret are authorized.
- **S-2 — Placement and packages.** Implement the adapter under `tests/App.Harness.Monday`. Use injected transport and
  token-source seams. Do not add a new package. Do not change anything under `src/`. Production placement remains
  undecided.
- **S-3 — Value encoding and size.** Adapter-written values use base64url without padding and one exact Fully content
  type. They must round-trip byte-for-byte. Unmarked plaintext is accepted only for `wce-monday-client-secret` and
  `wce-monday-signing-secret`. Any unmarked value under another secret name is Unavailable. Any unknown content type is
  Unavailable. A malformed value carrying the Fully content type is Unavailable and must never fall back to plaintext.
  The adapter must refuse values above 18,000 bytes before sending any request. The 18,000-byte boundary is explicitly
  a supported compatibility subset and is NOT claimed to cover every theoretical value the connector can produce.
- **S-4 — Confirmed deletion semantics.** For the Key Vault adapter, Succeeded from Delete means the vault accepted
  deletion and the secret no longer exists in the active namespace for ordinary Get access. It does NOT mean the
  underlying secret material has been irreversibly destroyed. NotFound means no active secret existed under that name
  when the vault answered. Verifier-cleanup confirmed deletion therefore means removal from ordinary Get access.
  Retention, purge protection, recover authority, purge authority and post-run cleanup remain future live decisions and
  are NOT approved here.
- **S-5 — Failure mapping and retry.** Implement the corrected operation-specific fail-closed policy from the plan.
  Only a qualifying Key Vault 404 with a well-formed error response may map to NotFound. All unresolved ambiguity,
  malformed responses, refusals, conflicts, throttling exhaustion, server failures, connection failures and timeouts
  map to Unavailable unless a later permitted retry settles the outcome. Get, Set and Delete: maximum 3 attempts; retry
  only the approved retryable classes; 5-second per-attempt timeout; 10-second total Get budget; 15-second total
  Set/Delete budget. Honor Retry-After only within the approved 2-second cap and remaining operation budget. A retried
  Set must resend identical bytes to the same name. 409 must not be retried. Caller cancellation propagates and must not
  be converted into a store status. Do not cache values or absence. A timeout must never be treated as proof that a
  write did not occur.

A further owner instruction of the same session, on the vault address: host-name casing is not a different origin; a
trailing-slash difference is not a different origin; an explicit port must still resolve to the same effective origin;
the test is fixed rather than the adapter tightened when an address is semantically the same origin, and the origin
boundary is not weakened.

**Owner clarification of S-1 and D3G-1 (approved by the product owner on 2026-10-06), in the owner's words:** "S-1
permits only the OFFLINE Key Vault adapter that does not hold, use, or obtain any real credential. D3G-1 remains fully
in force and continues to prohibit every real-credential adapter and every real connection to Azure or Key Vault." This
clarification does not authorize live execution. Consequently: S-1 authorizes only an offline adapter; the adapter may
not hold, obtain, use or authenticate with a real credential; D3G-1 stays fully in force, unweakened and
unreinterpreted; and no real Azure or Key Vault connection is authorized.

Status is unchanged: **Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**; real acceptance NOT PROVEN; real
execution and Gate closure NOT AUTHORIZED. Webhook provisioning (W-1, W-2) is not approved.

### Offline adapter slice: scope and acceptance (recorded before code)

- **Placement.** New files under `tests/App.Harness.Monday/Secrets/`: the adapter, its value codec and its access-token
  source seam. Tests under `tests/App.Integration.Tests/Harness/`. New harness-scoped guards in
  `tests/App.Architecture.Tests/HarnessBoundaryTests.cs`. No change under `src/`, no package, no project reference, no
  change to an existing guard's assertions.
- **Not wired.** `HarnessProgram`, `HarnessHost` and `HarnessStandaloneGate` are unchanged: the default executable still
  composes the refusing secret store and approves no database. No real HTTP handler, no real token source, no Azure,
  Key Vault, monday, OAuth or identity-provider contact, and no real secret.
- **Seams.** The adapter takes an injected `HttpMessageHandler` (sent through `HttpMessageInvoker`), an injected access
  token source, the connector's existing `IMondayDelay` seam for retry waits and a `TimeProvider` for attempt timeouts and
  budgets. Existing harness guards forbid naming a network handler, a sleep or a stopwatch in harness sources, and
  they stay unchanged.
- **Requests.** Only the configured HTTPS vault origin (`https://{name}.vault.azure.net/`), only get
  (`secrets/{name}/`, the latest version), set (`PUT secrets/{name}`) and delete (`DELETE secrets/{name}`), with the
  pinned API version `2025-07-01`, never a secret version, list, deleted-secret, recover, backup, restore, purge or
  update request.
- **Acceptance.** The 22 test requirements listed in the owner's implementation authorization of 2026-10-05 (given in
  the working session, not stored verbatim here; the tests that meet them are listed in the offline report); targeted
  mutation evidence for the new guards; the existing guards; the full local suite; and the canonical-auditor,
  tenant-security-reviewer and gate-verifier reviews. An evidence package outside the repository. No stage, commit or
  push.

### Offline adapter slice: implementation record (2026-10-05)

Implemented as recorded above, offline only, uncommitted. Full record and evidence: `docs/gates/gate-3G-offline-report.md`,
section "Addendum — offline Key Vault adapter slice (S-1 to S-5)".

- New: `tests/App.Harness.Monday/Secrets/` (`KeyVaultSecretStore.cs`, `KeyVaultSecretCodec.cs`,
  `IKeyVaultAccessTokenSource.cs`); `tests/App.Integration.Tests/Harness/` (`HarnessKeyVaultFakes.cs`,
  `HarnessKeyVaultSecretStoreTests.cs`, `HarnessKeyVaultConnectorFlowTests.cs`). Changed: one new guard method in
  `tests/App.Architecture.Tests/HarnessBoundaryTests.cs`; no existing assertion changed. No change under `src/`, no
  package, not wired into the harness host.
- The vault address is checked by meaning, not spelling: host-name case, an implied root path and an explicit default
  port are the same origin, and every request is built from the parsed address.
- Local results on 2026-10-05, uncommitted tree, against the shared local test database: the harness namespace was 163
  of 168, with 5 pre-existing relay-dependent harness tests failing because of stale unrelayable outbox rows in that
  database (see the report).
- Revalidation on 2026-10-06, same uncommitted tree, against a temporary isolated database on the local server (created
  for the run, given only the database-scoped grants of `infra/local/postgres/init/10-runtime-role.sh`, migrated from
  zero by the test fixture, dropped afterwards; no row count or content of the shared database changed): clean build
  with 0 warnings and 0 errors; both repository verifiers PASS; the five previously failing tests 5/5; full local suite
  **1,783 of 1,783**, none failed, none skipped (unit 949, architecture 44, security 168, integration 622). Targeted
  mutation: 44 mutants, 43 caught, and K05 **EQUIVALENT MUTANT — PROVEN** by an exhaustive offline comparison over every
  final base64url quantum (17,309,696 inputs, 0 disagreements). Reviews (final round): canonical-auditor CONFORMS WITH
  FOLLOW-UP; tenant-security-reviewer PASS WITH FOLLOW-UP; gate-verifier READY WITH FOLLOW-UP (readiness for owner
  review of this offline slice only, not a closure). CI NOT RUN.
- `READY WITH FOLLOW-UP` means ready for owner review and checkpoint of this OFFLINE slice only. It does not mean Gate 3G
  closure, Gate 3 closure, live readiness, Azure approval or monday approval.

### Offline adapter slice: owner decisions on the evidence (approved by the product owner on 2026-10-06)

The product owner explicitly approved the following. The quoted sentences are the owner's words; the text outside the
quotes restates the rest of each decision closely:

1. **K05.** "K05 is accepted as an EQUIVALENT MUTANT for the current runtime, .NET 10.0.11. The exhaustive proof showed
   no behavioral difference across the relevant canonical/non-canonical base64url trailing-bit space. The existing
   defensive check remains in production/test code as defense in depth." The canonical re-encoding check in
   `KeyVaultSecretCodec.DecodeMarked` stays.
2. **Shared-database statistics delta.** "The unexplained shared-database PostgreSQL statistics delta (`tup_updated` +7)
   is accepted as a NON-BLOCKING EVIDENCE NOTE. This does NOT mean we claim to know its exact cause." The evidence
   showed no shared application-row count change, no shared content-fingerprint change, and no shared rows deleted. No
   cause is claimed, and the delta stays recorded transparently in the evidence.
3. **Temporary isolated database.** "The hand-run temporary isolated database lifecycle is accepted as compliant with
   D3G-4 for this evidence run." The owner's rationale: the database was temporary and separate; the shared database was
   not cleaned or altered for the test; grants were scoped to the temporary database; the runtime role itself was not
   elevated; the temporary database was dropped after the run.
4. **Stale shared rows.** "The stale rows in the shared local database are NOT to be cleaned as part of Gate 3G. They
   are not a Gate 3G blocker because the full suite was proven on the accepted isolated database. Any cleanup of those
   rows is a separate maintenance task requiring its own authorization."

These decisions settle evidence questions for this offline slice and this evidence run only. They authorize no live
execution, no real credential, no Azure, Key Vault or monday contact, no shared-database cleanup, no commit and no push.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real execution and Gate 3 closure are NOT AUTHORIZED; real
acceptance is NOT PROVEN.

## 3G — Stage B owner decision B-1 (approved 2026-10-06)

Stage B (live readiness preparation) was planned on 2026-10-06 in `docs/gates/gate-3G-live-enablement-plan.md`, section
"Gate 3G — Stage B Live Readiness Preparation", committed as `34e1705` (CI run 37414993310 passed). That plan proposes
decisions B-1 to B-19.

**B-1 — Stage B preparation boundary: APPROVED — 2026-10-06.** The product owner approved B-1 exactly as proposed in
that plan. The quoted sentences are the owner's words; the list restates the rest of the decision closely:

- Approved meaning: "Offline Stage B slices may author code and IaC that are capable of using a real credential in the
  future, including the real handler and token source, provided that none of that code is executed against a real
  endpoint or with a real credential."
- "This is a narrow, explicit supersession of D3G-1 for AUTHORING ONLY."
- D3G-1 continues to prohibit: obtaining a real credential; storing a real credential; using a real credential; Azure
  contact; Key Vault contact; Entra or identity-provider contact; monday contact; OAuth execution; deployment; and live
  execution.
- "No other B decision is approved." **B-2 to B-19 remain PROPOSED — NOT APPROVED.**

Consequences recorded here, not added to the decision: authoring capability is allowed; real execution is not. B-1
supersedes D3G-1 only for authoring, and only as stated. S-1 to S-5 and every earlier Gate 3 decision are unchanged.
The text of the owner's clarification of S-1 and D3G-1 of 2026-10-06 is not edited.

**Owner confirmation (2026-10-06), in the owner's words:** "B-1 supersedes the earlier S-1 clarification only for
AUTHORING: code for a future real-credential adapter may be written offline, but D3G-1 still prohibits obtaining,
storing, using, or executing with any real credential, and prohibits all real Azure, Key Vault, Entra, monday, OAuth,
deployment, or live execution." Approving B-1 starts no Stage B slice: each slice still needs the B
decisions it depends on and its own authorization, and **no Stage B implementation has started.**

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate
closure are NOT AUTHORIZED.

## 3G — Stage B owner decisions B-7, B-10 and B-11 (approved 2026-10-06)

On 2026-10-06, after B-1 was recorded and committed as `b0b7248` (CI run 37416642461 passed), the product owner
explicitly approved the following decisions exactly as recommended in `docs/gates/gate-3G-live-enablement-plan.md`,
section "Gate 3G — Stage B Live Readiness Preparation". The list restates the owner's text closely:

- **B-7 — Runtime identity: APPROVED — 2026-10-06.** Use the certificate-credential service principal described in the
  Stage B plan as the future harness runtime identity. The alternatives remain rejected or not selected: a dedicated
  interactive Entra user; a client secret; a managed identity from the owner's machine. This approval does NOT create
  the identity or the certificate.
- **B-10 — Token source and transport approach: APPROVED — 2026-10-06.** Use a hand-written certificate assertion; no
  new package; an explicit credential only, never an ambient credential chain; a pinned `SocketsHttpHandler` per
  destination; enablement only through reviewed code, never arbitrary configuration. `Azure.Identity` /
  `ClientCertificateCredential` is NOT approved.
- **B-11 — Guard evolution: APPROVED — 2026-10-06.** An existing blanket guard may be replaced only by an
  equal-or-stricter pinned allowance in exactly named files. No existing guard is simply deleted; the allowance must be
  narrower than or equal to the old protection; targeted mutation evidence is required for the changed guard.

State after these decisions:

- Approved Stage B decisions: exactly **B-1** (authoring only), **B-7**, **B-10** and **B-11**.
- **B-2 to B-6, B-8, B-9 and B-12 to B-19 remain PROPOSED — NOT APPROVED.**
- The approval set that slice B2 (real transport and token source) depends on, B-1, B-7, B-10 and B-11, is now
  complete. **Stage B2 is NOT STARTED and NOT AUTHORIZED**: these decisions authorize no slice, and B2 needs the
  owner's own authorization. No Stage B implementation has started.
- B-1's boundary and the owner's confirmation of it are unchanged: authoring only; D3G-1 still prohibits obtaining,
  storing, using or executing with any real credential, and all real Azure, Key Vault, Entra, monday, OAuth,
  deployment or live execution. S-1 to S-5, D3G-1 to D3G-8 and every earlier Gate 3 decision are unchanged.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate
closure are NOT AUTHORIZED.

## 3G — Stage B2 real transport and token source (authorized 2026-10-06, offline only)

### Authorization (recorded before code)

On 2026-10-06, after the B2 decision set was recorded and pushed as `56f61a0` (CI run 37419000306 passed), the product
owner explicitly authorized **Stage B2 — Real Transport and Token Source** as defined in
`docs/gates/gate-3G-live-enablement-plan.md`, section "Gate 3G — Stage B Live Readiness Preparation". In the owner's
words: "The product owner explicitly AUTHORIZES Stage B2: Stage B2 — Real Transport and Token Source. This authorization
is OFFLINE IMPLEMENTATION ONLY." and "No other B decision is approved." The owner's full text was given in the working
session and is kept verbatim in the slice's evidence package outside the repository; the list below restates the rest of
it closely:

- **Stage B2 implementation is OFFLINE ONLY.** Code capable of using a real credential may be authored (B-1), under
  `tests/App.Harness.Monday/Live/`, and exercised only by offline tests with fake in-memory handlers and generated,
  ephemeral test certificates.
- **Its approved decision set is B-1, B-7, B-10 and B-11.** No other B decision is approved: B-2 to B-6, B-8, B-9 and
  B-12 to B-19 remain PROPOSED — NOT APPROVED.
- **No real credential and no provider execution are authorized:** no real certificate, no Azure, Key Vault, Entra or
  identity-provider contact, no monday contact, no OAuth execution, no deployment, no live execution. D3G-1 stays in
  force except for the authoring supersession of B-1.
- **Not wired:** nothing in `HarnessProgram`, the default executable, `HarnessStandaloneGate`, any configuration or any
  environment variable reaches the new components; no database name is approved.
- Guards change only under B-11: equal-or-stricter pinned allowances in exactly named files, never a deletion, with
  targeted mutation evidence.
- Stop conditions: a change under `src/`; a new package; `Azure.Identity` or `DefaultAzureCredential`; a real credential
  or certificate; any provider, DNS or network access by the new tests; a general weakening of a guard;
  configuration-controlled enablement; a change to `HarnessStandaloneGate`; any deployment or cloud resource.

Protocol contract used, from Microsoft's public documentation read on 2026-10-06 (no account, tenant or endpoint
contacted): "Microsoft identity platform certificate credentials" (header `alg` PS256, `typ` JWT, `x5t#S256` the
base64url SHA-256 thumbprint of the certificate's DER encoding; claims `aud` =
`https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token`, `iss` = `sub` = the client id, a GUID `jti`, `nbf`,
`iat`, and `exp` at most 5 to 10 minutes after `nbf`; PSS padding) and "OAuth 2.0 client credentials flow" (form
`client_id`, `scope`, `client_assertion_type` = `urn:ietf:params:oauth:client-assertion-type:jwt-bearer`,
`client_assertion`, `grant_type` = `client_credentials`; a response of `token_type`, `expires_in` in seconds and
`access_token`).

Status: **Stage B2: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**, not PASS and not closed. **Gate 3G stays IN PROGRESS —
OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate closure are NOT AUTHORIZED.

### Implementation record (2026-10-06, offline, uncommitted)

Implemented as authorized above, offline only, on the uncommitted tree at `56f61a0`. Full record and evidence:
`docs/gates/gate-3G-offline-report.md`, section "Addendum — Stage B2 real transport and token source".

- New, under `tests/App.Harness.Monday/Live/`: `LiveTransport.cs` (the pinned `SocketsHttpHandler`, built in one place, and
  three destination guards: the Entra token request of one tenant, the Key Vault adapter's get, set and delete on one
  vault, and the connector's three fixed monday endpoints), `EntraClientAssertion.cs` (the documented PS256 certificate
  assertion) and `EntraCertificateTokenSource.cs` (an `IKeyVaultAccessTokenSource` over the client-credentials grant, with
  an explicit tenant, client and certificate only, the Key Vault scope only, a fail-closed answer reader and an in-memory
  token kept until five minutes before expiry).
- Not wired: nothing outside `Live/` names a Stage B2 type, nothing constructs the token source or a live transport,
  `HarnessStandaloneGate` still approves no database, and the default executable is unchanged.
- Guards under B-11: three existing harness guard methods replaced blanket bans by pinned allowances naming the Stage B2 files
  (`SocketsHttpHandler`, handler construction, `HttpMessageInvoker`, `HttpMethod`, the name Key Vault, and the
  five-minute constant), each equal or stricter elsewhere; one new guard pins the Stage B2 files, their settings, their
  form and their non-wiring, and forbids every ambient or alternative credential. No guard was deleted.
- No change under `src/`, no package, no project reference, no configuration key; no real credential, certificate,
  network, DNS or provider contact. Tests use fake in-memory handlers and certificates generated in memory per test.
- Local results: clean build 0 warnings, 0 errors; both verifiers PASS; 78 new tests (77 integration, 1 architecture);
  full local suite on an owner-approved temporary isolated database **1,861 of 1,861** (unit 949, architecture 45, security
  168, integration 699), the shared database's fingerprint unchanged; targeted mutation 78 mutants: 77 CAUGHT, 0
  SURVIVED, 1 INVALID (a non-compiling form, superseded by its compiling form, CAUGHT); pinned gitleaks 8.30.1: no leaks.
  CI NOT RUN.

Status at the time of this record: **Stage B2: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**, ready for owner review only
after the reviews recorded in the report; not PASS and not closed. The reviews were then recorded in the report:
canonical-auditor CONFORMS WITH FOLLOW-UP, tenant-security-reviewer PASS WITH FOLLOW-UP, and gate-verifier round 2 READY
WITH FOLLOW-UP.

### Owner review (2026-10-06): accepted as a checkpoint candidate

The product owner reviewed the evidence package `FULLY-3G-stage-B2-offline-evidence-20261006T071054Z.zip` (SHA-256
`8556600255054e29677445cbce04c29516ed8c469e3807335753d0733f5eb534`) and accepted the Stage B2 implementation as a
**CHECKPOINT CANDIDATE**. In the owner's words: "This is NOT yet Stage B2 PASS or closure." and "Final PASS requires the
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
- the three reviews.

**BEFORE-WIRING requirements (accepted by the owner on 2026-10-06; open, not resolved).** They do not block the Stage B2
offline checkpoint, and they are mandatory before any future wiring or real execution:

1. Harden Authorization rejection by raw header name, not only the typed Authorization property.
2. Define and enforce destination-specific header allowlists.
3. Resolve telemetry handling for Key Vault URL paths, so that tenant-embedded secret names cannot leak through HTTP
   telemetry.
4. Resolve the trusted Fully tenant context used when this slice is wired.

Status at that review (superseded by the owner's PASS below):
- Stage B2 implementation: COMPLETE OFFLINE.
- Owner review: ACCEPTED FOR CHECKPOINT.
- Stage B2 PASS and closure: not yet.
- CI for the implementation candidate: not run.

The checkpoint was then committed as `d1468bb6a6bde130ec24dc72c0571255d1391f8c` ("feat: checkpoint Gate 3G Stage B2
offline transport", parent `56f61a0`, the 12 reviewed paths) and pushed by separate owner authorizations. CI run
37434239946 succeeded on that commit: secret scan, build/verify/test and container images, with 1,861 tests (1,851
passed, 0 failed, 10 skipped, the Service Bus emulator tests that CI skips by design).

### Owner PASS (2026-10-06)

The product owner explicitly approved: **"Stage B2 — Real Transport and Token Source = PASS"**, dated 2026-10-06. The
approval applies to the offline Stage B2 slice only.

The owner's accepted basis:
- the checkpoint commit `d1468bb6a6bde130ec24dc72c0571255d1391f8c`;
- CI run 37434239946 — SUCCESS: 1,861 total, 1,851 passed, 0 failed, 10 skipped, the 10 being the expected Service Bus
  emulator tests in CI;
- the evidence package `FULLY-3G-stage-B2-offline-evidence-20261006T071054Z.zip` (SHA-256
  `8556600255054e29677445cbce04c29516ed8c469e3807335753d0733f5eb534`).

**Stage B2: PASS — OFFLINE SLICE COMPLETE.** The implementation and the offline evidence are accepted.

It does **not** mean any of the following:
- Gate 3G PASS or Gate 3 PASS;
- real acceptance proven;
- real execution or live wiring authorized;
- Azure, Entra, Key Vault or monday execution authorized.

**The four BEFORE-WIRING requirements remain OPEN** and are mandatory before any future wiring or real execution:
1. harden Authorization rejection by raw header name;
2. define and enforce destination-specific header allowlists;
3. resolve Key Vault path telemetry handling, so that tenant-embedded secret names cannot leak through HTTP telemetry;
4. resolve the trusted Fully tenant context used when this slice is wired.

The approved B decisions remain exactly B-1, B-7, B-10 and B-11. B-2 to B-6, B-8, B-9 and B-12 to B-19 remain PROPOSED —
NOT APPROVED. No other Stage B slice (B0, B1, B3, B4, B5) is authorized or started, and Stage B2 needs no further work
unless separately authorized.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate 3G
closure are NOT AUTHORIZED.

## 3G — Stage B owner decisions B-14 and B-15 (approved 2026-10-06)

On 2026-10-06, after the Stage B2 PASS was recorded and pushed as `2aa9575` (CI run 37437521793 passed), the product
owner explicitly approved the following decisions exactly as recommended in `docs/gates/gate-3G-live-enablement-plan.md`,
section "Gate 3G — Stage B Live Readiness Preparation". The list restates the owner's text closely.

**B-14 — Standalone database: APPROVED — 2026-10-06.** Approved direction:
- use a persistent dedicated database on the existing local PostgreSQL server;
- migrate it from zero;
- grant only the database-scoped privileges defined by the existing local runtime-role grant procedure;
- seed one fresh tenant, user and connection through a reviewed owner-run script outside the harness;
- the harness itself must retain no administrative database path;
- take fresh RLS and privilege evidence on that dedicated database; the shared suite database is not acceptable as live
  evidence;
- extend the runtime-role safety check with a recursive `pg_auth_members` membership check;
- accept the remaining limitation that `workcontinuity_app` is a cluster-wide PostgreSQL role shared with the suite
  database.

This approval supersedes the previous D3G-4 deferral of a persistent standalone database for this Stage B preparation
path. The alternative, a dedicated PostgreSQL container, is NOT selected. **It does NOT authorize creating or
provisioning the persistent database now:** actual persistent database creation remains a separate local approval
boundary.

**B-15 — Transport isolation: APPROVED — 2026-10-06.** Use the existing shared local emulator under the Stage B plan
conditions:
- no concurrent full-suite run;
- a fresh tenant GUID never used before;
- the consumer limited to its own session;
- the session verified empty before the run.

The alternative, a dedicated emulator instance, is NOT selected. This approval does NOT authorize a live run or any
provider execution.

State after these decisions:

- Approved Stage B decisions: exactly **B-1** (authoring only), **B-7**, **B-10**, **B-11**, **B-14** and **B-15**.
  B-2 to B-6, B-8, B-9, B-12, B-13 and B-16 to B-19 remain **PROPOSED — NOT APPROVED**.
- **Stage B2 remains PASS — OFFLINE SLICE COMPLETE.** Its four BEFORE-WIRING requirements remain open.
- **Stage B3 — Standalone database and transport preparation:** its decision prerequisites, B-14 and B-15, are
  complete. **Stage B3 itself is NOT STARTED and NOT AUTHORIZED** until the owner separately authorizes the slice.
  - Its offline implementation scope is limited to: the recursive role-membership safety check; a reviewed seed script;
    the procedure for fresh RLS and R3B-2 evidence; and the shared-emulator isolation checks and procedure.
  - `HarnessStandaloneGate` must remain empty.
  - The persistent standalone database is not created by this decision and remains subject to a separate local
    approval.
- D3G-1 to D3G-3 and D3G-5 to D3G-8, S-1 to S-5, R3B-1, R3B-2 and every earlier Gate 3 decision are unchanged. D3G-4 is
  superseded only as B-14 states, and only for this Stage B preparation path.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate 3G
closure are NOT AUTHORIZED.

## 3G — Stage B3 authorization (2026-10-06)

On 2026-10-06, after the B-14 and B-15 decision set was recorded and pushed as `3eb24d9` (CI run 37442070826 SUCCESS),
the product owner explicitly authorized:

**Stage B3 — Standalone Database and Transport Preparation: AUTHORIZED — 2026-10-06. IN PROGRESS — OFFLINE
IMPLEMENTATION ONLY.**

- Approved decision set governing the slice: exactly **B-14** (standalone database direction) and **B-15**
  (shared-emulator isolation direction).
- Authorized offline scope, limited to four areas: A, the recursive PostgreSQL role-membership safety check; B, a
  reviewed standalone seed script, authored and not run; C, the fresh RLS and R3B-2 evidence procedure; D, the
  shared-emulator isolation checks and procedure.
- **Persistent standalone database provisioning is NOT authorized yet.** No persistent database is created, dropped,
  migrated or seeded; the seed script is not executed; no role or grant is altered.
- **`HarnessStandaloneGate` stays empty**, with no configuration or environment override around it.
- **No live run is authorized. No real provider execution is authorized.** No Azure, Entra, Key Vault, monday or OAuth
  contact, no real credential and no deployment.
- A temporary isolated test database, if one is needed, is a separate approval: the Stage B2 temporary-database approval
  does not carry forward.
- Stage B2 remains **PASS — OFFLINE SLICE COMPLETE**; its four BEFORE-WIRING requirements remain OPEN and are not
  addressed by B3. No other Stage B slice is authorized.

Stage B3 is not PASS and not complete. **Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is
NOT PROVEN; real execution and Gate 3G closure are NOT AUTHORIZED.

## 3G — Stage B3 owner decisions: B-14 seed clarification and owner-run tool structure (approved 2026-10-06)

On 2026-10-06, after the Stage B3 working tree had been verified intact following an unexpected shutdown, and after a
read-only design review of the seed, the product owner explicitly approved the following two decisions. The list
restates the owner's text closely; quoted sentences are the owner's words.

**Decision 1 — B-14 clarification: APPROVED — 2026-10-06.** The Stage B3 standalone seed prepares only:
- one fresh tenant;
- one fresh canonical user.

It MUST NOT directly seed:
- a monday connection;
- connector authorization;
- OAuth transaction;
- token material;
- PKCE verifier;
- delegated provider identity.

The single fresh monday connection required by the Stage B preparation path must be created later through the
product's existing Connect / `MondayConnectionAuthorizer` lifecycle during a separately approved run.

Reason: "A direct SQL connection insert would bypass the existing connector lifecycle and could create a connection
without valid authorization/delegated identity."

"This clarification supersedes only the literal earlier wording that implied the seed itself creates the connection."
That wording is B-14's "one fresh tenant, user and connection seeded by a reviewed owner-run script outside the
harness" and, in `docs/gates/gate-3G-live-enablement-plan.md` area I, "the `tenants`, `users` and `connections` rows
must exist". "R3B-1, D3B-1 and all earlier Gate 3 decisions remain unchanged."

**Decision 2 — Owner-run tool structure: APPROVED — 2026-10-06.**
- One PowerShell owner entry point: `eng/harness-standalone.ps1`.
- The existing SQL files remain reviewed payloads: `eng/harness-standalone-seed.sql` and
  `eng/harness-standalone-evidence.sql`. "The SQL files are payloads only, not supported operator entry points."
- The wrapper must: require an explicit database name; have no default target database; refuse the shared suite
  database; never derive the target database from `.env`; accept only the approved standalone naming pattern; require
  `-Approve` before seed execution; support a read-only evidence command separately; never print passwords,
  connection strings, tokens or secrets; never create or drop a database; never alter, grant or revoke roles or
  privileges; never run migrations; and operate only against the local PostgreSQL container.

State after these decisions:
- **Stage B3 stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY. Stage B3 is NOT PASS.**
- Provisioning the persistent standalone database still requires a separate owner approval. The seed has never been
  executed, and `HarnessStandaloneGate` stays empty.
- Creating and dropping a temporary isolated test database still requires a separate owner approval.
- The approved B decisions remain exactly B-1, B-7, B-10, B-11, B-14 (as clarified above) and B-15; every other B
  decision remains PROPOSED — NOT APPROVED. Stage B2 remains PASS — OFFLINE SLICE COMPLETE, and its four
  BEFORE-WIRING requirements remain OPEN.
- Areas A, C and D of Stage B3 need no change because of the seed clarification.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate 3G
closure are NOT AUTHORIZED.

## 3G — Stage B3 temporary isolated validation database (approved 2026-10-06)

On 2026-10-06, with the Stage B3 offline implementation validated up to the temporary-database boundary, the product
owner explicitly approved "the temporary isolated database lifecycle" for "Stage B3 validation only". The list restates
the owner's text closely; quoted sentences are the owner's words.

- Exact database name: `wce_it_b3_isolated_20261006`.
- **Phase 1 — APPROVED:** create the database, apply only the approved database-scoped grants, let the existing test
  fixture migrate it from zero, then run the two `HarnessBackboneLoopTests` first and, if both pass, the same 15 harness
  test classes (271 tests). Had either of them failed, the work would have stopped before Phase 2 and the failure would
  have been treated as a potential Stage B3 regression. If both passed, "the shared-database stale-state hypothesis is
  accepted as the explanation for the prior shared-DB failures, and Phase 2 is authorized automatically."
- **Phase 2 — CONDITIONALLY APPROVED**, only after Phase 1 passed fully: the full local suite against the same database,
  with the suite's existing test lifecycle (its ordinary exact-named temporary test databases, the documented throwaway
  bootstrap role, and transactional NOLOGIN probe roles that are rolled back). "This approval does NOT permit changing
  the attributes of workcontinuity_app." The runtime role must remain LOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE,
  NOBYPASSRLS and NOINHERIT: "Do not ALTER ROLE workcontinuity_app."
- Grants: only the existing database-scoped grant model of `infra/local/postgres/init/10-runtime-role.sh`. "No new
  privilege model is authorized." "No shared-database grant or role change is authorized."
- Migration: the existing test fixture's migration from zero. "Do not use this approval to provision the future
  persistent B3 standalone DB." The Stage B3 seed payload is not run against the temporary database, and "The owner-run
  persistent seed remains NOT RUN."
- Shared database: a read-only fingerprint before the temporary database is created and the same fingerprint after all
  validation and cleanup, compared. Its stale rows are not cleaned, no application row is deleted, it is not reset or
  migrated, and its grants and roles are not changed. "Normal Web/Worker health-check/statistical activity is not
  considered an application-data change, but record it if observed."
- Cleanup: drop exactly `wce_it_b3_isolated_20261006`, with a normal `DROP DATABASE` first and `WITH (FORCE)` only if an
  open session prevents cleanup, and verify that `pg_database` reports no database of that name. "Do not drop any other
  database."
- After a green Phase 2: resume the remaining Stage B3 validation (targeted mutation, both verifiers, architecture and
  security checks, the secret scan, the three review agents, the implementation record in
  `docs/gates/gate-3G-offline-report.md`, and the evidence package), and correct the stale Stage B2 status line of the
  live-enablement plan by current-status wording only. "Do not mark Stage B3 PASS. Stop at owner review."
- Boundaries kept: no persistent standalone database; no owner seed; no database added to `HarnessStandaloneGate`; the
  shared database not cleaned; the runtime role's attributes not altered; the emulator not reset or reconfigured;
  nothing under `src/`; no package; no Azure, Entra, Key Vault, monday or OAuth contact; no real credential; no
  deployment; no staging, commit or push.

## 3G — Stage B3 implementation record (2026-10-06, offline, uncommitted)

Implemented as authorized above, offline only, on the uncommitted tree at `3eb24d9`. Full record and evidence:
`docs/gates/gate-3G-offline-report.md`, section "Addendum — Stage B3 standalone database and transport preparation".

- Area A (B-14): the harness's runtime-role safety check follows `pg_auth_members` recursively. Membership at any depth
  in a superuser, in a role with BYPASSRLS or in the owner of a table of the schema is a fourth privilege, enough by
  itself, and any answer the check cannot read fails closed (`tests/App.Harness.Monday/Evidence/EvidenceReaders.cs`).
- Area B (B-14 as clarified): `eng/harness-standalone-seed.sql`, a reviewed payload that writes one fresh tenant and one
  fresh canonical user only, never run; and the owner entry point `eng/harness-standalone.ps1` (`seed` behind
  `-Approve`, and a separate read-only `evidence` command), never run outside its test sandbox.
- Area C (B-14, R3B-2): `eng/harness-standalone-evidence.sql`, one read-only transaction, rolled back, of 22 closed
  checks (18 catalog checks and 4 isolation checks), never run on a standalone database.
- Area D (B-15): `tests/App.Harness.Monday/Composition/TransportSessionPreflight.cs`. Before it publishes or consumes
  anything, the harness requires the configured tenant's own session on the local emulator to be empty and its own to
  take (two new closed refusals), and its consumer stays limited to that session.
- `HarnessStandaloneGate` is still empty; no change under `src/`, no package, no configuration key; no real credential
  or provider contact.
- Local results: 84 new tests (integration 26, security 57, architecture 1); clean non-incremental build 0 warnings,
  0 errors. Phase 1 on the temporary database: both `HarnessBackboneLoopTests` passed, then the 15 harness classes
  271 of 271. Phase 2: **1,931 of 1,931 executed tests passed, none failed, none skipped** (unit 949, security 225,
  architecture 46, integration 711); the 14 integration tests that bootstrap the real runtime role, and so run
  `ALTER ROLE workcontinuity_app`, were not run, by the owner's condition. Targeted mutation: 59 mutants, 59 CAUGHT,
  0 SURVIVED, 0 INVALID. Both verifiers PASS; pinned gitleaks 8.30.1: no leaks. The temporary database was dropped by a
  normal `DROP DATABASE` and none of that name remains; the shared database's fingerprint is unchanged apart from server
  statistics. **CI: NOT RUN.**

Status at the time of this record: **Stage B3: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**; the offline implementation
is validated locally and is ready for owner review only after the reviews recorded in the report; not PASS and not
closed. The reviews were then recorded in the report: canonical-auditor CONFORMS WITH FOLLOW-UP, tenant-security-reviewer
PASS WITH FOLLOW-UP, and gate-verifier READY WITH FOLLOW-UP (in the sense "ready for the owner's review"); none found a
defect. Their follow-ups and six open questions for the owner are recorded there, among them the naming pattern of the
standalone database, and whether keeping the temporary database after Phase 2 for the mutation campaign's integration
runs (13:26 to 13:53 UTC; dropped at 14:03:53 UTC) was within the approval.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate 3G
closure are NOT AUTHORIZED.

## 3G — Stage B3 owner review decisions (approved 2026-10-06)

On 2026-10-06, after the Stage B3 offline implementation, its evidence package and a read-only review of six proposed
decisions, the product owner explicitly approved the following six decisions. The list restates the owner's text
closely; quoted sentences are the owner's words.

1. **Standalone database naming.** `^wce_harness_[a-z0-9_]{1,51}$`: "Full-string, lowercase, case-sensitive." "No
   default target." "The shared DB must also be refused case-insensitively."
2. **The temporary database's use for mutation.** "The already-completed use of wce_it_b3_isolated_20261006 for
   mutation is accepted for this evidence run only." It is recorded as an accepted procedural deviation. "No rerun is
   required solely because of that deviation." "Future temp-DB approvals must explicitly include mutation use."
3. **Runtime-role hardening.** `workcontinuity_app` must be LOGIN, NOSUPERUSER, NOBYPASSRLS, NOCREATEROLE, NOCREATEDB,
   NOREPLICATION and NOINHERIT. Additionally: zero direct or transitive memberships in `pg_auth_members`; no ownership
   of database, schema, table, view or sequence; and "missing or unexpected evidence fails closed." "Do not maintain a
   pg_* allowlist." "Membership itself is disqualifying regardless of INHERIT semantics."
4. **The owner-run seed.** "Stage B3 accepts static/offline proof of the owner-run seed." "Do not execute the seed."
   "Its first execution remains part of a separately approved persistent-DB provisioning step."
5. **Isolation evidence.** "One-tenant isolation evidence is accepted for Stage B3 only." "Multi-tenant acceptance
   remains OPEN for Gate 3G." "Do not require a second tenant inside the B3 standalone DB now."
6. **The 14 tests that alter the runtime role.** They remain "OWNER-EXCLUDED FROM LOCAL RUN — SAFETY BOUNDARY." "Do not
   ALTER ROLE locally." "Before Stage B3 may PASS, those exact tests must execute and pass in CI on the exact future B3
   checkpoint commit." "If CI skips or omits any of them, Stage B3 cannot PASS and a separately isolated PostgreSQL
   environment will be required."

The 14 tests of decision 6, each of which runs the runtime-role bootstrap and so `ALTER ROLE workcontinuity_app`
(namespace `WorkContinuity.Integration.Tests`):

- `Connectors.ConnectorAuthorizationFreshDatabaseTests`: `After_bootstrap_then_migration_the_runtime_role_holds_exactly_read_insert_and_update_under_forced_row_level_security`,
  `There_the_store_reads_inserts_and_updates_while_delete_truncate_and_every_cross_tenant_access_are_refused`,
  `A_later_global_bootstrap_rerun_re_grants_delete_until_the_recorded_reassertion_restores_the_3b_privileges`.
- `Database.RuntimeRoleBootstrapTests`: `Bootstrapping_the_real_runtime_role_with_its_current_password_is_idempotent`.
- `InitialSync.InitialSyncDatabaseSecurityTests`: `After_bootstrap_then_migration_the_runtime_role_holds_exactly_what_initial_sync_needs_under_forced_row_level_security`,
  `A_sync_rows_shape_and_scope_set_versions_are_checked_by_the_database_so_no_live_row_stands_on_an_earlier_scope_set`,
  `The_ingress_sequence_is_generated_once_never_supplied_never_changed_and_never_cached_per_session`,
  `A_bootstrap_rerun_re_grants_delete_which_the_restrictive_policies_still_refuse_until_the_reassertion_restores_the_exact_grants`,
  `The_current_work_view_shows_only_enabled_baselined_scopes_to_the_callers_own_tenant_and_the_check_is_not_vacuous`,
  `A_disabled_scope_can_hold_no_baseline_and_a_field_awaits_one_only_at_a_real_version`,
  `Another_tenant_sees_claims_and_changes_no_scope_or_sync_and_the_check_is_not_vacuous`.
- `Reconciliation.ReconciliationTargetDatabaseSecurityTests`: `After_bootstrap_then_migration_the_runtime_role_reads_inserts_and_updates_targets_under_forced_row_level_security`,
  `A_bootstrap_rerun_re_grants_delete_which_the_restrictive_policy_still_refuses_until_the_reassertion_removes_it`,
  `Another_tenant_sees_and_changes_no_target_and_the_check_is_not_vacuous`.

These decisions answer the six open questions the Stage B3 addendum of `docs/gates/gate-3G-offline-report.md` recorded.
They change no frozen rule. B-14, B-15, R3B-1, R3B-2, D3B-1 and D3G-1 to D3G-8 stand; decision 3 makes the
runtime-role safety check that B-14 extended stricter. The four Stage B2 BEFORE-WIRING requirements remain OPEN.

## 3G — Stage B3 final delta (2026-10-06, offline, uncommitted)

Decision 3 was implemented on the same uncommitted tree; the other five decisions needed no code. Full record and
evidence: `docs/gates/gate-3G-offline-report.md`, Stage B3 addendum, section "Final delta: the owner's review
decisions".

- The harness's runtime-role check (`tests/App.Harness.Monday/Evidence/EvidenceReaders.cs`) reads eleven facts, each
  by itself: SUPERUSER, BYPASSRLS, CREATEROLE, CREATEDB, REPLICATION, INHERIT, the absence of LOGIN, ownership of the
  current database, of any schema, and of any table, view, materialized view, sequence or foreign table outside the
  temporary schemas (directly or through a membership), and membership in any role. Any one of them refuses the start.
  Only an answer of exactly one well-formed row with no membership at all passes the membership check; a role the
  catalog does not answer about counts as having every fact. There is no allowlist.
- The evidence procedure's role checks follow the same rule, under names that say what they check:
  `role.login_nosuperuser_nobypassrls_nocreaterole_nocreatedb_noreplication_noinherit`,
  `role.owns_no_table_view_or_sequence`, `role.owns_neither_database_nor_schema` (now every schema) and
  `role.member_of_no_role`. The owner-run tool's check list follows the procedure.
- Tests and guards: the decision is tested over every combination of the eleven facts; the real catalog is read as both
  local roles; a membership of any kind is refused on synthetic catalogs; the procedure's new conditions and the role
  statement are pinned word for word; the harness guard's allowlist of the catalogs it may read adds `pg_class`,
  `pg_database` and `pg_namespace`, read only by the role statement. No new test class.
- Nothing changed under `src/`; no package; `HarnessStandaloneGate` still empty; the seed design and the tool's design
  are unchanged and the seed was not run.
- Local results: clean build 0 warnings, 0 errors; unit 949/949, security 225/225, architecture 46/46; the 15 harness
  classes on the shared suite database 266 of 271, the other 5 failing only by the documented relay starvation of that
  database (174 tenants with unpublished outbox rows, beyond the relay's 100-tenant discovery limit), with relay code
  unchanged since they passed on the isolated temporary database and with exact-commit CI running them on a fresh
  database; targeted mutation of the hardened boundary 32 of 32 CAUGHT; both verifiers PASS; pinned gitleaks: no
  leaks. The shared database's security state (every ACL, row-level security flag and policy, the runtime role and the
  version of its catalog row, roles, memberships, databases and migrations) was identical before and after; only test
  rows were added. No temporary database was created and no role was altered.
- **CI: NOT RUN.** The final-tree reviews are recorded in the evidence package outside the repository.

Status: **Stage B3: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**, ready for owner checkpoint review, NOT PASS. Stage B3
may PASS only after a separately authorized checkpoint commit whose exact-commit CI executes and passes the 14 tests of
decision 6, and only by the owner's own decision.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate 3G
closure are NOT AUTHORIZED.

## 3G — Stage B3 owner clarification: temporary relations (approved 2026-10-06)

On 2026-10-06, after the final delta, its evidence package (`FULLY-3G-stage-B3-final-evidence-20261006T172600Z.zip`,
SHA-256 `d40d10d22740ae211c52b9be146c837992e2abce48b6edab75d876b22b280ed9`) and a read-only investigation of the
ownership read's exclusion of temporary relations, the product owner approved the following clarification. Quoted
sentences are the owner's words.

- "Temporary relations remain outside the Stage B3 runtime-role ownership check." The owner's reasons: "temporary
  relations are session-private and are not the persistent ownership risk targeted by R3B-2 / Decision 3"; "adding
  temporary-relation ownership to the startup check would not prevent creation after that check"; and "the actual
  broader control is PostgreSQL TEMPORARY privilege."
- "The current TEMPORARY privilege inherited through PUBLIC is accepted as a NON-BLOCKING FOLLOW-UP."
- "Any future revocation of TEMPORARY from PUBLIC is a separate privilege-model / Owner decision outside Stage B3."
- "No code/test change is required." "No validation or review rerun is required solely because of this documentation
  clarification."

This confirms the ownership read as implemented in the final delta: the harness's role statement and the evidence
procedure's `role.owns_no_table_view_or_sequence` both leave out relations whose `relpersistence` is `'t'`. Decision 3
is otherwise unchanged, and so are B-14, B-15, R3B-1, R3B-2, D3B-1 and D3G-1 to D3G-8. No grant or role changes: the
TEMPORARY privilege that PUBLIC holds is not revoked. The clarification changes documents only; every code, test and
tool path of the Stage B3 tree is byte-identical to the final tree recorded in that package, so its validation,
mutation evidence and three final-tree reviews remain valid, and the owner follow-up those reviews raised about the
exclusion of temporary relations is answered by this clarification. Only the hashes the package records for the five
status documents predate it.

Status at the time of this clarification (superseded by the owner PASS below): **Stage B3: IN PROGRESS — OFFLINE
IMPLEMENTATION ONLY**, NOT PASS; uncommitted; CI NOT RUN. The next
owner boundary is the Stage B3 checkpoint authorization: the owner's checkpoint review of the final tree and its
evidence package, and a checkpoint commit and push only by the owner's separate authorization. Stage B3 may PASS only
after that checkpoint's exact-commit CI executes and passes the 14 tests of decision 6, and only by the owner's own
decision.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate 3G
closure are NOT AUTHORIZED.

## 3G — Stage B3 owner PASS (2026-10-06)

The product owner explicitly approved **"Stage B3 — PASS, OFFLINE SLICE COMPLETE"**, dated 2026-10-06: "This PASS applies
only to Stage B3 offline preparation." "The Owner-approved temporary-relations clarification remains in force."

The owner's accepted basis:
- the checkpoint commit `736db05e912ca9e50c73205124ff8c6b2430d11d` ("feat: checkpoint Gate 3G Stage B3 offline
  preparation", parent `3eb24d9`, the 19 reviewed Stage B3 paths), committed and then pushed, each by separate owner
  authorization, after the owner accepted the final evidence package
  `FULLY-3G-stage-B3-final-evidence-20261006T172600Z.zip` (SHA-256
  `d40d10d22740ae211c52b9be146c837992e2abce48b6edab75d876b22b280ed9`);
- exact-commit CI run 37509953860 — SUCCESS (head SHA `736db05e912ca9e50c73205124ff8c6b2430d11d`, attempt 1): every job
  succeeded (secret scan; build, verify and test; container images); 1,945 total, 1,932 passed, 0 failed, 13 skipped,
  the 13 being the expected Service Bus emulator tests in CI;
- the 14 tests of decision 6, owner-excluded from every local run, executed and passed in that CI run, and none of them
  was skipped.

**Stage B3: PASS — OFFLINE SLICE COMPLETE.** Stage B3 needs no further work unless separately authorized.

It does **not** mean any of the following:
- Gate 3G PASS or Gate 3 PASS;
- real acceptance proven;
- real execution or live wiring authorized;
- Azure, Entra, Key Vault or monday execution authorized;
- the persistent standalone database created or authorized: it has not been created, the seed has not been run, and
  `HarnessStandaloneGate` stays empty unless separately authorized.

Unchanged by this PASS:
- **The four Stage B2 BEFORE-WIRING requirements remain OPEN.**
- The TEMPORARY privilege inherited through PUBLIC remains the approved NON-BLOCKING FOLLOW-UP of the temporary-relation
  clarification; revoking it stays a separate privilege-model owner decision.
- Multi-tenant acceptance remains OPEN for Gate 3G (decision 5).

The approved B decisions remain exactly B-1 (authoring only), B-7, B-10, B-11, B-14 (as clarified) and B-15; B-2 to B-6,
B-8, B-9, B-12, B-13 and B-16 to B-19 remain PROPOSED — NOT APPROVED. This PASS authorizes no other Stage B slice: B0,
B1, B4 and B5 are not authorized or started. The next owner boundary is the owner's selection and authorization of the
next Stage B slice, together with the owner decisions that slice requires.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**, and Gate 3 stays IN PROGRESS. Real acceptance is NOT
PROVEN; real execution and the closure of Gate 3G and of Gate 3 are NOT AUTHORIZED.

## 3G — Stage B owner decisions B-12 and B-13 and Stage B4 authorization (approved 2026-10-06)

On 2026-10-06, after the Stage B3 PASS was recorded and pushed as `519d440` and a read-only investigation recommended
Stage B4 with a narrowed scope as the next slice, the product owner explicitly approved, in the owner's words:

- "B-12 exactly as proposed in the current Gate 3G plan";
- "B-13 exactly as proposed in the current Gate 3G plan";
- "Stage B4 offline implementation";
- "the narrowed B4 scope";
- "delegation to Claude to choose the fixed callback port after inspecting the repository/environment".

**B-12 — Operator identity: APPROVED — 2026-10-06**, as proposed in `docs/gates/gate-3G-live-enablement-plan.md`: an
honest harness provider key (for example `wce-dev-harness`); the current console and loopback model kept, explicitly
DEV/TEST only; an owner attestation per run; Gate 7 replaces it for production.

**B-13 — Loopback TLS: APPROVED — 2026-10-06**, as proposed in the same plan: the ephemeral in-memory certificate with a
printed fingerprint, a one-time browser warning, a fixed callback port and no trust-store change (area H: SAN
`IP:127.0.0.1`, a validity of at most one day). The alternative, a trusted `dotnet dev-certs` certificate, is NOT
selected.

**Stage B4 — Operator identity and loopback TLS: AUTHORIZED — 2026-10-06. IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.**
The narrowed scope, as the owner's authorization lists it:

- the approved honest DEV/TEST harness operator/provider identity;
- an ephemeral in-memory self-signed loopback TLS certificate per B-13;
- the SHA-256 certificate fingerprint surfaced to the operator;
- one fixed non-default loopback callback port selected from repository evidence;
- supplying that certificate to the existing harness host path.

Excluded by the same authorization: wiring the Stage B2 real transport or token seams; resolving the four BEFORE-WIRING
requirements; adding anything to `HarnessStandaloneGate`; creating or seeding the persistent standalone database;
contacting monday, Azure, Entra, Key Vault or any provider; using a real credential; executing OAuth or browser
authorization; modifying a machine or user trust store or running `dotnet dev-certs`; modifying `src/`; adding a package;
weakening a guard. B-11 stays in force for every guard the slice changes.

State after these decisions:

- Approved Stage B decisions: exactly **B-1** (authoring only), **B-7**, **B-10**, **B-11**, **B-12**, **B-13**, **B-14**
  (as clarified) and **B-15**. B-2 to B-6, B-8, B-9 and B-16 to B-19 remain **PROPOSED — NOT APPROVED**.
- Stage B2 and Stage B3 remain **PASS — OFFLINE SLICE COMPLETE**; the four Stage B2 BEFORE-WIRING requirements remain
  OPEN and are not addressed by B4. No other Stage B slice (B0, B1, B5) is authorized.
- D3G-1 to D3G-8 (D3G-4 as superseded by B-14), S-1 to S-5, R3B-1, R3B-2, D3B-1 and every earlier Gate 3 decision are
  unchanged. B-12 settles open question 3 and B-13 open question 4 of `docs/gates/gate-3G-offline-report.md`.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate 3G
closure are NOT AUTHORIZED.

## 3G — Stage B4 implementation record (2026-10-06, offline, uncommitted)

**The fixed callback port: `47443`**, chosen under the owner's delegation and recorded as
`HarnessOptions.OperatorCallbackPort`. The standalone redirect address is therefore exactly
`https://127.0.0.1:47443/operator/callback`. The evidence for the choice:

- Repository: at `519d440` no tracked file names 47443. The ports the repository uses are PostgreSQL 5432, the Service Bus
  emulator 5300 and 5672, and test or example values (among them 8080, 8443, 9001 and 9002); 8443 is a common
  alternative HTTPS default and the harness's own option tests use it as an example, so it is avoided.
- Environment (read-only, 2026-10-06): the Windows dynamic TCP range is 49152 to 65535 and every excluded port range
  starts at 49671 or above; nothing listens on 47443. A port below the dynamic range is never handed out as an ephemeral
  port, so neither the system nor the tests' free-port choice can take it.
- It is not a default: not 443, not the ASP.NET Core defaults 5000 and 5001, not 8443, and outside the Visual Studio
  HTTPS range 44300 to 44399.

The harness's in-process tests keep composing on free ports of their own; only the standalone entry point requires the
fixed port, before its database gate, and refuses any other with `RedirectNotTheFixedCallbackPort`.

**B-12 as implemented.** The operator principal's identity provider is `wce-dev-harness`
(`HarnessTrust.OperatorProviderKey`, through the public `IdentityProviderId.From`); `LocalDemo` is named nowhere in the
harness. The key is a label only: no production component stores or checks the operator's provider, so no behavior of
the authorization lifecycle changes. Past its database gate, the standalone entry point shows that the run is DEV/TEST
only and never the production trust model, which Gate 7's Entra sign-in replaces, and starts nothing until the operator
types exactly `attest` to confirm being the product owner acting as the configured canonical user for the configured
tenant (`OperatorAttestation`); any other line, or the end of input, stops the run with `OperatorNotAttested`. The
statement names neither the tenant nor the user.

**B-13 as implemented.** `OperatorTlsCertificate.Create`, called only by the standalone entry point and only after the
fixed-port check and the database gate: a new RSA 2048 key and a self-signed SHA-256 certificate for each run; its one
name is the subject alternative name `IP:127.0.0.1` (no host name, no other address); valid from one minute before
creation for exactly one day; an end-entity certificate (basic constraints, not an authority) for TLS server
authentication only. Its SHA-256 fingerprint, in the colon-separated form browsers show, is printed before the
attestation and before anything listens. The certificate is the one seam the standalone entry point passes; it is
disposed after the host, at the end of the run. No certificate store is opened, read or written, and no development
certificate is used.

A platform fact, observed locally on 2026-10-06 and recorded for the owner: the Windows TLS stack cannot serve a key that
exists only in process memory (a directly created certificate and an `EphemeralKeySet` load both failed the handshake),
so the certificate is reloaded from an in-memory PKCS#12 copy with the default key storage. While the certificate is in
use, its private key therefore sits in a per-user key container file of its own; disposing the certificate deletes it
(observed: one key file during use, none after disposal, and no certificate store changed). A process killed before
disposal can leave that key file behind; it is never added to a trust store. The PKCS#12 bytes are cleared once loaded.

**Guards (B-11).** Two existing harness guards were replaced by equal-or-stricter pinned allowances, and one guard was
added:

- "No harness code supplies a seam": now only `OperatorCertificate` may be assigned, exactly once, in
  `HarnessProgram.cs`, as `new HarnessSeams { RunBackboneLoops = true, OperatorCertificate = certificate }`; every other
  seam stays unassigned everywhere. The Stage B2 guard pins the same expression.
- "No certificate is taken from the machine": `CreateSelfSigned` and `X509CertificateLoader` are allowed in exactly one
  file, `Operator/OperatorTlsCertificate.cs`, once each, as one pinned in-memory PKCS#12 load; the store and
  development-certificate bans are kept, and key storage flags, `PersistKeySet`, `MachineKeySet`, `Exportable`,
  certificate files and file loaders are newly banned everywhere in the harness.
- New: the honest provider key, the certificate's pinned shape, the fixed port and the order of the standalone entry
  point (fixed port, database gate, certificate, fingerprint, attestation, host).

Not changed: anything under `src/`; any package; `HarnessStandaloneGate` (still empty); the Stage B2 `Live/` files and
their wiring (still unwired); the seed and evidence tools; every Dockerfile, workflow and infrastructure file.

**Local verification (2026-10-06, uncommitted tree; no temporary database, no role altered):**

- Build of the solution: 0 warnings, 0 errors. Unit 949/949, security 225/225, architecture 47/47 (one new fact).
- The directly affected integration classes, 66/66: the 16 new tests, `HarnessFailClosedTests` and `HarnessOptionsTests`.
- The harness integration namespace on the shared suite database: 287 tests, 282 passed. The full integration suite
  without the 14 owner-excluded tests that alter the runtime role (excluded by exact name, Stage B3 decision 6): 727
  executed, 722 passed. In both runs the same 5 failed, only by that database's documented relay starvation ("The
  harness tenant's outbox was not published within 100 relay passes", or the equivalent 100-pass wait for another
  tenant's row): the two `HarnessBackboneLoopTests`, `HarnessCompositionTests`, `HarnessLeakPreventionTests` and
  `HarnessTenantIsolationTests` tests the Stage B3 final delta lists. B4 changes no relay, ingress or transport code.
- Targeted mutation: 36 of 36 source mutants of the changed and new guards CAUGHT by the architecture tests, and
  12 of 12 behavioral mutants CAUGHT by the new and affected integration tests (rebuilt one at a time); every file
  restored byte for byte.
- `eng/verify-architecture.py` PASS; `eng/verify-local-env.py` PASS; the six frozen documents unchanged (working copy
  and HEAD blob); the CI-pinned gitleaks 8.30.1 (binary verified against the release checksums): no leaks in the history
  or in any changed path.

**Not run, and why.** A clean full local suite is not possible within the existing approvals: the shared suite database
is starved (the 5 failures above), so a clean run needs a fresh isolated database, which is a separate owner approval
(Stage B3's temporary database was used once and dropped); and the 14 tests that alter the runtime role stay excluded
from every local run (Stage B3 decision 6), so they can execute only in CI or in a separately isolated PostgreSQL
environment. CI has not run on this tree.

Status: **Stage B4: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**, implemented and validated locally, uncommitted, NOT
PASS. The next owner boundary is the owner's review of Stage B4 and the authorization of its checkpoint; Stage B4 may
PASS only by the owner's own decision.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate 3G
closure are NOT AUTHORIZED.

## 3G — Stage B4 owner decisions on the B-13 key container and clean verification (approved 2026-10-07)

On 2026-10-07, after a read-only review of the two open Stage B4 questions recorded above, the product owner approved
the following, in the owner's words.

**1. The B-13 key-container deviation: ACCEPTED — 2026-10-07.** "The ephemeral loopback TLS certificate may use a
temporary per-user private-key container file while in use. It is deleted on dispose. A hard crash/power loss may leave
an orphan file. No trust-store change occurs. This is accepted as satisfying B-13, with the residual risk recorded."

- B-13 is unchanged otherwise: a new self-signed certificate per run, SAN `IP:127.0.0.1` only, a validity of at most one
  day, its SHA-256 fingerprint printed, a one-time browser warning, the fixed callback port `47443`. **No trust-store
  change is allowed**: no certificate store is opened, read or written, and `dotnet dev-certs` stays excluded.
- **Residual risk, recorded:** a hard crash, a forced kill or a power loss before disposal can leave one orphan private-key
  file in the operator's per-user key store. It belongs to a certificate that no store trusts, whose fingerprint never
  matches a later run's, and which expires within one day. A normal end, an early refusal, an exception or Ctrl+C each
  dispose the certificate and delete the file.

**2. Clean verification: exact-commit CI after the checkpoint — APPROVED — 2026-10-07.** "Stage B4 clean verification
will be exact-commit CI after the checkpoint. No temporary isolated database is required for B4 verification. CI must run
the full suite on a fresh environment, including the 14 role-altering tests."

- No temporary or isolated database is created for Stage B4 verification, and the 14 tests that alter the runtime role
  stay excluded from every local run (Stage B3 decision 6).
- The clean full-suite evidence for Stage B4 is the CI run on the exact checkpoint commit: the full suite on a fresh
  environment, with the 14 role-altering tests executed, not skipped.

Status after these decisions: **Stage B4: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY, NOT PASS**, uncommitted, CI NOT
RUN. Stage B4 can PASS only after a successful exact-commit CI run on its checkpoint, and only by the owner's own
decision. The next owner boundaries, each separate: the authorization of the checkpoint commit (exactly the 15 Stage B4
paths), the authorization of its push, the review of its exact-commit CI, and the Stage B4 PASS decision. Every
follow-up kept outside Stage B4 stays outside it.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate 3G
closure are NOT AUTHORIZED.

## 3G — Stage B4 owner PASS (2026-10-07)

The product owner explicitly approved **"Stage B4 — PASS, OFFLINE SLICE COMPLETE"**, dated 2026-10-07.

The owner's accepted basis:
- the checkpoint commit `ba6a086fae4ee6b1c62ba67bdaf498b7921654f2` ("feat: checkpoint Gate 3G Stage B4 offline operator
  TLS", parent `519d440`, exactly the 15 Stage B4 paths), committed and then pushed, each by separate owner
  authorization;
- exact-commit CI run 37638185897 — SUCCESS (head SHA `ba6a086fae4ee6b1c62ba67bdaf498b7921654f2`, attempt 1): 1,962
  total, 1,949 passed, 0 failed, 13 skipped, the 13 being the expected Service Bus emulator tests in CI; this run is the
  clean verification the owner chose on 2026-10-07 (the full suite on a fresh environment, no temporary database);
- all 14 role-altering tests (Stage B3 decision 6), excluded from every local run, executed and passed in that CI run;
- the Stage B4-specific tests executed and passed in that CI run.

**Stage B4: PASS — OFFLINE SLICE COMPLETE.** Stage B4 needs no further work unless separately authorized.

It does **not** mean any of the following:
- Gate 3G PASS or Gate 3 PASS;
- real acceptance proven;
- real execution or live wiring authorized;
- Azure, Entra, Key Vault or monday execution authorized;
- the persistent standalone database created or authorized: it has not been created, the seed has not been run, and
  `HarnessStandaloneGate` stays empty unless separately authorized;
- any other Stage B slice authorized.

Unchanged by this PASS:
- No Stage B2 seam is wired. **The four Stage B2 BEFORE-WIRING requirements remain OPEN.**
- **The B-13 key-container residual remains recorded:** a hard crash, a forced kill or a power loss before disposal can
  leave one orphan private-key file in the operator's per-user key store (section "3G — Stage B4 owner decisions on the
  B-13 key container and clean verification (approved 2026-10-07)"). No trust-store change is allowed.

The approved B decisions remain exactly B-1 (authoring only), B-7, B-10, B-11, B-12, B-13, B-14 (as clarified) and
B-15; B-2 to B-6, B-8, B-9 and B-16 to B-19 remain PROPOSED — NOT APPROVED. This PASS authorizes no other Stage B slice:
B0, B1 and B5 are not authorized or started. The next owner boundary is the owner's selection and authorization of the
next Stage B slice, together with the owner decisions that slice requires.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**, and Gate 3 stays IN PROGRESS. Real acceptance is NOT
PROVEN; real execution and the closure of Gate 3G and of Gate 3 are NOT AUTHORIZED.

## 3G — Stage B owner decisions B-2, B-3, B-4, B-8 and B-9 and Stage B1 authorization (approved 2026-10-08)

On 2026-10-08, after the Stage B4 PASS was recorded and pushed as `5822698` (CI run 37747426660 SUCCESS), and after a
read-only review in the working session of the decisions Stage B1 still needed, the product owner explicitly approved the
following. The lists reproduce the owner's text.

**B-2 — Dedicated vault: APPROVED — 2026-10-08.**

- one new dedicated DEV/TEST harness Key Vault;
- Standard SKU, Azure RBAC;
- dedicated resource group in North Europe;
- firewall default deny;
- dedicated Log Analytics workspace in that resource group;
- separate Bicep entry point, not referenced by main.bicep or existing Dev/Synthetic parameter files;
- authoring only; nothing is created.

**B-3 — Soft-delete retention: APPROVED — 2026-10-08.**

- soft-delete retention = 7 days.

**B-4 — Purge protection: APPROVED — 2026-10-08.**

- purge protection = OFF for this DEV/TEST harness vault.

**B-8 — Runtime RBAC: APPROVED — 2026-10-08.**

- one custom role with exactly: `Microsoft.KeyVault/vaults/secrets/getSecret/action`,
  `Microsoft.KeyVault/vaults/secrets/setSecret/action`, `Microsoft.KeyVault/vaults/secrets/delete`;
- zero Actions, NotActions, NotDataActions, wildcards or conditions;
- AssignableScopes = dedicated harness resource group;
- assignment itself = dedicated vault scope only;
- principal ID supplied as a parameter;
- never widen permissions silently.

**B-9 — Administration separation and identity creation: APPROVED — 2026-10-08.**

- Product Owner is infrastructure administrator/operator;
- Azure resources stay in Bicep;
- Entra application, service principal and certificate credential are authored through one reviewed idempotent
  PowerShell script under eng/ using Azure CLI;
- script must refuse writes without -Approve and verify results by read-back;
- single-tenant app;
- no API permissions, redirect URI or app roles in B1;
- service principal has no directory role/group ownership;
- private key is generated non-exportable locally and never leaves the machine;
- Microsoft Graph Bicep extension is NOT approved;
- for this harness only, this reviewed/versioned script is accepted as satisfying invariant 69.

In the owner's words: "These approvals are authoring approvals only."

**Stage B1 — IaC authoring: AUTHORIZED — 2026-10-08. IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** In the owner's words:
"Implement Stage B1 — IaC authoring — offline only, under the Product Owner-approved B-2, B-3, B-4, B-8 and B-9
decisions."

Excluded by the same authorization, as the owner lists it: running `az login`; running Azure validate or what-if;
deploying or creating any Azure resource; creating Entra or Graph objects; creating a certificate; contacting monday,
Azure, Entra, Key Vault or any provider; modifying `src/`; adding packages; modifying existing Dev or Synthetic templates
or parameter files; wiring Stage B2; resolving unrelated follow-ups. "D3G-1 remains in force except for the
already-approved authoring carve-out." No stage, commit or push.

Stop conditions, as the owner lists them: "If the exact three-action custom role cannot be authored with zero Actions,
or the role assignment cannot be scoped to the vault, STOP and report." Stop also if completion requires Azure, Entra,
Graph or provider contact; modifying `src/`; a new dependency; broadening the approved role; changing existing Dev or
Synthetic infrastructure; or resolving work outside B1.

Acceptance, recorded before code from the owner's success criteria and verification list:

- an isolated harness infrastructure entry point;
- dedicated resource group, vault, logging, network and RBAC definitions matching B-2, B-3, B-4 and B-8;
- an owner-run deployment path that is inert without explicit approval;
- the reviewed Entra provisioning script described by B-9;
- documentation recording the five owner decisions and the Stage B1 authorization;
- nothing real created or contacted;
- offline verification only: Bicep build and lint of the new entry point; static validation of the PowerShell scripts;
  the relevant architecture and security tests; both repository verifiers; the frozen-document hash check;
  `git diff --check`; the secret scan; a contradiction check against the canonical documents; targeted mutation evidence
  for every guard added or changed; and the canonical-auditor, tenant-security-reviewer and gate-verifier reviews of the
  final tree. Stage B1 is not declared PASS by its implementation; that is the owner's decision.

Consequences recorded here, not added to the decisions:

- Approved Stage B decisions: **B-1** (authoring only), **B-2**, **B-3**, **B-4**, **B-7**, **B-8**, **B-9**, **B-10**,
  **B-11**, **B-12**, **B-13**, **B-14** (as clarified) and **B-15**. B-5, B-6 and B-16 to B-19 remain **PROPOSED — NOT
  APPROVED**.
- Where `docs/gates/gate-3G-live-enablement-plan.md` proposed a role "assignable at the dedicated vault only" (area C)
  and a B1 stop condition on a "vault-only assignable scope", the approved B-8 governs: the assignable scope is the
  dedicated harness resource group, and the assignment is made at the vault only.
- The invariant 69 acceptance of B-9 applies to this harness only. Every other piece of Azure infrastructure stays in
  Bicep.
- Nothing is created by these decisions or by Stage B1: no resource group, vault, workspace, role, assignment, Entra
  application, service principal, certificate or secret, and no deployment.
- Stage B2, Stage B3 and Stage B4 remain **PASS — OFFLINE SLICE COMPLETE**. The four Stage B2 BEFORE-WIRING requirements
  remain OPEN, no Stage B2 seam is wired, `HarnessStandaloneGate` stays empty, the persistent standalone database has not
  been created and the seed has not been run. No other Stage B slice (B0, B5) is authorized.
- D3G-1 to D3G-8 (D3G-4 as superseded by B-14), S-1 to S-5, R3B-1, R3B-2, D3B-1 and every earlier Gate 3 decision are
  unchanged.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate 3G
closure are NOT AUTHORIZED.

## 3G — Stage B1 implementation record (2026-10-08, offline, uncommitted)

Implemented as authorized above, offline only, on the uncommitted tree at `5822698`. Evidence and the facts left for a
future real deployment: `docs/gates/gate-3G-offline-report.md`, "Addendum — Stage B1 IaC authoring".

New:
- `infra/bicep/harness-vault.bicep`: the separate subscription-scope entry point. It creates the dedicated resource group
  `rg-wce-harness-neu` in `northeurope` (both fixed in the template) and the custom role
  `wce-harness-keyvault-secrets-runtime`, and deploys the module into that resource group. Its only parameters are
  `runtimePrincipalObjectId` and `allowedIpv4Address`, both empty by default.
- `infra/bicep/modules/harnessVault.bicep`: the vault `kv-wce-harness-<token>`, its audit workspace
  `log-wce-harness-neu` (the existing `logAnalytics.bicep` module, unchanged: 30-day retention, 1 GB daily cap), the
  vault's `AuditEvent` diagnostic setting, and the runtime role's one assignment.
- `eng/harness-vault.ps1`: the owner-run preview and deployment path. `eng/harness-identity.ps1`: the owner-run Entra
  identity script.
- 88 security tests in `tests/App.Security.Tests/` (`HarnessVaultInfrastructureTests.cs`,
  `HarnessVaultDeployScriptTests.cs`, `HarnessIdentityScriptTests.cs`) and their sandbox (`HarnessAzureCliSandbox.cs`).

Changed: `eng/infra.ps1` (`build` and `lint` also compile and lint `harness-vault.bicep`, so CI does; `validate`,
`whatif` and `deploy` still take `main.bicep` only); `infra/bicep/README.md`; the status documents. Not changed: anything
under `src/`; any package; every Dev and Synthetic template and parameter file (`main.bicep`, `environment.bicep`, every
existing module, both parameter files); every workflow; the harness project and the Stage B2 `Live/` files (unwired);
`HarnessStandaloneGate` (empty); the six frozen documents.

**B-2 as implemented.** Standard SKU (`family: 'A'`, `name: 'standard'`), `enableRbacAuthorization: true`, no access
policy, `enabledForDeployment`, `enabledForDiskEncryption` and `enabledForTemplateDeployment` false; the firewall
`defaultAction: 'Deny'` with `bypass: 'None'`, no virtual-network rule and an IP rule only when `allowedIpv4Address` is
given; `publicNetworkAccess: 'Enabled'`, because disabling it would also override every firewall rule. The vault's
`AuditEvent` logs go from creation to the dedicated workspace in the same resource group; nothing names a Dev or
Synthetic resource. No secret is created by the templates. `main.bicep` and the parameter files never reference the
entry point; the module is referenced only by it.

**B-3 as implemented.** `var softDeleteRetentionInDays = 7`: a constant of the template, not a parameter.

**B-4 as implemented.** `enablePurgeProtection` is absent from both harness templates: Azure accepts no `false` for it,
and leaving it out is how the existing foundation module keeps purge protection off.

**B-8 as implemented.** The role's permissions are exactly `actions: []`, `notActions: []`, `notDataActions: []` and the
three `dataActions` `Microsoft.KeyVault/vaults/secrets/getSecret/action`, `.../setSecret/action` and `.../delete`, with no
wildcard and no condition; `assignableScopes` is the dedicated resource group only. The one assignment is in the
module, `scope: vault`, `principalType: 'ServicePrincipal'`, no condition, and is deployed only when
`runtimePrincipalObjectId` is given. The template compiled locally (Bicep CLI 0.47.16) to ARM JSON in which the role
definition holds exactly those permissions and assignable scope, and the role assignment's `scope` is the vault. **The
owner's stop condition did not trigger.**

**B-9 as implemented.** Azure resources are only in the Bicep above; the Microsoft Graph Bicep extension is used nowhere
and no `bicepconfig.json` exists. `eng/harness-identity.ps1` reaches Entra only through the Azure CLI (`az account show`,
and `az rest` against `https://graph.microsoft.com/v1.0`), with commands `create`, `add-credential`, `remove-credential`
and `verify`:
- every command refuses without `-Approve`, shows its plan first, requires an explicit lower-case `-TenantId`, and
  refuses unless the Azure CLI is already signed in to that tenant as a user; it never signs in;
- `create` is idempotent: it finds the application by its exact display name `wce-dev-harness-keyvault-runtime`, refuses
  when more than one has it, and creates the application (single-tenant `AzureADMyOrg`, display name and notes only) and
  its service principal only when missing; it never repairs an existing deviating object;
- every write is followed by a read-back of the application, the service principal, its transitive memberships, owned
  objects, application-permission assignments and delegated grants, checked by 14 named checks: single-tenant; no API
  permission, redirect URI (web, SPA or public client), app role, exposed API or client secret; the service principal has
  no credential of its own, no group membership or directory role, owns nothing and holds no permission grant; and the
  application credentials as each command requires;
- `add-credential` requires the application to have no credential and the store to hold no harness certificate, creates
  a certificate in `Cert:\CurrentUser\My` with a non-exportable 3,072-bit RSA key from the Microsoft Software Key Storage
  Provider (signing only, SHA-256, valid 1 to 24 hours as given), proves the key non-exportable before anything is sent
  and deletes it otherwise, sends only the public certificate as the application's only key credential, and reads it
  back by its name and end date;
- `remove-credential` removes every application key credential, reads that back, and deletes the local certificate and
  its key by exact thumbprint.

Implementation choices made within the decisions, for the owner's review (not owner decisions): the optional single
admitted public IPv4 address, empty by default, without which the owner's machine could never reach the vault's data
plane in an approved run; no trusted-service bypass; the `AuditEvent` category only, to a workspace that reuses the
existing module as it is (its shared-key ingestion stays enabled, as that module documents for Dev); `-Approve` also for
the read-only `whatif` and `verify`, because each contacts Azure or Entra; a refusal of service-principal sessions in
both scripts; `remove-credential`, because B-7's credential is valid only for the run window and removed afterwards
(when it is used belongs to the post-run procedure, B-6, PROPOSED); the software key storage provider, not a TPM, because
a TPM key's PS256 signature is unproven offline; and `eng/infra.ps1` building and linting the new entry point, so CI
compiles it.

**Local verification (2026-10-08, uncommitted tree; no Azure, Entra, Key Vault, monday or other provider contact):**

- Bicep: `harness-vault.bicep` built and linted with the local Bicep CLI, no diagnostic; `./eng/infra.ps1 build` and
  `lint` PASS for both entry points and both parameter files (the Azure CLI's version check and telemetry turned off for
  the run; no sign-in).
- PowerShell: both scripts and `eng/infra.ps1` parse with no error, plain ASCII, LF only; every parameter the script
  passes to `New-SelfSignedCertificate` exists with a valid value (read from the cmdlet's metadata; the cmdlet was not run).
- Solution build: 0 warnings, 0 errors. Unit 949/949; security 313/313 (225 before, 88 new); architecture 47/47.
- `eng/verify-architecture.py` PASS; `eng/verify-local-env.py` PASS; the six frozen documents unchanged (working copy and
  HEAD blob).
- Targeted mutation of the new guards: **52 of 52 CAUGHT** (24 template mutants, 2 of `eng/infra.ps1`, 9 of the
  deployment path, 16 of the identity script, and one probe file naming the harness among the Dev templates), each run
  against the new test classes it concerns, with the three unmutated controls passing first; every file was restored and
  its SHA-256 checked, and the probe file was deleted.

**Not run, and why.** ARM validation and what-if, any Entra call, certificate creation and deployment are real steps
outside this authorization, so the templates are proven only by compilation, linting, static pins and the compiled ARM
JSON, and the identity script's certificate commands only by static checks and their refusals. The integration suite was
not run: B1 changes no code it exercises, and a clean run needs a separately approved isolated database. CI has not run
on this tree.

Status: **Stage B1: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**, implemented and validated locally, uncommitted, CI NOT
RUN, NOT PASS. The next owner boundary is the owner's review of Stage B1 and the authorization of its checkpoint; Stage B1
may PASS only by the owner's own decision.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate 3G
closure are NOT AUTHORIZED.

## 3G — Stage B1 owner review decisions and checkpoint authorization (approved 2026-10-10)

On 2026-10-10, after the Stage B1 report (the implementation record above, revalidated on 2026-10-10 on the unchanged
tree, and the canonical-auditor, tenant-security-reviewer and gate-verifier verdicts CONFORMS WITH FOLLOW-UP, PASS WITH
FOLLOW-UP and READY WITH FOLLOW-UP), the product owner approved "the remaining Stage B1 review decisions exactly as
recommended" and authorized "committing exactly the 16 Stage B1 paths". The list reproduces the owner's text:

1. B-9 / invariant-69 interpretation:
   - Azure/ARM infrastructure remains Bicep.
   - For this DEV/TEST harness only, the reviewed, versioned, idempotent eng/ script for Entra app/service-principal/
     certificate creation is accepted as satisfying invariant 69.
   - This does not amend the frozen canonical documents and does not establish a general project-wide exception.
2. The implementation choices a–h from the Stage B1 report:
   - a. optional single admitted public IPv4 address;
   - b. Key Vault bypass = None;
   - c. AuditEvent-only logging to the dedicated workspace using the existing Log Analytics module as-is;
   - d. -Approve required for read-only whatif and verify as well;
   - e. service-principal CLI sessions refused;
   - f. remove-credential remains included, but its real use stays gated by B-6;
   - g. software KSP, RSA 3072, signing-only, Cert:\CurrentUser\My, no TPM requirement for this slice;
   - h. eng/infra.ps1 build/lint covers the new harness entry point.
3. One Fully tenant per harness run is accepted as the current operating rule. Multi-tenant acceptance remains OPEN and
   is not proven by B1.
4. B-18 remains OPEN. Do not resolve or expand audit-log policy beyond the current B1 implementation.
5. The local Integration suite omission is accepted for this checkpoint. Exact-commit CI is the next clean verification
   boundary.
6. Do not change the local approval-guard hook in B1. That remains outside this slice.

**Checkpoint authorization — 2026-10-10.** A checkpoint commit of exactly these 16 paths, in the owner's list: modified
`CLAUDE.md`, `INIT.md`, `docs/canonical/PROJECT-STATUS.md`, `docs/gates/gate-3-scope.md`,
`docs/gates/gate-3G-live-enablement-plan.md`, `docs/gates/gate-3G-offline-report.md`, `eng/infra.ps1`,
`infra/bicep/README.md`; added `eng/harness-identity.ps1`, `eng/harness-vault.ps1`, `infra/bicep/harness-vault.bicep`,
`infra/bicep/modules/harnessVault.bicep`, `tests/App.Security.Tests/HarnessAzureCliSandbox.cs`,
`tests/App.Security.Tests/HarnessIdentityScriptTests.cs`, `tests/App.Security.Tests/HarnessVaultDeployScriptTests.cs`,
`tests/App.Security.Tests/HarnessVaultInfrastructureTests.cs`. "Do not push."

Consequences recorded here, not added to the decisions:
- Decision 1 answers the canonical-auditor's question whether B-9 also covers the "IaC: Bicep" lines of PRD v1 §18 and
  System Architecture v1 §9: Azure and ARM infrastructure stays in Bicep, and the script's acceptance holds for this
  harness only. No frozen document changes.
- Decision 3 is the operating rule that `docs/gates/gate-3G-offline-report.md` records in its Stage B1 limitations.
- B-5, B-6 and B-16 to B-19 remain PROPOSED — NOT APPROVED; B-18 stays OPEN.
- **Stage B1 stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY, NOT PASS.** Stage B1 can PASS only after the owner
  separately authorizes the push, the exact-commit CI succeeds, and the owner decides. Nothing was created, deployed or
  contacted, and no real step is authorized.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN; real execution and Gate 3G
closure are NOT AUTHORIZED.

## 3G — Stage B1 owner PASS (2026-10-10)

The product owner explicitly declared **"Stage B1 — PASS, OFFLINE SLICE COMPLETE"**, dated 2026-10-10.

The owner's accepted basis:
- the checkpoint commit `310e3ff069bd2edb5d478ca1098547456aca1cad` ("feat: checkpoint Gate 3G Stage B1 offline IaC",
  parent `5822698`, exactly the 16 Stage B1 paths), committed and then pushed as a fast-forward, each by separate owner
  authorization;
- exact-commit CI run 38044980337 — SUCCESS (head SHA `310e3ff069bd2edb5d478ca1098547456aca1cad`, attempt 1), all
  three jobs successful: the secret scan; build, verify and test, with 2,050 total, 2,037 passed, 0 failed and 13
  skipped, the 13 being the expected Service Bus emulator tests in CI; and the container images (Release build, no
  push). This run is the clean verification boundary the owner named on 2026-10-10 (decision 5 of section "3G — Stage
  B1 owner review decisions and checkpoint authorization (approved 2026-10-10)");
- Bicep build and lint passed on Linux: the CI step "Bicep build and lint" ran `./eng/infra.ps1 build` and `lint` under
  PowerShell and built and linted `infra/bicep/harness-vault.bicep` (BICEP BUILD: PASS, BICEP LINT: PASS).

Recorded with it, not added to the owner's basis: the total is the Stage B4 run's 1,962 plus the 88 Stage B1 tests, and
none of the 88 has a skip path; their PowerShell sandbox fails rather than passes when no PowerShell is found. With 0
failed, all 88 therefore executed and passed in that CI run, the sandbox tests running the two owner scripts under
`pwsh` on Linux. The CI log reports totals and does not name skipped tests.

**Stage B1: PASS — OFFLINE SLICE COMPLETE.** Stage B1 requires no further offline work unless separately authorized.

It does **not** mean any of the following:
- Gate 3G PASS or Gate 3 PASS;
- real acceptance proven;
- real execution or live wiring authorized;
- any Azure, Entra, Graph, Key Vault or monday action authorized: deploying the harness vault, assigning its role,
  creating the Entra application, service principal and certificate credential, the monday app and the runs each stay a
  separate owner approval;
- the persistent standalone database created or authorized: it has not been created, the seed has not been run, and
  `HarnessStandaloneGate` stays empty unless separately authorized;
- any other Stage B slice authorized.

Unchanged by this PASS:
- Nothing exists in Azure or Entra for the harness: no resource group, vault, workspace, role, assignment, application,
  service principal, certificate or secret.
- No live wiring exists and no Stage B2 seam is wired. **The four Stage B2 BEFORE-WIRING requirements remain OPEN.**
- One Fully tenant per harness run remains the current operating rule; multi-tenant acceptance remains OPEN and is not
  proven by B1.
- **B-18 remains OPEN** (PROPOSED — NOT APPROVED).

The approved B decisions remain exactly B-1 (authoring only), B-2, B-3, B-4, B-7, B-8, B-9, B-10, B-11, B-12, B-13, B-14
(as clarified) and B-15; B-5, B-6 and B-16 to B-19 remain PROPOSED — NOT APPROVED. This PASS authorizes no other Stage B
slice: B0 and B5 are not authorized or started. The next owner boundary is the owner's selection and authorization of
the next Stage B slice, together with the owner decisions that slice requires.

**Gate 3G stays IN PROGRESS — OFFLINE IMPLEMENTATION ONLY**, and Gate 3 stays IN PROGRESS. Real acceptance is NOT
PROVEN; real execution and the closure of Gate 3G and of Gate 3 are NOT AUTHORIZED.
