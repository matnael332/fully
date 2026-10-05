using System.Text.RegularExpressions;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 3G, offline slice: the tenant-trust rules of the non-production harness at <c>tests/App.Harness.Monday</c> (owner
/// decision D3G-2). The production rules of <see cref="TenantTrustTests"/> are unchanged and still pin exactly four
/// production call sites; these are their harness-scoped counterparts. The harness assigns a tenant in exactly one
/// reviewed place and only as a trusted service; it parses the tenant id and the canonical user id only at its
/// configuration boundary; no request, cookie, header or console argument can name a tenant or a user; and every
/// operator action takes its tenant and its user from the trusted chain alone.
/// </summary>
public sealed class HarnessTenantTrustTests
{
    private const string HarnessDirectory = "tests/App.Harness.Monday/";
    private const string FlowFile = HarnessDirectory + "Trust/TrustedFlow.cs";
    private const string OptionsFile = HarnessDirectory + "Configuration/HarnessOptions.cs";
    private const string HostFile = HarnessDirectory + "Composition/HarnessHost.cs";
    private const string ActionsFile = HarnessDirectory + "Operator/OperatorActions.cs";
    private const string ListenerFile = HarnessDirectory + "Operator/OperatorListener.cs";
    private const string ObserverFile = HarnessDirectory + "Diagnostics/WebhookRequestObserver.cs";
    private const string ConsoleFile = HarnessDirectory + "Operator/OperatorConsole.cs";

    private static readonly string[] RequestInputApis =
    [
        "Request.Query",
        "Request.Headers",
        "Request.Form",
        "Request.Cookies",
        "Request.Body",
        "Request.RouteValues",
        "RouteValues",
        "GetRouteValue",
        "FromQuery",
        "FromHeader",
        "FromRoute",
        "FromBody",
        "request.Query",
        "request.Headers",
        "request.Cookies",
        "QueryString",
    ];

    [Fact]
    public void The_harness_assigns_a_tenant_in_exactly_one_reviewed_place_and_only_as_a_trusted_service()
    {
        Assert.True(HarnessSources().Count >= 12);

        var assigning = HarnessSources().Where(path => Read(path).Contains("SetResolved", StringComparison.Ordinal)).ToList();
        Assert.Equal([FlowFile], assigning);

        var flow = Read(FlowFile);
        Assert.Equal(1, Occurrences(flow, "SetResolved("));
        Assert.Equal(1, Occurrences(flow, "TenantResolutionSource."));
        Assert.Contains("_tenantContext.SetResolved(TenantContext.Resolved(_trust.Tenant, TenantResolutionSource.TrustedServiceIdentity));", flow, StringComparison.Ordinal);

        // No other harness file builds a resolved context, names a resolution source or holds the accessor's setter.
        Assert.Equal([FlowFile], HarnessSources().Where(path => Read(path).Contains("TenantContext.Resolved", StringComparison.Ordinal)));
        Assert.Equal([FlowFile], HarnessSources().Where(path => Read(path).Contains("TenantResolutionSource", StringComparison.Ordinal)));

        // The run has a flow of its own and a scope of its own, and takes no tenant from its caller.
        Assert.Contains("return DetachedFlow.Run(() => RunScopedAsync(work, cancellationToken));", flow, StringComparison.Ordinal);
        Assert.Contains("await using var scope = _scopes.CreateAsyncScope();", flow, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"public Task<TResult> RunAsync<TResult>\(\s*Func<IServiceProvider, HarnessTrust, CancellationToken, Task<TResult>> work,\s*CancellationToken cancellationToken\)"),
            flow);
        Assert.Equal(1, Regex.Count(flow, @"public \w+(<\w+>)? RunAsync"));
        Assert.DoesNotMatch(new Regex(@"RunAsync<TResult>\([^)]*TenantId"), flow);

        // A detached start suppresses the caller's execution context while the work is queued.
        var detached = Read(HarnessDirectory + "Trust/DetachedFlow.cs");
        Assert.Equal(2, Occurrences(detached, "using (ExecutionContext.SuppressFlow())"));
        Assert.Equal(2, Occurrences(detached, "if (ExecutionContext.IsFlowSuppressed())"));
    }

    [Fact]
    public void The_tenant_and_the_canonical_user_are_parsed_only_at_the_reviewed_configuration_boundary()
    {
        Assert.Equal([OptionsFile], HarnessSources().Where(path => Read(path).Contains("TenantId.TryParse", StringComparison.Ordinal)));
        Assert.Equal([OptionsFile], HarnessSources().Where(path => Read(path).Contains("UserId.TryParse", StringComparison.Ordinal)));

        var options = Read(OptionsFile);
        Assert.Equal(1, Occurrences(options, "TenantId.TryParse("));
        Assert.Equal(1, Occurrences(options, "UserId.TryParse("));
        Assert.Contains("TenantId.TryParse(configuration[TenantKey], out var tenant)", options, StringComparison.Ordinal);
        Assert.Contains("UserId.TryParse(configuration[CanonicalUserKey], out var canonicalUser)", options, StringComparison.Ordinal);

        // Nothing else in the harness makes a tenant or a user out of a value, by any route.
        foreach (var maker in new[] { "TenantId.From", "TenantId.New", "UserId.From", "UserId.New", "new TenantId", "new UserId", "Guid.Parse(", "Guid.TryParse(", "Principal.Anonymous" })
        {
            Assert.DoesNotContain(HarnessSources(), path => Read(path).Contains(maker, StringComparison.Ordinal));
        }

        // The trusted chain is built once, from the options, by a constructor nothing outside the harness can call.
        Assert.Contains("internal HarnessTrust(HarnessOptions options)", Read(FlowFile), StringComparison.Ordinal);
        Assert.Equal(1, HarnessSources().Sum(path => Occurrences(Read(path), "new HarnessTrust(")));
        Assert.Contains("services.AddSingleton(new HarnessTrust(options));", Read(HostFile), StringComparison.Ordinal);
        Assert.Equal(1, HarnessSources().Sum(path => Occurrences(Read(path), "Principal.Authenticated(")));
        Assert.Contains("Principal.Authenticated(\n            options.CanonicalUser,", Read(FlowFile), StringComparison.Ordinal);

        // The options have no public constructor and no setter: only Read makes them.
        Assert.Contains("private HarnessOptions(", options, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\{\s*get;\s*(set|init);"), options);
    }

    [Fact]
    public void No_harness_source_takes_a_tenant_or_a_user_from_a_request()
    {
        // The production rule, line by line: no line names a tenant or a user alongside request input.
        var offenders = new List<string>();
        foreach (var path in HarnessSources())
        {
            var lines = Read(path).Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (!line.Contains("tenant", StringComparison.OrdinalIgnoreCase) && !line.Contains("user", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                offenders.AddRange(RequestInputApis.Where(api => line.Contains(api, StringComparison.Ordinal)).Select(api => $"{path}:{index + 1} '{api}'"));
            }
        }

        Assert.Empty(offenders);

        // Stronger: the two files that see a request at all name no tenant, no user and no principal, in any spelling,
        // and neither reaches the trusted flow or the database.
        var handlers = HarnessSources().Where(path => Regex.IsMatch(Read(path), @"\bHttp(Context|Request|Response)\b")).ToList();
        Assert.Equal([ObserverFile, ListenerFile], handlers);
        foreach (var handler in handlers)
        {
            var text = Read(handler);
            foreach (var forbidden in new[] { "tenant", "canonicaluser", "principal", "harnesstrust", "trustedflow", "itenantscopeddatabase", "dbcontext", "servicescope" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }

            // The user id type by name. The provider's own claim name for a member is a different spelling, and is only counted.
            Assert.DoesNotMatch(new Regex(@"\bUserId\b"), text);
        }

        // The listener reaches an authorization through one interface, and neither of its methods takes an identity.
        var listener = Read(ListenerFile);
        Assert.Contains("Task<OperatorStart> StartAsync(Guid? reauthorizeConnectionId, CancellationToken cancellationToken);", listener, StringComparison.Ordinal);
        Assert.Contains("Task<AuthorizationCompletionOutcome> CompleteAsync(MondayAuthorizationCallback callback, CancellationToken cancellationToken);", listener, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(listener, "_authorization.StartAsync(redemption.ReauthorizeConnectionId, context.RequestAborted)"));
        Assert.Equal(1, Occurrences(listener, "_authorization.CompleteAsync(new MondayAuthorizationCallback(state, code, error, issuer), context.RequestAborted)"));

        // The connection to restore comes from the capability the console minted, never from the request.
        Assert.Equal(1, Occurrences(listener, "ReauthorizeConnectionId"));
        Assert.DoesNotContain("Guid", Regex.Replace(listener, @"Task<OperatorStart> StartAsync\(Guid\? reauthorizeConnectionId", string.Empty), StringComparison.Ordinal);

        // The console's arguments are a connection id and a resource id; it names no tenant, user or principal either.
        var console = Read(ConsoleFile);
        foreach (var forbidden in new[] { "TenantId", "UserId", "Principal", "HarnessTrust", "TrustedFlow" })
        {
            Assert.DoesNotContain(forbidden, console, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_operator_action_takes_its_tenant_and_its_user_from_the_trusted_chain_alone()
    {
        var actions = Read(ActionsFile);

        // Every run and scope the actions build names the trusted tenant first, and nothing else supplies one.
        foreach (var built in new[] { "new ConnectorSourceScope(", "new InitialSyncRun(", "new ReconciliationRun(" })
        {
            var all = Occurrences(actions, built);
            Assert.True(all >= 1, built);
            Assert.Equal(all, Occurrences(actions, built + "trust.Tenant, "));
        }

        Assert.Equal(1, Occurrences(actions, "new ConnectorSourceScope("));
        Assert.Equal(1, Occurrences(actions, "new InitialSyncRun("));
        Assert.Equal(2, Occurrences(actions, "new ReconciliationRun("));

        // Every call of the authorizer that needs a caller is given the configured operator.
        Assert.Equal(1, Occurrences(actions, ".StartAsync(trust.Operator, reauthorizeConnectionId, token)"));
        Assert.Equal(1, Occurrences(actions, ".CompleteAsync(trust.Operator, callback, token)"));
        Assert.Equal(1, Occurrences(actions, ".DisconnectAsync(trust.Operator, connectionId, token)"));
        Assert.Equal(3, Occurrences(actions, "trust.Operator"));
        Assert.Equal(4, Occurrences(actions, "trust.Tenant"));

        // No public member takes a tenant, a user or a principal, and every action goes through the trusted flow.
        foreach (Match signature in Regex.Matches(actions, @"public [^\n=]*\([^)]*\)"))
        {
            foreach (var forbidden in new[] { "TenantId", "UserId", "Principal", "HarnessTrust", "ITenantContext" })
            {
                Assert.DoesNotContain(forbidden, signature.Value, StringComparison.Ordinal);
            }
        }

        Assert.Equal(11, Regex.Count(actions, @"public (async )?Task<"));
        Assert.Equal(10, Regex.Count(actions, @"_flow\.RunAsync\("));
        Assert.Equal(2, Regex.Count(actions, @"=>\s*SyncAsync\(connectionId, "));
        Assert.DoesNotContain("ITenantContextAccessor", actions, StringComparison.Ordinal);
        Assert.DoesNotContain("TenantContext", actions, StringComparison.Ordinal);

        // The bound of a reconciliation run and of a sweep is a constant.
        Assert.Contains("public const int MaxTargetsPerAction = 100;", actions, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(actions, "connectionId, MaxTargetsPerAction)"));
    }

    private static List<string> HarnessSources() =>
        new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "tests", "App.Harness.Monday"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var path = file.FullName.Replace('\\', '/');
                return !path.Contains("/bin/", StringComparison.Ordinal) && !path.Contains("/obj/", StringComparison.Ordinal);
            })
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
