---
name: database-migrations
description: Use when changing the FULLY PostgreSQL schema - adding or altering EF Core entities, migrations, indexes, row-level security policies, grants, database functions or roles - or when planning a migration run, rollback or runtime-role bootstrap in any environment.
---

# Database Migrations in FULLY

## Overview

Schema changes are forward-only, additive by default, tenant-safe by construction, and never carry a
credential. The Gate 1D report explains the roles and row-level security design; Gate 2 decisions 1
and 5 in `docs/gates/gate-2-scope.md` bound tenant-less functions and retention.

## Where things live

- **Model:** `src/App.Infrastructure/Persistence/WorkContinuityModel.cs` is the single schema definition
  for the runtime and administrative contexts.
- **Migrations:** `src/App.Infrastructure/Persistence/Migrations/`. Use the pinned `dotnet-ef` local tool
  with the options `eng/db.ps1` passes.
- **Apply locally:** `./eng/db.ps1 migrate` or `status`. `reset` destroys the local volume and is local only.
- **Order everywhere:** bootstrap the runtime role, then migrate. Locally that is the `db-bootstrap` and
  `db-migrator` compose services; in Azure the bootstrap and migration jobs. Starting an Azure job needs
  explicit owner approval.
- **Roles:** the schema owner migrates; the application runs as `workcontinuity_app`, which has no
  superuser or row-level-security bypass. `RuntimeRoleBootstrap` creates and updates that role; a
  migration never does.

## Procedure

1. **Classify the change.** Additive: a new table, a nullable column, an index. Transforming: a backfill
   or a type change. Destructive: a drop, rename, narrowing or data deletion. Destructive changes stop
   here and need explicit owner approval plus an expand-then-contract plan.
2. **New tenant-owned table.** The entity implements `ITenantOwned` with a `TenantId` and has no public
   setters. The migration enables and forces row-level security and adds the tenant policy comparing
   `tenant_id` with `nullif(current_setting('app.tenant_id', true), '')::uuid`. Grants are guarded by a
   `pg_roles` check. Add the entity to the security tests' tenant-owned list.
3. **Tenant-less function.** Allowed only for decision 1's three operations: connection to tenant,
   outbox claim, purge. Use `SECURITY DEFINER` with a fixed `SET search_path = pg_catalog, public`,
   schema-qualified names, typed parameters and no dynamic SQL. Return ids, status or counts only.
   `REVOKE ALL ... FROM PUBLIC`, then `GRANT EXECUTE` to the runtime role only, and add it to the pinned
   allowlist test. Anything else: stop and report.
4. **Write and review.** Generate the migration, then read the SQL it produces with
   `dotnet ef migrations script <previous> <new> --idempotent`. Never apply SQL you have not read.
   Keep `Down` non-destructive where possible and call out any data loss.
5. **Data and secrets.** No cross-tenant backfill through the runtime role, no personal or tenant data in
   a migration, and never a credential.
6. **Retention shape.** Keep payload-bearing columns separate from metadata so a purge removes content
   without touching provenance (decision 5).
7. **Verify.** Migrate from an empty database, then re-run as a no-op. Run the full suite with the local
   stack. Vacuity-check isolation: temporarily disable row-level security on the new table, confirm the
   isolation tests fail, then restore it.
8. **Rollback plan.** State what `Down` does and whether it loses data. Prefer a forward fix. Each Azure
   run is a separate approval.

## Red flags: stop

- A migration that creates a role, sets a password or reads Key Vault.
- `BYPASSRLS`, `SECURITY DEFINER` outside the approved three, or no fixed `search_path`.
- `DROP`, `RENAME` or a narrowing `ALTER` without approval.
- Editing a migration that has already been applied anywhere, instead of adding a new one.
- `AutoMigrateOnStartup` enabled outside local development.
