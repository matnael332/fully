using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>
/// A connection's initial sync (Gate 3F, decision D3F-4): its scope-set version, its current generation and state, the
/// scope-set version and authority snapshot the generation read under, its ingress fences, its worker's lease and its
/// retry state. One row per connection that takes part in initial sync, from the moment its configuration first enables a
/// scope; a connection without one never took part.
/// </summary>
/// <remarks>
/// <para>
/// Metadata only: no value, label, cursor, credential or provider response. The fences are positions in the local ingress
/// sequence (decision D3F-2), never business ordering and never times. Rows are never deleted.
/// </para>
/// <para>
/// The replay floor is where the connection began holding realtime receipts: the ingress fence when its configuration first
/// enabled a scope. It never moves: a generation that fails or is restarted, and a change of the scope set, keep it, so
/// nothing the connection held is ever skipped. A transition applies only to a row the caller holds locked, and a
/// generation's transitions only for the generation and claim the caller holds.
/// </para>
/// <para>
/// The scope-set version rises with every change to the connection's enabled scopes, and each generation records the version
/// it read: a generation becomes Live only while its version is current, so a baseline always represents the configured set.
/// </para>
/// </remarks>
public sealed class ConnectionSyncRecord : ITenantOwned
{
    /// <summary>The maximum stored length of an outcome code.</summary>
    public const int MaxOutcomeLength = 100;

    private ConnectionSyncRecord()
    {
        // Materialization only.
    }

    /// <summary>Gets the owning tenant.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the connection.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>Gets the source system.</summary>
    public string SourceSystem { get; private set; } = string.Empty;

    /// <summary>Gets the current generation.</summary>
    public long Generation { get; private set; }

    /// <summary>Gets the current generation's state.</summary>
    public InitialSyncState State { get; private set; }

    /// <summary>Gets the delegated identity's subject the generation reads under; none before the first generation.</summary>
    public string? ProviderSubject { get; private set; }

    /// <summary>
    /// Gets when the connection was last authorized when the generation started, a renewal never changing it; none before the
    /// first generation.
    /// </summary>
    public DateTimeOffset? AuthorizedAtUtc { get; private set; }

    /// <summary>Gets the version of the connection's enabled scope set, raised by every change to it.</summary>
    public long ScopeVersion { get; private set; }

    /// <summary>Gets the scope-set version the current generation read; none before the first generation.</summary>
    public long? GenerationScopeVersion { get; private set; }

    /// <summary>Gets the ingress position above which a non-Live connection's receipts are held and later replayed.</summary>
    public long ReplayFloor { get; private set; }

    /// <summary>Gets the highest ingress position of the connection's receipts when the generation started.</summary>
    public long BufferStartFence { get; private set; }

    /// <summary>Gets the ingress position the replay first reached, once every scope completed.</summary>
    public long? ReplayFence { get; private set; }

    /// <summary>Gets the ingress position of the cutover: every held receipt up to it was replayed before Live.</summary>
    public long? CutoverFence { get; private set; }

    /// <summary>Gets the claim of the worker running the generation, if one is.</summary>
    public Guid? ClaimId { get; private set; }

    /// <summary>Gets when the current claim lapses.</summary>
    public DateTimeOffset? ClaimedUntilUtc { get; private set; }

    /// <summary>Gets the attempts in the generation's current run of like waits.</summary>
    public int Attempts { get; private set; }

    /// <summary>Gets when the generation may be claimed again, if it is waiting.</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    /// <summary>Gets the code of the last recorded outcome.</summary>
    public string? LastOutcome { get; private set; }

    /// <summary>Gets when the generation started; none before the first generation.</summary>
    public DateTimeOffset? StartedAtUtc { get; private set; }

    /// <summary>Gets when the generation reached Live.</summary>
    public DateTimeOffset? LiveAtUtc { get; private set; }

    /// <summary>Gets when the row last changed.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Gets a value indicating whether the claim is the caller's.</summary>
    public bool IsClaimedBy(Guid claimId) => claimId != Guid.Empty && ClaimId == claimId;

    /// <summary>Gets a value indicating whether the generation is still running: buffering, reading or replaying.</summary>
    public bool IsRunning => State is InitialSyncState.Buffering or InitialSyncState.Reading or InitialSyncState.Replaying;

    /// <summary>
    /// The waits in the run of like outcomes this one would continue: the count so far when the last recorded outcome has the
    /// same disposition, otherwise one.
    /// </summary>
    public int AttemptsWith(SyncScopeOutcome outcome) =>
        SyncScopeOutcomes.TryParse(LastOutcome, out var last)
        && SyncScopeOutcomes.DispositionOf(last) == SyncScopeOutcomes.DispositionOf(outcome)
            ? Attempts + 1
            : 1;

    /// <summary>
    /// A connection whose configuration first enabled a scope: it takes part in initial sync, NotStarted, holding realtime
    /// receipts above the replay floor from now on, at scope-set version 1.
    /// </summary>
    public static ConnectionSyncRecord Configure(
        TenantId tenant,
        Guid connectionId,
        string sourceSystem,
        long replayFloor,
        long fence,
        DateTimeOffset now) =>
        new()
        {
            TenantId = tenant,
            ConnectionId = connectionId,
            SourceSystem = sourceSystem,
            Generation = 0,
            State = InitialSyncState.NotStarted,
            ScopeVersion = 1,
            ReplayFloor = replayFloor,
            BufferStartFence = fence,
            UpdatedAtUtc = now.ToUniversalTime(),
        };

    /// <summary>
    /// The first generation of a connection with no row: its scopes were configured outside the configuration service, so
    /// no hold boundary was recorded and the caller gives the replay floor.
    /// </summary>
    public static ConnectionSyncRecord Begin(
        TenantId tenant,
        Guid connectionId,
        string sourceSystem,
        string providerSubject,
        DateTimeOffset authorizedAtUtc,
        long replayFloor,
        long bufferStartFence,
        DateTimeOffset now) =>
        new()
        {
            TenantId = tenant,
            ConnectionId = connectionId,
            SourceSystem = sourceSystem,
            Generation = 1,
            State = InitialSyncState.Buffering,
            ScopeVersion = 1,
            GenerationScopeVersion = 1,
            ProviderSubject = providerSubject,
            AuthorizedAtUtc = authorizedAtUtc.ToUniversalTime(),
            ReplayFloor = replayFloor,
            BufferStartFence = bufferStartFence,
            StartedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime(),
        };

    /// <summary>
    /// A new generation, the first of a configured connection or one after a generation that failed or must restart. It reads
    /// the current scope set under the current authority; the replay floor is kept, so what the connection held is replayed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection is Live or its generation is still running.</exception>
    public void Restart(string providerSubject, DateTimeOffset authorizedAtUtc, long bufferStartFence, DateTimeOffset now)
    {
        if (State is not (InitialSyncState.NotStarted or InitialSyncState.Failed or InitialSyncState.RequiresRestart))
        {
            throw new InvalidOperationException("Only a connection not yet started, a failed generation or one that must restart starts a new one.");
        }

        Generation++;
        State = InitialSyncState.Buffering;
        GenerationScopeVersion = ScopeVersion;
        ProviderSubject = providerSubject;
        AuthorizedAtUtc = authorizedAtUtc.ToUniversalTime();
        BufferStartFence = bufferStartFence;
        ReplayFence = null;
        CutoverFence = null;
        ClaimId = null;
        ClaimedUntilUtc = null;
        Attempts = 0;
        NextAttemptAtUtc = null;
        LastOutcome = null;
        StartedAtUtc = now.ToUniversalTime();
        LiveAtUtc = null;
        UpdatedAtUtc = now.ToUniversalTime();
    }

    /// <summary>
    /// The connection's enabled scope set changed, in the caller's transaction, which holds the connection's and this row's
    /// exclusive locks. The version rises; a Live, running or failed generation no longer represents the configured set, so it
    /// must restart, and its claim is released, so its worker records nothing more. The replay floor is kept.
    /// </summary>
    public void ChangeScopeSet(string outcomeCode, DateTimeOffset now)
    {
        ScopeVersion++;
        LastOutcome = outcomeCode;
        if (State is not (InitialSyncState.NotStarted or InitialSyncState.RequiresRestart))
        {
            State = InitialSyncState.RequiresRestart;
            CutoverFence = null;
            LiveAtUtc = null;
            Attempts = 0;
            NextAttemptAtUtc = null;
            Release();
        }

        UpdatedAtUtc = now.ToUniversalTime();
    }

    /// <summary>
    /// A run claims the running generation until its lease ends. Only one claim is live at a time: a claim whose lease ended
    /// may be taken over, and the worker that held it then records nothing more.
    /// </summary>
    /// <exception cref="InvalidOperationException">The generation is not running, or another claim is still live.</exception>
    public void Claim(Guid claimId, DateTimeOffset leaseUntil, DateTimeOffset now)
    {
        if (claimId == Guid.Empty || !IsRunning)
        {
            throw new InvalidOperationException("Only a running generation is claimed, by a named claim.");
        }

        if (ClaimId is not null && ClaimedUntilUtc > now)
        {
            throw new InvalidOperationException("Another worker's claim on the generation is still live.");
        }

        ClaimId = claimId;
        ClaimedUntilUtc = leaseUntil.ToUniversalTime();
        UpdatedAtUtc = now.ToUniversalTime();
    }

    /// <summary>The claim holder extends its lease.</summary>
    public void Renew(Guid claimId, DateTimeOffset leaseUntil, DateTimeOffset now)
    {
        RequireClaim(claimId, InitialSyncState.Buffering, InitialSyncState.Reading, InitialSyncState.Replaying);
        ClaimedUntilUtc = leaseUntil.ToUniversalTime();
        UpdatedAtUtc = now.ToUniversalTime();
    }

    /// <summary>The claimed generation's baseline starts.</summary>
    public void BeginReading(Guid claimId, DateTimeOffset now)
    {
        RequireClaim(claimId, InitialSyncState.Buffering, InitialSyncState.Reading);
        State = InitialSyncState.Reading;
        UpdatedAtUtc = now.ToUniversalTime();
    }

    /// <summary>Every scope completed: the replay of held receipts starts, up to the given fence first.</summary>
    public void BeginReplay(Guid claimId, long replayFence, DateTimeOffset now)
    {
        RequireClaim(claimId, InitialSyncState.Reading);
        if (replayFence < ReplayFloor)
        {
            throw new InvalidOperationException("A replay fence is never below the replay floor.");
        }

        State = InitialSyncState.Replaying;
        ReplayFence = replayFence;
        Attempts = 0;
        NextAttemptAtUtc = null;
        UpdatedAtUtc = now.ToUniversalTime();
    }

    /// <summary>
    /// The cutover: every held receipt up to the fence was replayed in the caller's transaction, which holds the row's
    /// exclusive lock, so the connection is Live from its commit and the claim is released. Only a generation that read the
    /// current scope set becomes Live.
    /// </summary>
    public void GoLive(Guid claimId, long cutoverFence, DateTimeOffset now)
    {
        RequireClaim(claimId, InitialSyncState.Replaying);
        if (GenerationScopeVersion != ScopeVersion)
        {
            throw new InvalidOperationException("A generation that read an earlier scope set never becomes Live.");
        }

        if (cutoverFence < (ReplayFence ?? ReplayFloor))
        {
            throw new InvalidOperationException("A cutover fence is never below the replay fence.");
        }

        State = InitialSyncState.Live;
        CutoverFence = cutoverFence;
        LastOutcome = null;
        Attempts = 0;
        NextAttemptAtUtc = null;
        LiveAtUtc = now.ToUniversalTime();
        UpdatedAtUtc = LiveAtUtc.Value;
        Release();
    }

    /// <summary>A required scope cannot complete: the generation fails and never reaches Live.</summary>
    public void Fail(Guid claimId, string outcomeCode, DateTimeOffset now)
    {
        RequireClaim(claimId, InitialSyncState.Reading);
        State = InitialSyncState.Failed;
        LastOutcome = outcomeCode;
        NextAttemptAtUtc = null;
        UpdatedAtUtc = now.ToUniversalTime();
        Release();
    }

    /// <summary>The connection was authorized again: the generation is stale and the next run starts a new one.</summary>
    public void RequireRestart(Guid claimId, string outcomeCode, DateTimeOffset now)
    {
        RequireClaim(claimId, InitialSyncState.Buffering, InitialSyncState.Reading, InitialSyncState.Replaying);
        State = InitialSyncState.RequiresRestart;
        LastOutcome = outcomeCode;
        NextAttemptAtUtc = null;
        UpdatedAtUtc = now.ToUniversalTime();
        Release();
    }

    /// <summary>The generation waits, in its state, until the given time: a transient failure or access that does not stand.</summary>
    public void Wait(Guid claimId, string outcomeCode, int attempts, DateTimeOffset nextAttemptAt, DateTimeOffset now)
    {
        RequireClaim(claimId, InitialSyncState.Buffering, InitialSyncState.Reading, InitialSyncState.Replaying);
        LastOutcome = outcomeCode;
        Attempts = attempts;
        NextAttemptAtUtc = nextAttemptAt.ToUniversalTime();
        UpdatedAtUtc = now.ToUniversalTime();
        Release();
    }

    /// <summary>Releases the claim without changing the generation, so another run may continue it.</summary>
    public void Yield(Guid claimId, DateTimeOffset now)
    {
        RequireClaim(claimId, InitialSyncState.Buffering, InitialSyncState.Reading, InitialSyncState.Replaying);
        UpdatedAtUtc = now.ToUniversalTime();
        Release();
    }

    private void RequireClaim(Guid claimId, params InitialSyncState[] states)
    {
        if (!IsClaimedBy(claimId))
        {
            throw new InvalidOperationException("Only the worker holding the claim changes the generation.");
        }

        if (!states.Contains(State))
        {
            throw new InvalidOperationException("The generation is not in a state that allows this transition.");
        }
    }

    private void Release()
    {
        ClaimId = null;
        ClaimedUntilUtc = null;
    }
}
