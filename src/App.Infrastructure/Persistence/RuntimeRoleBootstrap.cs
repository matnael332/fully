using System.Text.RegularExpressions;
using Npgsql;

namespace WorkContinuity.Infrastructure.Persistence;

/// <summary>The outcome of one bootstrap run. Never carries the password.</summary>
public sealed record RuntimeRoleBootstrapResult(
    string RoleName,
    bool Created,
    bool CanLogin,
    bool RowLevelSecurityEnforced,
    int TablesGranted,
    bool LoginVerified);

/// <summary>A bootstrap failure, described without credentials, hosts or stack frames.</summary>
public sealed class RuntimeRoleBootstrapException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RuntimeRoleBootstrapException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    public RuntimeRoleBootstrapException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public RuntimeRoleBootstrapException()
    {
    }
}

/// <summary>
/// Creates or updates the non-owner runtime role in a database, running as the schema owner.
/// </summary>
/// <remarks>
/// <para>
/// This is the Azure counterpart of <c>infra/local/postgres/init/10-runtime-role.sh</c> and is a job
/// entry point, never a migration: a migration is committed and replayed everywhere and must never
/// carry a credential. It is a separate type from <see cref="DatabaseMigrator"/> on purpose, so the
/// migration path never sees the password and the migrator's surface stays exactly what the security
/// tests pin it to.
/// </para>
/// <para>
/// The password reaches the server only as a bind parameter of <c>set_config</c>; the DDL that uses
/// it runs inside the server with <c>format('%L')</c>, so this process never composes SQL text that
/// contains the password. The role is created with, or reset to, exactly
/// <c>LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT</c>, which is what keeps
/// row-level security effective. Grants: CONNECT, USAGE on <c>public</c>, DML on every existing
/// table and USAGE/SELECT on every existing sequence, plus default privileges for objects the owner
/// creates later, so bootstrap may run before or after the first migration and re-running is safe.
/// </para>
/// <para>
/// The run ends by opening a second connection as the role itself, so the result states that the
/// credential works, not merely that the statements succeeded.
/// </para>
/// </remarks>
public static partial class RuntimeRoleBootstrap
{
    /// <summary>The shortest password accepted, matching the infrastructure scripts.</summary>
    public const int MinimumPasswordLength = 12;

    private const string StageCredentialsSql =
        "SELECT set_config('wce.bootstrap_role', @role, true), set_config('wce.bootstrap_password', @password, true);";

    private const string RoleExistsSql =
        "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role);";

    private const string ApplySql = """
        DO $$
        DECLARE
            role_name text := current_setting('wce.bootstrap_role');
            role_password text := current_setting('wce.bootstrap_password');
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = role_name) THEN
                EXECUTE format(
                    'ALTER ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT',
                    role_name, role_password);
            ELSE
                EXECUTE format(
                    'CREATE ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT',
                    role_name, role_password);
            END IF;

            -- CONNECT and USAGE on public default to PUBLIC; on a managed server the administrator may not own
            -- the database or the schema, so an ungrantable privilege is reported, not fatal. The login check
            -- at the end of the bootstrap proves connectivity either way.
            BEGIN
                EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), role_name);
            EXCEPTION WHEN insufficient_privilege THEN
                RAISE NOTICE 'CONNECT on database % is not grantable by %; the PUBLIC default applies', current_database(), current_user;
            END;
            BEGIN
                EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', role_name);
            EXCEPTION WHEN insufficient_privilege THEN
                RAISE NOTICE 'USAGE on schema public is not grantable by %; the PUBLIC default applies', current_user;
            END;
            EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO %I', role_name);
            EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO %I', role_name);
            EXECUTE format(
                'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I',
                current_user, role_name);
            EXECUTE format(
                'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO %I',
                current_user, role_name);
        END
        $$;
        """;

    private const string VerifySql = """
        SELECT r.rolcanlogin,
               (NOT r.rolsuper AND NOT r.rolbypassrls),
               (SELECT count(*)::int
                  FROM information_schema.role_table_grants g
                 WHERE g.grantee = r.rolname
                   AND g.table_schema = 'public'
                   AND g.privilege_type = 'SELECT')
          FROM pg_roles r
         WHERE r.rolname = @role;
        """;

    /// <summary>Creates or updates the role and proves it can log in.</summary>
    /// <param name="administrativeConnectionString">The schema owner's connection string.</param>
    /// <param name="roleName">The runtime role name; lower-case identifier characters only.</param>
    /// <param name="password">The role's password; at least <see cref="MinimumPasswordLength"/> characters.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <exception cref="ArgumentException">An argument is missing or malformed.</exception>
    /// <exception cref="RuntimeRoleBootstrapException">The database rejected the bootstrap.</exception>
    public static async Task<RuntimeRoleBootstrapResult> ApplyAsync(
        string administrativeConnectionString,
        string roleName,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(administrativeConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        if (!RoleNamePattern().IsMatch(roleName))
        {
            throw new ArgumentException(
                "The role name must be a lower-case PostgreSQL identifier of at most 63 characters.",
                nameof(roleName));
        }

        if (password.Length < MinimumPasswordLength)
        {
            throw new ArgumentException(
                $"The runtime role password must be at least {MinimumPasswordLength} characters.",
                nameof(password));
        }

        bool existed;
        bool canLogin;
        bool rowLevelSecurityEnforced;
        int tablesGranted;

        try
        {
            await using var connection = new NpgsqlConnection(administrativeConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var exists = new NpgsqlCommand(RoleExistsSql, connection, transaction))
            {
                exists.Parameters.AddWithValue("role", roleName);
                existed = (bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            await using (var stage = new NpgsqlCommand(StageCredentialsSql, connection, transaction))
            {
                stage.Parameters.AddWithValue("role", roleName);
                stage.Parameters.AddWithValue("password", password);
                _ = await stage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var apply = new NpgsqlCommand(ApplySql, connection, transaction))
            {
                _ = await apply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var verify = new NpgsqlCommand(VerifySql, connection, transaction))
            {
                verify.Parameters.AddWithValue("role", roleName);
                await using var reader = await verify.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new RuntimeRoleBootstrapException("The role is absent after the bootstrap statements ran.");
                }

                canLogin = reader.GetBoolean(0);
                rowLevelSecurityEnforced = reader.GetBoolean(1);
                tablesGranted = reader.GetInt32(2);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException postgres)
        {
            throw new RuntimeRoleBootstrapException(
                $"PostgreSQL rejected the bootstrap: SQLSTATE {postgres.SqlState} {postgres.MessageText}",
                postgres);
        }
        catch (NpgsqlException npgsql)
        {
            throw new RuntimeRoleBootstrapException(
                $"The administrative connection failed: {npgsql.InnerException?.GetType().Name ?? npgsql.GetType().Name}",
                npgsql);
        }

        var loginVerified = await VerifyLoginAsync(
            administrativeConnectionString, roleName, password, cancellationToken).ConfigureAwait(false);

        return new RuntimeRoleBootstrapResult(
            roleName,
            Created: !existed,
            canLogin,
            rowLevelSecurityEnforced,
            tablesGranted,
            loginVerified);
    }

    private static async Task<bool> VerifyLoginAsync(
        string administrativeConnectionString,
        string roleName,
        string password,
        CancellationToken cancellationToken)
    {
        // Same host, port, database and TLS settings as the owner; only the credential differs.
        var builder = new NpgsqlConnectionStringBuilder(administrativeConnectionString)
        {
            Username = roleName,
            Password = password,
            Pooling = false,
        };

        try
        {
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = new NpgsqlCommand("SELECT current_user", connection);
            var user = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;

            return string.Equals(user, roleName, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleNamePattern();
}
