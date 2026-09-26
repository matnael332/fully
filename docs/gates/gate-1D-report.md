# Gate 1D Report — Database Foundation

## Scope

Gate 1D establishes the PostgreSQL operational data foundation: controlled migrations, tenant-aware
persistence, storage-level tenant isolation, pgvector availability, durable inbox and outbox storage,
and base persistence abstractions. It adds no business behaviour — no monday logic, no event
pipeline, no Service Bus publishing, no Work Memory, no AI, no UI, no authentication.

## Database stack

| Component | Version | Notes |
|---|---|---|
| PostgreSQL | 17.11 | Local image `pgvector/pgvector:pg17` |
| pgvector | 0.8.6 | Enabled by migration |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.3 | Stable |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.4 | Stable, design-time only (`PrivateAssets=all`) |
| `dotnet-ef` | 10.0.4 | Pinned local tool in `.config/dotnet-tools.json` |

No preview package was required. The Design package is held at 10.0.4 rather than the newest 10.0.12
because the Npgsql provider 10.0.3 is built against EF Core 10.0.4; taking the newer one made MSBuild
resolve two EF Core assembly versions and emit `MSB3277`, and an EF provider is coupled tightly enough
to EF internals that crossing patch bands is not worth the risk.

## The local PostgreSQL image changed, and why

`postgres:17-alpine` ships **no** vector extension files, so `CREATE EXTENSION vector` cannot
succeed on it — verified directly by listing the image's extension directory. The local image is now
`pgvector/pgvector:pg17`, which is the same PostgreSQL 17.11 with pgvector built in.

This is a local-image change only. The production target remains **Azure Database for PostgreSQL
Flexible Server**, where pgvector is an allow-listed extension enabled per server rather than a
bundled binary. The documented difference is therefore: locally the extension ships with the image;
in Azure it is enabled on the server. The migration statement (`CREATE EXTENSION IF NOT EXISTS
vector`) is identical in both.

Because the image changed, an existing local data volume must be recreated once:
`./eng/db.ps1 reset`.

## Two database roles, and why row-level security needs them

PostgreSQL row-level security is bypassed **unconditionally by any SUPERUSER**, and by a table owner
unless the table is set to `FORCE ROW LEVEL SECURITY`. `POSTGRES_USER` is a superuser. Had the
application connected as it, every policy in this Gate would have been silently ineffective and every
isolation test would have passed for the wrong reason.

| Role | Used by | Attributes |
|---|---|---|
| `workcontinuity` | Migrations and system maintenance only | Owner, SUPERUSER, BYPASSRLS |
| `workcontinuity_app` | All normal Web and Worker access | NOSUPERUSER, NOBYPASSRLS, not the owner |

The runtime role is created by `infra/local/postgres/init/10-runtime-role.sh` on first database
initialization, **not** by a migration, because a migration is committed and replayed everywhere and
must never contain a credential. Its password comes from the untracked `.env`. `ALTER DEFAULT
PRIVILEGES` in that script means tables created later by the migration are granted to the runtime role
automatically, so a new table is never accidentally unreachable or over-permissioned. The migration
additionally issues explicit grants, guarded by a `pg_roles` check so the schema still migrates on a
database where the role does not exist.

A test asserts that `workcontinuity_app` holds neither SUPERUSER nor BYPASSRLS. Without it, the rest
of the isolation suite would be meaningless.

## How the tenant reaches the database

`TenantScopedDatabase` is the only persistence entry point available to normal code. Per call it:

1. reads the server-resolved `ITenantContext`, and **returns a failure without opening a transaction**
   if no tenant is resolved;
2. begins a transaction;
3. executes `SELECT set_config('app.tenant_id', @tenant_id, true)`;
4. runs the caller's work, saves, and commits.

Three properties follow from that design:

- **No pool leakage.** The third argument to `set_config` makes the setting *transaction-local*.
  PostgreSQL discards it when the transaction ends, so a connection returned to the pool carries no
  tenant. This is the failure mode that makes naive implementations unsafe: a plain `SET` would leave
  the value on the physical connection for whoever borrows it next.
- **No injection.** The tenant travels as a bound parameter, never as interpolated SQL.
- **Fail closed.** Policies compare `tenant_id` against
  `nullif(current_setting('app.tenant_id', true), '')::uuid`. An absent setting yields NULL, so the
  comparison is never true: the policy matches no rows rather than falling back to "all rows".

`ITenantScopedDatabase` has **no overload that accepts a tenant**, so a caller cannot choose which
tenant to act as; asserted by test.

## Privileged access is isolated, not general

`DatabaseMigrator` is the only elevated path. It exposes exactly `ForConnection`, `MigrateAsync` and
`GetStatusAsync`, and its `AdministrativeDbContext` declares **no `DbSet`**, so it can operate on the
schema but cannot read or write a tenant row. It is registered by `AddDatabaseMigrator`, which the Web
host never calls, so request handling cannot resolve an administrative connection even by asking. No
public member anywhere in Infrastructure disables row-level security or offers cross-tenant access;
asserted by a reflection test over the whole assembly.

## Schema

Four tables, deliberately minimal. Nothing was created for a feature that no Gate needs yet — no
work memory, AI insight, brief, action, metrics or audit persistence.

| Table | Purpose |
|---|---|
| `tenants` | `tenant_id`, `status`, `created_at_utc` |
| `users` | `user_id`, `tenant_id`, `identity_provider`, `provider_subject`, `is_active`, `created_at_utc` |
| `raw_receipts` | `receipt_id`, `tenant_id`, `connection_reference`, `source_system`, `source_event_id`, `schema_version`, `received_at_utc`, `payload` (jsonb), `payload_fingerprint`, `processing_state` |
| `outbox_messages` | `outbox_message_id`, `tenant_id`, `message_type`, `payload` (jsonb), `occurred_at_utc`, `correlation_id`, `published_at_utc` |

### One `users` table, justified

Canonical user and tenant membership share one table. The MVP has no user belonging to two
employers, so a separate membership table would model a relationship no Gate needs; splitting it later
is a migration, not a redesign. Roles are **not** persisted: authorization, including role and
resource permission, belongs to the security Gate, and storing roles now would be speculative.

There is no email column anywhere. Email is mutable, reassignable and provider-controlled, so it is
never an identity key. The lookup key is `(tenant_id, identity_provider, provider_subject)`, unique so
one provider identity cannot map to two canonical users in a tenant.

### Deduplication

`ux_raw_receipts_tenant_connection_source_event` is unique on
`(tenant_id, connection_reference, source_event_id)` **filtered to `source_event_id IS NOT NULL`**.
PostgreSQL treats NULLs as distinct, so an unfiltered unique index would silently permit unlimited
duplicates among rows without a provider event id; the filter makes that behaviour deliberate rather
than accidental. `payload_fingerprint` exists only to flag duplicate *candidates* and is never the
authoritative key, because two genuinely distinct events can be byte-identical.

### Retention foundation

`received_at_utc` is the age basis for the future rolling 30-day purge of raw work payloads, and it is
indexed so that purge will not table-scan. No purge job was implemented. Security audit retention
remains a separate lifecycle and is deliberately not served from this table.

## Migrations

Two migrations, both committed with their designer files and model snapshot:

1. `20260926213721_InitialGate1DSchema` — tables, keys and indexes.
2. `20260926213758_TenantIsolationAndVectorExtension` — `CREATE EXTENSION vector`, then
   `ENABLE`/`FORCE ROW LEVEL SECURITY` and one isolation policy per tenant-owned table, then guarded
   runtime-role grants.

`Down` on the second migration drops the policies and disables RLS but deliberately leaves the
extension in place: dropping an extension cascades to dependent objects, which is a destructive side
effect a schema rollback must not have.

### Commands and startup behaviour

`./eng/db.ps1 migrate | status | reset`. `migrate` applies pending migrations only and never drops or
recreates anything. `reset` destroys the local volume by its explicit name and is for local
development only.

In the local stack, migration runs as a **one-shot `db-migrator` compose service** that must complete
successfully before Web and Worker start. That keeps two instances from racing to migrate one schema
and mirrors how a deployment applies migrations as an explicit step. `AutoMigrateOnStartup` exists and
defaults to **false**: for the Human Pilot and production, schema change remains an explicit
deployment responsibility, and a running application never decides on its own to alter a shared
schema. `./eng/dev.ps1 check` now also verifies the migrator exited 0, so a failed migration cannot
present as a healthy environment.

## Verification (executed 2026-09-27)

| Check | Result |
|---|---|
| `dotnet restore` | PASS |
| `dotnet build -c Debug` | PASS — 0 warnings, 0 errors |
| `dotnet test` (whole solution) | **PASS — 230 tests, 0 failed, 0 skipped** |
| Existing Gate 1A–1C tests | PASS — all 188 retained |
| `eng/verify-architecture.py` | PASS |
| `eng/verify-local-env.py` | PASS |
| Migration from an empty database | PASS — both migrations applied, re-run is a no-op |
| RLS read isolation | PASS |
| RLS write isolation | PASS |
| RLS delete isolation | PASS |
| Mismatched tenant insert | PASS — refused as `persistence.tenant_isolation_violation` |
| Missing tenant fail-closed | PASS — refused before any statement, and zero rows at the storage layer |
| Connection-pool isolation | PASS — proven with a single-connection pool |
| Raw receipt dedup | PASS — same tenant rejected, other tenant independent, NULL ids unconstrained |
| Transactional outbox | PASS — commits together; rolls back together on both a thrown failure and an RLS rejection |
| pgvector | PASS — extension 0.8.6 present and a real vector operation succeeds |
| UTC persistence | PASS — non-UTC input normalised, `timestamptz` columns confirmed |
| Secret scan | PASS |
| Gate 1B local environment regression | PASS from a clean slate |

Test distribution: App.Unit.Tests 138, App.Integration.Tests 44 (19 middleware + 25 database),
App.Security.Tests 35, App.Architecture.Tests 13.

### The isolation tests were checked for vacuity

An isolation test that passes because the guarantee is untestable is worse than no test. Row-level
security was therefore disabled on `raw_receipts` and the suite re-run: **8 tests failed**, covering
read, update, delete, mismatched insert, absent-setting deny, the RLS-state assertion and both
pool-reuse tests. RLS was restored and the schema state re-verified against the migration's intent
(`rls=true forced=true policies=1` on all four tables), after which all 44 integration tests passed
again.

The credential scan was mutation-tested the same way: planting a literal connection-string password
into `appsettings.json` failed it, and the file was reverted. (The literal is not reproduced here,
because the scan quite correctly covers committed documentation too.)

## Problems found and fixes applied

1. **`postgres:17-alpine` has no pgvector.** Verified by listing the image's extension directory
   rather than assuming. Local image changed to `pgvector/pgvector:pg17` (same PostgreSQL 17.11).
2. **A single database role would have made RLS ineffective**, because `POSTGRES_USER` is a superuser
   and superusers bypass row-level security. Added the non-owner `workcontinuity_app` runtime role,
   created by a bootstrap script so no credential enters a migration, plus a test asserting the role
   holds neither SUPERUSER nor BYPASSRLS.
3. **EF Core version conflict (`MSB3277`)** between the Npgsql provider's EF Core 10.0.4 and Design
   10.0.12. Aligned Design and `dotnet-ef` to 10.0.4 instead of suppressing the warning.
4. **`IConfigurationSection.GetValue` needs the configuration binder package.** Parsed the flag
   directly rather than adding a dependency Infrastructure does not otherwise need.
5. **Two of my own new security assertions were over-strict** and produced false positives, not real
   findings: the credential scan flagged deliberately fake credential strings inside test fixtures
   (one of which exists precisely to prove such a value never reaches an HTTP response) and a shell
   `$VAR` reference without braces; and the error-message check forbade the ordinary English word
   "constraint" in "The record failed a database constraint." Both assertions were narrowed to target
   genuine leaks — SQL text, identifiers, driver names, connection details, stack frames — and the
   scan was re-proven against a planted literal password.
6. **Benign diagnostic:** the Worker container logs `Cannot load library libgssapi_krb5.so.2` once at
   startup. Npgsql probes for Kerberos/GSSAPI support, which password authentication does not need;
   the `dotnet/runtime` image simply lacks the library. Migration and all database access succeed.
   Left as-is rather than adding a library to the image for a capability the MVP does not use.

## A note on running the test suite

The database tests are genuine integration tests against the local PostgreSQL container: no
in-memory provider and no mocks, because an in-memory store cannot exhibit row-level security, a
partial unique index or transaction-local settings — which is exactly what is under test. If the
database is unreachable they **fail with an explicit message** telling the developer to run
`./eng/dev.ps1 up`, rather than skipping. A skipped isolation test looks identical to a passing one,
and an unproven isolation guarantee must never read as green.

`dotnet test` on the whole solution therefore now requires the local stack to be running.

## Architecture consistency

Core remains persistence-agnostic: it has zero package references and no source file mentions EF Core,
Npgsql, `DbContext`, `DbSet` or pgvector — asserted by test. All Gate 1C dependency boundaries are
unchanged. The persistence packages are referenced by `App.Infrastructure` only, also asserted.

`Npgsql` was removed from the architecture test's forbidden-package list, since it is now the
approved Gate 1D dependency. Azure, AI and semantic-kernel packages remain forbidden.

No PRD or System Architecture v1 contradiction was found. PostgreSQL as the primary operational
store, pgvector for vector retrieval rather than a separate authoritative store, RLS as storage-level
defence in depth in addition to application authorization, the transactional outbox, and separate
lifecycles for raw work payload and security audit data are all followed as written.

## Gate status

**PASS.** Gate 1E was not started.
