# Gate 3 Scope — monday Connector

## Status

| Item | Status |
|---|---|
| Gate 3 — monday Connector | **AUTHORIZED 2026-09-29, IN PROGRESS** |
| 3A — Connector contract and monday client foundation | **PASS** — approved by the product owner on 2026-09-30 |
| 3B — OAuth 2.1 connection lifecycle | **PASS** — approved by the product owner on 2026-09-30 |
| 3C — Webhook intake | **PASS** — approved by the product owner on 2026-09-30 |
| 3D — Source mapping and normalization | **PASS** — approved by the product owner on 2026-10-01 |
| 3E — API reconciliation and authoritative reread | **NEXT / NOT STARTED** — needs its own product-owner authorization |
| 3F to 3G | Not started; each needs its own product-owner authorization |
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
- Decided for 3B on 2026-09-30: the identity a connection acts under (D3B-1) and the secret boundary (D3B-2); see
  *3B owner decisions*. Decisions expected later: widening `EventText.MaxValueLength` if monday data needs it (3D),
  the clock-skew tolerance (3E), and rate budgets and scheduling (3E, 3F).

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
