using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Reconciliation;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The monday <see cref="IAuthoritativeSourceReader"/> (Gate 3E): rereads the current status values of known items through
/// the 3A read client, whose every attempt acquires the connection's 3B credential.
/// </summary>
/// <remarks>
/// <para>
/// It reads only the surface 3D supports, an item's status column, and binds each answer to what was asked (D3E-8): the
/// returned item must be the requested item, active, and on the target's board, its source scope (D3E-10); the column
/// must be the target's column, of monday's <c>status</c> type, with the typed <c>StatusValue</c> fields; and the call
/// must have used the connection's delegated identity. Anything else is refused, never remapped: an item on another
/// board stays unresolved, an archived or deleted item is never applied, a rollup status (a <c>BatteryValue</c>) is not
/// taken for a status, and absence is never read as deletion.
/// </para>
/// <para>
/// The value is the typed label, exactly as 3D maps a status, and null when the status is empty. The source update time
/// is the status value's own <c>updated_at</c>, read by exact formats; when monday gives none that reads, the state
/// carries none, and nothing else, neither the item's <c>updated_at</c> nor any local time, takes its place (D3E-3). The
/// evidence is a copy of exactly the provider values consumed, byte for byte, under its own schema version; never the
/// full response.
/// </para>
/// <para>
/// It calls monday only through <see cref="MondayReadClient"/>, which reads and never writes, and it writes no log.
/// </para>
/// </remarks>
public sealed class MondayStatusReader : IAuthoritativeSourceReader
{
    /// <summary>The schema version of a read's evidence, distinct from every webhook schema.</summary>
    public const string EvidenceSchemaVersion = "monday.api-read.v1";

    private const string StatusType = "status";

    // ISO 8601 with an offset, as monday documents a Date (2026-03-21T08:36:10+00:00), or in UTC with Z; whole seconds or
    // up to seven decimals; nothing else.
    private static readonly string[] UpdatedAtFormats =
    [
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.fzzz",
        "yyyy-MM-dd'T'HH:mm:ss.ffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.ffffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.fffffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.ffffffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.f'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
    ];

    private readonly MondayReadClient _client;

    /// <summary>Creates a reader over the 3A read client.</summary>
    /// <exception cref="ArgumentNullException">The client is null.</exception>
    public MondayStatusReader(MondayReadClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public string SourceSystem => MondayWebhooks.SourceSystem;

    /// <inheritdoc />
    public int MaxObjectsPerRead => MondayOperations.MaxItemsPerRead;

    /// <inheritdoc />
    public async Task<AuthoritativeReadResult> ReadAsync(AuthoritativeReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.SourceSystem, SourceSystem, StringComparison.Ordinal))
        {
            throw new ArgumentException("The monday reader reads monday targets only.", nameof(request));
        }

        // A target this reader cannot address is refused before anything is sent.
        var readings = new Dictionary<ReconciliationTarget, TargetReading>();
        var readable = new List<ReconciliationTarget>();
        foreach (var target in request.Targets)
        {
            if (Refusal(target) is { } refusal)
            {
                readings[target] = TargetReading.Refused(refusal);
            }
            else
            {
                readable.Add(target);
            }
        }

        var itemIds = readable.Select(target => target.WorkObject.SourceObjectId).Distinct(StringComparer.Ordinal).ToArray();
        var columnIds = readable.Select(target => target.Field).Distinct(StringComparer.Ordinal).ToArray();
        if (itemIds.Length == 0)
        {
            return AuthoritativeReadResult.Refused(readings);
        }

        if (itemIds.Length > MaxObjectsPerRead || columnIds.Length > MondayOperations.MaxColumnsPerRead)
        {
            return AuthoritativeReadResult.Failed(ReconciliationOutcome.UnsupportedResponse, identity: null);
        }

        var context = new MondayRequestContext(request.Tenant, request.ConnectionId, request.CorrelationId);
        var call = await _client.ReadItemStatusesByIdAsync(context, itemIds, columnIds, cancellationToken).ConfigureAwait(false);
        if (!call.IsSuccess)
        {
            return AuthoritativeReadResult.Failed(OutcomeOf(call.Failure.Kind), call.Identity, call.Failure.RetryAfter);
        }

        // Only the connection's delegated identity may confirm anything; the client already refused any other credential.
        if (call.Identity is not { Kind: ConnectorIdentityKind.DelegatedUser } identity
            || !identity.Serves(request.Tenant, request.ConnectionId))
        {
            return AuthoritativeReadResult.Failed(ReconciliationOutcome.IdentityMismatch, call.Identity);
        }

        var items = call.Value.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var target in readable)
        {
            readings[target] = Bind(target, items);
        }

        return AuthoritativeReadResult.Read(identity, readings);
    }

    // What this reader cannot read at all: another object type, a field 3D does not map, or a target without its board.
    private static ReconciliationOutcome? Refusal(ReconciliationTarget target) =>
        !string.Equals(target.WorkObject.ObjectType, MondayWebhookNormalizer.ObjectType, StringComparison.Ordinal)
            || target.DataType != FactDataType.TaskStatus
            || !target.WorkObject.SourceObjectId.All(char.IsAsciiDigit)
            ? ReconciliationOutcome.FieldUnsupported
            : target.SourceScope is null
                ? ReconciliationOutcome.ScopeMissing
                : null;

    private static TargetReading Bind(ReconciliationTarget target, Dictionary<string, MondayItemStatus> items)
    {
        if (!items.TryGetValue(target.WorkObject.SourceObjectId, out var item))
        {
            return TargetReading.Refused(ReconciliationOutcome.ObjectMissing);
        }

        if (item.State != MondayItemState.Active)
        {
            return TargetReading.Refused(ReconciliationOutcome.ObjectInactive);
        }

        // The same item on another board is not the same field: never remapped to whatever monday returned.
        if (item.BoardId is null || !string.Equals(item.BoardId, target.SourceScope, StringComparison.Ordinal))
        {
            return TargetReading.Refused(ReconciliationOutcome.BindingMismatch);
        }

        var column = item.Columns.SingleOrDefault(candidate => string.Equals(candidate.ColumnId, target.Field, StringComparison.Ordinal));
        if (column is null)
        {
            return TargetReading.Refused(ReconciliationOutcome.FieldMissing);
        }

        if (!string.Equals(column.ColumnType, StatusType, StringComparison.Ordinal)
            || column.Status is not { } status
            || (status.Label is null) != (status.Index is null))
        {
            return TargetReading.Refused(ReconciliationOutcome.FieldUnsupported);
        }

        if (status.Label is not null && !EventText.IsValue(status.Label))
        {
            return TargetReading.Refused(ReconciliationOutcome.ValueInvalid);
        }

        return TargetReading.Observed(new AuthoritativeFieldState(status.Label, UpdatedAt(status.UpdatedAt), Evidence(item, column, status)));
    }

    private static DateTimeOffset? UpdatedAt(string? text) =>
        text is not null
            && DateTimeOffset.TryParseExact(
                text, UpdatedAtFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    // Exactly the provider values consumed, each copied from monday's own JSON text, in the response's own shape.
    private static ObservationEvidence Evidence(MondayItemStatus item, MondayStatusColumn column, MondayStatusFields status)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", MondayOperations.ReadItemStatusesById.Name);
            writer.WriteString("api_version", MondayApiVersion.Pinned);
            writer.WriteStartObject("item");
            writer.WritePropertyName("id");
            writer.WriteRawValue(item.RawId);
            writer.WritePropertyName("state");
            writer.WriteRawValue(item.RawState);
            writer.WriteStartObject("board");
            writer.WritePropertyName("id");
            writer.WriteRawValue(item.RawBoardId!);
            writer.WriteEndObject();
            writer.WriteStartArray("column_values");
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            writer.WriteRawValue(column.RawId);
            writer.WritePropertyName("type");
            writer.WriteRawValue(column.RawType);
            writer.WritePropertyName("label");
            writer.WriteRawValue(status.RawLabel);
            writer.WritePropertyName("index");
            writer.WriteRawValue(status.RawIndex);
            writer.WritePropertyName("updated_at");
            writer.WriteRawValue(status.RawUpdatedAt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new ObservationEvidence(EvidenceSchemaVersion, Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    // A credential that cannot be obtained just now is transient; a credential monday or 3B refuses (revoked, needing
    // authorization again, rejected after a renewal) and a disabled connection or refused identity wait for access.
    private static ReconciliationOutcome OutcomeOf(ConnectorFailureKind kind) => kind switch
    {
        ConnectorFailureKind.RateLimited => ReconciliationOutcome.RateLimited,
        ConnectorFailureKind.ProviderTransient or ConnectorFailureKind.NetworkTransient => ReconciliationOutcome.ProviderUnavailable,
        ConnectorFailureKind.CredentialUnavailable => ReconciliationOutcome.CredentialUnavailable,
        ConnectorFailureKind.AuthenticationFailed or ConnectorFailureKind.ConnectionDisabled or ConnectorFailureKind.AuthorizationFailed
            => ReconciliationOutcome.ConnectionUnavailable,
        ConnectorFailureKind.ResourceNotFound => ReconciliationOutcome.ObjectMissing,
        _ => ReconciliationOutcome.UnsupportedResponse,
    };
}
