using System.Text.RegularExpressions;

namespace WorkContinuity.Architecture.Tests;

/// <summary>
/// Gate 3G, offline slice: source rules for the non-production harness at <c>tests/App.Harness.Monday</c> (owner decisions
/// D3G-2 to D3G-6). The harness is new executable code with its own trust boundaries, and living under <c>tests/</c>
/// exempts it from nothing: it is one project that production never references and no image or deployment path names;
/// it has no real provider transport and no way to be given one by configuration; it writes nothing to monday and
/// manages no webhook; it holds no administrative database path and its own statements only read; current work is read
/// through the two active views only; its listeners bind loopback only; it composes no exporter and no request log; and
/// only the PKCE verifier cleanup is scheduled, every five minutes, by a constant. The production guards are unchanged:
/// these are their harness-scoped counterparts.
/// </summary>
public sealed class HarnessBoundaryTests
{
    private const string HarnessDirectory = "tests/App.Harness.Monday/";
    private const string ProjectFile = HarnessDirectory + "App.Harness.Monday.csproj";
    private const string OptionsFile = HarnessDirectory + "Configuration/HarnessOptions.cs";
    private const string HostFile = HarnessDirectory + "Composition/HarnessHost.cs";
    private const string TransportFile = HarnessDirectory + "Provider/ProviderTransport.cs";
    private const string EvidenceFile = HarnessDirectory + "Evidence/EvidenceReaders.cs";
    private const string CleanupFile = HarnessDirectory + "Hosting/VerifierCleanupLoop.cs";
    private const string ProgramFile = HarnessDirectory + "HarnessProgram.cs";
    private const string EntryFile = HarnessDirectory + "Program.cs";
    private const string SecretsDirectory = HarnessDirectory + "Secrets/";
    private const string VaultStoreFile = SecretsDirectory + "KeyVaultSecretStore.cs";
    private const string VaultCodecFile = SecretsDirectory + "KeyVaultSecretCodec.cs";
    private const string LiveDirectory = HarnessDirectory + "Live/";
    private const string LiveTransportFile = LiveDirectory + "LiveTransport.cs";
    private const string TokenSourceFile = LiveDirectory + "EntraCertificateTokenSource.cs";
    private const string AssertionFile = LiveDirectory + "EntraClientAssertion.cs";
    private const string PreflightFile = HarnessDirectory + "Composition/TransportSessionPreflight.cs";
    private const string TlsCertificateFile = HarnessDirectory + "Operator/OperatorTlsCertificate.cs";
    private const string AttestationFile = HarnessDirectory + "Trust/OperatorAttestation.cs";
    private const string FlowFile = HarnessDirectory + "Trust/TrustedFlow.cs";

    // The raw Snapshot tables by name, their entity sets and their records, exactly as the production guard names them.
    private static readonly Regex RawSnapshot = new(
        @"\bsnapshot_(fields|objects)\b|\bSnapshot(Fields|Objects)\b|\bSnapshot(Field|Object)Record\b",
        RegexOptions.CultureInvariant);

    [Fact]
    public void The_harness_is_one_non_production_project_under_tests_that_references_only_what_it_composes()
    {
        var project = new FileInfo(Path.Combine(RepositoryLayout.Root.FullName, ProjectFile));
        Assert.True(project.Exists);

        Assert.Equal(
            ["App.Connectors.Monday", "App.Core", "App.Infrastructure", "App.Web"],
            RepositoryLayout.ProjectReferences(project).Order(StringComparer.Ordinal));
        Assert.Empty(RepositoryLayout.PackageReferences(project));
        Assert.DoesNotContain(RepositoryLayout.RawProjectReferencePaths(project), path => path.Contains("Tests", StringComparison.OrdinalIgnoreCase));

        // An executable that dotnet test never runs and that is never published.
        var text = Read(ProjectFile);
        Assert.Contains("<OutputType>Exe</OutputType>", text, StringComparison.Ordinal);
        Assert.Contains("<IsTestProject>false</IsTestProject>", text, StringComparison.Ordinal);
        Assert.Contains("<IsPublishable>false</IsPublishable>", text, StringComparison.Ordinal);

        // Its internals are visible to its own tests and to no other assembly.
        Assert.Contains("<InternalsVisibleTo Include=\"App.Integration.Tests\" />", text, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(text, @"<InternalsVisibleTo\b"));
        Assert.Contains("tests\\App.Harness.Monday\\App.Harness.Monday.csproj", Read("WorkContinuityEngine.sln"), StringComparison.Ordinal);

        // It is still one of the seven production projects' consumers only: production is exactly what it was.
        Assert.Equal(7, RepositoryLayout.ProductionProjectFiles.Count);

        // The rules below are not vacuous: there is harness source to hold to them.
        Assert.True(HarnessSources().Count >= 12, HarnessSources().Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Nothing_in_production_and_no_image_workflow_or_deployment_path_names_the_harness()
    {
        var root = RepositoryLayout.Root.FullName;
        var files = new DirectoryInfo(Path.Combine(root, "src")).EnumerateFiles("*", SearchOption.AllDirectories)
            .Concat(new DirectoryInfo(Path.Combine(root, ".github")).EnumerateFiles("*", SearchOption.AllDirectories))
            .Concat(new DirectoryInfo(Path.Combine(root, "infra")).EnumerateFiles("*", SearchOption.AllDirectories))
            .Concat(new DirectoryInfo(Path.Combine(root, "eng")).EnumerateFiles("*", SearchOption.AllDirectories))
            .Append(new FileInfo(Path.Combine(root, ".dockerignore")))
            .Where(file => !IsBuildOutput(file))
            .Where(file => file.Extension is ".cs" or ".csproj" or ".json" or ".yml" or ".yaml" or ".ps1" or ".py" or ".sh" or ".bicep" or ".bicepparam" or ".props" or "" or ".dockerignore")
            .ToList();
        Assert.Contains(files, file => file.Name == "Dockerfile");
        Assert.Contains(files, file => file.Name == "ci.yml");
        Assert.Contains(files, file => file.Name == "docker-compose.yml");

        var naming = files
            .Where(file => Regex.IsMatch(File.ReadAllText(file.FullName), @"App\.Harness|Harness\.Monday|WorkContinuity\.Harness", RegexOptions.CultureInvariant))
            .Select(RepositoryLayout.RelativePath)
            .ToList();
        Assert.Empty(naming);

        // Each image publishes exactly its own project, so a project the solution merely lists is never in an image.
        Assert.Contains("RUN dotnet publish src/App.Web/App.Web.csproj", Read("src/App.Web/Dockerfile"), StringComparison.Ordinal);
        Assert.Contains("RUN dotnet publish src/App.Worker/App.Worker.csproj", Read("src/App.Worker/Dockerfile"), StringComparison.Ordinal);
        foreach (var dockerfile in new[] { "src/App.Web/Dockerfile", "src/App.Worker/Dockerfile" })
        {
            Assert.Equal(1, Regex.Count(Read(dockerfile), @"dotnet publish"));
        }

        // Among the test projects only the integration tests reference it, and it references none of them.
        var referencing = new DirectoryInfo(Path.Combine(root, "tests")).EnumerateFiles("*.csproj", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Where(file => RepositoryLayout.ProjectReferences(file).Contains("App.Harness.Monday"))
            .Select(RepositoryLayout.ProjectName)
            .ToList();
        Assert.Equal(["App.Integration.Tests"], referencing);
    }

    [Fact]
    public void The_harness_has_no_real_provider_transport_no_fallback_to_one_and_no_setting_that_enables_one()
    {
        // No network handler, client factory, socket, resolver or browser launch is named anywhere in the harness.
        AssertNamedNowhere(
            "HttpClientHandler", "WinHttpHandler", "MondayHttp", "IHttpClientFactory", "AddHttpClient",
            "HttpWebRequest", "WebRequest.Create", "TcpClient", "ClientWebSocket", "Dns.", "monday.com", "Process.Start",
            "ProcessStartInfo", "UseShellExecute", "DefaultProxy", "new HttpClient()", "new HttpClient {");

        // B-11 (2026-10-06): the one network handler the harness may name is Stage B2's pinned one, in exactly one file,
        // built in exactly one place; everywhere else it stays forbidden. Its settings are pinned by the Stage B2 guard.
        Assert.Equal([LiveTransportFile], Holders(new Regex(@"\bSocketsHttpHandler\b")));
        Assert.Equal(1, Regex.Count(Read(LiveTransportFile), @"new\s+SocketsHttpHandler\b"));

        // The one place a client is built: on the guarded path, never on a handler of the platform's choosing.
        Assert.Equal([TransportFile], Holders(new Regex(@"new\s+HttpClient\s*\(")));
        Assert.Equal(1, Regex.Count(Read(TransportFile), @"new\s+HttpClient\s*\("));
        Assert.Contains("new HttpClient(guarded, disposeHandler: false)", Read(TransportFile), StringComparison.Ordinal);
        Assert.Empty(Holders(new Regex(@"HttpClient\??\s+\w+\s*=\s*new\s*\(")));
        Assert.Equal([HostFile, TransportFile], Holders(new Regex(@"\bHttpClient\b")));

        // The guarded path stands on the refusing transport whenever none was given, and nothing else.
        Assert.Contains("new(transport ?? new RefusingProviderTransport(), diagnostics)", Read(TransportFile), StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(Read(HostFile), @"ProviderClients\.Guard\(seams\.ProviderTransport, diagnostics\)"));
        Assert.Equal(2, Regex.Count(Read(HostFile), @"ProviderClients\.Create\(providerPath\)"));
        Assert.Equal(1, Regex.Count(Read(HostFile), @"seams\.SecretStore \?\? new RefusingSecretStore\(\)"));

        // A seam is only ever read by the harness. No harness code supplies one, and the standalone entry point passes
        // exactly the one flag that runs the loops.
        // B-11 (2026-10-06): under B-13 the standalone entry point also passes the operator certificate it created for
        // its run, in exactly one assignment, in that one file; no other seam is assigned anywhere.
        Assert.Empty(Holders(new Regex(@"\b(ProviderTransport|SecretStore|TransportPublisher|TimeProvider|LogSink|Delay|Clock)\s*=[^=>]")));
        Assert.Equal([ProgramFile], Holders(new Regex(@"\bOperatorCertificate\s*=[^=>]")));
        Assert.Equal(1, Regex.Count(Read(ProgramFile), @"\bOperatorCertificate\s*=[^=>]"));
        Assert.Contains("new HarnessSeams { RunBackboneLoops = true, OperatorCertificate = certificate }", Read(ProgramFile), StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(string.Concat(HarnessSources().Select(Read)), @"new HarnessSeams\b"));

        // Configuration: the keys are read at one boundary, they are exactly these, and none of them is a switch.
        Assert.Equal([OptionsFile], Holders(new Regex(@"\bconfiguration\[")));
        Assert.Equal(
            [
                "CanonicalUserKey=Harness:CanonicalUserId",
                "ClientIdKey=Harness:OAuth:ClientId",
                "DatabaseKey=ConnectionStrings:Postgres",
                "RedirectKey=Harness:OAuth:RedirectUri",
                "TenantKey=Harness:TenantId",
                "TransportKey=ServiceBus:ConnectionString",
                "TransportNamespaceKey=ServiceBus:FullyQualifiedNamespace",
                "WebhookBaseAddressKey=Connectors:Monday:WebhookBaseAddress",
                "WebhookPortKey=Harness:Webhook:ListenPort",
            ],
            Regex.Matches(Read(OptionsFile), @"public const string (\w+Key) = ""([^""]+)"";").Select(match => match.Groups[1].Value + "=" + match.Groups[2].Value).Order(StringComparer.Ordinal));
        Assert.DoesNotMatch(new Regex(@"(?i)\b(live|enable|allow|real)\w*Key\b"), Read(OptionsFile));

        // The process reads its own prefixed environment variables and nothing else: no file, no command line.
        Assert.Contains("new ConfigurationBuilder().AddEnvironmentVariables(\"WCE_HARNESS_\").Build()", Read(EntryFile), StringComparison.Ordinal);
        AssertNamedNowhere("AddJsonFile", "AddCommandLine", "AddUserSecrets", "AddIniFile", "AddXmlFile", "GetEnvironmentVariable", "appsettings");
        Assert.Equal([EntryFile], Holders(new Regex(@"AddEnvironmentVariables")));
        Assert.DoesNotContain("args", Read(EntryFile), StringComparison.Ordinal);
    }

    [Fact]
    public void The_harness_writes_nothing_to_monday_manages_no_webhook_and_sends_no_document_of_its_own()
    {
        var write = new Regex(@"\b(mutation|subscription)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        Assert.Empty(Holders(write));
        AssertNamedNowhere(
            "create_webhook", "delete_webhook", "webhooks:write", ":write", "MondayQueries", "MondayGraphQl", "MondayOperations",
            "GraphQL", "create_item", "change_column", "change_simple_column", "\"query");

        // Positive control: the same pattern finds the production guard that refuses a write.
        Assert.Matches(write, Read("src/App.Connectors.Monday/MondayQueryGuard.cs"));

        // The scopes it asks for are the connector's read-only default, which it does not name or change.
        AssertNamedNowhere("MondayScopePolicy", "boards:read", "me:read");
        Assert.Equal(1, Regex.Count(Read(HostFile), @"new MondayOAuthOptions\(options\.ClientId, options\.RedirectUri\)"));
    }

    [Fact]
    public void The_harness_holds_no_administrative_database_path_and_its_own_statements_only_read_approved_metadata()
    {
        AssertNamedNowhere(
            "PostgresAdmin", "AdministrativeConnectionString", "AdministrativeDbContext", "DatabaseMigrator", "AddDatabaseMigrator",
            "RuntimeRoleBootstrap", "MigrateAsync", "Database.Migrate", "EnsureCreated", "EnsureDeleted", "AutoMigrateOnStartup",
            "IDeadLetterQueue", "RedriveAsync", "PeekAsync", "ExecuteSqlRaw", "ExecuteSqlInterpolated", "ExecuteNonQuery", "ExecuteUpdate", "ExecuteDelete");

        // No statement of the harness changes a schema, a privilege or a row. SQL keywords are written in upper case here.
        Assert.Empty(Holders(new Regex(@"\b(INSERT\s+INTO|UPDATE\s+\w+\s+SET|DELETE\s+FROM|TRUNCATE|DROP|ALTER|CREATE|GRANT|REVOKE|COPY|VACUUM|SET\s+ROLE|SET\s+SESSION)\b", RegexOptions.CultureInvariant)));
        Assert.Empty(Holders(new Regex(@"set_config|SaveChanges|\.Add\(new \w+Record|\.Remove\(", RegexOptions.CultureInvariant)));

        // Every statement lives in the evidence readers, is a single fixed SELECT, and takes no parameter and no text.
        Assert.Equal([EvidenceFile], Holders(new Regex(@"\bSELECT\b", RegexOptions.CultureInvariant)));
        var statements = Statements();
        Assert.Equal(13, statements.Count);
        Assert.All(statements, statement =>
        {
            Assert.StartsWith("SELECT ", statement, StringComparison.Ordinal);
            Assert.DoesNotContain(";", statement, StringComparison.Ordinal);
            Assert.DoesNotContain("@", statement, StringComparison.Ordinal);
            Assert.DoesNotContain("{", statement, StringComparison.Ordinal);
        });
        Assert.Equal(1, Regex.Count(Read(EvidenceFile), @"command\.CommandText = sql;"));
        var executed = Regex.Matches(Read(EvidenceFile), @"EvidenceSql\.ReadAsync\(\s*dbContext,\s*(\w+)").Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(13, executed.Count);
        Assert.All(executed, name => Assert.EndsWith("Sql", name, StringComparison.Ordinal));
        Assert.Equal(executed.Count, executed.Distinct(StringComparer.Ordinal).Count());

        // The relations it names are the two active views, the operational tables and the role catalog, and no other.
        // Stage B3 (owner decision B-14) adds the membership catalog, read only by the recursive membership statement,
        // whose own common table expressions are named in it and are not relations. Exactly those four names are set
        // aside, and only because that one statement declares them. The owner's Decision 3 of 2026-10-06 adds the database,
        // schema and relation catalogs, read only by the role statement to find what the role owns.
        var declared = statements
            .SelectMany(statement => Regex.Matches(statement, @"(?:WITH RECURSIVE |\), )([a-z_]+) \([a-z_, ]+\) AS \(").Select(match => match.Groups[1].Value))
            .ToList();
        Assert.Equal(["role_facts", "memberships", "current_role_row", "granted"], declared);
        Assert.Single(statements, statement => statement.Contains("WITH RECURSIVE ", StringComparison.Ordinal));
        var relations = statements
            .SelectMany(statement => Regex.Matches(statement, @"\b(?:FROM|JOIN)\s+([a-z_]+)").Select(match => match.Groups[1].Value))
            .Where(name => !declared.Contains(name, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            [
                "active_snapshot_fields", "active_snapshot_objects", "connection_sync_scopes", "connection_syncs", "connector_authorization_transactions",
                "connector_authorizations", "connector_source_scopes", "event_ledger", "pg_auth_members", "pg_class", "pg_database", "pg_namespace",
                "pg_roles", "pg_tables", "raw_receipts", "reconciliation_targets",
            ],
            relations);

        // No payload, no provider identifier and no stored hash is selected; a value is selected from the current work
        // view only.
        foreach (var column in new[] { "payload", "provider_subject", "provider_account", "state_hash", "redirect_hash", "granted_scopes", "material_version", "source_event_id", "correlation_id" })
        {
            Assert.All(statements, statement => Assert.DoesNotMatch(new Regex(@"\b" + column + @"\b"), statement));
        }

        Assert.Equal(["active_snapshot_fields"], statements.Where(statement => Regex.IsMatch(statement, @"\bvalue\b")).Select(statement => Regex.Match(statement, @"\bFROM\s+([a-z_]+)").Groups[1].Value));

        // The harness never runs as anything other than the plain runtime role (owner decisions D3G-2, D3G-4 and B-14,
        // hardened by the owner's Decision 3 of 2026-10-06): any one of the eleven facts refuses the start, the role
        // statement reads each of them exactly as pinned here, and membership in any role at all is disqualifying. And no
        // database is approved for a standalone run: the list is empty.
        Assert.Contains(
            "public bool IsPrivileged =>\n"
            + "        Superuser || BypassesRowSecurity || CreatesRoles || CreatesDatabases || Replicates || InheritsPrivileges || CannotLogIn\n"
            + "        || OwnsDatabase || OwnsSchema || OwnsRelation || MemberOfAnyRole;",
            Read(EvidenceFile),
            StringComparison.Ordinal);
        Assert.Contains(
            "SELECT r.rolsuper, r.rolbypassrls, r.rolcreaterole, r.rolcreatedb, r.rolreplication, r.rolinherit, NOT r.rolcanlogin, "
            + "EXISTS (SELECT 1 FROM pg_database AS d WHERE d.datname = current_database() AND pg_has_role(current_user, d.datdba, 'MEMBER')), "
            + "EXISTS (SELECT 1 FROM pg_namespace AS n WHERE pg_has_role(current_user, n.nspowner, 'MEMBER')), "
            + "EXISTS (SELECT 1 FROM pg_class AS c WHERE c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f') AND c.relpersistence <> 't' "
            + "AND pg_has_role(current_user, c.relowner, 'MEMBER')) "
            + "FROM pg_roles AS r WHERE r.rolname = current_user",
            statements);
        Assert.Contains("? role[0] with { MemberOfAnyRole = RoleMembershipFacts.IsPrivileged(membership) }", Read(EvidenceFile), StringComparison.Ordinal);
        Assert.Contains("            || rows[0].GrantedRoles != 0\n", Read(EvidenceFile), StringComparison.Ordinal);
        Assert.Contains(
            "if (privileged.Value)\n            {\n                return HarnessRefusal.PrivilegedDatabaseRole;\n            }",
            Read(HostFile),
            StringComparison.Ordinal);
        Assert.Contains("private static readonly string[] ApprovedDatabases = [];", Read(OptionsFile), StringComparison.Ordinal);
        Assert.Contains("return ApprovedDatabases.Contains(options.DatabaseName, StringComparer.Ordinal);", Read(OptionsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Current_work_is_read_only_through_the_two_active_views_and_the_raw_snapshot_is_named_nowhere()
    {
        Assert.Empty(Holders(RawSnapshot));

        // Positive control: the same pattern finds the projection, which owns the raw tables.
        Assert.Matches(RawSnapshot, Read("src/App.Infrastructure/Processing/SnapshotProjection.cs"));

        // The views are named in the evidence readers only, each by one statement of the current work reader.
        Assert.Equal([EvidenceFile], Holders(new Regex(@"snapshot", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)).Where(path => Regex.IsMatch(Read(path), @"active_snapshot_")));
        Assert.Equal(1, Statements().Count(statement => statement.Contains("FROM active_snapshot_fields", StringComparison.Ordinal)));
        Assert.Equal(1, Statements().Count(statement => statement.Contains("FROM active_snapshot_objects", StringComparison.Ordinal)));

        // The context's entity sets are not used at all: the harness reads through its fixed statements only.
        Assert.Empty(Holders(new Regex(@"dbContext\.(?!Database\b)\w+", RegexOptions.CultureInvariant)));
    }

    [Fact]
    public void Both_listeners_bind_loopback_only_and_no_exporter_request_log_or_framework_logging_is_composed()
    {
        AssertNamedNowhere(
            "IPAddress.Any", "IPAddress.IPv6Any", "IPv6Loopback", "0.0.0.0", "[::]", "ListenAnyIP", "ListenLocalhost", "UseUrls(", "http://*", "http://+",
            "https://*", "https://+", "ASPNETCORE_URLS", "ForwardedHeaders", "UseHsts", "ngrok", "tunnel");
        Assert.Equal([HostFile], Holders(new Regex(@"\.Listen\(")));
        Assert.Equal(2, Regex.Count(Read(HostFile), @"kestrel\.Listen\(IPAddress\.Loopback, options\.(WebhookPort|OperatorPort)"));
        Assert.Equal(2, Regex.Count(Read(HostFile), @"\.Listen\("));
        Assert.Equal(1, Regex.Count(Read(HostFile), @"listen => listen\.UseHttps\(certificate\)"));

        // No certificate is taken from the machine: the only one is the seam's.
        // B-11 (2026-10-06): under B-13 the one certificate the harness creates is the standalone run's ephemeral operator
        // certificate, in exactly one file, created once and reloaded once from memory with the default key storage. No
        // store, file, key storage flag or development certificate is named anywhere.
        AssertNamedNowhere(
            "X509Store", "StoreName.", "StoreLocation.", "dev-certs", "UseHttps()", "X509KeyStorageFlags", "PersistKeySet", "MachineKeySet",
            "Exportable", ".pfx", ".pem", "LoadCertificateFromFile", "LoadPkcs12FromFile");
        Assert.Equal([TlsCertificateFile], Holders(new Regex(@"X509CertificateLoader|CreateSelfSigned")));
        Assert.Equal(1, Regex.Count(Read(TlsCertificateFile), @"X509CertificateLoader\."));
        Assert.Equal(1, Regex.Count(Read(TlsCertificateFile), @"CreateSelfSigned\("));
        Assert.Contains("return X509CertificateLoader.LoadPkcs12(pkcs12, password: null);", Read(TlsCertificateFile), StringComparison.Ordinal);

        // No exporter, no request log, no framework log provider, and no host that reads ambient configuration.
        AssertNamedNowhere(
            "OpenTelemetry", "AzureMonitor", "APPLICATIONINSIGHTS", "AddHttpLogging", "UseHttpLogging", "W3CLogging", "UseDeveloperExceptionPage",
            "AddConsole", "AddSimpleConsole", "AddDebug", "AddEventSourceLogger", "AddEventLog", "LoggerFactory.Create", "ClearProviders",
            "SetMinimumLevel", "AddFilter", "ActivityListener", "MeterListener", "EventListener", "CreateBuilder(", "CreateSlimBuilder(",
            "CreateDefaultBuilder", "UseDefaultServiceProvider");
        Assert.Equal(2, Regex.Count(Read(HostFile), @"WebApplication\.CreateEmptyBuilder\("));

        // Each of the three service containers is given the harness's logger factory in place of the framework's.
        Assert.Equal(2, Regex.Count(Read(HostFile), @"builder\.Services\.Replace\(ServiceDescriptor\.Singleton<ILoggerFactory>\(loggers\)\);"));
        Assert.Equal(1, Regex.Count(Read(HostFile), @"services\.AddSingleton<ILoggerFactory>\(loggers\);"));
        Assert.Equal(1, Regex.Count(Read(HostFile), @"new HarnessLoggerFactory\("));
        var logging = Read(HarnessDirectory + "Hosting/HarnessLogging.cs");
        Assert.Contains("HarnessLogPolicy.Allows(categoryName) ? new SinkLogger(categoryName, this) : NullLogger.Instance", logging, StringComparison.Ordinal);
        Assert.Contains("public const string AllowedPrefix = \"WorkContinuity.\";", logging, StringComparison.Ordinal);

        // A record reaches the sink in one place only, after the policy decided whose it is: the configured tenant's or
        // the harness's own. The factory is given the configured tenant and only reads the logging flow's.
        Assert.Equal(1, Regex.Count(logging, @"_sink\.Write\("));
        Assert.Equal(1, Regex.Count(logging, @"if \(!HarnessLogPolicy\.Shows\(owner\)\)"));
        var decided = logging.IndexOf("if (!HarnessLogPolicy.Shows(owner))", StringComparison.Ordinal);
        var written = logging.IndexOf("_sink.Write(", StringComparison.Ordinal);
        Assert.True(decided > 0 && written > decided, $"{decided} {written}");
        Assert.Contains(
            "public static bool Shows(LogRecordOwner owner) => owner is LogRecordOwner.Harness or LogRecordOwner.ConfiguredTenant;",
            logging,
            StringComparison.Ordinal);
        Assert.Contains("diagnostics, options.Tenant, new TenantContextAccessor());", Read(HostFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_verifier_cleanup_is_scheduled_at_five_minutes_by_a_constant_and_everything_else_is_an_operator_action()
    {
        // The interval: one constant, in one place, read from nothing.
        // B-11 (2026-10-06): five minutes otherwise appears only as two pinned Stage B2 constants, the assertion's lifetime
        // and the token refresh margin, once each; neither schedules anything.
        Assert.Equal([CleanupFile, TokenSourceFile, AssertionFile], Holders(new Regex(@"TimeSpan\.FromMinutes\(5\)")));
        Assert.Equal(1, Regex.Count(Read(CleanupFile), @"TimeSpan\.FromMinutes\(5\)"));
        Assert.Contains("public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);", Read(CleanupFile), StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(Read(AssertionFile), @"TimeSpan\.FromMinutes\(5\)"));
        Assert.Contains("internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);", Read(AssertionFile), StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(Read(TokenSourceFile), @"TimeSpan\.FromMinutes\(5\)"));
        Assert.Contains("public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);", Read(TokenSourceFile), StringComparison.Ordinal);
        Assert.Contains("new PeriodicTimer(Interval, _time)", Read(CleanupFile), StringComparison.Ordinal);
        foreach (var source in new[] { "IConfiguration", "HarnessOptions", "HarnessSeams", "configuration" })
        {
            Assert.DoesNotContain(source, Read(CleanupFile), StringComparison.Ordinal);
        }

        // Start-up first, awaited, then the timer.
        var host = Read(HostFile);
        var startup = host.IndexOf("await cleanup.RunStartupAsync(cancellationToken);", StringComparison.Ordinal);
        var periodic = host.IndexOf("_cleanup = DetachedFlow.Run(() => cleanup.RunPeriodicallyAsync(_stopping.Token));", StringComparison.Ordinal);
        Assert.True(startup > 0 && periodic > startup, $"{startup} {periodic}");

        // Each run is the operator action, and so goes through the trusted flow: the one loop is given nothing else to run.
        Assert.Equal(
            1,
            Regex.Count(host, @"new VerifierCleanupLoop\(actions\.CleanUpVerifiersAsync, seams\.TimeProvider \?\? TimeProvider\.System, _diagnostics, loggers\)"));
        Assert.Equal(1, Regex.Count(string.Concat(HarnessSources().Select(Read)), @"new VerifierCleanupLoop\("));
        Assert.Equal(1, Regex.Count(Read(CleanupFile), @"await _cleanUp\(cancellationToken\)"));
        Assert.DoesNotContain("MondayConnectionAuthorizer", Read(CleanupFile), StringComparison.Ordinal);

        // Nothing else is scheduled: no other timer, no hosted service, no sleep.
        Assert.Equal([CleanupFile], Holders(new Regex(@"PeriodicTimer")));
        AssertNamedNowhere("BackgroundService", "IHostedService", "AddHostedService", "Task.Delay", "Thread.Sleep", "System.Timers", "new Timer(", "Stopwatch");

        // Initial sync, reconciliation, sweep and scope configuration are reached only from the operator actions.
        foreach (var port in new[] { "IInitialSync>", "ISourceReconciler>", "IConnectorSourceScopes>" })
        {
            Assert.Equal([HarnessDirectory + "Operator/OperatorActions.cs"], Holders(new Regex(Regex.Escape("GetRequiredService<" + port))));
        }

        // The continuous relay and the consumer are the only loops, started on flows of their own, and only when asked.
        Assert.Equal(1, Regex.Count(host, @"_relay = DetachedFlow\.Run\(\(\) => relay\.RunAsync\(_stopping\.Token\)\);"));
        Assert.Equal(1, Regex.Count(host, @"await DetachedFlow\.Run\(\(\) => consumerHost\.StartAsync\(cancellationToken\)\);"));
        Assert.Equal(1, Regex.Count(host, @"if \(seams\.RunBackboneLoops\)"));
        Assert.Equal(1, Regex.Count(host, @"transport\.ConsumerSessionIds\.Add\(TransportEnvelope\.SessionIdFor\(options\.Tenant\)\)"));
    }

    [Fact]
    public void The_key_vault_adapter_is_offline_unwired_and_can_send_only_get_set_and_delete_to_one_vault()
    {
        // Owner decisions S-1 to S-5 (2026-10-05). Placement: exactly three files, and nothing outside them names the
        // adapter, its codec or its token seam; nothing in the harness constructs it, so it is not wired.
        Assert.Equal(
            [SecretsDirectory + "IKeyVaultAccessTokenSource.cs", VaultCodecFile, VaultStoreFile],
            HarnessSources().Where(path => path.StartsWith(SecretsDirectory, StringComparison.Ordinal)));
        // B-11 (2026-10-06): outside the adapter's own directory, only the two named Stage B2 files may name Key Vault (the
        // token source implements the adapter's token seam; the vault destination guard checks the adapter's requests).
        Assert.Equal(
            [TokenSourceFile, LiveTransportFile],
            HarnessSources().Where(path => !path.StartsWith(SecretsDirectory, StringComparison.Ordinal) && Read(path).Contains("KeyVault", StringComparison.Ordinal)));
        Assert.Empty(Holders(new Regex(@"new\s+KeyVaultSecretStore\s*\(")));

        // Transport: one invoker over the handler it is given; it builds no handler and no client of its own.
        // B-11 (2026-10-06): the Stage B2 token source is the only other invoker, built the same way over its given handler.
        var store = Read(VaultStoreFile);
        Assert.Equal([TokenSourceFile, VaultStoreFile], Holders(new Regex(@"\bHttpMessageInvoker\b")));
        foreach (var invoking in new[] { VaultStoreFile, TokenSourceFile })
        {
            Assert.Equal(1, Regex.Count(Read(invoking), @"new HttpMessageInvoker\("));
            Assert.Contains("new HttpMessageInvoker(_transport, disposeHandler: false)", Read(invoking), StringComparison.Ordinal);
        }

        // B-11 (2026-10-06): no handler is constructed anywhere, in either constructor form, except the one pinned network
        // handler of Stage B2, once, in its one file.
        Assert.Equal([LiveTransportFile], Holders(new Regex(@"new\s+\w*Handler\b")));
        Assert.Equal(1, Regex.Count(Read(LiveTransportFile), @"new\s+\w*Handler\b"));

        // Requests: get, set and delete, one place each, to one pinned path shape on the configured vault.
        // B-11 (2026-10-06): an HTTP method is otherwise named only by the Stage B2 token source (its one POST) and the
        // Stage B2 destination guards (the methods each admits); the adapter's own set is unchanged.
        Assert.Equal([TokenSourceFile, LiveTransportFile, VaultStoreFile], Holders(new Regex(@"HttpMethod\.")));
        Assert.Equal(["Delete", "Get", "Put"], Regex.Matches(store, @"HttpMethod\.(\w+)").Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
        Assert.Equal(["Post"], Regex.Matches(Read(TokenSourceFile), @"HttpMethod\.(\w+)").Select(match => match.Groups[1].Value));
        Assert.Equal(
            ["Delete", "Get", "Post", "Post", "Put"],
            Regex.Matches(Read(LiveTransportFile), @"HttpMethod\.(\w+)").Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
        Assert.Equal([TokenSourceFile, LiveTransportFile], Holders(new Regex(@"HttpMethod\.Post\b")));
        AssertNamedNowhere(
            "deletedsecrets", "/recover", "/backup", "/restore", "purge", "Purge", "maxresults", "/versions", "outContentType",
            "HttpMethod.Patch", "HttpMethod.Head");
        Assert.Contains("public const string ApiVersion = \"2025-07-01\";", store, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(store, @"""\?api-version="" \+ ApiVersion"));
        Assert.Equal(1, Regex.Count(store, @"new Uri\("));
        Assert.Contains("new Uri(_vault, path + ApiVersionQuery)", store, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(store, @"""secrets/"""));
        Assert.Contains("private static string ItemPath(MondaySecretName name) => \"secrets/\" + name.Value;", store, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(store, @"ItemPath\(name\) \+ ""/"""));
        Assert.Contains("private const string VaultSuffix = \".vault.azure.net\";", store, StringComparison.Ordinal);
        Assert.Contains("vault.Scheme != Uri.UriSchemeHttps", store, StringComparison.Ordinal);
        Assert.Contains("!vault.IsDefaultPort", store, StringComparison.Ordinal);

        // Status mapping and attempts (S-4, S-5): only a Key Vault error body makes a 404 not-found; exactly five
        // statuses are retried, a conflict is never named; the bounds are the approved constants.
        Assert.Contains(
            "HttpStatusCode.NotFound => AttemptResult.Settled(IsKeyVaultError(body) ? Outcome.NotFound : Outcome.Unavailable, null),",
            store,
            StringComparison.Ordinal);
        Assert.Equal(
            ["BadGateway", "GatewayTimeout", "InternalServerError", "ServiceUnavailable", "TooManyRequests"],
            Regex.Matches(Regex.Match(store, @"HttpStatusCode\.TooManyRequests[\s\S]*?=> AttemptResult\.Retry").Value, @"HttpStatusCode\.(\w+)")
                .Select(match => match.Groups[1].Value)
                .Order(StringComparer.Ordinal));
        Assert.DoesNotContain("Conflict", store, StringComparison.Ordinal);
        foreach (var constant in new[]
                 {
                     "public const int MaxAttempts = 3;",
                     "public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);",
                     "public static readonly TimeSpan GetBudget = TimeSpan.FromSeconds(10);",
                     "public static readonly TimeSpan WriteBudget = TimeSpan.FromSeconds(15);",
                     "public static readonly TimeSpan RetryAfterCap = TimeSpan.FromSeconds(2);",
                     "private static readonly TimeSpan[] DefaultWaits = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800)];",
                 })
        {
            Assert.Contains(constant, store, StringComparison.Ordinal);
        }

        // Encoding (S-3): one content type, one bound, plain text only under the two owner-placed names.
        var codec = Read(VaultCodecFile);
        Assert.Contains("public const string ContentType = \"application/vnd.wce.opaque-bytes+base64url;v=1\";", codec, StringComparison.Ordinal);
        Assert.Contains("public const int MaxValueBytes = 18_000;", codec, StringComparison.Ordinal);
        Assert.Contains("return IsOwnerPlaced(name) ? DecodePlain(text) : MondaySecretLookup.Unavailable();", codec, StringComparison.Ordinal);
        Assert.Contains("name.Equals(MondaySecretName.ClientSecret) || name.Equals(MondaySecretName.SigningSecret)", codec, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(codec, @"DecodePlain\(text\)"));

        // No log, no cache and no state of its own: nothing to leak and nothing to serve stale.
        foreach (var path in HarnessSources().Where(path => path.StartsWith(SecretsDirectory, StringComparison.Ordinal)))
        {
            Assert.DoesNotMatch(new Regex(@"\bILogger\b|\bConsole\.|\bDebug\.|\bTrace\.|Dictionary|MemoryCache|Lazy<|HashSet"), Read(path));
            // A static field is read-only or a constant: a mutable static field is the only place a value could outlive a call.
            Assert.DoesNotMatch(new Regex(@"\bstatic\s+(?!readonly\b)[\w<>\[\]?,]+\s+\w+\s*[=;]"), Read(path));
        }
    }

    [Fact]
    public void Stage_b2_transport_and_token_source_are_pinned_explicit_and_unwired()
    {
        // Owner decisions B-1, B-7, B-10 and B-11 (2026-10-06). Placement: exactly three files under Live/.
        Assert.Equal(
            [TokenSourceFile, AssertionFile, LiveTransportFile],
            HarnessSources().Where(path => path.StartsWith(LiveDirectory, StringComparison.Ordinal)));

        // Not wired: nothing outside Live/ names any Stage B2 type or namespace, so no host, entry point, option, gate or
        // console can reach it; nothing anywhere constructs the token source or asks for a live transport.
        var stageB2 = new Regex(
            @"Harness\.Monday\.Live\b|\bLiveTransport\b|\bEntraCertificateTokenSource\b|\bEntraClientAssertion\b|\bEntraTokenDestination\b|\bKeyVaultDestination\b|\bMondayDestination\b|\bDestinationGuard\b",
            RegexOptions.CultureInvariant);
        Assert.DoesNotContain(HarnessSources(), path => !path.StartsWith(LiveDirectory, StringComparison.Ordinal) && stageB2.IsMatch(Read(path)));
        Assert.Empty(Holders(new Regex(@"new\s+EntraCertificateTokenSource\s*\(")));
        Assert.Empty(Holders(new Regex(@"LiveTransport\.For\w+\(")));
        Assert.Contains("private static readonly string[] ApprovedDatabases = [];", Read(OptionsFile), StringComparison.Ordinal);
        Assert.Contains("new HarnessSeams { RunBackboneLoops = true, OperatorCertificate = certificate }", Read(ProgramFile), StringComparison.Ordinal);

        // No ambient or alternative credential anywhere in the harness: only the explicit certificate credential.
        AssertNamedNowhere(
            "DefaultAzureCredential", "Azure.Identity", "Azure.Core", "TokenCredential", "ClientCertificateCredential", "ClientSecretCredential",
            "ManagedIdentity", "AzureCliCredential", "AzurePowerShell", "AzureDeveloperCli", "EnvironmentCredential", "VisualStudioCredential",
            "InteractiveBrowser", "DeviceCode", "WorkloadIdentity", "Microsoft.Identity", "Msal", "ConfidentialClientApplication",
            "PublicClientApplication", "client_secret", "UseDefaultCredentials", "DefaultCredentials", "CredentialCache", "AZURE_",
            "IDENTITY_ENDPOINT", "MSI_ENDPOINT", "169.254.169.254");

        // Nothing in Live/ reads configuration, the environment or the machine's certificate stores, or logs, caches or holds
        // mutable static state.
        foreach (var path in new[] { TokenSourceFile, AssertionFile, LiveTransportFile })
        {
            var text = Read(path);
            Assert.DoesNotMatch(new Regex(@"IConfiguration|configuration\[|GetSection\(|HarnessOptions|HarnessSeams|Environment\.|X509Store|X509CertificateLoader|\.pfx|\.pem"), text);
            Assert.DoesNotMatch(new Regex(@"\bILogger\b|\bConsole\.|\bDebug\.|\bTrace\.|Dictionary|MemoryCache|Lazy<|HashSet|File\.|Directory\."), text);
            Assert.DoesNotMatch(new Regex(@"\bstatic\s+(?!readonly\b)[\w<>\[\]?,]+\s+\w+\s*[=;]"), text);
        }

        // The pinned handler: exactly these settings, no certificate-validation override and no connection hook.
        var transport = Read(LiveTransportFile);
        foreach (var setting in new[]
                 {
                     "AllowAutoRedirect = false,", "UseCookies = false,", "UseProxy = false,", "Proxy = null,", "Credentials = null,",
                     "PreAuthenticate = false,", "ConnectTimeout = ConnectTimeout,", "AutomaticDecompression = DecompressionMethods.None,",
                     "MaxResponseHeadersLength = MaxResponseHeadersKilobytes,", "ActivityHeadersPropagator = null,",
                     "public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);",
                 })
        {
            Assert.Equal(1, Regex.Count(transport, Regex.Escape(setting)));
        }

        AssertNamedNowhere(
            "ServerCertificateCustomValidationCallback", "RemoteCertificateValidationCallback", "DangerousAcceptAnyServerCertificateValidator",
            "SslOptions", "ConnectCallback", "PlaintextStreamFilter", "CheckCertificateRevocationList", "SslProtocols", "AllowAutoRedirect = true",
            "UseCookies = true", "UseProxy = true", "PreAuthenticate = true", "CookieContainer", "WebProxy");

        // Each destination has its own guard over its own pinned handler, and every guard admits by exact shape.
        Assert.Contains("public static HttpMessageHandler ForEntraToken(Guid tenant) => new EntraTokenDestination(tenant, CreatePinnedHandler());", transport, StringComparison.Ordinal);
        Assert.Contains("public static HttpMessageHandler ForKeyVault(Uri vault) => new KeyVaultDestination(vault, CreatePinnedHandler());", transport, StringComparison.Ordinal);
        Assert.Contains("public static HttpMessageHandler ForMonday() => new MondayDestination(CreatePinnedHandler());", transport, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Count(transport, @"CreatePinnedHandler\(\)\)"));
        Assert.Equal(1, Regex.Count(transport, @"if \(!Admits\(request\)\)"));
        Assert.Contains("&& address.Scheme == Uri.UriSchemeHttps", transport, StringComparison.Ordinal);
        Assert.Contains("&& address.IsDefaultPort", transport, StringComparison.Ordinal);
        Assert.Contains("&& request.Headers.Authorization is null", transport, StringComparison.Ordinal);
        Assert.Contains("request.Headers.Authorization is not { Scheme: \"Bearer\", Parameter.Length: > 0 }", transport, StringComparison.Ordinal);
        Assert.Contains("if (!KeyVaultSecretStore.IsVaultOrigin(vault))", transport, StringComparison.Ordinal);

        // The token source: one explicit credential, one endpoint, one scope, one grant; the documented form, no secret.
        var source = Read(TokenSourceFile);
        foreach (var pinned in new[]
                 {
                     "public const string AuthorityHost = \"https://login.microsoftonline.com/\";",
                     "public const string Scope = \"https://vault.azure.net/.default\";",
                     "public const string GrantType = \"client_credentials\";",
                     "public const string AssertionType = \"urn:ietf:params:oauth:client-assertion-type:jwt-bearer\";",
                     "public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);",
                     "public static Uri TokenEndpointFor(Guid tenant) => new(AuthorityHost + tenant.ToString(\"D\") + \"/oauth2/v2.0/token\", UriKind.Absolute);",
                     "using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)",
                     "if (!EntraClientAssertion.IsSuitable(certificate))",
                 })
        {
            Assert.Equal(1, Regex.Count(source, Regex.Escape(pinned)));
        }

        Assert.Equal(
            ["client_assertion", "client_assertion_type", "client_id", "grant_type", "scope"],
            Regex.Matches(source, @"new\(""(\w+)"",").Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
        Assert.Equal(1, Regex.Count(source, @"\bnew Uri\(|\bnew\(AuthorityHost"));

        // The assertion: the documented PS256 form with the SHA-256 thumbprint; no other algorithm, padding or thumbprint.
        var assertion = Read(AssertionFile);
        foreach (var pinned in new[]
                 {
                     "internal const string Algorithm = \"PS256\";",
                     "writer.WriteString(\"x5t#S256\", Base64Url.EncodeToString(SHA256.HashData(certificate.RawData)));",
                     "key.SignData(System.Text.Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)",
                     "internal const int MinimumKeySize = 2048;",
                     "return key is not null && key.KeySize >= MinimumKeySize;",
                     "if (client == Guid.Empty || id == Guid.Empty || !IsSuitable(certificate) || !IsValidAt(certificate, now))",
                 })
        {
            Assert.Equal(1, Regex.Count(assertion, Regex.Escape(pinned)));
        }

        Assert.DoesNotMatch(new Regex(@"RS256|Pkcs1|SHA1|HMAC|""x5t""|""none""|ECDsa|GetECDsa"), assertion);
    }

    [Fact]
    public void Stage_b3_keeps_the_standalone_gate_empty_and_the_harness_broker_use_to_a_peek_of_its_own_session()
    {
        // The standalone gate (owner decisions D3G-4 and B-14): its list is empty, it reads no setting and no environment
        // variable, and nothing else decides whether a standalone run may use a database.
        var options = Read(OptionsFile);
        var gate = options[options.IndexOf("public static class HarnessStandaloneGate", StringComparison.Ordinal)..];
        Assert.Equal(
            "public static class HarnessStandaloneGate\n{\n    private static readonly string[] ApprovedDatabases = [];\n\n"
            + "    /// <summary>Whether a standalone run may use the options' database.</summary>\n"
            + "    public static bool Permits(HarnessOptions options)\n    {\n        ArgumentNullException.ThrowIfNull(options);\n"
            + "        return ApprovedDatabases.Contains(options.DatabaseName, StringComparer.Ordinal);\n    }\n}\n",
            gate);
        Assert.Equal(2, Regex.Count(string.Concat(HarnessSources().Select(Read)), @"\bApprovedDatabases\b"));
        Assert.Empty(Holders(new Regex(@"GetEnvironmentVariable|Environment\.GetCommandLineArgs|wce_harness_", RegexOptions.CultureInvariant)));
        Assert.Equal(1, Regex.Count(Read(ProgramFile), @"if \(!HarnessStandaloneGate\.Permits\(options\)\)"));

        // The harness names the broker's client in two places only: the host, which hands the composed client to the check,
        // and the check itself.
        Assert.Equal([HostFile, PreflightFile], Holders(new Regex(@"\bServiceBusClient\b", RegexOptions.CultureInvariant)));
        Assert.Equal(1, Regex.Count(Read(HostFile), @"Worker\.GetService<Azure\.Messaging\.ServiceBus\.ServiceBusClient>\(\) is \{ \} broker"));
        Assert.Equal(1, Regex.Count(Read(HostFile), @"TransportSessionPreflight\.CheckAsync\(broker, options\.Tenant, cancellationToken\)"));

        // The check takes exactly the configured tenant's session, in peek-lock mode with no prefetch, peeks one message
        // and settles, receives, sends and processes nothing.
        var preflight = Read(PreflightFile);
        Assert.Equal(1, Regex.Count(preflight, @"client\.AcceptSessionAsync\(\s*TransportContract\.EventProcessingQueue,\s*TransportEnvelope\.SessionIdFor\(tenant\),"));
        Assert.Equal(1, Regex.Count(preflight, Regex.Escape("new ServiceBusSessionReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock, PrefetchCount = 0 }")));
        Assert.Equal(1, Regex.Count(preflight, Regex.Escape("var waiting = await session.PeekMessageAsync(cancellationToken: bounded.Token);")));
        Assert.Equal(1, Regex.Count(preflight, Regex.Escape("return waiting is null ? HarnessRefusal.None : HarnessRefusal.TransportSessionNotEmpty;")));
        Assert.Equal(1, Regex.Count(preflight, Regex.Escape("internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);")));
        Assert.DoesNotMatch(
            new Regex(@"ReceiveMessage|ReceiveDeferred|Complete|Abandon|DeadLetter|Defer|RenewSessionLock|SetSessionState|PeekMessagesAsync|AcceptNextSession|CreateSender|CreateReceiver|CreateProcessor|CreateSessionProcessor|ReceiveAndDelete|SubQueue|Purge"),
            preflight);
        Assert.DoesNotMatch(
            new Regex(@"ServiceBusReceiver\b|ServiceBusSender\b|ServiceBusProcessor\b|AcceptSessionAsync|PeekMessage"),
            string.Concat(HarnessSources().Where(path => path != PreflightFile).Select(Read)));
    }

    [Fact]
    public void Stage_b4_names_the_operator_honestly_and_gives_each_standalone_run_one_ephemeral_loopback_certificate()
    {
        // B-12 (2026-10-06): the operator's identity provider is the harness's own DEV/TEST key, named once, in the one place
        // the operator principal is built. The local demo provider, and every other provider, is named nowhere.
        AssertNamedNowhere("LocalDemo", "local-demo", "EntraId");
        Assert.Equal([FlowFile], Holders(new Regex(@"\bIdentityProviderId\b")));
        Assert.Equal(1, Regex.Count(Read(FlowFile), @"\bIdentityProviderId\."));
        Assert.Contains("public const string OperatorProviderKey = \"wce-dev-harness\";", Read(FlowFile), StringComparison.Ordinal);
        Assert.Contains("new ExternalIdentity(IdentityProviderId.From(OperatorProviderKey), OperatorSubject)", Read(FlowFile), StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(string.Concat(HarnessSources().Select(Read)), @"new ExternalIdentity\("));

        // The per-run attestation: one exact answer, compared exactly, in one file.
        var attestation = Read(AttestationFile);
        Assert.Contains("public const string Answer = \"attest\";", attestation, StringComparison.Ordinal);
        Assert.Contains("public static bool IsGiven(string? line) => string.Equals(line, Answer, StringComparison.Ordinal);", attestation, StringComparison.Ordinal);

        // B-13 (2026-10-06): the certificate names the loopback address and nothing else, is valid for one day at most, is
        // no authority, and its PKCS#12 copy is cleared once loaded.
        var certificate = Read(TlsCertificateFile);
        foreach (var pinned in new[]
                 {
                     "public static readonly TimeSpan Validity = TimeSpan.FromDays(1);",
                     "public static readonly TimeSpan Backdating = TimeSpan.FromMinutes(1);",
                     "using var created = request.CreateSelfSigned(notBefore, notBefore + Validity);",
                     "names.AddIpAddress(IPAddress.Loopback);",
                     "new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true)",
                     "new X509EnhancedKeyUsageExtension([new Oid(ServerAuthentication)], critical: false)",
                     "public const string ServerAuthentication = \"1.3.6.1.5.5.7.3.1\";",
                     "var pkcs12 = created.Export(X509ContentType.Pfx);",
                     "CryptographicOperations.ZeroMemory(pkcs12);",
                 })
        {
            Assert.Equal(1, Regex.Count(certificate, Regex.Escape(pinned)));
        }

        Assert.Equal(1, Regex.Count(certificate, @"\.Add\w*(IpAddress|DnsName|Uri|EmailAddress|UserPrincipalName)\("));
        Assert.Equal(1, Regex.Count(certificate, @"\.Export\("));
        foreach (var path in new[] { TlsCertificateFile, AttestationFile })
        {
            Assert.DoesNotMatch(
                new Regex(@"IConfiguration|configuration\[|GetSection\(|HarnessOptions|HarnessSeams|Environment\.|\bILogger\b|\bConsole\.|\bDebug\.|\bTrace\.|File\.|Directory\."),
                Read(path));
            Assert.DoesNotMatch(new Regex(@"\bstatic\s+(?!readonly\b)[\w<>\[\]?,]+\s+\w+\s*[=;]"), Read(path));
        }

        // The fixed callback port: one constant, required by the standalone entry point only.
        Assert.Contains("public const int OperatorCallbackPort = 47443;", Read(OptionsFile), StringComparison.Ordinal);
        Assert.Equal([OptionsFile, ProgramFile], Holders(new Regex(@"\bOperatorCallbackPort\b")));
        Assert.Equal(1, Regex.Count(Read(ProgramFile), @"\bOperatorCallbackPort\b"));

        // The certificate is created only by the standalone entry point, once, after the fixed port and the database were
        // accepted; its fingerprint is shown and the attestation required before the host is started with it; and it is
        // disposed with the run.
        Assert.Equal([ProgramFile], Holders(new Regex(@"OperatorTlsCertificate\.Create\(")));
        Assert.Equal([ProgramFile], Holders(new Regex(@"OperatorAttestation\.IsGiven\(")));
        var program = Read(ProgramFile);
        Assert.Equal(1, Regex.Count(program, @"OperatorTlsCertificate\.Create\("));
        Assert.Equal(1, Regex.Count(program, @"\bcertificate\s*=[^=>]"));
        int At(string fragment)
        {
            Assert.Equal(1, Regex.Count(program, Regex.Escape(fragment)));
            return program.IndexOf(fragment, StringComparison.Ordinal);
        }

        int[] order =
        [
            At("if (options.OperatorPort != HarnessOptions.OperatorCallbackPort)"),
            At("if (!HarnessStandaloneGate.Permits(options))"),
            At("using var certificate = OperatorTlsCertificate.Create(TimeProvider.System.GetUtcNow());"),
            At("$\"harness: operator TLS certificate SHA-256 fingerprint {OperatorTlsCertificate.Fingerprint(certificate)}\""),
            At("if (!OperatorAttestation.IsGiven(await input.ReadLineAsync(cancellationToken)))"),
            At("var started = await HarnessHost.StartAsync(options, new HarnessSeams { RunBackboneLoops = true, OperatorCertificate = certificate }, cancellationToken);"),
        ];
        Assert.Equal(order.Order(), order);
    }

    private static void AssertNamedNowhere(params string[] fragments)
    {
        var violations = new List<string>();
        foreach (var path in HarnessSources())
        {
            var text = Read(path);
            violations.AddRange(fragments.Where(fragment => text.Contains(fragment, StringComparison.Ordinal)).Select(fragment => $"{path} names '{fragment}'"));
        }

        Assert.Empty(violations);
    }

    // The fixed statements of the evidence readers: each a constant built from adjacent string literals.
    private static List<string> Statements() =>
        Regex.Matches(Read(EvidenceFile), @"private const string \w+Sql =\s*((?:""[^""]*""\s*\+?\s*)+);")
            .Select(match => string.Concat(Regex.Matches(match.Groups[1].Value, @"""([^""]*)""").Select(literal => literal.Groups[1].Value)))
            .ToList();

    private static List<string> Holders(Regex pattern) =>
        HarnessSources().Where(path => pattern.IsMatch(Read(path))).ToList();

    private static List<string> HarnessSources() =>
        new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "tests", "App.Harness.Monday"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static bool IsBuildOutput(FileInfo file)
    {
        var path = file.FullName.Replace('\\', '/');
        return path.Contains("/bin/", StringComparison.Ordinal) || path.Contains("/obj/", StringComparison.Ordinal);
    }
}
