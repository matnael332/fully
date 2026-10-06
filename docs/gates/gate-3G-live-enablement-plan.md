# Gate 3G — Live-enablement plan: secret storage and signed-webhook provisioning

This is a planning document. It records recommendations, not decisions: nothing in it is approved until the product
owner says so in their own words. When this plan was written (2026-10-05), S-1 to S-5 and W-1 to W-2 below were
**PROPOSED, NOT APPROVED**; S-1 to S-5 have since been approved (see "Current status" below), and W-1 and W-2 remain
**PROPOSED, NOT APPROVED**. The document implements nothing, authorizes nothing, and is not evidence of how monday or
Azure behaves.

## Current status (2026-10-06)

- **Gate 3G: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY.** Real acceptance is NOT PROVEN. Real execution is NOT
  AUTHORIZED. Gate closure is NOT AUTHORIZED.
- Current checkpoint: `main` == `origin/main` == `446fa5a1b1e77223d3eedfd89569c338dfda12eb` ("feat: checkpoint Gate 3G
  offline Key Vault adapter"). CI run 37409608267 passed with all three jobs successful (secret scan; build, verify and
  test; container images, Release build, no push). The offline harness and the offline Key Vault adapter are both
  integrated on `main`.
- S-1 to S-5 were approved by the product owner on 2026-10-05 exactly as defined in this plan, in the version with
  SHA-256
  `8a9ade1c5b1e4db227bc790f892268b48b859a16e88bfcdf5d06cebf1da19dc9` (the blob of this file at commit `446fa5a`). The
  record is `docs/gates/gate-3-scope.md`, section "3G — Offline Key Vault secret-store adapter (S-1 to S-5, approved
  2026-10-05)", together with the owner's clarification of 2026-10-06 that S-1 permits only the offline adapter and
  that D3G-1 stays fully in force. The text of Part 1, Part 2, "Sequencing", the S and W proposals and the offline
  slice is unchanged by later revisions of this file. On 2026-10-06 only the opening paragraph's status sentence, this
  status block, a dated heading, a dated note under "Smallest next implementation slice" and the Stage B section at
  the end were added or adjusted.
- The offline adapter slice that "Smallest next implementation slice" recommended is implemented and integrated
  (`tests/App.Harness.Monday/Secrets/`); its evidence is in `docs/gates/gate-3G-offline-report.md`, "Addendum — offline
  Key Vault adapter slice (S-1 to S-5)", including a full local suite of 1,783 of 1,783 on an isolated database.
- Stage B planning is complete and recorded in the last section of this file, "Gate 3G — Stage B Live Readiness
  Preparation". Its decisions B-1 to B-19 are **PROPOSED — NOT APPROVED**. No Stage B implementation has started.
- W-1 and W-2 remain PROPOSED, NOT APPROVED. U1 to U9 remain unresolved.

## Status and baseline (as of 2026-10-05, historical)

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

*Dated note, 2026-10-06: the heading above records the state of 2026-10-05. The slice was then authorized with S-1 to
S-5, implemented offline, and integrated on `main` at `446fa5a` (CI run 37409608267 passed). See "Current status".*

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

---

## Gate 3G — Stage B Live Readiness Preparation

Planning recorded on 2026-10-06 at baseline `446fa5a1b1e77223d3eedfd89569c338dfda12eb` (CI run 37409608267 passed).
This section is planning only. **Every decision B-1 to B-19 is PROPOSED — NOT APPROVED.** None is an owner decision,
and none may be read as one until the product owner approves it in their own words.

- **D3G-1 remains fully in force**, as clarified by the owner on 2026-10-06: it prohibits every real-credential adapter
  and every real connection to Azure or Key Vault.
- B-1 would require a future, explicit owner decision **before any code capable of using a real credential is
  authored**. Without it, no such code is written.
- **No Stage B implementation has started.** No vault, identity, role, assignment, monday app, database, certificate,
  credential or live run exists or is authorized.
- The planning session contacted no provider: no Azure, Key Vault, Entra, monday, OAuth or identity-provider call, and
  no network request.

Labels as in the rest of this plan: **[A]** a repository or documentation fact; **[B]** a design inference;
**[C]** provider behavior unknown until an approved live run.

### Objective

Stage B prepares everything a single future, separately approved, **outbound-only** live run needs: the Key Vault and
its identity, the real Key Vault and monday transports and the Azure token source, the monday app, the operator and
TLS setup, and a dedicated database. In that run the harness would contact Entra, the dedicated vault,
`auth.monday.com` and `api.monday.com`, and the owner's browser would load monday's authorize page. Nothing comes
inbound: no webhook, no Signing Secret, no public ingress. Those belong to Stage C.

### Existing reusable pieces [A]

- The offline Key Vault adapter (`tests/App.Harness.Monday/Secrets/KeyVaultSecretStore.cs`): get, set and delete only,
  one vault origin, API version `2025-07-01`, the S-3 to S-5 rules, an injected handler and an injected
  `IKeyVaultAccessTokenSource`. It is not wired into the harness.
- `GuardedProviderTransport`, which already passes only the connector's three fixed monday endpoints, and
  `HarnessSeams`, which already accepts a real `ProviderTransport` and `SecretStore`.
- The trusted tenant and user chain (`TrustedFlow`, `HarnessTrust`); the operator listener's safeguards (single-use
  capability, Host and origin checks, `__Host-` binding cookie with `SameSite=Lax`, fetch-metadata checks); a
  loopback-only HTTPS redirect; the PKCE cleanup loop at start-up and every five minutes.
- The role check (superuser, BYPASSRLS, table ownership) and `HarnessStandaloneGate`, whose approved-database list is
  empty and so acts as a code-level switch that only a reviewed change can lift.
- The tenant-filtered log policy, the closed-value diagnostics and the evidence readers.
- The accepted isolated-database mechanism (`POSTGRES_DB` override, migrated from zero by the fixture).
- `infra/bicep/modules/keyVault.bicep` (RBAC mode, retention and purge-protection parameters), `eng/infra.ps1 -Approve`
  and the R3B-2 re-assertion SQL.
- `IdentityProviderId.From(...)` is public, so the harness can use an honest provider key without a change under
  `src/`. `Azure.Identity` 1.21.0 is in the approved package set, but approved for `App.Infrastructure` only.

### Missing prerequisites, by area

**A. Dedicated Key Vault.**

- Purpose: hold the harness's monday secrets for DEV/TEST runs and nothing else.
- Why it must be dedicated [B]: token and verifier names are dynamic (`wce-monday-tokens-{tenant}-{connection}-{version}`,
  `wce-monday-pkce-{tenant}-{transaction}`), so a per-secret role assignment cannot be made in advance and the runtime's
  set and delete must apply to the whole vault. In the Dev environment vault that would let the runtime overwrite or
  delete the database administrator credentials.
- Names for Stage B: `wce-monday-client-secret` (placed by the owner), the token names and the PKCE names.
  `wce-monday-signing-secret` is not needed until Stage C and should not be placed earlier.
- One vault is enough for the first run if that run uses exactly one tenant.
- Unresolved tenant separation: separation inside the vault rests only on the tenant id embedded in each name; the
  client secret is app-global by design; the runtime could overwrite the client secret. It can already read it, so the
  extra impact is integrity or availability, not disclosure.
- To decide before creation: subscription and resource group (the Free Trial ends on 2026-10-27, as recorded in
  `INIT.md`); retention, which cannot change after creation; purge protection, which cannot be turned off once on;
  network rules; audit logs; definition as Bicep (invariant 69).

**B. Runtime identity.**

- Recommended: a dedicated Entra app registration and service principal with a **certificate** credential and no
  client secret. The private key is non-exportable in the owner's `CurrentUser\My` store, ideally TPM-backed. The
  certificate is valid only for the run window and is removed afterwards. The principal owns nothing, holds no
  Graph permission or directory role, belongs to no group, and has exactly one role assignment.
- It does store sensitive material locally: a private key, non-exportable and short-lived.
- Alternatives: a client secret (rejected: a long-lived bearer secret on disk); a managed identity (not usable for this
  run: it works only inside Azure, and the loopback OAuth design needs the owner's machine; federated credentials have
  no OIDC issuer on a workstation); a dedicated non-owner Entra user signing in interactively with MFA (nothing stored
  on disk, but a user principal subject to Conditional Access and groups, needing a public-client flow, effectively
  operated by the owner).
- Verifying effective permissions, by the owner at run time: `az role assignment list --assignee <appId> --all
  --include-inherited --include-groups`, plus checks that the principal has no group membership, no app-role
  assignment and owns no app.

**C. Key Vault RBAC.**

- Runtime: one custom role with exactly the DataActions `Microsoft.KeyVault/vaults/secrets/getSecret/action`,
  `Microsoft.KeyVault/vaults/secrets/setSecret/action` and `Microsoft.KeyVault/vaults/secrets/delete`, no control-plane
  Actions, assignable at the dedicated vault only. Whether get also needs `readMetadata` is unknown [C]: if the live run
  shows it does, the run stops, and `readMetadata` (which permits listing) is never added silently.
- Owner and administrator capability, never held by the runtime: inventory (`secrets/readMetadata/action`, including
  deleted-secret metadata), recover (`secrets/recover/action`), purge (`secrets/purge/action`), and role assignment
  (`Microsoft.Authorization/roleAssignments/write`, `roleDefinitions/write`).
- The runtime must also not hold `Microsoft.KeyVault/vaults/write`: it can switch the vault to the access-policy model
  and so open a path to granting itself data access.
- The recover, purge and readMetadata action names are taken from Microsoft's RBAC documentation as read in planning;
  they are to be re-verified against the published documentation before the IaC slice is approved.

**D. Retention and purge.** See B-3 to B-6.

**E. Real HTTP handler and token source.**

- Placement: harness only, under `tests/App.Harness.Monday/Live/`: one `SocketsHttpHandler` factory, used for exactly
  three destinations (monday behind the existing guard, the vault origin, and `login.microsoftonline.com/{tenant}`); a
  host-allowlist handler for the vault and Entra paths; an `EntraCertificateTokenSource` implementing
  `IKeyVaultAccessTokenSource`.
- Pinned handler settings: `AllowAutoRedirect = false`, `UseCookies = false`, `UseProxy = false`, `Credentials = null`,
  `PreAuthenticate = false`; default certificate validation with no callback; a connect timeout of at most five
  seconds; bounded response sizes.
- No package is necessary [B]. The token source can be hand-written: an RFC 7523 client-credentials request with a
  certificate-signed assertion (`x5t#S256`), scope `https://vault.azure.net/.default`, the token cached in memory until
  five minutes before expiry, and `null` returned on failure, which the adapter answers as unavailable. The
  alternative, `Azure.Identity` `ClientCertificateCredential`, reaches the harness only transitively and would still
  need an owner decision. `DefaultAzureCredential` and every ambient credential chain must be forbidden by a guard,
  because one could pick up the owner's own sign-in.
- Logging: no `IHttpClientFactory`, no `System.Net` event listener, no exporter; exceptions by type only; a bearer token
  never crosses from one destination's handler to another's.
- Enabling: no configuration switch, which keeps the existing guard. Real seams are composed only on the standalone
  path, which still requires an owner-approved database name compiled into `HarnessStandaloneGate`, as `HarnessProgram`
  states: what lifts a fail-closed default is a reviewed change, not configuration.
- Guards that must evolve: the harness guards today forbid `SocketsHttpHandler`, `Dns.`, `new …Handler(` and every
  `live`, `enable`, `allow` or `real` setting key. Each blanket ban may only be replaced by an equal-or-stricter pinned
  allowance, with mutation evidence (B-11).

**F. monday app and workspace.**

- The owner creates a private app with the **New OAuth Flow**, configured scopes **exactly `boards:read me:read`**
  (exactly what Fully requests, which avoids U1 for Stage B), and the redirect address exactly
  `https://127.0.0.1:{fixed port}/operator/callback`.
- A dedicated test account or workspace, one board with a status column and synthetic content only, and a defined
  monday test user mapped to the configured canonical user (D3B-1).
- The client secret is placed by the owner as plain text with no content type, no trailing newline and visible ASCII
  only (S-3), through the portal rather than a command that lands in shell history.
- The Signing Secret belongs to Stage C only. `webhooks:write` is not added before U1, U2, U8 and U9 are answered.

**G. Operator identity.**

- What it must prove: that the person at the console and the browser is the owner acting as the configured canonical
  user, for the configured tenant (R3B-1). The delegated monday identity itself is proven by monday's `me` answer
  (D3B-1).
- What may stay local for DEV/TEST: the console, loopback, capability and browser-binding model, and the tenant and
  user taken from server-side configuration.
- What must be replaced before live execution: the `LocalDemo` provider key. Labelling a real delegation "local/demo"
  conflicts with the intent of PRD v1 §16 and invariant 56 [B]. An honest harness-only key through
  `IdentityProviderId.From` needs no change under `src/`.
- Boundary: an explicit owner record that this model is DEV/TEST only and never the production trust model; Gate 7
  Entra SSO replaces it for production.

**H. Loopback redirect and TLS.**

- Callback: exactly `https://127.0.0.1:{fixed, non-default port}/operator/callback`. HTTPS is mandatory: the production
  `MondayOAuthOptions` refuses anything else, so plain-HTTP loopback would need a change under `src/`, which is a stop
  condition.
- Certificate: an ephemeral self-signed certificate generated in memory for each run, with SAN `IP:127.0.0.1` and a
  validity of at most one day; its SHA-256 fingerprint printed at the console; the browser warning accepted once by the
  owner; nothing added to any trust store (D3G-5).
- Testable offline: certificate generation, the standalone wiring, redirect validation and the route checks.
- Provable only in the live run [C]: that monday accepts an HTTPS IP-literal redirect with a port; that the browser
  keeps the `__Host-` cookie after the certificate warning is bypassed; that the cross-site callback carries the
  binding; the real callback `status` and `iss` values (3B open items 5 and 6); and that monday login and consent fit
  in the ten-minute transaction.

**I. Standalone database and transport.**

- A persistent dedicated database on the local server (for example `wce_harness_live_<date>`), migrated from zero by the
  migrator, given only the database-scoped grants of `infra/local/postgres/init/10-runtime-role.sh`, re-asserted under
  R3B-2, and seeded with one fresh tenant, user and connection by a reviewed owner-run script outside the harness (the
  harness may hold no administrative path, and the `tenants`, `users` and `connections` rows must exist).
- Fresh RLS and privilege evidence taken on that database; the shared suite database is never live evidence.
- Role-check limitation needing owner acceptance: `workcontinuity_app` is a cluster-wide role shared with the suite
  database, its password included; the check does not detect membership in a privileged role. Either extend it with a
  recursive `pg_auth_members` check (a harness change) or accept the gap.
- Transport: the shared local emulator, with no concurrent suite run, a tenant GUID never used before, the consumer
  limited to its own session, and a pre-run check that the session is empty.
- Alternatives: a dedicated PostgreSQL container and a dedicated emulator instance; each changes `infra/local` and
  needs its own approval.

### Proposed owner decisions B-1 to B-19 (each PROPOSED — NOT APPROVED)

Each entry gives the question, the recommendation, the alternatives, the practical and security effect, why it is
needed now, what approval would enable, what stays prohibited, and whether an existing decision changes.

**B-1 — Stage B preparation boundary. PROPOSED — NOT APPROVED.**

- Question: may offline Stage B slices author code capable of using a real credential (the real handler and token
  source) and IaC, without executing any of it?
- Recommended: yes, only for slices B1 to B5 below, under the harness, never executed against a real endpoint.
- Alternatives: (a) no, so only slices with no real-credential code proceed (operator key, TLS certificate, role
  check); (b) no Stage B code until a live authorization.
- Effect: mirrors decision B ("code capable of calling … is not itself a real call"). Security: every guard is replaced
  only by an equal-or-stricter one.
- Why now: B1 and B2 are blocked by D3G-1 without it.
- Enables: B1 and B2.
- Still prohibited: any credential; any Azure, Key Vault, Entra or monday contact; any deployment.
- Changes an existing decision: **yes — a narrow, explicit supersession of D3G-1 as clarified on 2026-10-06.** Until
  the owner decides it, D3G-1 stays fully in force.

**B-2 — Dedicated vault. PROPOSED — NOT APPROVED.**

- Recommended: one new vault in its own resource group, for DEV/TEST harness use only; defined by a separate Bicep entry
  point under `infra/bicep/` that `main.bicep` never deploys; deployed only through an `-Approve` path; RBAC mode; audit
  logs on; a public endpoint with an IP allowlist for the owner's address during the run window. It holds the client
  secret, the token names and the PKCE names; no Signing Secret before Stage C; one tenant per run.
- Alternatives: the Dev environment vault (rejected, area A); RBAC only with no IP rule; two vaults, a get-only one for
  owner-placed secrets and one for dynamic names (stronger, but needs a routing store in the harness).
- Security: invariants 68 and 69 and System Architecture v1 §8 honored. Enables: B1 authoring. Still prohibited:
  creating the vault. Changes an existing decision: no.

**B-3 — Soft-delete retention. PROPOSED — NOT APPROVED.**

- Recommended: **7 days.** Deleted credential residue stays recoverable for the shortest time; unique GUID-based names
  mean reserved deleted names never block a test.
- Alternative: 90 days, a longer forensic window and a longer recoverable residue.
- Note: fixed at creation. Closes part of the S-4 deferral. Changes an existing decision: no.

**B-4 — Purge protection. PROPOSED — NOT APPROVED.**

- Recommended: **off** for this DEV/TEST vault, so the owner can purge residue right after a run and the vault can be
  torn down cleanly; this matches the documented foundation default of `keyVault.bicep`.
- Alternative: on. It protects against a malicious purge, but residue cannot be destroyed before retention ends, the
  vault name stays reserved, and it cannot be undone.
- Security: with it off, only a principal holding purge can destroy secrets, and the runtime holds none. Closes part of
  the S-4 deferral. Changes an existing decision: no.

**B-5 — Recover and purge authority. PROPOSED — NOT APPROVED.**

- Recommended: no standing holder. The owner takes a just-in-time, vault-scoped data role (for example Key Vault
  Secrets Officer) only for secret placement and post-run cleanup, removes it afterwards, and records both. The runtime
  never holds it.
- Alternative: a standing owner custom role, simpler but always on. Closes part of the S-4 deferral.

**B-6 — Post-run cleanup and orphaned names. PROPOSED — NOT APPROVED.**

- Recommended procedure: (1) disconnect through the harness, which revokes at monday; (2) stop the harness; (3) the
  owner takes the just-in-time role and inventories active and deleted names; (4) compares them with the database's
  current version pointers and open transactions; (5) treats every unreferenced active name as an orphan, deletes it,
  then purges every harness-written name; (6) the test user removes the app authorization in monday as a backstop,
  whose effect on tokens is recorded as observed [C]; (7) removes the just-in-time role and the runtime certificate
  credential; (8) drops the standalone database once the evidence is captured; (9) records counts and metadata only,
  never a value.
- Closes the remaining S-4 deferral and the orphan concern of Part 1 (scenarios 2, 6 and 7).

**B-7 — Runtime identity. PROPOSED — NOT APPROVED.**

- Recommended: the certificate-credential service principal of area B.
- Alternatives: a dedicated interactive Entra user; a client secret (rejected); a managed identity (not possible from
  the owner's machine).
- Note: System Architecture v1 §8 asks for managed workload identities "where possible"; here it is not possible, and
  the reason is recorded.

**B-8 — Runtime RBAC. PROPOSED — NOT APPROVED.**

- Recommended: the exact three-DataAction custom role of area C, one assignment at vault scope, and an
  effective-permission check before every run. If the live run shows get needs more, stop and decide; never widen
  silently.

**B-9 — Administration separation and how identities are created. PROPOSED — NOT APPROVED.**

- Recommended: the owner's account acts as infrastructure administrator. The vault, custom role and assignment are
  created through Bicep; the Entra app and certificate credential through a reviewed, idempotent `eng/` script gated
  by `-Approve`, because Entra objects are outside the current Bicep pattern.
- Alternative: the Microsoft Graph Bicep extension. Invariant 69 is fully met only with that alternative; the script
  route needs the owner's acceptance.

**B-10 — Token source and transport approach. PROPOSED — NOT APPROVED.**

- Recommended: a hand-written certificate assertion with no package; an explicit credential only, never an ambient
  chain; a pinned `SocketsHttpHandler` per destination; enabled only by a reviewed code change (an approved database
  name), never by configuration.
- Alternative: `Azure.Identity` `ClientCertificateCredential` in the harness, which is its own package decision under
  D3G-2 and the dependency rules.

**B-11 — How guards evolve. PROPOSED — NOT APPROVED.**

- Recommended: a blanket ban may be replaced only by an equal-or-stricter pinned allowance in exactly named files, with
  targeted mutation evidence. No existing guard is deleted.

**B-12 — Operator identity. PROPOSED — NOT APPROVED.**

- Recommended: an honest harness provider key (for example `wce-dev-harness`); the current console and loopback model
  kept, explicitly DEV/TEST only; an owner attestation per run; Gate 7 replaces it for production.
- Closes open question 3 of the offline report and settles the operator-identity row of the 3G contradiction audit.

**B-13 — Loopback TLS. PROPOSED — NOT APPROVED.**

- Recommended: the ephemeral in-memory certificate with a printed fingerprint, a one-time browser warning, a fixed
  callback port and no trust-store change.
- Alternative: a trusted `dotnet dev-certs` certificate, which changes machine trust and is excluded by D3G-1 unless
  superseded. Closes open question 4 of the offline report.

**B-14 — Standalone database. PROPOSED — NOT APPROVED.**

- Recommended: as in area I, on the local server, with the membership check added to the role check and the cluster-wide
  role limitation accepted.
- Alternative: a dedicated PostgreSQL container.
- Changes an existing decision: **yes — it supersedes the D3G-4 deferral** of a persistent standalone database. Closes
  open questions 2 and 5 of the offline report.

**B-15 — Transport isolation. PROPOSED — NOT APPROVED.**

- Recommended: the shared emulator under the conditions of area I. Alternative: a dedicated emulator instance.

**B-16 — monday app for Stage B. PROPOSED — NOT APPROVED.**

- Recommended: as in area F: scopes exactly `boards:read me:read`, no `webhooks:write`, no Signing Secret, a dedicated
  test workspace with synthetic content. Changes an existing decision: no; D3C-1 is kept.

**B-17 — Scope of the first live run. PROPOSED — NOT APPROVED.**

- Recommended: run 1 covers the OAuth and Key Vault lifecycle only: authorize, code exchange, `me`, storage, verifier
  cleanup, renewal if it occurs within the window, disconnect and revocation. A separate named run 2 covers outbound
  board reads (initial sync and reconciliation of one board, which also yields evidence for the cursor question of
  decision E). Each run has abort criteria.

**B-18 — Live evidence and logging. PROPOSED — NOT APPROVED.**

- Recommended: the strict tenant log policy is kept; only closed values, lengths and statuses are captured, never a
  token, code, state, verifier or value; Key Vault audit logs serve as evidence of which operations ran.

**B-19 — Approval format of each named run. PROPOSED — NOT APPROVED.**

- Recommended: one approval per real step, naming the date window, database, vault, principal, client id, operations
  and abort criteria.

### Recommended slices B0 to B5 (not started, not authorized)

- **B0 — Record the approved B decisions.** A Stage B section in `docs/gates/gate-3-scope.md`. Offline, documentation
  only. Approval: the B decisions themselves.
- **B1 — IaC authoring.** Bicep for the vault, custom role, assignment and audit logs, and the Entra and certificate
  script. Local Bicep compile only; static tests pin exactly three DataActions, zero Actions, vault scope and the B-3
  and B-4 parameters. Offline. Approval: B-1, B-2 to B-4, B-8, B-9. Stop if Bicep cannot express a vault-only assignable
  scope.
- **B2 — Real transport and token source.** Under `tests/App.Harness.Monday/Live/`: the handler factory, destination
  guard and Entra certificate token source, tested only against fake inner handlers, with guards replaced under B-11
  and mutation evidence. Offline. Approval: B-1, B-7, B-10, B-11. Stop if a package becomes necessary, a change under
  `src/` would be needed, or a guard would have to be weakened.
- **B3 — Standalone database and transport preparation.** The membership test in the role check; a reviewed seed
  script; the procedure for fresh RLS and R3B-2 evidence. `HarnessStandaloneGate` stays empty. Offline code; provisioning
  the persistent local database itself is a separate local approval. Approval: B-14, B-15.
- **B4 — Operator and TLS.** The honest operator key; the ephemeral certificate and its fingerprint; the standalone
  wiring of the certificate and the seams. Offline. Approval: B-12, B-13.
- **B5 — Live-run preparation package.** A run book: preflight (effective permissions, privileges, empty session),
  abort criteria, an evidence template and the cleanup procedure. Offline, documentation only. Approval: B-6, B-17 to
  B-19.

Each code slice requires the full local suite, both repository verifiers, the frozen-document hash check, the three
review agents and targeted mutation evidence for new or changed guards. If B-1 is not approved, B4 and the code part
of B3 can still proceed, because neither involves code capable of using a real credential. After B5 the real boundary
begins, one approval per step: deploy B1; create the principal and its certificate; create the monday app; the owner
places the client secret; provision the database; add its name to the gate; run; clean up.

### What remains offline

All of B0 to B5; every test against fake vault, Entra and monday handlers; certificate generation; the role check and
the seed script (written, not run); Bicep compilation; the run book.

### What crosses the real-execution boundary

Deploying the vault, role or assignment; creating the Entra app, service principal or certificate credential; any token
request; any vault request; creating or configuring the monday app, its redirect address included; placing the client
secret; provisioning or seeding the persistent database; adding a database name to `HarnessStandaloneGate`; running
the standalone harness; browser authorization; any monday token, revoke or API call; post-run inventory and purge.

### Provider unknowns and their sequencing

1. **Before Stage B implementation:** B-1 (the D3G-1 supersession), B-7, B-10, B-11, B-12, B-13. The production
   placement of S-2 stays undecided, which does not block the harness.
2. **Before the outbound-only live run:** B-2 to B-6, B-8, B-9, B-14 to B-19; the S-4 deferrals (closed by B-3 to B-6);
   the five open questions of the offline report (closed by B-18, B-14, B-12, B-13 and B-14).
3. **Only before Stage C webhook acceptance:** answers to U1, U2, U8 and U9; W-1 and W-2; an approved provisioning step;
   placement of the Signing Secret; public ingress; 3C open items 9 to 15 and U3 to U7, observed then.
4. **Learned during the real acceptance runs:** remote-revocation behavior; decision E (item move and copy identity;
   cursor binding, possibly from run 2); 3B open items 5 to 8 (`status`, `iss`, token lifetimes and refresh reuse, the
   `Authorization` form); 3A open item 1 (`retry_in_seconds`); 3D open items 16 to 21; the 3F paging items; the Gate 2
   carried-forward items 4 and 5 (clock skew, the 1,024-character limit); real token size against the S-3 bound; Key
   Vault behavior (the 404 body, the deleted-name 409, read-after-write, whether `getSecret` alone is enough); monday's
   acceptance of the loopback redirect and the browser's cookie behavior.
5. **Only before Gate 3G closure:** every observation above recorded as real authenticated evidence; real
   authenticated webhook evidence (W-2: not waived); a real change reaching the Snapshot and a missed event repaired;
   fresh tenant and RLS evidence; the three reviews; CI on the committed tree; decision F for the Premium row; the
   owner's closure decision.

### Contradiction check

No contradiction with PRD v1, System Architecture v1, the 83 Architecture Invariants or Development Gates v1:

- System Architecture v1 §8 and invariant 53: Key Vault is used; a managed identity is not possible off Azure, so B-7
  stays within "where possible".
- Invariants 56 and 68: a dedicated DEV/TEST vault and database, and B-12 removes the demo label from a real
  delegation.
- Invariant 69: met for the vault and role; the Entra app script needs the owner's acceptance (B-9).
- Invariants 44, 45, 52 and 59: one fresh tenant per run, server-side trust, the strict log policy.
- R3B-1, R3B-2, D3C-1, S-1 to S-5 and decisions A, B and F: compatible.

Conflicts with owner decisions, not with canonical rules, left for the owner to resolve: D3G-1 as clarified on
2026-10-06 forbids any real-credential adapter, so B1 and B2 need B-1 (smallest safe alternatives if refused: only the
B3 code and B4, or planning only); D3G-4 defers a persistent standalone database, so it needs B-14; D3G-5 forbids
machine-trust changes, which B-13 respects. The check was made by the planning session itself; the canonical-auditor,
tenant-security-reviewer and gate-verifier agents were not run on this plan.

### Recommended immediate next owner decision

**B-1**, the bounded Stage B carve-out from D3G-1. B1 and B2 depend on B-1. If it is approved, B-7, B-10 and B-11
together would allow B2 to start. Without it, only B4 and the code part of B3 can begin.

Status at the end of Stage B planning: **Gate 3G: IN PROGRESS — OFFLINE IMPLEMENTATION ONLY. Real acceptance: NOT
PROVEN. Real execution: NOT AUTHORIZED. Gate closure: NOT AUTHORIZED. B-1 to B-19: PROPOSED — NOT APPROVED. No Stage B
implementation has started.**
