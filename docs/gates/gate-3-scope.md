# Gate 3 Scope — monday Connector

## Status

| Item | Status |
|---|---|
| Gate 3 — monday Connector | **AUTHORIZED 2026-09-29, IN PROGRESS** |
| 3A — Connector contract and monday client foundation | **PASS** — approved by the product owner on 2026-09-30 |
| 3B — OAuth 2.1 connection lifecycle | **PASS** — approved by the product owner on 2026-09-30 |
| 3C — Webhook intake | **NEXT / NOT STARTED**; needs its own product-owner authorization |
| 3D to 3G | Not started; each needs its own product-owner authorization |
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
