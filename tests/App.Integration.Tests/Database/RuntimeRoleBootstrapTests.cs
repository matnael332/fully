using Npgsql;
using WorkContinuity.Infrastructure.Persistence;

namespace WorkContinuity.Integration.Tests.Database;

/// <summary>
/// The runtime-role bootstrap against the real local database: it creates a role with exactly the
/// attributes row-level security depends on, grants the existing tables, proves the login, updates
/// the role and its password on a second run, and is a safe no-op for the real runtime role.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RuntimeRoleBootstrapTests(DatabaseFixture fixture)
{
    private const string TestRole = "wce_bootstrap_test";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bootstrap_creates_then_updates_a_login_role_that_cannot_bypass_row_level_security()
    {
        await DropTestRoleAsync();

        try
        {
            var firstPassword = "bootstrap-test-" + Guid.NewGuid().ToString("N");
            var created = await RuntimeRoleBootstrap.ApplyAsync(
                fixture.AdministrativeConnectionString, TestRole, firstPassword, Token);

            Assert.True(created.Created);
            Assert.True(created.CanLogin);
            Assert.True(created.RowLevelSecurityEnforced);
            Assert.True(created.LoginVerified);
            Assert.True(created.TablesGranted >= 4, $"only {created.TablesGranted} tables were granted");

            // Row-level security applies to the new role: without a tenant context it sees no rows.
            await using (var connection = await OpenAsAsync(TestRole, firstPassword))
            await using (var command = new NpgsqlCommand("SELECT count(*) FROM tenants", connection))
            {
                Assert.Equal(0L, await command.ExecuteScalarAsync(Token));
            }

            var secondPassword = "bootstrap-test-" + Guid.NewGuid().ToString("N");
            var updated = await RuntimeRoleBootstrap.ApplyAsync(
                fixture.AdministrativeConnectionString, TestRole, secondPassword, Token);

            Assert.False(updated.Created);
            Assert.True(updated.LoginVerified);

            // The previous password no longer works: 28P01 is invalid_password.
            var stale = new NpgsqlConnectionStringBuilder(fixture.AdministrativeConnectionString)
            {
                Username = TestRole,
                Password = firstPassword,
                Pooling = false,
            };
            await using var staleConnection = new NpgsqlConnection(stale.ConnectionString);
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => staleConnection.OpenAsync(Token));
            Assert.Equal("28P01", rejected.SqlState);
        }
        finally
        {
            await DropTestRoleAsync();
        }
    }

    [Fact]
    public async Task Bootstrapping_the_real_runtime_role_with_its_current_password_is_idempotent()
    {
        var settings = LocalEnvironment.Read();
        var role = settings.GetValueOrDefault("POSTGRES_APP_USER", PersistenceOptions.RuntimeRoleName);
        var password = settings["POSTGRES_APP_PASSWORD"];

        var result = await RuntimeRoleBootstrap.ApplyAsync(fixture.AdministrativeConnectionString, role, password, Token);

        Assert.False(result.Created);
        Assert.True(result.CanLogin);
        Assert.True(result.RowLevelSecurityEnforced);
        Assert.True(result.LoginVerified);

        await using var context = fixture.CreateApplicationContext();
        Assert.True(await context.Database.CanConnectAsync(Token));
    }

    [Fact]
    public async Task Bootstrap_rejects_a_malformed_role_name_and_a_short_password_before_touching_the_database()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => RuntimeRoleBootstrap.ApplyAsync(
            fixture.AdministrativeConnectionString, "Bad-Role", "long-enough-password-value", Token));

        await Assert.ThrowsAsync<ArgumentException>(() => RuntimeRoleBootstrap.ApplyAsync(
            fixture.AdministrativeConnectionString, "wce_bootstrap_test", "short", Token));
    }

    private async Task<NpgsqlConnection> OpenAsAsync(string role, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.AdministrativeConnectionString)
        {
            Username = role,
            Password = password,
            Pooling = false,
        };

        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(Token);
        return connection;
    }

    private async Task DropTestRoleAsync()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = connection.CreateCommand();

        // DROP OWNED revokes the grants and default privileges the bootstrap gave the role.
        command.CommandText = $"""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{TestRole}') THEN
                    EXECUTE 'DROP OWNED BY {TestRole}';
                    EXECUTE 'DROP ROLE {TestRole}';
                END IF;
            END
            $$;
            """;

        await command.ExecuteNonQueryAsync(Token);
    }
}
