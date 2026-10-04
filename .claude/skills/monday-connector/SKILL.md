---
name: monday-connector
description: User-invoked only, as /monday-connector <task>. Use when planning, reviewing or verifying work on the FULLY monday connector - OAuth 2.1 and PKCE, webhook intake, normalization, API reconciliation, initial sync and source scopes - or when preparing Gate 3G. It separates verified monday facts from project decisions and from what 3G must confirm. It never makes a real monday call or write, takes an Azure action, commits, pushes or starts Gate 3G.
disable-model-invocation: true
user-invocable: true
argument-hint: "<task>"
---

# monday Connector for FULLY

## Overview

This skill holds the verified monday facts, the Fully connector rules and the Gate 3 operating rules in one place, so
connector work need not reconstruct them from `docs/gates/gate-3-scope.md`. It condenses and decides nothing:
CLAUDE.md, the six frozen documents and `docs/gates/gate-3-scope.md` stay the authority. Where this skill and a source
disagree, the source wins and the drift is reported. A task that conflicts with a canonical rule stops and is reported
with the exact conflict, the rule it breaks and the smallest safe alternatives (CLAUDE.md).

**The boundary: monday MCP = reference / verification tool; Fully connector = system under test.** A monday MCP tool
never implements, bypasses or substitutes Fully's connector, and nothing it returns is evidence that Fully's path works.

**Violating the letter of these rules is violating their spirit.**

## Authority and invocation

- Invoking the skill is never authorization. `<task>` names what to plan, review or verify; when it is empty or
  unclear, ask before answering.
- State when this skill was written (2026-10-03): Gate 3 IN PROGRESS; 3A to 3F and the PKCE verifier cleanup PASS;
  **3G NEXT / NOT STARTED**. Read the status table at the top of `docs/gates/gate-3-scope.md` before relying on it.
- The invocation authorizes reading the repository and answering by the output contract. Changing code still needs the
  current gate's authorization and stays inside that gate (CLAUDE.md, scope discipline).
- It never authorizes, and an answer never proposes doing without the owner's own approval:
  - any real monday call: the API, OAuth, webhook registration or a monday MCP tool, reads included;
  - any monday write, `webhooks:write` or another write scope;
  - Azure, Key Vault, RBAC, `eng/infra.ps1`, `CD Dev`, or applying a migration outside the local stack;
  - a commit or a push;
  - starting, planning in code or scaffolding Gate 3G.
- The local hook `fully_approval_guard.py` classifies every `mcp__*monday*` tool (reads pass to the normal permission
  flow; tools that change data ask; destructive, administrative, code-executing and automation tools are denied) and
  asks before direct calls to `*.monday.com`; `.claude/settings.local.json` mirrors its sets. A deny is a stop, never a
  cue to rephrase. These rules hold in full when the guards are absent.

## Labels

Every claim in an answer carries exactly one label, with its source.

- **FACT**: monday behaviour the repository records as verified (the sources below) or an exact code constant.
- **PROJECT DECISION**: a Fully rule: an owner decision such as D3E-8, a review decision such as R3B-2, an invariant, a
  Gate 2 decision, or the code that implements one.
- **MUST CONFIRM IN 3G**: provider behaviour that is assumed, undocumented or only modelled by a test fake. It is never
  stated as a fact, and the answer says how Fully fails closed until 3G confirms it.

Rules for labelling:
- A fake's behaviour is never a FACT. `FakeMondayWorkspace` answers an expired cursor with an error shape it invented,
  lists items in numeric id order and may answer fewer items than the limit: none of that is verified monday behaviour.
- A compatibility assumption is never a FACT: HS256 for webhook tokens and the bare-or-`Bearer` header forms are
  MUST CONFIRM IN 3G.
- Anything the tables below do not hold is unknown. Look it up in the repository, or say it is unknown and label it
  MUST CONFIRM IN 3G; never fill it from memory or from general knowledge of monday.

## Verified monday facts (FACT)

Sources, each a heading of `docs/gates/gate-3-scope.md`:

- [S1] "monday platform baseline (verified 2026-09-29)"
- [S2] "monday OAuth 2.1, reverified (2026-09-30)"
- [S3] "monday webhooks, reverified (2026-09-30)"
- [S4] "monday webhook payloads, reverified (2026-09-30)"
- [S5] "monday item and status reads, reverified (2026-10-01)"
- [S6] "Reverified monday facts (2026-10-02)"
- [S7] "3E blocker (stopped before code)"

| Topic | Fact | Source |
|---|---|---|
| API endpoint | `https://api.monday.com/v2`, GraphQL as JSON over POST; one fixed constant, `MondayEndpoints.Api`, never taken from configuration or provider data | [S1]; `MondayEndpoints.cs` |
| API version | The `API-Version` header on every request; pinned `2026-07` (`MondayApiVersion.Pinned`), which was Current on 2026-09-29, with `2026-10` the release candidate and `2026-04` in maintenance; a version stays stable for at least six months, and moving the pin is a deliberate, reviewed and tested change | [S1]; `MondayApiVersion.cs` |
| OAuth 2.1 endpoints | Authorize `https://auth.monday.com/oauth2/authorize`; token `https://auth.monday.com/oauth_ms/oauth/token`; revoke `https://auth.monday.com/oauth_ms/oauth/revoke`; discovery (authorization server metadata) `https://auth.monday.com/oauth_ms/.well-known/oauth-authorization-server`; issuer `https://auth.monday.com` | [S2]; `MondayEndpoints.cs` |
| OAuth 2.1 flow | Response type `code` only; grants `authorization_code` and `refresh_token`; an app opts in through its "New OAuth Flow" setting, and the legacy flow issues non-expiring tokens and no refresh token | [S1], [S2] |
| PKCE | Required, `S256` only: `code_challenge` is the base64url SHA-256 of the verifier, and the verifier is 43–128 unreserved characters | [S2]; `MondayPkce.cs` |
| Client authentication | The client secret is required (`client_secret_post` or `client_secret_basic`); the code exchange, the refresh and the revocation are JSON POSTs carrying `client_id` and `client_secret` | [S1], [S2] |
| Authorization code | The callback carries `code`, `state` and `status`; the code is valid for 10 minutes; the exchange sends `code_verifier`, and `redirect_uri` when the request carried one | [S2] |
| Tokens | The access token is a JWT whose `exp` claim is its expiry, with `token_type` `Bearer`; refresh when under 5 minutes remain; each refresh returns a new refresh token: keep the latest and discard the old (rotation); a revocation answers `{"success": true}` | [S1], [S2] |
| Scopes | The metadata lists 26 scopes: 14 end in `:read`, 11 in `:write`, plus `ai:consume` | [S2] |
| Rate limits | Each limit has its own answer: complexity (5,000,000 points per query; an app's reads 5,000,000 points per minute) `ComplexityException` or `COMPLEXITY_BUDGET_EXHAUSTED`; daily calls `DAILY_LIMIT_EXCEEDED`; the minute rate HTTP 429 with `Retry-After`; concurrency `maxConcurrencyExceeded`; IP (5,000 per 10 s) `IP_RATE_LIMIT_EXCEEDED`. Every rate-limit error carries `retry_in_seconds`, and the `RateLimit` and `RateLimit-Policy` headers report quota and reset | [S1], [S5] |
| Errors | Application errors can return HTTP 200 with `errors[].extensions.code`, possibly with partial data; transport errors use 400, 401, 403, 404, 409, 422, 423, 429 and 500; documented codes include `ResourceNotFoundException`, `UserUnauthorizedException`, `missingRequiredPermissions`, `InvalidBoardIdException` and `InvalidColumnIdException` | [S1], [S5], [S6] |
| Items by id | `items(ids:)` takes at most 100 ids and its `limit` defaults to 25, so Fully passes `limit` equal to the id count; `state` is `active`, `archived` or `deleted`; items have no revision or version field, and `updated_at` is source update time; `text` may be null and `value` is raw JSON | [S1], [S5]; `MondayOperations.cs` |
| Status columns | The API names a status column's type `status`; its value is a `StatusValue` with `id`, `type`, `label`, `index`, `text`, `updated_at` ("the column's last updated date", documented as `2026-03-21T08:36:10+00:00`), `value`, `is_done`, `label_style`, `update_id`, `column` and `is_leaf`; `label`, `index` and `text` are null when the status is empty; at most 40 labels per column, each at most 30 characters | [S5], [S6] |
| Status in webhooks | A column-change payload reports a status column as `columnType` `color`, its `value` shaped `{"label": {"index", "text", "style"}, "post_id"}` | [S4], [S5] |
| Rollup and hierarchy | `hierarchy_type` is `classic` or `multi_level`; on a multi-level board a status column with rollup resolves to `BatteryValue` for every item, which `... on StatusValue` does not match, and `column_values` omits rollup columns unless `capabilities: [CALCULATED]` is passed | [S5], [S6] |
| Board context | Column ids are unique only within a board, and an item can move to another board | [S5], [S7] |
| Boards | `boards(ids:, state:)`: the state filter defaults to `active` (`active`, `archived`, `deleted`, `all`); `type` is `board`, `custom_object`, `document` or `sub_items_board` | [S6] |
| Pagination | `items_page` returns default 25, at most 500 items per page, active items only with no state filter, and an opaque cursor; `next_items_page(cursor:, limit:)` takes `limit` at most 500; the cursor is cached for 60 minutes and "will expire 60 minutes after the first request"; a `null` cursor means no more items | [S1], [S6]; `MondayOperations.cs` |
| Webhook challenge | When a webhook is created, monday POSTs `{"challenge": "…"}` to its URL; the endpoint answers HTTP 200 with the same JSON | [S3] |
| Webhook authentication | Requests of webhooks created with an app token carry a JWT in `Authorization`, verified with the app's Signing Secret (app lifecycle webhooks use the Client Secret); the documented claims are `accountId`, `userId`, `aud` ("your app's endpoint URL"), `exp`, `iat` and `shortLivedToken`, an API token valid for five minutes | [S1], [S3] |
| Webhook delivery | The body is `{"event": {…}}` with, among others, `triggerUuid`, `originalTriggerUuid`, `boardId`, `pulseId`, `columnId`, `columnType`, `value`, `previousValue`, `userId`, `changedAt` (epoch seconds with a fraction) and `triggerTime` (ISO 8601 UTC with milliseconds); `userId`, `boardId`, `pulseId` and `subscriptionId` are JSON numbers; failed deliveries are retried once a minute for 30 minutes; ordering, duplicate delivery and idempotency are not documented | [S1], [S3], [S4] |
| Webhook management | `create_webhook` and `delete_webhook` need `webhooks:write`; there are 21 event types and only five documented payloads, and a payload's `type` is not its subscription name | [S3], [S4] |

## Fully connector rules (PROJECT DECISION)

1. **Read and ingest only.** Gate 3 has no monday business write. Every operation is a typed READ
   (`MondayOperations`); `MondayQueryGuard` admits a single read query only; `MondayScopePolicy` allows only `:read`
   scopes (default `boards:read me:read`), and a grant holding a write scope or `ai:consume` is refused and revoked.
   No `webhooks:write` (D3C-1). Premium §20 constraint 5; invariant 37.
2. **Webhook = fast signal; provider API reread = correctness path.** They stay separate mechanisms (System
   Architecture v1 §2; invariant 17). A missed, refused, duplicated or out-of-order delivery is repaired by
   reconciliation, never inferred from arrival order or a UUID.
3. **Webhook values are untrusted hints.** The monday JWT authenticates the sender, not the body's fields. The
   normalizer declares every event `Unconfirmed` (`SourceValueConfirmation`; `Unspecified` counts the same), and
   processing records it `ReconciliationRequired` with `processing.source.unconfirmed`, every field `Withheld`, before
   any ordering or Snapshot state changes (D3D-1; invariant 13). Refusals are recorded under the provider-neutral
   `processing.normalizer.rejected` (D3D-6), and the handoff to 3E is that `ReconciliationRequired` outcome, with no
   new "reread object" concept (D3D-7).
4. **Confirmed only from an authoritative live API reread under current connection authority.** Only a successful,
   authenticated reread through the 3A `MondayReadClient` and the 3B access-time credential may produce `Confirmed`
   (D3E-1). It reads the typed `StatusValue`, preferring its `label`, and the field stays the column id (D3E-2). The
   response must match the requested item, the expected board, the column, the `status` type and the delegated
   identity of the user who authorized the connection (D3B-1), or it fails closed with nothing remapped (D3E-8).
   Current provider state beats the webhook value (D3E-4). The result is applied through processing, never by
   monday-specific Snapshot SQL (D3E-7), and the work is resolved only after that commit (D3E-6). Absence is never
   deletion, and an archived or deleted item is never applied (D3E-9). A read overtaken by a reauthorization is
   discarded and retried (`AuthorityChanged`), as the 3E final owner review decided; a stable wrong identity is held
   (`IdentityMismatch`); a sync generation captures its authority at start, a token renewal changes nothing and a
   reauthorization restarts it (D3F-5).
5. **Never fabricate timestamps, revisions, provider ordering or business identity.** monday has no item revision.
   `changedAt` is source update time and `triggerTime` occurrence time (D3D-4). On a reread the status value's own
   `updated_at` is the update time: never the item's `updated_at`, the read time or a local or database clock (D3E-3).
   Receipt time and ingress order never decide business order (invariant 12; D3F-2). Missing evidence keeps the work
   on the `ReconciliationRequired` path. There is no blind last-write-wins (invariant 18).
6. **Stable identity.** An object is the monday item id in its board context, the field is the column id, and the
   provider event id is `triggerUuid` only when monday supplies it, never synthesized. Item names, column titles, group
   names, user names and emails are untrusted display data and never keys (D3D-2; invariant 47). The actor, `userId`,
   stays unresolved provenance (D3D-3; invariant 19).
7. **SourceScope.** `NormalizedSourceEvent.SourceScope` is a provider-neutral, bounded, opaque id. For monday it holds
   the validated `boardId`, required for the supported event and never parsed from a reference or display text
   (D3E-10). It is provenance and resource scope, not canonical object identity: `ObjectReference` and the Snapshot key
   do not change. A board mismatch fails closed and is never remapped to what monday returned.
8. **Supported Gate 3 surface.** Explicit configured SourceScopes only, through `IConnectorSourceScopes` (D3F-1); no
   workspace or board auto-discovery (D3E-5). A scope is a board of type `board` with `classic` hierarchy and at least
   one `status` column; Fully reads its active parent items and their ordinary status columns, mapped to `TaskStatus`.
   Everything else is refused explicitly, never guessed: subitems, other column types, rollups, `multi_level` boards,
   names, lifecycle and update events. No date column becomes `Deadline` (D3D-8). `EventText.MaxValueLength` stays
   1,024, and nothing is truncated (D3D-5).
9. **Initial sync** follows System Architecture v1 §2, in this order: establish the connection; activate and buffer
   realtime intake; read the authorized baseline; build the initial Snapshot; record the sync watermark; replay
   buffered newer events; enter Live. Never "scan first, then enable webhooks". A durable state machine per connection,
   with generations, claims and leases, decides (D3F-4), and every step is restartable and idempotent (D3F-13).
   Baseline values are authoritative API reads confirmed through the 3E path (D3F-6), with 3E evidence receipts naming
   the initial-sync read (D3F-7). monday pages are no frozen snapshot: correctness comes from buffering, authoritative
   reads, normal ordering, the replay of buffered hints and 3E rereads (D3F-9). Every enabled scope completes before
   Live: a held scope fails the generation, and no connection is partly Live (D3F-15).
10. **Buffering.** Raw receipts are the buffer (D3F-3): there is no shadow raw-payload store, and sync tables hold
    metadata, fences and progress only. `raw_receipts.ingress_sequence`, generated by the database, is the fence: it
    decides only whether a receipt lies before, inside or after a cutover, never provider or business order (D3F-2).
    The cursor, `updated_at`, `changedAt`, `triggerTime`, receipt time and the clock are never the watermark. Buffer
    start and cutover are decided by durable fences and locks, never by time or sleeps (D3F-10, D3F-11), and a buffered
    receipt never reaches the Snapshot ahead of the baseline (D3F-12): it is held with
    `processing.receipt.held_for_initial_sync`.
11. **Scope changes.** `connection_syncs.scope_version` rises by one with every effective change of the enabled set: a
    scope added enabled, enabled again, or disabled; a call that changes nothing changes nothing. A change while
    `Live`, running or failed makes the generation `RequiresRestart` in the same transaction. A disabled scope leaves
    current work at once (its `baseline_generation` is cleared in the disabling transaction) and returns only after a
    new baseline reaches Live (`awaiting_baseline_version`). The raw Snapshot rows, the ledger, the receipts and the
    targets stay for provenance and audit. Current work is read only through the `security_invoker` views
    `active_snapshot_fields` and `active_snapshot_objects`, never the raw Snapshot; `CurrentWorkViewTests` enforces
    it.
12. **Reconciliation.** One provider-neutral, tenant-owned `reconciliation_targets` row per tenant, source,
    connection, object and field, holding the data type and the `source_scope`, is written in the same transaction as
    each `ReconciliationRequired` outcome (D3E-11). No work value, label, raw response, credential or display text is
    stored in it. Resolution is a compare-and-swap on the generation, so a stale worker records nothing. The
    dispositions are fixed in `ReconciliationOutcomes`: transient failures retry on a capped backoff and are never
    held; access refusals make no provider call and resume by themselves (`AwaitAccess`); `ObjectMissing` and
    `UnsupportedResponse` are held after 8 consecutive answers; semantic cases are held at once. Initial sync seeds
    every synchronized field as a known target (D3F-8) and classifies its failures the same way (D3F-14). The sweep
    rereads known `Resolved` targets only.
13. **PKCE.** The state is single-use: consumed atomically, so a replay or a concurrent callback loses. The code is
    exchanged at most once and never retried. A callback refused as a replay never deletes the winning completion's
    verifier (`273faa6`). The verifier lives only in secret storage (`IMondaySecretStore`; D3B-2), and its cleanup is
    durable (`verifier_deleted_at_utc`) and restart-safe. `MondayConnectionAuthorizer.CleanUpVerifiersAsync` runs once
    at the 3G host's start-up and every 5 minutes while it is active, only under a trusted tenant context, and never
    through a new unrestricted tenant enumeration.
14. **Security.** The tenant is resolved only server-side, from the connection (invariant 45). Every new table is
    tenant-owned with forced row-level security and least-privilege grants (R3B-2). Tokens, codes, verifiers, the
    client secret and the Signing Secret never reach Core, AI prompts, logs, events, ordinary database fields or
    business state (D3B-2; invariant 53; Premium §20 constraint 1). Every provider call records the authority it used
    (`ConnectorCallResult`; Premium §20 constraint 2). Revocation is enforced at access time, before every attempt
    (invariant 58; Premium §20 constraint 7).

## Authoritative source path

Webhook path, a fast signal and never truth: `MondayWebhookEndpoint` (HTTP only) → `MondayWebhookAuthenticator`
(the JWT under the Signing Secret) → `EventIngressService`: the approved resolver, `MondayWebhookVerifier` (the JWT
against the connection's own audience), the tenant set server-side from the connection, then
`ISourceVerifier.ConfirmAsync` (the monday account and the 3B authorization) → the raw receipt `monday.webhook.v1`,
committed before the 200 → `MondayWebhookNormalizer` (`Unconfirmed`, `SourceScope` = the board) →
`ReconciliationRequired`, with its `reconciliation_targets` row in the same transaction.

API reread, the only path to `Confirmed`: `SourceReconciler` claims a due target → a local access check, with no
provider call while access does not stand → `MondayStatusReader` (the monday `IAuthoritativeSourceReader`) →
`MondayReadClient.ReadItemStatusesByIdAsync`, which acquires the credential from `MondayConnectionCredentialSource`
before every attempt → binding of item, board, column, `status` type and delegated identity → the apply transaction:
the claim, access and authority compared again, and a read equal to the Snapshot resolved with no event → the evidence
receipt `monday.api-read.v1` (only the values consumed) → `SourceReconciler.ConfirmAsync` →
`EventProcessor.DecideAndProjectAsync` (normal ordering) → the Snapshot → the current work view
`active_snapshot_fields`.

Initial sync takes the same apply path: `InitialSyncEngine` → `MondayInitialSyncReader` (board validation, then pages)
→ `SourceReconciler.ConfirmAsync`.

## Reject on sight

Run this table before anything else. A match is REJECTED: name the rule and give the smallest safe alternative.

| Proposal | Verdict and rule | Do instead |
|---|---|---|
| Mark a webhook value `Confirmed` because its JWT verified, or let it reach the Snapshot | REJECT. The JWT authenticates the sender, not the fields: D3D-1, D3E-1; invariants 13 and 17 | Keep it `Unconfirmed` and let the 3E reread confirm the current value |
| Key an item, column, board or user by its name, title, label or email | REJECT. D3D-2, D3D-3; invariants 19 and 47 | Use the item id with its board as `SourceScope`, the column id, and the provider actor id left unresolved |
| Discover boards or a workspace, or sync whatever the token can see | REJECT. D3F-1, D3E-5 | Configure explicit scopes through `IConnectorSourceScopes.ConfigureAsync` |
| Store or reuse the `items_page` cursor as the sync watermark or progress | REJECT. D3F-2: the cursor lives in memory for one scan, and monday expires it 60 minutes after the first request | Fence with `raw_receipts.ingress_sequence`; restart the board from its first page |
| Read current work from the raw Snapshot tables | REJECT. 3F final owner review; `CurrentWorkViewTests` | Read `active_snapshot_fields` and `active_snapshot_objects` |
| Use a monday MCP tool in place of Fully's OAuth, webhook, reread, initial sync or Snapshot path, or as evidence that it works | REJECT. the boundary above; Premium §20 constraint 2: an MCP call acts under its own authority, not the connection's delegated identity | Prove Fully's path from Fully's own records; MCP output is reference only |
| Accept a read, a credential or a sync generation from before a reauthorization | REJECT. `AuthorityChanged`; D3F-5; invariant 58 | Discard it and retry under the current authority; restart the generation |
| Keep a disabled SourceScope's state in current work | REJECT. 3F final owner review: `baseline_generation` is cleared when the scope is disabled | Hide it at once; show it again only after a new baseline reaches Live |
| Fill a missing timestamp or revision from receipt time, the clock, the item's `updated_at` or ingress order | REJECT. D3D-4, D3E-3, D3F-2; invariants 12 and 18 | Keep the work `ReconciliationRequired` and report the gap |
| Add a monday write, a GraphQL mutation, a write scope or `webhooks:write` inside Gate 3 | REJECT. D3C-1; Premium §20 constraint 5; `MondayQueryGuard`; invariant 37 | Stay read-only; a write belongs to a later gate with its own authorization |
| Map a monday date column to `Deadline` | REJECT. D3D-8 | Keep date columns unsupported until the owner approves a mapping |
| Truncate or cut a value longer than 1,024 characters | REJECT. D3D-5; `EventText.MaxValueLength` | Refuse it explicitly; widening is an owner decision |
| Synthesize a provider event id when `triggerUuid` is absent | REJECT. System Architecture v1 §3 deduplication; the 3C design | Leave it absent; the fingerprint and duplicate-candidate path applies |
| Take the tenant from the route, the body, the JWT or a header | REJECT. invariant 45; Gate 2 decision 1 | Resolve it server-side from the connection |
| Add a tenant-less cleanup, discovery or sweep function | REJECT. Gate 2 decision 1; the PKCE hosting decision | Stay tenant-scoped; reuse an approved tenant-discovery mechanism later |
| Store a token, code, verifier, state or secret in a database field, log, event or prompt | REJECT. D3B-2; invariant 53; Premium §20 constraint 1 | Secret storage only (`IMondaySecretStore`); ids, hashes and versions in the database |

## Gate 3G (NOT STARTED)

- 3G is **NEXT / NOT STARTED** and begins only on its own product-owner authorization. Invoking this skill never
  starts it, and nothing of 3G is planned in code or scaffolded before that authorization.
- Once 3G is authorized, these rules apply:
  - monday MCP is reference and read-only by default: looking up ids in the test workspace the owner chose, and
    comparing what monday shows with what Fully recorded. A SourceScope is still configured explicitly, never from
    MCP output.
  - Write-capable monday MCP tools stay Ask or Deny. A data change in the test workspace, such as the status change a
    webhook should report, happens only when the owner explicitly authorizes that named test mutation: the tool, the
    board, the item, the column and the value. The guard's prompt is the owner's confirmation, not the authorization.
    A denied tool stays denied, and no shell call to `*.monday.com` replaces it.
  - Playwright may operate the approved browser flow for Fully's own OAuth start and callback through the R3B-1
    harness. The authorization code, the state, the tokens and the secrets never enter the conversation, a log or a
    file (D3B-2; Premium §20 constraint 1).
  - What must be proven is Fully's own path: OAuth 2.1 through `MondayConnectionAuthorizer`, intake through the 3C
    endpoint, the 3D normalizer, the 3E reread, 3F initial sync, the Snapshot and the current work view. The evidence
    is Fully's own records: raw receipts, ledger outcomes, reconciliation targets, evidence receipts,
    `connection_syncs`, Snapshot rows and `active_snapshot_fields`.
  - No MCP shortcut may simulate success: no MCP read stands in for a webhook, a reread or a baseline, and no MCP
    output is written into Fully's tables or fed into its ingress.

The guard's MCP classes, mirrored in `.claude/settings.local.json`:

| Class | Examples | Guard |
|---|---|---|
| Read | `get_board_info`, `get_board_items_page`, `get_column_type_info`, `all_api_read` | passes to the normal permission flow |
| Changes test data | `change_item_column_values`, `create_item`, `create_board`, `create_column` | asks |
| Destructive, administrative, code-executing, generic write, automation | `all_api_write`, `all_monday_api`, `execute_code`, `create_automation`, `create_workflow` | denied |

- Acceptance target, from Development Gates v1: "a real monday change reaches canonical event and Snapshot
  end-to-end; reconciliation repairs missed event scenarios." Spelled out: (1) a real monday change reaches the
  canonical event path and the Snapshot end to end; (2) reconciliation repairs a missed event. The Gate 3 Definition
  of Done (item 7) adds initial sync and revocation failing closed on the real workspace.
- Preconditions, each its own owner decision or approval:
  - The PKCE cleanup: the 3G harness or host runs `CleanUpVerifiersAsync` once at start-up and every 5 minutes while
    active, only under a trusted tenant context, with no tenant-less cleanup or discovery function.
  - R3B-1: real OAuth only through an owner-operated DEV/TEST harness that supplies the trusted Tenant → canonical user
    → connection chain; no anonymous production OAuth-start endpoint.
  - The monday app: none exists yet (D3B-3). The owner creates it (New OAuth Flow, read scopes only, the redirect URL)
    and places its secrets in the approved secret store as `wce-monday-client-secret` and `wce-monday-signing-secret`;
    the deployed secret store is an owner decision.
  - Webhooks: the public base address (`Connectors:Monday:WebhookBaseAddress`) and how monday reaches it; registration
    needs `webhooks:write`, which D3C-1 defers to a separate authorization; register only the supported status-change
    event.
  - Azure, if 3G uses it: the Gate 2B to 2F and Gate 3 migrations, and the R3B-2 re-assertion after any bootstrap
    rerun.
  - Host wiring for initial sync and reconciliation: tenant-scoped, with no new tenant-less lookup (Gate 2 decision 1).

## MUST CONFIRM IN 3G

Not facts. Each item below is held fail-closed today; 3G records the real behaviour, and changing the handling is an
owner decision, never a silent relaxation. "Item n" is the numbered open verification item in
`docs/gates/gate-3-scope.md`. "Decision E" is decision E of "Owner decisions on record before 3G (recorded
2026-10-04)" in the same document: the authority for the two unknowns it names. The handling beside them is this
skill's reading of the decisions it cites, not part of decision E.

| Open provider behaviour | Fully until confirmed | Recorded at |
|---|---|---|
| Webhook JWT algorithm | The allowlist is exactly HS256, a 3C compatibility assumption; `none`, asymmetric and other HMAC algorithms are refused | Item 9 |
| Webhook `Authorization` form | Exactly the bare JWT, or exactly `Bearer`, one space and the JWT; any other form is refused | Item 10 |
| Webhook `aud` form | Equal, character for character, to the configured base address followed by `/api/connectors/monday/webhooks/{connectionId}` | Item 11 |
| JWT on the challenge | The challenge path neither requires nor reads `Authorization` | Item 12 |
| Webhook token lifetime | `exp` is enforced with a 30-second tolerance; `nbf` or `iat` in the future is refused; no maximum is invented | Item 13 |
| Whether a webhook created with the connection's OAuth token carries the JWT | A delivery without a valid JWT is always refused | Items 3, 14 |
| Which answers monday treats as failures, whether a retry carries a fresh JWT, whether a failing webhook is disabled | Every delivery not durably stored gets a non-2xx answer; reconciliation repairs what never arrives | Item 15 |
| An unknown or inaccessible board in `boards(ids:)` | An empty answer is retried, then held | 3F deferred items |
| An unknown or inaccessible item id in `items(ids:)` | `ObjectMissing`: retried, then held after 8 consecutive answers | 3E deferred items |
| What moving or copying an item between boards does to its item id and to its board and object identity | A reread answered on another board than the expected scope fails closed as `BindingMismatch`: held, never remapped; an object monday places on another board is neither applied nor made known in the scope being read; no identity is derived across boards or from a source item | Decision E |
| The exact response to an expired cursor | An error and a missing next page are both a refusal: the board restarts from its first page and is never held | 3F deferred items |
| Whether an `items_page` or `next_items_page` cursor is bound to the OAuth token, the connection's authority, the account or the session that created it | The cursor lives in memory for one scan only and is never stored or carried into another scan (D3F-2); a reauthorization during the scan discards the read and restarts the generation (D3F-5), while a token renewal keeps it, so a continuation monday refuses, after a renewal or otherwise, restarts that board from its first page and is never held | Decision E |
| Item order and consistency across pages | Nothing relies on order: rereads converge, and an item answered twice is applied once | 3F deferred items |
| The `hierarchy_type` monday reports for classic boards | Anything but `classic`, null included, fails closed as unsupported | 3F deferred items |
| Real subscription deliveries buffered through 3C during a real initial sync | Held receipts replay as unconfirmed hints before Live | 3F deferred items |
| The JSON path of `retry_in_seconds` | Read from the error extensions and their `error_data`, and from `Retry-After` and `RateLimit` | Item 1 |
| The `Authorization` form for API calls with OAuth tokens, bare or `Bearer` | The documented bare form, written in one place | Items 2, 8 |
| Callback `status` values | Accepted only with a code and no `error`; exchanging the code proves the approval | Item 5 |
| `iss` on the callback | A present `iss` must be exactly `https://auth.monday.com`; its absence is accepted | Item 6 |
| Token endpoint error codes, token lifetimes, reuse of a refresh token | Decided by HTTP status alone; a refresh token is never reused | Item 7 |
| Payloads of the 16 undocumented event types | Refused as unsupported; real samples come before any registration | Item 16 |
| Subitem column changes | A payload with `parentItemId` or `parentItemBoardId` is refused, and none is registered | Item 17 |
| Status type string in real webhooks | Only `color` is mapped; another string is refused | Item 18 |
| Special `userId` values, for example for automations | Anything but a positive numeric id is refused | Item 19 |
| Status label limits in webhooks | A label that is not a valid canonical value is refused, never cut | Item 20 |
| `changedAt` precision | Up to seven decimal places; more is refused, never rounded | Item 21 |
| `updated_at` precision and format, and whether renaming a label changes it | Strict parsing; a renamed label that ordering cannot place as newer stays unresolved | 3E deferred items |
| The 5-minute clock-skew tolerance against real monday data | `EventProcessingOptions.ClockSkewTolerance` stays unchanged | Gate 2 carry-forward 4 |
| `EventText.MaxValueLength`, 1,024 characters, against real monday data | Unchanged; a longer value is refused and never truncated | Gate 2 carry-forward 5 |
| Multi-level boards and rollup columns | The board is refused at validation; a rollup status is unsupported, never a cleared value | 3E deferred items |

## Test doubles and test infrastructure

Every double below exists, in the file named; use these and invent no other. They never contact monday: `FakeMonday`
fails on any address but monday's fixed endpoints, and every scripted fake fails the test on a request nobody scripted.

| Double | File | Use it for |
|---|---|---|
| `FakeMonday`, `MemorySecrets`, `MovingClock`, `NoWait` | `tests/App.Integration.Tests/Connectors/MondayFakes.cs` | monday's token, revocation and API endpoints by address, with recorded requests and scripted or awaitable answers (`ThenToken`, `ThenApi`, `AnswerApi`, `AnswerApiAsync`); in-memory secret storage with a deletion override; a clock the test moves; a delay that never waits |
| `FakeMondayWorkspace`, `FakeBoard`, `FakeItem` | `tests/App.Integration.Tests/InitialSync/FakeMondayWorkspace.cs` | A stateful fake account that answers exactly the connector's board validation, sync pages and status reread; `Intercept` and `AfterAnswer` interleave work with a call; `ExpireCursors` and `ExpiredCursorAnswersNull` cover both expiry forms the code handles |
| `InitialSyncStack`, `InitialSyncData` | `tests/App.Integration.Tests/InitialSync/InitialSyncStack.cs` | The whole 3F path on the local PostgreSQL: signed deliveries through the real 3C endpoint, the processor behind the transport consumer, initial sync and 3E reconciliation; reads of sync rows, scope rows, receipts and the current work view |
| `ReconciliationStack`, `ReconciliationData`, `FixedMondayCredentials`, `SwitchableMondayCredentials` | `tests/App.Integration.Tests/Reconciliation/ReconciliationStack.cs` | The 3E path end to end; targets and receipts as stored; a credential the test makes unobtainable |
| `MondayWebhookHost`, `WebhookTestTokens`, `WebhookSecrets`, `WebhookData`, `SeededConnection` | `tests/App.Integration.Tests/Connectors/MondayWebhookHost.cs` | The real 3C endpoint in Kestrel with every log record captured; fake HS256 tokens; connections seeded as 3B leaves them |
| `ConnectorAuthorizationData` | `tests/App.Integration.Tests/Connectors/ConnectorAuthorizationData.cs` | Seeding and reaching the 3B tables as the runtime role |
| `ScriptedMondayServer`, `ScriptedCredentialSource`, `RecordingDelay`, `StoppedClock`, `MondayHarness`, `MondayJson` | `tests/App.Unit.Tests/MondayTestDoubles.cs` | The 3A client in unit tests: scripted answers, scripted credentials, recorded backoff, monday-shaped JSON |
| `FakeMondayAuthServer`, `InMemorySecretStore`, `InMemoryAuthorizationDatabase`, `InMemoryAuthorizationStore`, `MutableClock`, `TestTokens`, `OAuthHarness` | `tests/App.Unit.Tests/MondayOAuthTestDoubles.cs` | The 3B lifecycle in unit tests; `InMemorySecretStore` records every operation and injects failures (`SetOverride`, `GetOverride`, `DeleteOverride`); the in-memory store can fail before or after a change |
| `WebhookTokens`, `WebhookHarness`, `VerifierHarness` | `tests/App.Unit.Tests/MondayWebhookTestDoubles.cs` | 3C token, authenticator and verifier unit tests |
| `SyntheticNormalizer`, `SyntheticEvent` | `tests/App.Integration.Tests/Processing/SyntheticNormalizer.cs` | Provider-neutral processing tests with the Gate 2 synthetic source |
| `IngressHost`, `SyntheticSourceVerifier`, `CapturingLoggerProvider` | `tests/App.Integration.Tests/Ingress/IngressHost.cs` | The real ingress, with every log record captured |

Patterns proven in 3A to 3F:
- Authoritative evidence: assert the evidence receipt (`monday.api-read.v1`, only the values consumed, no outbox
  message) and the target's outcome, not only the Snapshot value (`ReconciliationData.ReceiptsAsync`,
  `ReconciliationData.TargetsAsync`).
- Races are made deterministic with awaitable fake answers and interceptors, never with sleeps. Work that a fake runs
  inside the engine's call runs detached (`ExecutionContext.SuppressFlow`, as `InitialSyncStack` does), so it never
  inherits the trusted tenant.
- Schema, grant and view changes are proven on fresh databases (`ConnectorAuthorizationFreshDatabaseTests`,
  `InitialSyncDatabaseSecurityTests`), because the shared test database reruns the runtime-role bootstrap.
- Leakage tests capture every log category and scan the whole database, each with a positive control, such as the
  delivered payload found in `raw_receipts`.
- A new tenant-owned table is listed in `WorkContinuityModel.TenantOwnedTables`; `TenantIsolationAcceptanceTests`
  then attacks it from both tenants and requires rows of its own, so the test seeds it.
- Mutation evidence for a changed guard follows `/mutation-evidence`.

## Targeted tests by area

| Area | Unit | Integration | Security and architecture |
|---|---|---|---|
| 3A client and contract | `MondayReadClientTests`, `MondayQueryGuardTests`, `MondayFailureTaxonomyTests`, `ConnectorContractTests` | none | `ConnectorSecurityTests`; `ConnectorBoundaryTests` |
| 3B OAuth and PKCE | `MondayConnectionAuthorizerTests`, `MondayConnectionCredentialSourceTests`, `MondayOAuthPrimitivesTests` | `MondayConnectionLifecycleTests`, `ConnectorAuthorizationStoreTests`, `ConnectorAuthorizationFreshDatabaseTests` | `ConnectorAuthorizationSecurityTests` |
| 3C webhook intake | `MondayWebhookAuthenticatorTests`, `MondayWebhookBodyTests`, `MondayWebhookVerifierTests` | `MondayWebhookEndpointTests`, `MondayWebhookSecurityTests` | `MondayWebhookIntakeSecurityTests` |
| 3D normalization | `MondayWebhookNormalizerTests` | `MondayNormalizationTests`, `UnconfirmedSourceValueTests` | `MondayNormalizationSecurityTests` |
| 3E reconciliation | `MondayStatusReaderTests`, `ReconciliationContractTests` | `MondayReconciliationTests`, `ReconciliationTargetTests`, `ReconciliationTargetDatabaseSecurityTests` | `ReconciliationSecurityTests` |
| 3F initial sync and scopes | `MondayInitialSyncReaderTests` | `InitialSyncTests`, `InitialSyncFenceTests`, `InitialSyncRecoveryTests`, `InitialSyncRecordTests`, `InitialSyncConfigurationTests`, `InitialSyncActiveStateTests`, `InitialSyncDatabaseSecurityTests` | `InitialSyncSecurityTests`; `CurrentWorkViewTests` |
| Tenant isolation, for any new table | none | `TenantIsolationAcceptanceTests`, `Gate2AcceptanceTests` | none |

Run them with `dotnet test --project tests/App.<P>.Tests/App.<P>.Tests.csproj` and `--filter-class` or
`--filter-namespace`; never combine `--filter-class` with `--filter-method`, which AND to zero tests. Exit code 8 means
zero tests ran, which is no evidence. Integration and database tests need the local Docker stack. The full suite and its
batches belong to `/gate-closure`.

## Output contract

Before answering, read the status table of `docs/gates/gate-3-scope.md` and the files the task touches. Answer in this
shape. Label every claim FACT, PROJECT DECISION or MUST CONFIRM IN 3G, and cite its source: an [S#] source or a code
constant, a decision id, an invariant, or an open item.

```
MONDAY CONNECTOR - <task in a few words>
Verdict: WITHIN CURRENT GATE | OUTSIDE CURRENT GATE | REJECTED (<rule>) | NEEDS OWNER DECISION
1. Affected invariants: <rule numbers of this skill, decision ids, invariant numbers> (PROJECT DECISION)
2. Provider facts relied on: <each FACT with its source>; <each assumption as MUST CONFIRM IN 3G>
3. Authoritative source path: <how a value becomes Confirmed, or "none: nothing becomes Confirmed">
4. Webhook vs API role: <what the webhook only signals; what the API reread confirms>
5. Tenant and security implications: <tenant resolution, RLS and grants, secrets, logs, authority, revocation>
6. Required targeted tests: <classes from the table, new tests, mutation evidence for a changed guard>
7. Gate scope: <the current gate state from gate-3-scope.md; inside or outside it; the authorization needed>
8. Blockers and unknowns: <conflicts to stop and report, MUST CONFIRM IN 3G items, owner decisions> | none
```

- Run the rejection table first. A match makes the verdict REJECTED, names the rule, and gives the smallest safe
  alternative in item 8.
- WITHIN CURRENT GATE needs an open sub-gate that the owner has authorized, or the owner's explicit instruction for
  this change, and the change inside it. While no sub-gate is open, a change that breaks no rule is NEEDS OWNER
  DECISION, and work that belongs to a later gate is OUTSIDE CURRENT GATE.
- A planned change, a design or a review always gets the full template. A question that plans no change gets a
  labelled answer without it.

## Never

- Call real monday (the API, OAuth, webhook registration or a monday MCP tool) without the owner's explicit approval,
  or change monday data.
- Add a monday write, a write scope, `webhooks:write` or a GraphQL mutation in Gate 3.
- Use a monday MCP tool to implement, bypass, substitute or evidence Fully's connector.
- State a MUST CONFIRM IN 3G item, a compatibility assumption or a fake's behaviour as a FACT.
- Touch Azure, commit, push, or start, plan in code or scaffold Gate 3G.
- Weaken, skip or delete a test or a guard to make a change pass.
- Put a token, code, verifier, state, client secret or Signing Secret in a prompt, a log, an event, an ordinary database
  field or a file.
