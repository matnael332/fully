-- Gate 3G Stage B3, area B (owner decision B-14, as clarified by the product owner on 2026-10-06): the reviewed seed
-- payload of the future persistent standalone harness database. AUTHORED ONLY: running it is part of the separately
-- approved provisioning step, never of Stage B3.
--
-- A payload only, not a supported operator entry point (owner decision, 2026-10-06). Its one supported entry point is
--   ./eng/harness-standalone.ps1 seed
-- which takes the target database explicitly, refuses the shared suite database, validates every value, shows the plan
-- and this file's SHA-256, and runs it only with -Approve, only inside the local compose PostgreSQL service.
--
-- What it seeds (owner clarification of B-14, 2026-10-06): one fresh tenant and one fresh canonical user, and nothing
-- else. It never seeds a monday connection, a connector authorization, an OAuth transaction, token material, a PKCE
-- verifier or a delegated provider identity. The single fresh monday connection is created later, through the
-- product's existing Connect / MondayConnectionAuthorizer lifecycle, during a separately approved run.
--
-- What it does, in one transaction, or nothing at all:
--   1. refuses to start unless every required variable is given;
--   2. refuses unless the connected database is exactly the named target, the target is named like a standalone
--      harness database (wce_harness_...), the session can see every row (so that "empty" means empty), the schema is
--      migrated exactly to the latest migration, every application table is empty, and the two tables it writes keep
--      forced row-level security;
--   3. inserts exactly one tenant and exactly one canonical user of that tenant, and checks that they are the only
--      rows and that no connection exists.
--
-- What it never does: create, drop, migrate or reconfigure a database; create, alter, grant or revoke a role or a
-- privilege; write any table other than tenants and users; read or print a credential, a connection string or any
-- row; contact monday or any provider. It holds no credential and no connection details.
--
-- Required psql variables (no defaults; each is validated here, and the entry point validates them first):
--   target_database    the exact name of the database psql is connected to; must start with wce_harness_
--   tenant_id          a fresh tenant GUID, never used before (B-15), not the nil GUID
--   user_id            a fresh canonical user GUID, different from the tenant, not the nil GUID
--   identity_provider  the canonical user's identity provider key: 1-64 of a-z 0-9 . _ -
--   provider_subject   the canonical user's synthetic subject: 1-200 visible ASCII characters, no personal data

\set ON_ERROR_STOP on

\if :{?target_database}
\else
DO $refuse$ BEGIN RAISE EXCEPTION 'seed: refused (target_database is missing)'; END $refuse$;
\endif
\if :{?tenant_id}
\else
DO $refuse$ BEGIN RAISE EXCEPTION 'seed: refused (tenant_id is missing)'; END $refuse$;
\endif
\if :{?user_id}
\else
DO $refuse$ BEGIN RAISE EXCEPTION 'seed: refused (user_id is missing)'; END $refuse$;
\endif
\if :{?identity_provider}
\else
DO $refuse$ BEGIN RAISE EXCEPTION 'seed: refused (identity_provider is missing)'; END $refuse$;
\endif
\if :{?provider_subject}
\else
DO $refuse$ BEGIN RAISE EXCEPTION 'seed: refused (provider_subject is missing)'; END $refuse$;
\endif

BEGIN;

SELECT set_config('wce_seed.target_database', :'target_database', true) IS NOT NULL AS target_given,
       set_config('wce_seed.tenant_id', :'tenant_id', true) IS NOT NULL AS tenant_given,
       set_config('wce_seed.user_id', :'user_id', true) IS NOT NULL AS user_given,
       set_config('wce_seed.identity_provider', :'identity_provider', true) IS NOT NULL AS provider_given,
       set_config('wce_seed.provider_subject', :'provider_subject', true) IS NOT NULL AS subject_given;

DO $seed$
DECLARE
    target text := current_setting('wce_seed.target_database');
    tenant_text text := current_setting('wce_seed.tenant_id');
    user_text text := current_setting('wce_seed.user_id');
    provider text := current_setting('wce_seed.identity_provider');
    subject text := current_setting('wce_seed.provider_subject');
    tenant uuid;
    canonical_user uuid;
    sees_every_row boolean;
    applied bigint;
    latest text;
    table_name text;
    occupied boolean;
BEGIN
    -- The target: exactly the connected database, named like a standalone harness database. The shared suite database
    -- and the suite's own test databases are named otherwise, so neither can ever be the target.
    IF target IS NULL OR target <> current_database() THEN
        RAISE EXCEPTION 'seed: refused (the connected database is not the named target)';
    END IF;
    IF current_database() !~ '^wce_harness_[a-z0-9_]{1,51}$' THEN
        RAISE EXCEPTION 'seed: refused (the target is not named like a standalone harness database)';
    END IF;

    -- The values.
    IF tenant_text !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
       OR user_text !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN
        RAISE EXCEPTION 'seed: refused (tenant_id and user_id must be lower-case GUIDs)';
    END IF;
    tenant := tenant_text::uuid;
    canonical_user := user_text::uuid;
    IF tenant = '00000000-0000-0000-0000-000000000000'::uuid
       OR canonical_user = '00000000-0000-0000-0000-000000000000'::uuid
       OR tenant = canonical_user THEN
        RAISE EXCEPTION 'seed: refused (tenant_id and user_id must be distinct and not the nil GUID)';
    END IF;
    IF provider IS NULL OR provider !~ '^[a-z0-9._-]{1,64}$' THEN
        RAISE EXCEPTION 'seed: refused (identity_provider is malformed)';
    END IF;
    IF subject IS NULL OR subject !~ '^[!-~]{1,200}$' THEN
        RAISE EXCEPTION 'seed: refused (provider_subject is malformed)';
    END IF;

    -- Emptiness is provable only by a session that row-level security does not filter.
    SELECT r.rolsuper OR r.rolbypassrls INTO sees_every_row FROM pg_catalog.pg_roles AS r WHERE r.rolname = current_user;
    IF sees_every_row IS NOT TRUE THEN
        RAISE EXCEPTION 'seed: refused (this session cannot see every row, so emptiness cannot be proven)';
    END IF;

    -- Migrated from zero, exactly to the latest migration of this repository.
    IF to_regclass('public."__EFMigrationsHistory"') IS NULL THEN
        RAISE EXCEPTION 'seed: refused (the target is not migrated)';
    END IF;
    SELECT count(*), max("MigrationId") INTO applied, latest FROM public."__EFMigrationsHistory";
    IF applied <> 14 OR latest IS DISTINCT FROM '20261003073534_ConnectorAuthorizationVerifierCleanup' THEN
        RAISE EXCEPTION 'seed: refused (the target is not migrated exactly to the latest migration)';
    END IF;

    -- Every application table is empty: nothing was seeded, synced or received before.
    FOR table_name IN
        SELECT c.relname FROM pg_catalog.pg_class AS c JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND c.relname <> '__EFMigrationsHistory'
    LOOP
        EXECUTE format('SELECT EXISTS (SELECT 1 FROM public.%I)', table_name) INTO occupied;
        IF occupied THEN
            RAISE EXCEPTION 'seed: refused (the target already holds rows)';
        END IF;
    END LOOP;

    -- The two tables written keep forced row-level security.
    IF (SELECT count(*) FROM pg_catalog.pg_class AS c JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relname IN ('tenants', 'users') AND c.relrowsecurity AND c.relforcerowsecurity) <> 2 THEN
        RAISE EXCEPTION 'seed: refused (tenants and users must keep forced row-level security)';
    END IF;

    -- Exactly one tenant and one canonical user of it.
    INSERT INTO public.tenants (tenant_id, status, created_at_utc) VALUES (tenant, 'Active', now());
    INSERT INTO public.users (user_id, tenant_id, identity_provider, provider_subject, is_active, created_at_utc)
    VALUES (canonical_user, tenant, provider, subject, true, now());

    IF (SELECT count(*) FROM public.tenants) <> 1 OR (SELECT count(*) FROM public.users) <> 1
       OR (SELECT count(*) FROM public.connections) <> 0 THEN
        RAISE EXCEPTION 'seed: refused (the seeded rows are not exactly one tenant and one user)';
    END IF;
END
$seed$;

COMMIT;

\echo 'seed: done (one tenant, one canonical user; no connection, no authorization)'
