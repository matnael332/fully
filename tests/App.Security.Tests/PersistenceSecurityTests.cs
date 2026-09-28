using System.Reflection;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Security properties of the persistence layer that must hold by construction, independently of any
/// running database.
/// </summary>
public sealed class PersistenceSecurityTests
{
    private static readonly Type[] TenantOwnedEntities =
    [
        typeof(TenantRecord),
        typeof(UserRecord),
        typeof(RawReceiptRecord),
        typeof(OutboxMessageRecord),
        typeof(ConnectionRecord),
        typeof(EventLedgerRecord),
        typeof(EventLedgerFieldRecord),
        typeof(FieldOrderingStateRecord),
        typeof(SnapshotObjectRecord),
        typeof(SnapshotFieldRecord),
    ];

    [Fact]
    public void Every_tenant_owned_entity_carries_the_canonical_tenant_id()
    {
        foreach (var entity in TenantOwnedEntities)
        {
            Assert.True(
                typeof(ITenantOwned).IsAssignableFrom(entity),
                $"{entity.Name} does not declare tenant ownership.");

            var property = entity.GetProperty("TenantId", BindingFlags.Public | BindingFlags.Instance);

            Assert.NotNull(property);
            Assert.Equal(typeof(TenantId), property.PropertyType);
        }
    }

    [Fact]
    public void Every_persisted_entity_in_the_model_is_tenant_owned()
    {
        // A future entity that forgets tenant ownership would sit outside the isolation boundary, so
        // the set of persisted types is asserted rather than assumed.
        var persistedTypes = typeof(WorkContinuityDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition().Name.StartsWith("DbSet", StringComparison.Ordinal))
            .Select(property => property.PropertyType.GetGenericArguments()[0])
            .ToList();

        Assert.NotEmpty(persistedTypes);

        foreach (var type in persistedTypes)
        {
            Assert.True(
                typeof(ITenantOwned).IsAssignableFrom(type),
                $"{type.Name} is persisted but is not tenant-owned.");
        }
    }

    [Fact]
    public void A_persisted_tenant_id_cannot_be_reassigned_from_outside()
    {
        // Tenant ownership is fixed at creation. A public setter would let application code move a
        // row between tenants after the fact.
        foreach (var entity in TenantOwnedEntities)
        {
            var property = entity.GetProperty("TenantId", BindingFlags.Public | BindingFlags.Instance);

            Assert.NotNull(property);
            Assert.True(
                property.SetMethod is null || !property.SetMethod.IsPublic,
                $"{entity.Name}.TenantId has a public setter.");
        }
    }

    [Fact]
    public void Tenant_owned_entities_expose_no_public_mutators_at_all()
    {
        foreach (var entity in TenantOwnedEntities)
        {
            var publicSetters = entity
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.SetMethod?.IsPublic == true)
                .Select(property => $"{entity.Name}.{property.Name}")
                .ToList();

            Assert.Empty(publicSetters);
        }
    }

    [Fact]
    public void A_connection_stores_no_credential_or_verification_secret()
    {
        // Secrets are never stored in ordinary database fields (invariant 53). A source's verification
        // secret belongs to its connector Gate and Key Vault, so the connection's columns are pinned.
        var properties = typeof(ConnectionRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ConnectionId", "CreatedAtUtc", "SourceSystem", "Status", "TenantId"], properties);
    }

    [Fact]
    public void The_runtime_persistence_path_exposes_no_way_to_disable_row_level_security()
    {
        var infrastructure = typeof(WorkContinuityDbContext).Assembly;

        var suspicious = infrastructure
            .GetTypes()
            .Where(type => type.IsPublic)
            .SelectMany(type => type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(member => $"{type.Name}.{member.Name}"))
            .Where(name =>
                name.Contains("BypassRls", StringComparison.OrdinalIgnoreCase)
                || name.Contains("DisableRls", StringComparison.OrdinalIgnoreCase)
                || name.Contains("DisableRowLevel", StringComparison.OrdinalIgnoreCase)
                || name.Contains("IgnoreTenant", StringComparison.OrdinalIgnoreCase)
                || name.Contains("AllTenants", StringComparison.OrdinalIgnoreCase)
                || name.Contains("CrossTenant", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(suspicious);
    }

    [Fact]
    public void The_tenant_scoped_entry_point_offers_no_overload_that_accepts_a_tenant()
    {
        // If a caller could pass a tenant, the trusted server-side context would stop being the only
        // source of tenant identity.
        var parameters = typeof(ITenantScopedDatabase)
            .GetMethods()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.DoesNotContain(typeof(TenantId), parameters);
        Assert.DoesNotContain(typeof(ITenantContext), parameters);
    }

    [Fact]
    public void The_administrative_path_cannot_read_or_write_tenant_rows()
    {
        // The elevated path is deliberately narrow: schema operations only, no DbSet of its own.
        var members = typeof(DatabaseMigrator)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(method => !method.IsSpecialName && method.DeclaringType == typeof(DatabaseMigrator))
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ForConnection", "GetStatusAsync", "MigrateAsync"], members);

        var administrativeSets = typeof(AdministrativeDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition().Name.StartsWith("DbSet", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(administrativeSets);
    }

    [Fact]
    public void Persistence_errors_expose_no_database_detail()
    {
        var errors = typeof(PersistenceErrors)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(property => property.GetValue(null))
            .Cast<Core.Results.Error>()
            .ToList();

        Assert.NotEmpty(errors);

        foreach (var error in errors)
        {
            // SQL text, identifiers, driver names, connection details, stack frames and PostgreSQL
            // internals. Plain English words such as "constraint" are category vocabulary, not
            // leaked detail, so they are deliberately not on this list.
            foreach (var forbidden in new[]
                {
                    "SELECT ", "INSERT ", "UPDATE ", "DELETE ", "Npgsql", "Host=", "Password=",
                    "pg_", "SQLSTATE", "at Work", "relation \"", "tenant_id =",
                })
            {
                Assert.DoesNotContain(forbidden, error.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void The_runtime_role_is_documented_as_a_non_owner()
    {
        // The value is a role name, not a credential. It exists as a constant so tests and the
        // bootstrap script cannot drift apart.
        Assert.Equal("workcontinuity_app", PersistenceOptions.RuntimeRoleName);
    }

    [Fact]
    public void Auto_migration_on_startup_is_off_unless_explicitly_configured()
    {
        // A running application must not decide on its own to alter a shared schema.
        Assert.False(new PersistenceOptions().AutoMigrateOnStartup);
    }
}
