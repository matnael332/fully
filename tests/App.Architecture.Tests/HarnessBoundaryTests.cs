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
            "SocketsHttpHandler", "HttpClientHandler", "WinHttpHandler", "MondayHttp", "IHttpClientFactory", "AddHttpClient",
            "HttpWebRequest", "WebRequest.Create", "TcpClient", "ClientWebSocket", "Dns.", "monday.com", "Process.Start",
            "ProcessStartInfo", "UseShellExecute", "DefaultProxy", "new HttpClient()", "new HttpClient {");

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
        Assert.Empty(Holders(new Regex(@"\b(ProviderTransport|SecretStore|OperatorCertificate|TransportPublisher|TimeProvider|LogSink|Delay|Clock)\s*=[^=>]")));
        Assert.Contains("new HarnessSeams { RunBackboneLoops = true }", Read(ProgramFile), StringComparison.Ordinal);
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
        Assert.Equal(12, statements.Count);
        Assert.All(statements, statement =>
        {
            Assert.StartsWith("SELECT ", statement, StringComparison.Ordinal);
            Assert.DoesNotContain(";", statement, StringComparison.Ordinal);
            Assert.DoesNotContain("@", statement, StringComparison.Ordinal);
            Assert.DoesNotContain("{", statement, StringComparison.Ordinal);
        });
        Assert.Equal(1, Regex.Count(Read(EvidenceFile), @"command\.CommandText = sql;"));
        var executed = Regex.Matches(Read(EvidenceFile), @"EvidenceSql\.ReadAsync\(\s*dbContext,\s*(\w+)").Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(12, executed.Count);
        Assert.All(executed, name => Assert.EndsWith("Sql", name, StringComparison.Ordinal));
        Assert.Equal(executed.Count, executed.Distinct(StringComparer.Ordinal).Count());

        // The relations it names are the two active views, the operational tables and the role catalog, and no other.
        var relations = statements
            .SelectMany(statement => Regex.Matches(statement, @"\bFROM\s+([a-z_]+)").Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            [
                "active_snapshot_fields", "active_snapshot_objects", "connection_sync_scopes", "connection_syncs", "connector_authorization_transactions",
                "connector_authorizations", "connector_source_scopes", "event_ledger", "pg_roles", "pg_tables", "raw_receipts", "reconciliation_targets",
            ],
            relations);

        // No payload, no provider identifier and no stored hash is selected; a value is selected from the current work
        // view only.
        foreach (var column in new[] { "payload", "provider_subject", "provider_account", "state_hash", "redirect_hash", "granted_scopes", "material_version", "source_event_id", "correlation_id" })
        {
            Assert.All(statements, statement => Assert.DoesNotMatch(new Regex(@"\b" + column + @"\b"), statement));
        }

        Assert.Equal(["active_snapshot_fields"], statements.Where(statement => Regex.IsMatch(statement, @"\bvalue\b")).Select(statement => Regex.Match(statement, @"\bFROM\s+([a-z_]+)").Groups[1].Value));

        // The harness never runs as a role that could step around row-level security: any one of the three privileges
        // refuses the start. And no database is approved for a standalone run: the list is empty.
        Assert.Contains("public bool IsPrivileged => Superuser || BypassesRowSecurity || OwnsTables;", Read(EvidenceFile), StringComparison.Ordinal);
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
        AssertNamedNowhere("X509Store", "StoreName.", "StoreLocation.", "dev-certs", "UseHttps()", "X509CertificateLoader", "CreateSelfSigned");

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
        Assert.Equal([CleanupFile], Holders(new Regex(@"TimeSpan\.FromMinutes\(5\)")));
        Assert.Contains("public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);", Read(CleanupFile), StringComparison.Ordinal);
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
