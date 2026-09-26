namespace WorkContinuity.Infrastructure.Persistence;

/// <summary>
/// The persistence configuration, supplied entirely by configuration or environment.
/// </summary>
/// <remarks>
/// No credential is ever compiled in. Locally the values come from the untracked <c>.env</c>; the
/// deployment Gate replaces that source with managed identity and Key Vault without changing this
/// shape.
/// </remarks>
public sealed class PersistenceOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// The PostgreSQL role normal runtime code connects as. It is not the schema owner and holds
    /// neither SUPERUSER nor BYPASSRLS, which is what makes row-level security effective: an owner
    /// or superuser would silently bypass every policy.
    /// </summary>
    public const string RuntimeRoleName = "workcontinuity_app";

    /// <summary>
    /// Gets or sets the application connection string, used by all tenant-scoped runtime access.
    /// </summary>
    public string ApplicationConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the administrative connection string, used only to apply migrations and for
    /// isolated system maintenance. It is never registered for tenant-scoped runtime access.
    /// </summary>
    public string AdministrativeConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether migrations are applied automatically at startup.
    /// </summary>
    /// <remarks>
    /// Intended for local development convenience only, and false by default. Schema change for the
    /// Human Pilot and production remains an explicit deployment step, so a running application
    /// never decides on its own to alter a shared schema.
    /// </remarks>
    public bool AutoMigrateOnStartup { get; set; }
}
