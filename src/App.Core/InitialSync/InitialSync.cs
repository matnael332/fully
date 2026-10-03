using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Core.InitialSync;

/// <summary>
/// One provider resource a connection is configured to sync (decision D3F-1), by the provider's own id. Initial sync reads only
/// enabled scopes, and nothing discovers resources.
/// </summary>
/// <remarks>
/// The resource id is opaque and bounded; no display text is identity. A scope belongs to one tenant, connection and
/// source for good: only whether it is enabled can change.
/// </remarks>
public sealed record ConnectorSourceScope
{
    /// <summary>Initializes a new instance of the <see cref="ConnectorSourceScope"/> class.</summary>
    /// <exception cref="ArgumentException">A component is missing or malformed.</exception>
    public ConnectorSourceScope(TenantId tenant, Guid connectionId, string sourceSystem, string resourceId, bool enabled)
    {
        if (tenant.IsEmpty || connectionId == Guid.Empty || !EventText.IsToken(sourceSystem))
        {
            throw new ArgumentException("A source scope names its tenant, connection and source system.", nameof(tenant));
        }

        if (!EventText.IsIdentifier(resourceId))
        {
            throw new ArgumentException("A source scope's resource is a bounded, opaque identifier.", nameof(resourceId));
        }

        Tenant = tenant;
        ConnectionId = connectionId;
        SourceSystem = sourceSystem;
        ResourceId = resourceId;
        Enabled = enabled;
    }

    /// <summary>Gets the owning tenant.</summary>
    public TenantId Tenant { get; }

    /// <summary>Gets the connection the scope belongs to.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Gets the source system.</summary>
    public string SourceSystem { get; }

    /// <summary>Gets the provider's resource id.</summary>
    public string ResourceId { get; }

    /// <summary>Gets a value indicating whether initial sync reads the scope.</summary>
    public bool Enabled { get; }

    /// <summary>Identifies the scope by ids only.</summary>
    public override string ToString() => $"ConnectorSourceScope({SourceSystem}, {ResourceId}, {(Enabled ? "enabled" : "disabled")})";
}

/// <summary>Configures the source scopes of connections, in the trusted tenant only.</summary>
/// <remarks>
/// A connection takes part in initial sync from the moment its configuration first enables a scope: from then on its
/// realtime receipts are held until a generation's baseline is established. Every change to its enabled scope set raises
/// its scope-set version and, in the same transaction, makes a Live, running or failed generation restart over the new
/// set, so a baseline always represents the configured set. A call that changes no enabled scope changes nothing.
/// </remarks>
public interface IConnectorSourceScopes
{
    /// <summary>
    /// Creates a scope of a connection of the trusted tenant, or changes whether it is enabled. The connection must belong to
    /// the tenant and serve the scope's source system.
    /// </summary>
    Task<Result<ConnectorSourceScope>> ConfigureAsync(ConnectorSourceScope scope, CancellationToken cancellationToken = default);

    /// <summary>Lists the scopes of one connection of the trusted tenant.</summary>
    Task<Result<IReadOnlyList<ConnectorSourceScope>>> ListAsync(TenantId tenant, Guid connectionId, CancellationToken cancellationToken = default);
}

/// <summary>A field initial sync reads in a scope: the provider's field id and its canonical data type.</summary>
public sealed record SyncField
{
    /// <summary>Initializes a new instance of the <see cref="SyncField"/> class.</summary>
    /// <exception cref="ArgumentException">The field is not a token, or the type is not defined.</exception>
    public SyncField(string field, FactDataType dataType)
    {
        if (!EventText.IsToken(field))
        {
            throw new ArgumentException("A field is a token.", nameof(field));
        }

        if (dataType == FactDataType.None || !Enum.IsDefined(dataType))
        {
            throw new ArgumentException("A field has a defined data type.", nameof(dataType));
        }

        Field = field;
        DataType = dataType;
    }

    /// <summary>Gets the provider's field id.</summary>
    public string Field { get; }

    /// <summary>Gets the field's canonical data type.</summary>
    public FactDataType DataType { get; }
}

/// <summary>A request about one configured scope of one connection, under the tenant's trusted context.</summary>
public sealed class ScopeReadRequest
{
    /// <summary>Initializes a new instance of the <see cref="ScopeReadRequest"/> class.</summary>
    /// <exception cref="ArgumentException">A component is missing or malformed.</exception>
    public ScopeReadRequest(
        TenantId tenant,
        Guid connectionId,
        string connectionReference,
        string sourceSystem,
        string resourceId,
        CorrelationId correlationId)
    {
        if (tenant.IsEmpty || connectionId == Guid.Empty || correlationId.IsEmpty)
        {
            throw new ArgumentException("A scope read names its tenant, connection and correlation.", nameof(tenant));
        }

        if (!EventText.IsIdentifier(connectionReference) || !EventText.IsToken(sourceSystem) || !EventText.IsIdentifier(resourceId))
        {
            throw new ArgumentException("A scope read names a connection reference, a source system and a resource id.", nameof(connectionReference));
        }

        Tenant = tenant;
        ConnectionId = connectionId;
        ConnectionReference = connectionReference;
        SourceSystem = sourceSystem;
        ResourceId = resourceId;
        CorrelationId = correlationId;
    }

    /// <summary>Gets the trusted tenant.</summary>
    public TenantId Tenant { get; }

    /// <summary>Gets the connection.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Gets the connection reference the scope's objects are homed at.</summary>
    public string ConnectionReference { get; }

    /// <summary>Gets the source system.</summary>
    public string SourceSystem { get; }

    /// <summary>Gets the configured resource id.</summary>
    public string ResourceId { get; }

    /// <summary>Gets the correlation of the sync run.</summary>
    public CorrelationId CorrelationId { get; }

    /// <summary>Identifies the request by ids only.</summary>
    public override string ToString() => $"ScopeReadRequest({SourceSystem}, {ResourceId})";
}

/// <summary>What validating a scope found: the supported fields to read, or why the scope cannot be read.</summary>
public sealed class ScopeValidation
{
    private ScopeValidation(
        ConnectorIdentity? identity,
        SyncScopeOutcome refusal,
        TimeSpan? retryAfter,
        string? objectType,
        IReadOnlyList<SyncField> fields)
    {
        Identity = identity;
        Refusal = refusal;
        RetryAfter = retryAfter;
        ObjectType = objectType;
        Fields = fields;
    }

    /// <summary>Gets the identity whose authority the validation used, when it got that far.</summary>
    public ConnectorIdentity? Identity { get; }

    /// <summary>Gets why the scope cannot be read, or <see cref="SyncScopeOutcome.None"/>.</summary>
    public SyncScopeOutcome Refusal { get; }

    /// <summary>Gets how long the source asked to wait, when it did.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Gets the type token of the scope's objects, when supported.</summary>
    public string? ObjectType { get; }

    /// <summary>Gets the supported fields, when supported.</summary>
    public IReadOnlyList<SyncField> Fields { get; }

    /// <summary>A scope that can be read, with its object type and its supported fields.</summary>
    /// <exception cref="ArgumentNullException">A component is null.</exception>
    /// <exception cref="ArgumentException">The type is not a token, or the fields are empty or repeated.</exception>
    public static ScopeValidation Supported(ConnectorIdentity identity, string objectType, IReadOnlyList<SyncField> fields)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(fields);
        if (!EventText.IsToken(objectType))
        {
            throw new ArgumentException("An object type is a token.", nameof(objectType));
        }

        if (fields.Count == 0 || fields.Any(field => field is null) || fields.Select(field => field.Field).Distinct(StringComparer.Ordinal).Count() != fields.Count)
        {
            throw new ArgumentException("A supported scope has distinct fields.", nameof(fields));
        }

        return new ScopeValidation(identity, SyncScopeOutcome.None, retryAfter: null, objectType, fields.ToArray());
    }

    /// <summary>A scope that cannot be read.</summary>
    /// <exception cref="ArgumentException">The outcome is not one a reader may report, or the wait is negative.</exception>
    public static ScopeValidation Refused(SyncScopeOutcome refusal, ConnectorIdentity? identity, TimeSpan? retryAfter = null)
    {
        if (!SyncScopeOutcomes.IsReaderRefusal(refusal))
        {
            throw new ArgumentException("A scope is refused with a reader outcome.", nameof(refusal));
        }

        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentException("A provider's requested wait cannot be negative.", nameof(retryAfter));
        }

        return new ScopeValidation(identity, refusal, retryAfter, objectType: null, []);
    }

    /// <summary>Identifies the validation by outcome only.</summary>
    public override string ToString() => Refusal == SyncScopeOutcome.None
        ? $"ScopeValidation({Fields.Count} fields)"
        : $"ScopeValidation({Refusal})";
}

/// <summary>
/// An opaque position within one scan of a scope, issued by the reader for its next page (decision D3F-2). Temporary: it is
/// never stored, never logged, never interpreted outside the reader that issued it, and never a watermark or ordering
/// evidence.
/// </summary>
public sealed class ScopeContinuation
{
    /// <summary>The longest continuation accepted.</summary>
    public const int MaxLength = 4096;

    /// <summary>Initializes a new instance of the <see cref="ScopeContinuation"/> class.</summary>
    /// <exception cref="ArgumentException">The token is empty, too long or not printable.</exception>
    public ScopeContinuation(string token)
    {
        if (!IsValid(token))
        {
            throw new ArgumentException("A continuation is a bounded, printable token.", nameof(token));
        }

        Token = token;
    }

    /// <summary>Gets the token, for the reader that issued it only.</summary>
    public string Token { get; }

    /// <summary>Whether the text can be a continuation: 1 to <see cref="MaxLength"/> printable ASCII characters, no space.</summary>
    public static bool IsValid(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length <= MaxLength && token.All(character => character is >= '!' and <= '~');

    /// <summary>Never renders the token.</summary>
    public override string ToString() => "ScopeContinuation(redacted)";
}

/// <summary>A request for one page of a scope's objects: the first page, or the one a continuation points at.</summary>
public sealed class ScopePageRequest
{
    /// <summary>Initializes a new instance of the <see cref="ScopePageRequest"/> class.</summary>
    /// <exception cref="ArgumentNullException">A component is null.</exception>
    /// <exception cref="ArgumentException">The type is not a token, or the fields are empty or repeated.</exception>
    public ScopePageRequest(ScopeReadRequest scope, string objectType, IReadOnlyList<SyncField> fields, ScopeContinuation? continuation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(fields);
        if (!EventText.IsToken(objectType))
        {
            throw new ArgumentException("An object type is a token.", nameof(objectType));
        }

        if (fields.Count == 0 || fields.Any(field => field is null) || fields.Select(field => field.Field).Distinct(StringComparer.Ordinal).Count() != fields.Count)
        {
            throw new ArgumentException("A page request names distinct fields.", nameof(fields));
        }

        Scope = scope;
        ObjectType = objectType;
        Fields = fields.ToArray();
        Continuation = continuation;
    }

    /// <summary>Gets the scope.</summary>
    public ScopeReadRequest Scope { get; }

    /// <summary>Gets the type token of the scope's objects.</summary>
    public string ObjectType { get; }

    /// <summary>Gets the fields to read.</summary>
    public IReadOnlyList<SyncField> Fields { get; }

    /// <summary>Gets the continuation, or null for the first page.</summary>
    public ScopeContinuation? Continuation { get; }

    /// <summary>Identifies the request by ids only.</summary>
    public override string ToString() => $"ScopePageRequest({Scope.ResourceId}, {(Continuation is null ? "first" : "next")} page)";
}

/// <summary>One object of a page and what the read found for each requested field.</summary>
public sealed class SyncedObject
{
    /// <summary>Initializes a new instance of the <see cref="SyncedObject"/> class.</summary>
    /// <exception cref="ArgumentNullException">A component is null.</exception>
    /// <exception cref="ArgumentException">The object is malformed, or no field was read.</exception>
    public SyncedObject(ObjectReference workObject, IReadOnlyDictionary<SyncField, TargetReading> fields)
    {
        ArgumentNullException.ThrowIfNull(workObject);
        ArgumentNullException.ThrowIfNull(fields);
        if (!workObject.IsWellFormed)
        {
            throw new ArgumentException("A synced object is well formed.", nameof(workObject));
        }

        if (fields.Count == 0)
        {
            throw new ArgumentException("A synced object has its fields.", nameof(fields));
        }

        WorkObject = workObject;
        Fields = fields;
    }

    /// <summary>Gets the object.</summary>
    public ObjectReference WorkObject { get; }

    /// <summary>Gets what the read found for each field.</summary>
    public IReadOnlyDictionary<SyncField, TargetReading> Fields { get; }

    /// <summary>Identifies the object by ids only.</summary>
    public override string ToString() => $"SyncedObject({WorkObject.ObjectType} {WorkObject.SourceObjectId}, {Fields.Count} fields)";
}

/// <summary>One page of a scope: its objects and the continuation to the next page, or one failure.</summary>
public sealed class ScopePage
{
    private ScopePage(
        ConnectorIdentity? identity,
        SyncScopeOutcome failure,
        TimeSpan? retryAfter,
        IReadOnlyList<SyncedObject> objects,
        ScopeContinuation? next)
    {
        Identity = identity;
        Failure = failure;
        RetryAfter = retryAfter;
        Objects = objects;
        Next = next;
    }

    /// <summary>Gets the identity whose authority the read used, when it got that far.</summary>
    public ConnectorIdentity? Identity { get; }

    /// <summary>Gets the failure of the page, or <see cref="SyncScopeOutcome.None"/>.</summary>
    public SyncScopeOutcome Failure { get; }

    /// <summary>Gets how long the source asked to wait, when it did.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Gets the page's objects.</summary>
    public IReadOnlyList<SyncedObject> Objects { get; }

    /// <summary>Gets the continuation to the next page, or null when this was the last.</summary>
    public ScopeContinuation? Next { get; }

    /// <summary>A page that was read, with the identity it used.</summary>
    /// <exception cref="ArgumentNullException">A component is null.</exception>
    /// <exception cref="ArgumentException">An object is repeated within the page.</exception>
    public static ScopePage Read(ConnectorIdentity identity, IReadOnlyList<SyncedObject> objects, ScopeContinuation? next)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Any(item => item is null)
            || objects.Select(item => (item.WorkObject.ObjectType, item.WorkObject.SourceObjectId)).Distinct().Count() != objects.Count)
        {
            throw new ArgumentException("A page names each object once.", nameof(objects));
        }

        return new ScopePage(identity, SyncScopeOutcome.None, retryAfter: null, objects.ToArray(), next);
    }

    /// <summary>A page that could not be read.</summary>
    /// <exception cref="ArgumentException">The outcome is not one a reader may report, or the wait is negative.</exception>
    public static ScopePage Failed(SyncScopeOutcome failure, ConnectorIdentity? identity, TimeSpan? retryAfter = null)
    {
        if (!SyncScopeOutcomes.IsReaderRefusal(failure))
        {
            throw new ArgumentException("A page fails with a reader outcome.", nameof(failure));
        }

        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentException("A provider's requested wait cannot be negative.", nameof(retryAfter));
        }

        return new ScopePage(identity, failure, retryAfter, [], next: null);
    }

    /// <summary>Identifies the page by counts and outcome only.</summary>
    public override string ToString() => Failure == SyncScopeOutcome.None
        ? $"ScopePage({Objects.Count} objects, {(Next is null ? "last" : "more")})"
        : $"ScopePage({Failure})";
}

/// <summary>
/// Reads the configured scopes of a source for initial sync (System Architecture v1 §2: read current authorized source
/// state). One reader serves one source system.
/// </summary>
/// <remarks>
/// A reader only reads, and reads only the scope it is asked for: it never discovers resources, never writes to the
/// source and never decides what reaches the Snapshot. It binds every answer to the request: another resource, an object
/// outside the scope, another field or an unsupported shape is refused, never remapped. It reports values only as
/// <see cref="AuthoritativeFieldState"/>, with the exact provider values it consumed as evidence. It writes no log.
/// </remarks>
public interface IInitialSyncReader
{
    /// <summary>Gets the source system token this reader serves.</summary>
    string SourceSystem { get; }

    /// <summary>Checks that the scope's resource exists, is active and supported, and finds its supported fields.</summary>
    Task<ScopeValidation> ValidateScopeAsync(ScopeReadRequest request, CancellationToken cancellationToken);

    /// <summary>Reads one page of the scope's objects.</summary>
    Task<ScopePage> ReadPageAsync(ScopePageRequest request, CancellationToken cancellationToken);
}

/// <summary>Where a connection is in its initial sync (decision D3F-4).</summary>
public enum InitialSyncState
{
    /// <summary>Unspecified.</summary>
    None = 0,

    /// <summary>
    /// No generation has started. A connection whose configuration enables a scope holds its realtime receipts from the
    /// moment it was configured; a connection that never had one processes them as before.
    /// </summary>
    NotStarted = 1,

    /// <summary>
    /// A generation started: nothing has been read yet, and realtime receipts above the replay floor stay held.
    /// </summary>
    Buffering = 2,

    /// <summary>The baseline of the generation's scopes is being read and applied.</summary>
    Reading = 3,

    /// <summary>Every scope completed; held receipts are being replayed up to the cutover.</summary>
    Replaying = 4,

    /// <summary>The generation reached its cutover: realtime receipts are processed as they arrive.</summary>
    Live = 5,

    /// <summary>A required scope cannot complete: the connection is not Live and a new generation must be started.</summary>
    Failed = 6,

    /// <summary>
    /// The connection was authorized again, or its enabled scope set changed: the next run starts a new generation under the
    /// current authority, over the current scope set.
    /// </summary>
    RequiresRestart = 7,
}

/// <summary>One initial-sync run: one tenant, source system and connection.</summary>
public sealed record InitialSyncRun
{
    /// <summary>Initializes a new instance of the <see cref="InitialSyncRun"/> class.</summary>
    /// <exception cref="ArgumentException">A component is missing.</exception>
    public InitialSyncRun(TenantId tenant, string sourceSystem, Guid connectionId)
    {
        if (tenant.IsEmpty || connectionId == Guid.Empty || !EventText.IsToken(sourceSystem))
        {
            throw new ArgumentException("A run names its tenant, source system and connection.", nameof(tenant));
        }

        Tenant = tenant;
        SourceSystem = sourceSystem;
        ConnectionId = connectionId;
    }

    /// <summary>Gets the trusted tenant.</summary>
    public TenantId Tenant { get; }

    /// <summary>Gets the source system.</summary>
    public string SourceSystem { get; }

    /// <summary>Gets the connection.</summary>
    public Guid ConnectionId { get; }
}

/// <summary>Where a run left the connection's initial sync, by ids and counts only.</summary>
public sealed class InitialSyncSummary
{
    /// <summary>Initializes a new instance of the <see cref="InitialSyncSummary"/> class.</summary>
    /// <exception cref="ArgumentNullException">The outcomes are null.</exception>
    public InitialSyncSummary(
        long generation,
        InitialSyncState state,
        IReadOnlyDictionary<SyncScopeOutcome, int> scopeOutcomes,
        int fieldsApplied,
        int fieldsHeld,
        int receiptsReplayed)
    {
        ArgumentNullException.ThrowIfNull(scopeOutcomes);
        Generation = generation;
        State = state;
        ScopeOutcomes = scopeOutcomes;
        FieldsApplied = fieldsApplied;
        FieldsHeld = fieldsHeld;
        ReceiptsReplayed = receiptsReplayed;
    }

    /// <summary>Gets the current generation, or 0 when none started.</summary>
    public long Generation { get; }

    /// <summary>Gets the state the run left.</summary>
    public InitialSyncState State { get; }

    /// <summary>Gets how many scopes ended this run with each outcome.</summary>
    public IReadOnlyDictionary<SyncScopeOutcome, int> ScopeOutcomes { get; }

    /// <summary>Gets how many fields the run applied through processing, or found the Snapshot already holding.</summary>
    public int FieldsApplied { get; }

    /// <summary>Gets how many fields the run could not apply and held as known targets with their outcome.</summary>
    public int FieldsHeld { get; }

    /// <summary>Gets how many held receipts the run replayed.</summary>
    public int ReceiptsReplayed { get; }

    /// <summary>The number of scopes that ended the run with an outcome.</summary>
    public int Count(SyncScopeOutcome outcome) => ScopeOutcomes.TryGetValue(outcome, out var count) ? count : 0;

    /// <summary>Identifies the summary by counts only.</summary>
    public override string ToString() => $"InitialSyncSummary(generation {Generation}, {State})";
}

/// <summary>
/// Brings a connection from NotStarted to Live by the frozen initial-connection flow: buffer realtime intake, read the
/// configured scopes' current authorized state, build the initial Snapshot, record the watermark, replay the held
/// receipts and enter normal realtime mode.
/// </summary>
public interface IInitialSync
{
    /// <summary>
    /// Starts a new generation for a connection that is not running one: realtime receipts above its fence are held from
    /// the moment it commits, before anything is read.
    /// </summary>
    Task<Result<InitialSyncSummary>> StartAsync(InitialSyncRun run, CancellationToken cancellationToken = default);

    /// <summary>Advances the connection's current generation as far as it can: baseline, replay and cutover.</summary>
    Task<Result<InitialSyncSummary>> RunAsync(InitialSyncRun run, CancellationToken cancellationToken = default);
}

/// <summary>The fixed, content-free refusals of initial sync and of source-scope configuration.</summary>
public static class InitialSyncErrors
{
    /// <summary>The run names another tenant than the trusted context.</summary>
    public static Error TenantMismatch { get; } =
        Error.Forbidden("initial_sync.run.tenant_mismatch", "The run's tenant is not the trusted tenant.");

    /// <summary>No reader serves the source system.</summary>
    public static Error ReaderMissing { get; } =
        Error.NotFound("initial_sync.run.reader_missing", "No initial-sync reader serves the source system.");

    /// <summary>The database could not be reached; nothing was recorded.</summary>
    public static Error Unavailable { get; } =
        Error.Infrastructure("initial_sync.run.unavailable", "Initial sync could not reach its state; nothing was recorded.");

    /// <summary>
    /// The connection has no enabled source scope and never took part in initial sync. One that took part starts a generation
    /// over whatever its scope set is now, an empty one included, so what it held is replayed.
    /// </summary>
    public static Error NoScopes { get; } =
        Error.Validation("initial_sync.run.no_scopes", "The connection has no enabled source scope.");

    /// <summary>A generation is already running for the connection.</summary>
    public static Error InProgress { get; } =
        Error.Conflict("initial_sync.run.in_progress", "A generation is already running for the connection.");

    /// <summary>The connection is already Live.</summary>
    public static Error AlreadyLive { get; } =
        Error.Conflict("initial_sync.run.already_live", "The connection is already Live.");

    /// <summary>Another run took over the generation while this one ran; this run recorded nothing more.</summary>
    public static Error ClaimLost { get; } =
        Error.Conflict("initial_sync.run.claim_lost", "Another run took over the generation; this run recorded nothing more.");

    /// <summary>The tenant, the connection or its authorization does not stand; nothing was started.</summary>
    public static Error AccessRefused { get; } =
        Error.Forbidden("initial_sync.run.access_refused", "The tenant, the connection or its authorization does not stand.");

    /// <summary>No such connection of the trusted tenant serves the source system.</summary>
    public static Error ConnectionNotFound { get; } =
        Error.NotFound("initial_sync.scope.connection_not_found", "No such connection of the tenant serves the source system.");
}
