-- Gate 3G Stage B3, area C (owner decision B-14; review decision R3B-2): the repeatable, read-only procedure for fresh
-- row-level security and runtime-privilege evidence on the persistent standalone harness database. AUTHORED for the
-- separately approved provisioning step; Stage B3 runs it only in its own tests, never as evidence of that database.
--
-- It runs as the runtime role, connected to the standalone database, after the seed and before any harness run (and
-- again after the run if the owner asks). It takes two settings, passed as connection options, and no other input:
--   wce_evidence.expected_database  the exact name of the database the evidence is for
--   wce_evidence.tenant_id          the fresh tenant the seed wrote
-- A payload only, not a supported operator entry point (owner decision, 2026-10-06). Its one supported entry point is
--   ./eng/harness-standalone.ps1 evidence
-- which passes both settings, runs it inside the local compose PostgreSQL service and succeeds only when every check
-- below is reported exactly once, as PASS.
--
-- It answers closed values only: a check name, PASS or FAIL, and at most a count. It selects no payload, no value, no
-- identifier and no provider data, and its one transaction is READ ONLY and rolled back: it writes nothing, and it
-- changes no role, privilege, policy or setting beyond its own transaction. Every row must be PASS; any FAIL, error or
-- missing row means the evidence is not established.

BEGIN TRANSACTION READ ONLY;

-- Part 1: identity, the runtime role's attributes, ownership and memberships (as hardened by the owner's Decision 3 of
-- 2026-10-06: the plain runtime role only, owning nothing and a member of no role), row-level security and the R3B-2
-- privileges. Catalog only.
SELECT checks.check_name,
       CASE WHEN checks.passed IS TRUE THEN 'PASS' ELSE 'FAIL' END AS status,
       checks.observed
FROM (
    SELECT 1 AS ord, 'transaction.read_only' AS check_name,
           current_setting('transaction_read_only') = 'on' AS passed, NULL::bigint AS observed
    UNION ALL
    SELECT 2, 'identity.expected_database',
           current_database() = current_setting('wce_evidence.expected_database', true), NULL
    UNION ALL
    SELECT 3, 'identity.standalone_harness_database_name',
           current_database() ~ '^wce_harness_[a-z0-9_]{1,51}$', NULL
    UNION ALL
    SELECT 4, 'identity.session_user_is_current_user',
           session_user = current_user, NULL
    UNION ALL
    SELECT 5, 'identity.current_user_is_runtime_role',
           current_user = 'workcontinuity_app', NULL
    UNION ALL
    SELECT 6, 'role.login_nosuperuser_nobypassrls_nocreaterole_nocreatedb_noreplication_noinherit',
           (SELECT r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls AND NOT r.rolcreaterole AND NOT r.rolcreatedb
                   AND NOT r.rolreplication AND NOT r.rolinherit
            FROM pg_catalog.pg_roles AS r WHERE r.rolname = current_user), NULL
    UNION ALL
    SELECT 7, 'role.owns_no_table_view_or_sequence',
           NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class AS c
                       WHERE c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f') AND c.relpersistence <> 't'
                         AND pg_has_role(current_user, c.relowner, 'MEMBER')),
           NULL
    UNION ALL
    SELECT 8, 'role.owns_neither_database_nor_schema',
           NOT pg_has_role(current_user, (SELECT d.datdba FROM pg_catalog.pg_database AS d WHERE d.datname = current_database()), 'MEMBER')
           AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace AS n WHERE pg_has_role(current_user, n.nspowner, 'MEMBER')),
           NULL
    UNION ALL
    SELECT 9, 'role.member_of_no_role',
           (SELECT m.current_role_rows = 1 AND m.granted_roles = 0 AND m.privileged_granted_role IS FALSE
            FROM (SELECT answer.current_role_rows, answer.granted_roles, answer.privileged_granted_role FROM (WITH RECURSIVE role_facts (oid, rolname, privileged) AS (SELECT r.oid, r.rolname, (r.rolsuper OR r.rolbypassrls OR EXISTS (SELECT 1 FROM pg_tables AS t WHERE t.schemaname = 'public' AND t.tableowner = r.rolname)) FROM pg_roles AS r), memberships (member, roleid) AS (SELECT m.member, m.roleid FROM pg_auth_members AS m), current_role_row (oid) AS (SELECT f.oid FROM role_facts AS f WHERE f.rolname = current_user), granted (roleid) AS (SELECT m.roleid FROM memberships AS m JOIN current_role_row AS c ON m.member = c.oid UNION SELECT m.roleid FROM memberships AS m JOIN granted AS g ON m.member = g.roleid) SELECT (SELECT count(*) FROM current_role_row) AS current_role_rows, (SELECT count(*) FROM granted) AS granted_roles, EXISTS (SELECT 1 FROM granted AS g LEFT JOIN role_facts AS f ON f.oid = g.roleid WHERE f.privileged IS NOT FALSE) AS privileged_granted_role) AS answer) AS m),
           (SELECT m.granted_roles
            FROM (SELECT answer.current_role_rows, answer.granted_roles, answer.privileged_granted_role FROM (WITH RECURSIVE role_facts (oid, rolname, privileged) AS (SELECT r.oid, r.rolname, (r.rolsuper OR r.rolbypassrls OR EXISTS (SELECT 1 FROM pg_tables AS t WHERE t.schemaname = 'public' AND t.tableowner = r.rolname)) FROM pg_roles AS r), memberships (member, roleid) AS (SELECT m.member, m.roleid FROM pg_auth_members AS m), current_role_row (oid) AS (SELECT f.oid FROM role_facts AS f WHERE f.rolname = current_user), granted (roleid) AS (SELECT m.roleid FROM memberships AS m JOIN current_role_row AS c ON m.member = c.oid UNION SELECT m.roleid FROM memberships AS m JOIN granted AS g ON m.member = g.roleid) SELECT (SELECT count(*) FROM current_role_row) AS current_role_rows, (SELECT count(*) FROM granted) AS granted_roles, EXISTS (SELECT 1 FROM granted AS g LEFT JOIN role_facts AS f ON f.oid = g.roleid WHERE f.privileged IS NOT FALSE) AS privileged_granted_role) AS answer) AS m)
    UNION ALL
    SELECT 10, 'rls.every_tenant_table_enabled_and_forced',
           (SELECT count(*) = 16 AND bool_and(c.relrowsecurity AND c.relforcerowsecurity)
            FROM pg_catalog.pg_class AS c JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
              AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute AS a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)),
           (SELECT count(*)
            FROM pg_catalog.pg_class AS c JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
              AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute AS a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped))
    UNION ALL
    SELECT 11, 'rls.only_the_migration_history_has_no_tenant',
           (SELECT array_agg(c.relname::text ORDER BY c.relname) = ARRAY['__EFMigrationsHistory']
            FROM pg_catalog.pg_class AS c JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_attribute AS a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)),
           NULL
    UNION ALL
    SELECT 12, 'rls.every_tenant_table_has_its_isolation_policy',
           NOT EXISTS (
               SELECT 1 FROM pg_catalog.pg_class AS c JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
               WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
                 AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute AS a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)
                 AND NOT EXISTS (
                     SELECT 1 FROM pg_catalog.pg_policy AS p
                     WHERE p.polrelid = c.oid AND p.polname = 'tenant_isolation_' || c.relname AND p.polpermissive AND p.polcmd = '*'
                       AND p.polroles = '{0}'::oid[]
                       AND pg_get_expr(p.polqual, p.polrelid) = '(tenant_id = (NULLIF(current_setting(''app.tenant_id''::text, true), ''''::text))::uuid)'
                       AND pg_get_expr(p.polwithcheck, p.polrelid) = '(tenant_id = (NULLIF(current_setting(''app.tenant_id''::text, true), ''''::text))::uuid)')),
           NULL
    UNION ALL
    SELECT 13, 'rls.no_other_permissive_policy_applies_to_the_runtime_role',
           NOT EXISTS (
               SELECT 1 FROM pg_catalog.pg_policy AS p JOIN pg_catalog.pg_class AS c ON c.oid = p.polrelid
               JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
               WHERE n.nspname = 'public' AND p.polpermissive AND p.polname <> 'tenant_isolation_' || c.relname
                 AND (0::oid = ANY (p.polroles) OR EXISTS (SELECT 1 FROM unnest(p.polroles) AS granted_role WHERE pg_has_role(current_user, granted_role, 'MEMBER')))),
           NULL
    UNION ALL
    SELECT 14, 'r3b2.oauth_and_reconciliation_tables_exactly_insert_select_update',
           (SELECT string_agg(t.name || ':' || coalesce(t.privileges, ''), ' ' ORDER BY t.name COLLATE "C")
            FROM (SELECT tables.name, (SELECT string_agg(p.privilege, ',' ORDER BY p.privilege COLLATE "C")
                                       FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS p(privilege)
                                       WHERE has_table_privilege(current_user, 'public.' || tables.name, p.privilege)) AS privileges
                  FROM unnest(ARRAY['connector_authorizations', 'connector_authorization_transactions', 'reconciliation_targets']) AS tables(name)) AS t)
           = 'connector_authorization_transactions:INSERT,SELECT,UPDATE connector_authorizations:INSERT,SELECT,UPDATE reconciliation_targets:INSERT,SELECT,UPDATE',
           NULL
    UNION ALL
    SELECT 15, 'r3b2.initial_sync_tables_exactly_insert_select',
           (SELECT string_agg(t.name || ':' || coalesce(t.privileges, ''), ' ' ORDER BY t.name COLLATE "C")
            FROM (SELECT tables.name, (SELECT string_agg(p.privilege, ',' ORDER BY p.privilege COLLATE "C")
                                       FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS p(privilege)
                                       WHERE has_table_privilege(current_user, 'public.' || tables.name, p.privilege)) AS privileges
                  FROM unnest(ARRAY['connector_source_scopes', 'connection_syncs', 'connection_sync_scopes']) AS tables(name)) AS t)
           = 'connection_sync_scopes:INSERT,SELECT connection_syncs:INSERT,SELECT connector_source_scopes:INSERT,SELECT',
           NULL
    UNION ALL
    SELECT 16, 'r3b2.initial_sync_update_columns_exact',
           (SELECT string_agg(t.name || ':' || coalesce(t.columns, ''), ' ' ORDER BY t.name COLLATE "C")
            FROM (SELECT tables.name, (SELECT string_agg(a.attname::text, ',' ORDER BY a.attname)
                                       FROM pg_catalog.pg_attribute AS a
                                       WHERE a.attrelid = ('public.' || tables.name)::regclass AND a.attnum > 0 AND NOT a.attisdropped
                                         AND has_column_privilege(current_user, a.attrelid, a.attnum, 'UPDATE')) AS columns
                  FROM unnest(ARRAY['connector_source_scopes', 'connection_syncs', 'connection_sync_scopes']) AS tables(name)) AS t)
           = 'connection_sync_scopes:attempts,completed_at_utc,fields_applied,last_outcome,objects_read,pages_read,state'
             || ' connection_syncs:attempts,authorized_at_utc,buffer_start_fence,claim_id,claimed_until_utc,cutover_fence,generation,generation_scope_version,last_outcome,live_at_utc,next_attempt_at_utc,provider_subject,replay_fence,scope_version,started_at_utc,state,updated_at_utc'
             || ' connector_source_scopes:baseline_generation,enabled,updated_at_utc',
           NULL
    UNION ALL
    SELECT 17, 'r3b2.current_work_views_select_only',
           (SELECT string_agg(t.name || ':' || coalesce(t.privileges, ''), ' ' ORDER BY t.name COLLATE "C")
            FROM (SELECT views.name, (SELECT string_agg(p.privilege, ',' ORDER BY p.privilege COLLATE "C")
                                      FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS p(privilege)
                                      WHERE has_table_privilege(current_user, 'public.' || views.name, p.privilege)) AS privileges
                  FROM unnest(ARRAY['active_snapshot_objects', 'active_snapshot_fields']) AS views(name)) AS t)
           = 'active_snapshot_fields:SELECT active_snapshot_objects:SELECT',
           NULL
    UNION ALL
    SELECT 18, 'runtime.no_administrative_path_beyond_the_approved_functions',
           (SELECT array_agg(p.proname::text ORDER BY p.proname)
            FROM pg_catalog.pg_proc AS p JOIN pg_catalog.pg_namespace AS n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public' AND p.prosecdef)
           = ARRAY['outbox_pending_tenants', 'resolve_ingress_connection', 'retention_expired_tenants'],
           NULL
) AS checks
ORDER BY checks.ord;

-- Part 2: no tenant context. Row-level security shows nothing of any tenant table.
SELECT set_config('app.tenant_id', '', true) IS NOT NULL AS tenant_context_cleared;

SELECT 'isolation.without_tenant_no_row_is_visible' AS check_name,
       CASE WHEN count(*) = 16 AND sum(visible.n) = 0 THEN 'PASS' ELSE 'FAIL' END AS status,
       count(*) AS observed
FROM (SELECT (xpath('/row/n/text()', query_to_xml(format('SELECT count(*) AS n FROM public.%I', c.relname), false, true, '')))[1]::text::bigint AS n
      FROM pg_catalog.pg_class AS c JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
      WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
        AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute AS a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)) AS visible;

-- Part 3: the fresh tenant's own context. It sees itself once, its one canonical user, and no other tenant's row.
SELECT set_config('app.tenant_id', current_setting('wce_evidence.tenant_id'), true) IS NOT NULL AS tenant_context_set;

SELECT 'isolation.fresh_tenant_sees_no_other_tenant_row' AS check_name,
       CASE WHEN count(*) = 16 AND sum(visible.n) = 0 THEN 'PASS' ELSE 'FAIL' END AS status,
       count(*) AS observed
FROM (SELECT (xpath('/row/n/text()', query_to_xml(format('SELECT count(*) AS n FROM public.%I WHERE tenant_id IS DISTINCT FROM %L::uuid', c.relname, current_setting('wce_evidence.tenant_id')), false, true, '')))[1]::text::bigint AS n
      FROM pg_catalog.pg_class AS c JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
      WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
        AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute AS a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)) AS visible;

SELECT 'isolation.fresh_tenant_present_once_with_one_active_user' AS check_name,
       CASE WHEN (SELECT count(*) FROM public.tenants WHERE status = 'Active') = 1
                 AND (SELECT count(*) FROM public.tenants) = 1
                 AND (SELECT count(*) FROM public.users WHERE is_active) = 1
                 AND (SELECT count(*) FROM public.users) = 1
            THEN 'PASS' ELSE 'FAIL' END AS status,
       NULL::bigint AS observed;

-- Part 4: another tenant's context, never used: none of the fresh tenant's rows is visible to it.
SELECT set_config('app.tenant_id', gen_random_uuid()::text, true) IS NOT NULL AS other_tenant_context_set;

SELECT 'isolation.other_tenant_sees_no_fresh_tenant_row' AS check_name,
       CASE WHEN current_setting('app.tenant_id') <> current_setting('wce_evidence.tenant_id') AND count(*) = 16 AND sum(visible.n) = 0
            THEN 'PASS' ELSE 'FAIL' END AS status,
       count(*) AS observed
FROM (SELECT (xpath('/row/n/text()', query_to_xml(format('SELECT count(*) AS n FROM public.%I WHERE tenant_id = %L::uuid', c.relname, current_setting('wce_evidence.tenant_id')), false, true, '')))[1]::text::bigint AS n
      FROM pg_catalog.pg_class AS c JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
      WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
        AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute AS a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)) AS visible;

ROLLBACK;
