# Gate 3G — Live-enablement plan: secret storage and signed-webhook provisioning

This is a planning document. It records recommendations, not decisions: nothing in it is approved until the product
owner says so in their own words. S-1 to S-5 and W-1 to W-2 below are **PROPOSED, NOT APPROVED**. The document
implements nothing, authorizes nothing, and is not evidence of how monday or Azure behaves.

## Status and baseline

- **Gate 3G: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN. Real execution is NOT
  AUTHORIZED. Gate closure is NOT AUTHORIZED.
- Baseline: branch `main` at `884962f6a3e4f7ca820bd8159e7ac97fa2528cdd`, equal to `origin/main`. PR #1 is merged and
  main CI run 37307089471 succeeded. The owner's PR comment 5994066731 approved integrating the offline checkpoint
  only.
- Decisions that stay in force and are not reopened here: D3B-2, R3B-1, R3B-2, D3C-1, the PKCE hosting decision of
  2026-10-03, decisions A to F, and D3G-1 to D3G-8.
- Sources: the repository at the baseline, and public documentation of monday and Microsoft read on 2026-10-05 (listed
  at the end). No account, API, vault, OAuth endpoint or webhook endpoint was contacted.
- Labels used throughout: **[A]** a confirmed repository or documentation fact; **[B]** a design risk or inference,
  not a proven defect; **[C]** provider behavior that stays unknown until a future approved live run.

### Revision note (2026-10-05)

This revision corrects the first version of this plan on the owner's instruction. The corrections:

1. The claim that an 18,000-byte adapter limit covers every value the connector can write was wrong. It covers the
   unescaped maximum only. The worst case, after JSON escaping, is about 98.7 KB. See Part 1, "Serialized size".
2. `contentType` handling is now defined case by case, and plain text is proposed for the two owner-managed names only.
3. The uniform "three attempts" retry rule is replaced by an operation-specific policy with a time budget, after a
   scenario-by-scenario trace of the callers.
4. "Removal", "soft deletion", "recoverability" and "purge" are now kept apart, and the meaning of `Succeeded` and
   `NotFound` for a Key Vault adapter is defined.
5. The runtime identity is no longer the owner's own sign-in: Azure RBAC is additive, so that could not be least
   privilege.
6. U9 (webhook lifecycle after the provisioning authorization is revoked) is added; U1 and U2 stay unresolved.
7. Sequencing is split into five stages.

### Local draft work that is not part of this plan

At the time of this revision the working tree also holds five paths that the owner has identified as **foreign,
unauthorized local draft work**: a 43-line addition to `docs/gates/gate-3-scope.md`, and four new files under
`tests/App.Harness.Monday/Secrets/` and `tests/App.Integration.Tests/Harness/`. They are not owner-authorized, are not
an implementation of approved decisions, and were not used as evidence for this plan. Their hashes are recorded
outside the repository. They are not reconciled here.

## Part 1 — Real-secret storage

### Repository facts [A]

The port is `IMondaySecretStore` (`src/App.Connectors.Monday/IMondaySecretStore.cs`): `SetAsync`, `GetAsync` and
`DeleteAsync` by a `MondaySecretName`, with the statuses `Succeeded`, `NotFound` and `Unavailable` ("could not be
reached or refused the operation; the outcome is unknown"). There is no list or enumeration. Values are opaque bytes,
and an implementation "must be durable and must fail closed: a value is either returned exactly as set, reported
missing, or reported unavailable, never guessed".

Names (all lowercase letters, digits and dashes, at most 127 characters):

- `wce-monday-client-secret` and `wce-monday-signing-secret`: fixed, placed by the product owner. No code sets or
  deletes them.
- `wce-monday-tokens-{tenant}-{connection}-{version}`: the version is a fresh GUID for each authorization and each
  renewal, so a token name is written once and never reused.
- `wce-monday-pkce-{tenant}-{transaction}`: written once per OAuth transaction.

How each caller uses the port:

- **Authorization start.** The transaction row is opened first, then the verifier is set. If the set does not
  return `Succeeded`, start fails and nothing is deleted: the transaction expires, and the cleanup then lists it.
- **Authorization completion.** It gets the verifier and the client secret, exchanges the code, and sets the new
  token version before any row points at it. If that set fails, it deletes the new name best effort and revokes the
  pair. After activation it deletes the previous version best effort. If activation fails, it deletes the new name
  and revokes.
- **Renewal.** It reads the current version. `NotFound` triggers a compare-and-swap to "reauthorization required";
  `Unavailable` releases the claim and changes nothing. The new pair is set under the claim's GUID before the row is
  completed, and the old version is deleted only after the completion committed. If the set fails, it revokes the new
  pair, deletes the claim's name and requires reauthorization.
- **Abandoned renewal.** It reads the claim's name. A well-formed, matching pair there is adopted; `NotFound` or a
  malformed value requires reauthorization; `Unavailable` changes nothing.
- **Verifier cleanup** (start-up and every five minutes). `DeleteConfirmedAsync` counts `Succeeded` or `NotFound` as
  "confirmed gone" and records that durably; `Unavailable` or an exception keeps it due.
- **Disconnect.** It reads, revokes at the provider, and deletes best effort.
- **Webhook authentication.** It gets the Signing Secret once per request scope.
- Write and delete paths mostly pass `CancellationToken.None`; reads on request paths pass the caller's token.

### Serialized size [A, with inference marked]

**1. What the port accepts.** `IMondaySecretStore.SetAsync` takes any `ReadOnlyMemory<byte>`; the port states no size
bound [A]. The connector's own readers refuse a stored JSON value above 65,536 bytes (`StoredJson.Parse`), and a client
secret or Signing Secret outside 1 to 1,024 visible ASCII bytes [A].

**2. What the connector can produce.** Only two writers exist [A]:

- *Verifier material* (`MondayVerifierMaterial.ToBytes`): a fixed format string, two GUIDs and a verifier of 43 to 128
  unreserved characters. It is at most about 270 bytes, and none of its characters needs escaping.
- *Token material* (`MondayTokenMaterial.ToBytes`), written by `Utf8JsonWriter` with its default options [A]:
  - fixed overhead of ten property names, quotes, colons, commas and braces: 130 bytes;
  - `format` (20), `tenant`, `connection` and `version` (36 each);
  - `subject` and `account`: at most 20 ASCII digits each, because the only producer is
    `MondayResponseParser.AuthorizingIdentity`, which accepts numeric ids only;
  - `scopes`: at most 167 characters, because every write is preceded by `MondayScopePolicy.IsPermittedGrant`, whose
    allowlist is fourteen `:read` names (154 characters plus 13 spaces), none of which needs escaping;
  - `expires`: a 33-character round-trip date that contains one `+`, at most 38 bytes if that is escaped;
  - `access` and `refresh`: each 1 to 8,192 characters from `!` to `~` (`MondayIssuedTokens.IsToken`). The access
    token must also be three dot-separated parts with a base64url middle part (`TryReadExpiry`); its first and third
    parts may hold any of those characters except `.`. The refresh token has no further restriction.

Escaping [A for the documented part, B for the bound]: Microsoft documents that the default encoder escapes every
non-ASCII character as `\uxxxx`, escapes HTML-sensitive characters "such as `<`, `>`, `&`, and `'`", and always escapes
`\`. A JSON string must also escape `"`. The full set is not documented, and Microsoft says the block list is an
implementation detail that changes between releases. So the bound below assumes the worst documented form, six bytes
per escaped character.

- **Unescaped maximum** (both tokens at 8,192 characters, no escaped character): **16,887 bytes**.
- **Worst case** (every token character that may be escaped is escaped): about **98,737 bytes**. That is the refresh
  token at 49,152 bytes, the access token at about 49,082 bytes, and the rest as above.

**3. What a Key Vault representation can hold.** Microsoft documents a maximum of "25 KB" per secret, stored as
octets, with values accepted and returned as strings [A]. Whether "25 KB" means 25,000 or 25,600 bytes, and whether it
is measured on the string as sent, is not documented [C]. Assuming 25,000 bytes of value text, base64url text (four
characters for each three bytes) can carry at most **18,750 opaque bytes**. The proposed limit of **18,000 bytes**
(24,000 characters once encoded) keeps a margin of 1,000 characters.

**4. Can one adapter limit cover the whole connector domain?** **No.** The earlier claim is withdrawn.

- *Compatibility subset:* any value of at most 18,000 bytes. That always includes verifier material, the two
  owner-placed secrets, and token material whose two tokens together contain at most 222 characters that the encoder
  escapes (16,887 bytes plus at most 5 extra bytes per escaped character). Tokens made only of letters, digits, `-`,
  `_` and `.` need no escaping under the documented default encoder [B: to be confirmed by an offline test of the
  actual writer, not assumed]. Nothing in the official monday documentation establishes the alphabet of real monday
  tokens, so this plan does not rely on one.
- *What fails closed:* token material above 18,000 bytes. The adapter refuses it before sending anything, as
  `Unavailable`. Completion then deletes the name best effort, revokes the pair and reports a storage failure;
  renewal revokes the new pair and requires reauthorization. Such a connection could not be kept by the adapter [B].
- *Changing the boundary later:* splitting a value across several secrets, compressing it, or storing ASCII values
  without base64 are adapter-only changes. They would need an owner decision on S-3 but no production change. Storing
  ASCII text directly would raise the bound to about 25,000 bytes; still not the full domain. Changing how
  `MondayTokenMaterial` serializes, or the token bounds, would be a production-contract change under `src/` and a
  separate owner decision. It is **not** recommended merely to fit Key Vault.
- *A separate observation [B]:* the connector's worst-case write (about 98.7 KB) exceeds its own read limit (64 KiB).
  This exists with any store and is independent of Key Vault. It is recorded, not proposed for change here.

### Encoding and `contentType` [proposal]

Key Vault stores a value as text plus an optional `contentType` of up to 255 characters [A]. Proposed handling:

- **On set:** the adapter always writes base64url text without padding, with one exact Fully `contentType` (for
  example `application/vnd.wce.opaque-bytes+base64url;v=1`). Connector-written material therefore round-trips
  byte-for-byte whatever its bytes.
- **On get, recognized Fully `contentType`:** strict base64url decoding (no padding, no whitespace, canonical
  encoding). A value that does not decode, or decodes to more than 18,000 bytes, is `Unavailable`. **A malformed
  marked value never falls back to plain text.**
- **On get, absent `contentType`:** plain text, returned as its exact UTF-8 bytes, **only** for
  `wce-monday-client-secret` and `wce-monday-signing-secret`. For any other name it is `Unavailable`.
- **On get, unknown `contentType`** (any other value, including `text/plain`): `Unavailable`, for every name.
- Comparison is exact and ordinal. The adapter never guesses an encoding.

Plain text for every name, or only for the two fixed names:

- **Option A (every name)** would let any unmarked value under a token or verifier name reach the connector's readers.
  The readers would refuse a mismatched binding, but an unmarked value under a connector-written name can only come
  from something other than the adapter, and the adapter would be interpreting it.
- **Option B (two fixed names only)** is recommended. Connector-written names are always written marked by the
  adapter, so an unmarked value there means an outside write; refusing it fails closed. The owner can still place the
  client secret and the Signing Secret as plain text with ordinary tools. The cost is that a hand-placed value under
  any other name is unusable, which is intended. The placement instructions must say: no `contentType`, no trailing
  newline, visible ASCII only, because the connector's readers refuse anything else.

### Retry and ambiguity: scenario trace

What the documentation guarantees [A]: a set on an existing name creates a new version; a delete applies to the
whole secret, all versions, and cannot target one version; a deleted object is inaccessible to retrieval; creating an
object whose name is in the deleted state is refused. What it does **not** document [C]: any idempotency key or
conditional set; whether a request the client stopped waiting for is still applied; the relative ordering of two
requests in flight; read-after-write consistency; and the exact status and error code of each failure.

1. **A set commits but its answer is lost.** The adapter cannot tell this from a set that never arrived. Repository:
   every caller treats a non-`Succeeded` set as unknown and compensates (best-effort delete and revocation, or a
   failed start). Key Vault: nothing documented. Unknown: whether it committed [C]. A retry of the same bytes would
   either add a second identical version or create the first; both end in a readable value. Authority: unchanged,
   because no row points at the name until the caller's next step.
2. **A set that timed out completes after the adapter returned.** The caller has already compensated: on the
   completion and renewal paths it deletes the name; on the start path it does nothing and relies on expiry and
   cleanup. If the late set is applied after the delete, an active secret can exist under a name no row points at,
   holding a pair whose revocation was attempted but is not guaranteed [B]. If it arrives while the name is
   soft-deleted, the documented conflict should refuse it [A], but the actual ordering is unknown [C]. Authority: an
   orphaned name has none, because the database decides what is current; the residual risk is retention of
   credential material, not authority.
3. **A retry creates another Key Vault version.** The bytes are identical, a get returns the latest version, and a
   delete removes all versions [A]. Authority: unchanged; Fully never addresses Key Vault versions.
4. **A delete follows an ambiguous set.** If the set had committed, the delete moves the name to the deleted state; if
   it had not, the delete answers not-found. Either is reported as confirmed by `DeleteConfirmedAsync`. A set still in
   flight could land afterwards (scenario 2) [B, C].
5. **A delete reports not-found while an earlier request's outcome is unknown.** `NotFound` proves only that no active
   secret existed under the name when the vault processed that delete. It proves nothing about a request still in
   flight [B]. For the verifier cleanup this means a recorded deletion can, in that window, coexist with a later-landed
   verifier; the transaction is already closed, so the verifier can no longer be used [A].
6. **The process stops after a write but before the database points at it.** On the completion path, the new token
   version is left active with a valid pair, and nothing in the repository later finds and deletes it [A]; this is
   true of any durable store and not specific to Key Vault [B]. On the renewal path, the next caller finds the claim
   abandoned and adopts the stored pair or requires reauthorization [A]. On the start path, expiry and cleanup remove
   the verifier [A].
7. **An abandoned renewal is adopted from the claim's name.** The adopting reader gets the latest version, identical to
   any retried version. Because `NotFound` here makes the connection require reauthorization, a get must report
   `NotFound` only for a genuine absence of that secret, never for a refusal, a wrong address or a throttle [B]. If
   the set lands only after the reader decided not-found, the pair is orphaned and the connection already requires
   reauthorization [B, C]. Read-after-write consistency is not documented [C].
8. **Reauthorization after a storage failure.** A new authorization always uses a new version GUID, so no deleted name
   is ever rewritten and the documented deleted-name conflict cannot block it [A]. The fixed names are never deleted
   by code.

Conclusions: retrying a set inside the adapter is safe for authority, because names are unique and every attempt
sends identical bytes, but it only narrows the ambiguity window and never removes it [B]. One set attempt does not
remove ambiguity either. Orphaned credential material (scenarios 2, 6 and 7) cannot be prevented by the adapter. It
is a live-run cleanup concern for the owner, who would need to inventory and delete names that no row references.
That needs list access, which the runtime identity must not have.

### Proposed retry policy

Common rules:

- The adapter never converts a timeout, a connection failure or a cancelled request into proof that a write did not
  happen: such an outcome is `Unavailable` unless a later attempt settles it.
- Caller cancellation: if the caller's token is cancelled, `OperationCanceledException` propagates, and no further
  attempt is made. It is never mapped to a status.
- Internal timeout: each attempt has its own timeout of 5 seconds, never longer than what remains of the budget. An
  internal timeout is an ambiguous outcome. Because write paths pass `CancellationToken.None`, the budget is what
  bounds a stalled call.
- `Retry-After`: honored when it is no longer than 2 seconds and fits the remaining budget; otherwise the adapter stops
  and answers `Unavailable`. Without it, waits are 200 ms and then 800 ms.
- A refusal (400, 401, 403) or a conflict (409) is never retried and is `Unavailable`.
- `NotFound` requires a 404 that carries a well-formed Key Vault error body. A 404 without one (for example from a
  wrong host or path) is `Unavailable`. The exact error code to pin is not established by the documentation read [C]
  and is to be confirmed in a live run before being made stricter.

Per operation:

- **Get:** retryable on 429, 500, 502, 503, 504, a connection failure or an internal timeout. At most 3 attempts,
  total budget 10 seconds. 404 as defined above is `NotFound` and is not retried. A value is returned only from a 200
  whose body and `contentType` pass the encoding rules.
- **Set:** retryable on the same set, resending identical bytes to the same name. At most 3 attempts, total budget 15
  seconds. 200 is `Succeeded`. If any attempt was ambiguous and no later attempt returned 200, the answer is
  `Unavailable`. 409 is never retried: the adapter never recovers a deleted name to make a write succeed. A value
  above the size bound is refused before any request.
- **Delete:** retryable on the same set. At most 3 attempts, total budget 15 seconds. 200 is `Succeeded`; a 404 as
  defined above is `NotFound`, also when it follows an ambiguous earlier attempt. `NotFound` means only "no active
  secret under this name when the vault answered".

No caching: the adapter keeps no value and no absence, because `NotFound` after a rotation is a signal the credential
source depends on, and a cached Signing Secret would outlive an owner's rotation.

### Deletion semantics

Four distinct things:

1. **Active-namespace logical removal:** after a successful delete, an ordinary get of the name no longer returns a
   value. This is all the store contract observes.
2. **Key Vault soft deletion:** the vault keeps the deleted secret, with all its versions, outside the active
   namespace, inaccessible to retrieval, for the vault's retention interval (7 to 90 days, fixed at vault creation) [A].
3. **Recoverability:** during that interval a principal with the recover permission can bring the secret back [A].
4. **Irreversible purge:** a separate operation needing the purge permission; impossible before the interval ends
   when purge protection is on; otherwise performed by the service at the end of the interval [A].

What the adapter's statuses would mean (adapter semantics, needed offline):

- `DeleteAsync` → `Succeeded`: the vault accepted the delete; the name has left the active namespace and its value is
  no longer readable by an ordinary get. **It does not mean destroyed or irreversibly gone**; the value remains
  recoverable inside the vault unless and until it is purged.
- `DeleteAsync` → `NotFound`: when the vault processed the delete, no active secret existed under the name. It may
  never have been written, may already be soft-deleted or purged, or may still be written by a request in flight.
- `GetAsync` → `NotFound`: no active secret under the name when the vault answered.
- Consequently `DeleteConfirmedAsync` and the verifier cleanup's recorded deletion would mean "removed from ordinary
  get access", not destruction. Expiry metadata (`exp`, `nbf`) is not used, because Microsoft documents it as
  informational and a get still works outside that window [A].

Future real-vault decisions, deliberately **not** made here: the retention period; whether purge protection is on;
which principal may recover; which principal may purge; and the post-run cleanup procedure, including the inventory of
orphaned names from the scenarios above.

### Identity and least privilege (future live prerequisite only)

Microsoft documents that "Azure RBAC is an additive model, so your effective permissions are the sum of your role
assignments", that role assignments are inherited from parent scopes, and that they are "transitive for groups" [A].
The first version of this plan proposed the owner's own sign-in as the runtime identity while also describing a
separately privileged owner capability. Under an additive model that is not least privilege: the runtime would hold
whatever the owner holds through any other assignment, group or parent scope. That proposal is withdrawn.

Recommended boundary for a future live run (nothing is created or assigned now):

- **Runtime identity:** a dedicated principal used only by the harness, holding exactly
  `Microsoft.KeyVault/vaults/secrets/getSecret/action`, `.../setSecret/action` and `.../delete` through one custom role
  assigned at the scope of one dedicated vault. No other role assignment at that vault, its resource group, the
  subscription or a management group, and no group membership that grants Key Vault actions. No list
  (`readMetadata`), `update`, `recover`, `backup`, `restore` or `purge`. Before a run, its effective assignments are
  checked, not assumed. How that principal authenticates on the owner's machine without creating a new secret to store
  is itself an open decision.
- **Owner/operator identity:** places the client secret and the Signing Secret, runs the harness console, and performs
  the post-run inventory, recovery or purge through a distinct principal or path that the runtime never holds.
- **Infrastructure administrator:** creates the vault, defines the custom role and assigns roles. Microsoft warns that
  a principal able to write role assignments or, under the access-policy model, a control-plane Contributor can grant
  itself data access [A]; this identity is therefore kept apart from the runtime.

## Part 2 — Signed-webhook provisioning

No provisioning approach is approved or recommended as supported. D3C-1, `MondayScopePolicy`, `MondayQueryGuard`, the
existing JWT validation and the requirement for real authenticated webhook evidence all stay as they are.

### Repository facts [A]

- Deliveries are verified with the Signing Secret (`wce-monday-signing-secret`), `HS256` only, with expiry, an
  audience equal to the connection's own webhook address, and `accountId` bound to the 3B connection before any
  durable write. HS256 and the bare-or-`Bearer` header form are recorded 3C compatibility assumptions.
- `MondayScopePolicy` allows only `:read` scopes and refuses any grant with more. Fully always sends an explicit scope
  list, by default `boards:read me:read`.
- `MondayQueryGuard` keeps the connector's client to read documents. Nothing in the repository can create a webhook.

### Documentation facts [A]

- Webhooks reference: a JWT is present when the webhook was created "with an integration app token": "1. Create a
  monday app & add an integration feature to it 2. Generate an OAuth token for this app 3. Call the `create_webhook`
  mutation using the OAuth token". It is verified "against the app's Signing Secret". `create_webhook` and
  `delete_webhook` need `webhooks:write`.
- Integration authorization: board webhooks are signed with the Signing Secret; claims include `accountId`, `userId`,
  `aud` ("your app's endpoint URL"), `exp`, `iat` and `shortLivedToken`; no algorithm is named.
- OAuth guide: `scope` is optional, defaults to "the full scope list configured in your app", and "All requested scopes
  must match the list you configured in your app." The new OAuth flow page says nothing about scope subsets, consent
  changes or webhook signing.
- App versioning: scopes belong to a version; scope changes "require manual approval from users"; whether credentials
  differ by version is not stated.

### Four things kept apart

1. Scopes configured on the app version: they must include `webhooks:write` for any signed webhook to be created.
2. Scopes granted to Fully's read-only connection: they stay read-only and are checked by `MondayScopePolicy`.
3. A separately authorized, owner-operated provisioning credential: a different authorization of the same app,
   carrying `webhooks:write`, never stored as a Fully connection and never used by the connector's client.
4. The app and secret that sign a delivery: the app whose token created the webhook, and that app's Signing Secret.

### Unresolved questions [C]

- **U1:** can the authorize request ask for a strict subset of the scopes configured on the app? "Must match" does not
  say whether each requested scope must be configured or the lists must be equal.
- **U2:** can two authorizations of the same app, user and account with different scope sets coexist without
  invalidating each other?
- **U3:** is a webhook created with a token of the new OAuth flow signed the same way?
- **U4:** is the Signing Secret one per app, or per version?
- **U5:** is `aud` exactly the URL passed to `create_webhook`, and whose `accountId` and `userId` does the token carry?
- **U6:** the signing algorithm and the header form.
- **U7:** does the creation challenge carry a JWT?
- **U8:** does adding `webhooks:write` to the app affect an existing read-only grant?
- **U9:** if an owner-operated `webhooks:write` provisioning authorization creates the webhook and that
  authorization or token is then revoked, does the webhook stay active and keep producing signed deliveries, and is
  Fully's separate read-only authorization unaffected?

No answer is inferred. A personal API token, another app, another app version, or a webhook created in the monday UI
is not a substitute. If U1 turns out to require equal lists, every grant of that app would carry `webhooks:write`,
Fully would refuse it, and the work stops for an owner decision; the read-only policy is not weakened.

The candidate approach, for consideration only if U1, U2 and U9 are answered favorably: one app with an integration
feature and the new OAuth flow, configured with `boards:read`, `me:read` and `webhooks:write`; Fully keeps requesting
`boards:read me:read`; the owner makes a separate provisioning authorization, creates the webhook for the exact
approved board, event and connection address, and revokes that authorization; Fully verifies deliveries unchanged.

### Question for monday developer support (draft; not sent)

> We are building a private integration app on the new OAuth 2.1 flow (PKCE, expiring access tokens, rotating refresh
> tokens). Its OAuth tab is configured with `boards:read`, `me:read` and `webhooks:write`.
>
> 1. May the authorize request's `scope` name a strict subset of the configured scopes, such as
>    `boards:read me:read`, and does the token response then list only those? Your guide says "All requested scopes
>    must match the list you configured in your app": does "match" mean each requested scope must be configured, or
>    that the lists must be equal? Is the answer the same for the new OAuth flow?
> 2. If the same user in the same account authorizes the same app twice with two different scope lists, do both
>    access and refresh tokens stay valid, or does the later authorization replace the earlier one?
> 3. Does a webhook created by `create_webhook` with a new-flow access token send a JWT in `Authorization`? Is it signed
>    with the app's Signing Secret, with which algorithm, and is the header the bare token or `Bearer` and the token?
> 4. In that JWT, is `aud` exactly the URL passed to `create_webhook`, and are `accountId` and `userId` those of the
>    user whose token created the webhook?
> 5. Is the Signing Secret one value per app, or per app version?
> 6. Does the creation challenge carry the same JWT?
> 7. If scopes are later added or removed in the OAuth tab, what happens to tokens granted before?
> 8. If the authorization (or token) that created a webhook is later revoked, does the webhook stay active and keep
>    sending signed deliveries? Is a separate authorization of the same app by the same user, with read-only scopes,
>    unaffected by that revocation?

## Sequencing

**A. Before the offline secret-store adapter implementation:** owner decisions on S-1 to S-5 only. No vault, identity,
role, monday app, webhook provisioning, public ingress or database work is needed: the adapter is tested against an
in-memory fake transport and a fake token source.

**B. Before an outbound-only approved live Key Vault and OAuth test:**

- A dedicated vault created by the infrastructure administrator; the custom three-action role; a dedicated runtime
  principal with checked effective permissions; a distinct owner path for recovery and purge.
- Owner decisions on retention, purge protection, recovery and purge authority, and the post-run cleanup procedure.
- A real HTTP handler and a real token source, built and reviewed as their own slice, with redirects, ambient
  authentication and transport diagnostics excluded; the REST request shapes and API version re-verified.
- The monday app, workspace and exact approved runs; the client secret placed by the owner.
- The trusted operator identity (replacing `LocalDemo`); redirect and TLS compatibility of the loopback redirect.
- A dedicated standalone database and transport isolation; fresh privilege and row-level-security verification on it
  (with the R3B-2 rule); the role-check limitations addressed or accepted.
- An owner approval that names the run.

**C. Before inbound signed-webhook live acceptance:** everything in B, plus answers to U1, U2 and U9, an approved
provisioning step, the Signing Secret placed by the owner, and controlled public ingress for the webhook address (no
tunnel or public listener is approved today).

**D. Observations the live acceptance run itself exists to establish:** remote-revocation behavior; decision E (item
move and copy identity, cursor binding to authority) and the other MUST CONFIRM IN 3G items; the 3C assumptions and
U3 to U7 as observed; the `retry_in_seconds` path; Key Vault answers as observed (the not-found error body, the deleted
name conflict, a disabled secret, an oversized value, read-after-write behavior, a retried set); strict tenant-log
filtering under real traffic. None of these is required before the run that establishes it.

**E. Before Gate 3G and Gate 3 closure:** the observations in D recorded as real authenticated evidence; fresh tenant
and row-level-security evidence at Gate verification; the canonical, tenant-security and gate reviews; and the owner's
explicit closure decision.

## Proposed owner decisions (PROPOSED, NOT APPROVED)

### S-1 — Direction

- Current proposal: Key Vault-backed store as the direction, offline adapter and tests only.
- Correction: none.
- Recommended wording: "A Key Vault-backed `IMondaySecretStore` is the approved direction for real monday secrets in
  the 3G DEV/TEST harness. This approves an offline adapter and its tests only. It creates no vault, grants no
  permission and stores no real secret."
- Rationale: the port was designed for this ("for example Azure Key Vault"), names are valid Key Vault names, and the
  investigation found no reason to change direction.
- Owner approval needed before the next slice: yes.

### S-2 — Placement and packages

- Current proposal: in `tests/App.Harness.Monday`, REST over injected seams, no package, no `src/` change.
- Correction: none.
- Recommended wording: "The adapter is built in `tests/App.Harness.Monday` as a REST adapter over injected transport
  and token-source seams, with no new package and no change under `src/`. Production placement is decided later."
- Rationale: the dependency rules forbid a new `Azure.*` package and neither `App.Infrastructure` nor the connector
  can host the adapter today.
- Owner approval needed before the next slice: yes.

### S-3 — Value encoding and size

- Current proposal: marked base64url; unmarked read as UTF-8 for every name; values above 18,000 bytes refused; the
  bound described as covering everything the connector writes.
- Correction: the coverage claim is withdrawn; plain text is limited to the two fixed names; every `contentType` case
  is defined; a malformed marked value never falls back to plain text.
- Recommended wording: "Values the adapter writes are stored as base64url text with one exact Fully content type and
  must round-trip byte-for-byte. On read, a value with that content type is decoded strictly, and a value that does
  not decode is unavailable. A value with no content type is read as plain UTF-8 text only under
  `wce-monday-client-secret` and `wce-monday-signing-secret`; under any other name, and for any other content type, it
  is unavailable. The adapter refuses a value above 18,000 bytes before sending it. This bound does not cover every
  value the connector can produce: token material whose JSON form exceeds it fails closed, and changing the bound is a
  separate owner decision."
- Rationale: Part 1, "Serialized size" and "Encoding".
- Owner approval needed before the next slice: yes.

### S-4 — Meaning of a confirmed deletion

- Current proposal: accept soft deletion with 7-day retention and an owner purge after each run, with the harness
  holding neither recover nor purge.
- Correction: the proposal mixed adapter semantics with real-vault policy. It is reduced to adapter semantics; all
  real-vault choices move to stage B and are not decided here.
- Recommended wording: "For the Key Vault adapter, `Succeeded` from a delete means the vault accepted the delete and
  the name no longer answers an ordinary get; it does not mean the value is destroyed. `NotFound` means no active
  secret existed under the name when the vault answered. The verifier cleanup's recorded deletion therefore means
  removal from ordinary get access. Retention, purge protection, recovery and purge authority, and post-run cleanup are
  decided separately before any live run."
- Rationale: Part 1, "Deletion semantics"; the existing 2026-10-03 cleanup decision recorded "confirmed deletion",
  and its meaning under soft deletion is the owner's to state.
- Owner approval needed before the next slice: yes.

### S-5 — Failure mapping and retry

- Current proposal: only 404 is `NotFound`; everything else `Unavailable`; at most three attempts; no caching.
- Correction: operation-specific budgets, timeouts and `Retry-After` handling; `NotFound` only for a 404 with a
  well-formed Key Vault error body; explicit statement that retries narrow but never remove ambiguity.
- Recommended wording: "Only a 404 carrying a well-formed Key Vault error body is `NotFound`; every refusal, conflict,
  throttle, server error, timeout, connection failure, malformed answer and unresolved ambiguity is `Unavailable`. Get,
  set and delete each make at most three attempts, only for 429, 500, 502, 503, 504, connection failures and internal
  timeouts, with a 5-second attempt timeout and total budgets of 10 seconds for get and 15 seconds for set and delete.
  `Retry-After` is honored up to 2 seconds within the budget. A set is retried only with identical bytes, and a 409 is
  never retried. Caller cancellation propagates and is never a status. The adapter caches nothing. A timeout is never
  treated as proof that a write did not happen."
- Rationale: Part 1, "Retry and ambiguity" and "Proposed retry policy".
- Owner approval needed before the next slice: yes. The specific numbers are proposals the owner may change.

### W-1 and W-2 — Webhooks

- **W-1:** "No webhook provisioning approach is approved. The question to monday developer support may be sent by the
  owner. D3C-1, `MondayScopePolicy`, `MondayQueryGuard` and the existing JWT validation stay unchanged."
- **W-2:** "A personal API token, another app, another app version, or a webhook created in the monday UI is not a
  substitute, and real authenticated webhook evidence is not waived."
- Neither is needed before the offline adapter slice.

## Smallest next implementation slice (recommended; not started, not authorized)

**Offline Key Vault secret-store adapter in the harness**, only after S-1 to S-5 are decided.

Paths (new, none under `src/`): `tests/App.Harness.Monday/Secrets/` for the adapter, its codec and its token-source
seam; `tests/App.Integration.Tests/Harness/` for its tests; additions to `HarnessBoundaryTests`; a short record in
`docs/gates/gate-3-scope.md` and an addendum to the offline report. The unauthorized local draft under the same paths
must be dealt with by the owner before this slice begins; it is not a starting point.

Dependencies: none added.

Not in the slice: wiring into `HarnessProgram`; any change to `HarnessStandaloneGate`; a real handler or token source;
any Azure call; any webhook work.

Targeted acceptance tests:

1. Every byte sequence up to 18,000 bytes round-trips exactly, including invalid UTF-8.
2. Plain text without a content type round-trips for the two fixed names and is `Unavailable` for every other name.
3. An unknown content type, and a marked value that does not decode or decodes too large, are `Unavailable`; none
   falls back to plain text.
4. A value above the bound is refused with no request sent.
5. The serialized size of maximal token material, written by the actual `MondayTokenMaterial`, both unescaped and with
   escaped characters, is measured, and the result is checked against the documented subset.
6. The status mapping, row by row, including a 404 without a Key Vault error body as `Unavailable`.
7. Retries: bounded per operation, only for the retryable set, within the budget, honoring `Retry-After` only up to
   the cap; a 409 is never retried; a set retry sends identical bytes.
8. Ambiguity: an ambiguous first set followed by a 200 is `Succeeded`; an ambiguous first delete followed by a 404 is
   `NotFound`; an ambiguity never settled is `Unavailable`.
9. Caller cancellation propagates; an internal timeout is `Unavailable`.
10. Only the configured HTTPS vault origin and the get, set and delete requests are ever sent; no version is named.
11. Static guards: no list, deleted-secret, recover, backup, restore, purge or update request; no network handler;
    the default executable still has no usable secret store.
12. Sentinel tests with positive controls: no value, token or answer text reaches a log, diagnostic or exception.
13. The existing connector flows over the adapter and a fake vault that keeps deleted names reserved: authorization,
    renewal with adoption, verifier cleanup and disconnect.

Stop conditions: a change under `src/` or to the port contract would be needed; a package would be needed; a guard
would have to be weakened; a caller is found to depend on behavior Key Vault cannot give; or anything would require
contacting Azure, a vault or an identity provider.

## Sources

Repository at `884962f6a3e4f7ca820bd8159e7ac97fa2528cdd`: `src/App.Connectors.Monday/IMondaySecretStore.cs`,
`MondayStoredSecrets.cs`, `MondayTokenKeeper.cs`, `MondayConnectionAuthorizer.cs`,
`MondayConnectionCredentialSource.cs`, `MondayWebhookAuthenticator.cs`, `MondayScopePolicy.cs`, `MondayOAuthClient.cs`,
`MondayResponseParser.cs`, `MondayPkce.cs`; `src/App.Core/Connectors/ConnectorScopes.cs`; `docs/gates/gate-3-scope.md`;
`docs/gates/gate-3G-offline-report.md`; `docs/architecture/dependency-rules.md`.

Public documentation, read on 2026-10-05:

- monday: [Webhooks](https://developer.monday.com/api-reference/reference/webhooks),
  [Integration authorization](https://developer.monday.com/apps/docs/integration-authorization),
  [OAuth](https://developer.monday.com/apps/docs/oauth),
  [New OAuth flow](https://developer.monday.com/apps/docs/migrating-to-the-new-oauth-flow),
  [Developer Center](https://developer.monday.com/apps/docs/the-developer-center),
  [App versioning](https://developer.monday.com/apps/docs/app-versioning)
- Microsoft: [About secrets](https://learn.microsoft.com/en-us/azure/key-vault/secrets/about-secrets),
  [Soft-delete](https://learn.microsoft.com/en-us/azure/key-vault/general/soft-delete-overview),
  [Key Vault RBAC](https://learn.microsoft.com/en-us/azure/key-vault/general/rbac-guide),
  [Azure RBAC overview](https://learn.microsoft.com/en-us/azure/role-based-access-control/overview),
  [Set Secret](https://learn.microsoft.com/en-us/rest/api/keyvault/secrets/set-secret/set-secret),
  [Delete Secret](https://learn.microsoft.com/en-us/rest/api/keyvault/secrets/delete-secret/delete-secret),
  [Security permissions](https://learn.microsoft.com/en-us/azure/role-based-access-control/permissions/security),
  [System.Text.Json character encoding](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/character-encoding)

## Conclusion

Gate 3G remains IN PROGRESS — OFFLINE IMPLEMENTATION ONLY. Real acceptance is NOT PROVEN. Real execution and Gate
closure are NOT AUTHORIZED. This plan recommends; it decides nothing and starts nothing.
