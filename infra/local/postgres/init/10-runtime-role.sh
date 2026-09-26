#!/bin/bash
# Creates the non-owner runtime role on first database initialization.
#
# Row-level security is bypassed by a table owner that has FORCE disabled, and unconditionally by any
# SUPERUSER. POSTGRES_USER is a superuser, so if the application connected as it, every policy would
# be silently ineffective. The application therefore connects as this separate role, which holds
# neither SUPERUSER nor BYPASSRLS and does not own the tables.
#
# The role lives here rather than in a migration because a migration must never contain a credential.
# The password comes from the untracked .env.
set -euo pipefail

: "${POSTGRES_APP_USER:?POSTGRES_APP_USER must be set}"
: "${POSTGRES_APP_PASSWORD:?POSTGRES_APP_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 \
     --username "$POSTGRES_USER" \
     --dbname "$POSTGRES_DB" \
     -v app_user="$POSTGRES_APP_USER" \
     -v app_password="$POSTGRES_APP_PASSWORD" \
     -v owner="$POSTGRES_USER" \
     -v db="$POSTGRES_DB" <<'SQL'
\set quoted_app_user :"app_user"
\set quoted_app_password :'app_password'

CREATE ROLE :quoted_app_user LOGIN PASSWORD :quoted_app_password
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT;

GRANT CONNECT ON DATABASE :"db" TO :quoted_app_user;
GRANT USAGE ON SCHEMA public TO :quoted_app_user;

-- Tables are created later by the migration running as the owner. Default privileges make the
-- runtime grants automatic, so a new table is never accidentally unreachable or over-permissioned.
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :quoted_app_user;
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO :quoted_app_user;
SQL

echo "Runtime role '${POSTGRES_APP_USER}' created (NOSUPERUSER, NOBYPASSRLS)."
