# Gate 3 Scope — monday Connector

## Status

| Item | Status |
|---|---|
| Gate 3 — monday Connector | **AUTHORIZED 2026-09-29, IN PROGRESS** |
| 3A — Connector contract and monday client foundation | **PASS** — approved by the product owner on 2026-09-30 |
| 3B — OAuth 2.1 connection lifecycle | **NEXT / NOT STARTED**; needs its own product-owner authorization |
| 3C to 3G | Not started; each needs its own product-owner authorization |
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
- Decisions expected later: the identity a connection acts under (3B), token storage (3B), widening
  `EventText.MaxValueLength` if monday data needs it (3D), the clock-skew tolerance (3E), and rate budgets and
  scheduling (3E, 3F).

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
