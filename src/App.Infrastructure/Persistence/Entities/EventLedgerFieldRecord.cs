using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>
/// One field an event changed, with its data type and what processing decided for it. Never its value.
/// </summary>
/// <remarks>
/// Field names are tokens of the product's vocabulary, not work content, so the record outlives raw content
/// with the rest of the ledger. Append-only, like the ledger.
/// </remarks>
public sealed class EventLedgerFieldRecord : ITenantOwned
{
    private EventLedgerFieldRecord()
    {
        // Materialization only.
    }

    private EventLedgerFieldRecord(Guid eventId, TenantId tenantId, string field, FactDataType dataType, FieldOrderingDecision decision)
    {
        EventId = eventId;
        TenantId = tenantId;
        Field = field;
        DataType = dataType;
        Decision = decision;
    }

    /// <summary>Gets the ledger event the field belongs to.</summary>
    public Guid EventId { get; private set; }

    /// <summary>Gets the owning tenant. Fixed at creation.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the field name token.</summary>
    public string Field { get; private set; } = string.Empty;

    /// <summary>Gets the field's data type, which decided its authority.</summary>
    public FactDataType DataType { get; private set; }

    /// <summary>Gets what processing decided for the field.</summary>
    public FieldOrderingDecision Decision { get; private set; }

    /// <summary>Records one field's decision.</summary>
    /// <exception cref="ArgumentException">A component is missing or unspecified.</exception>
    public static EventLedgerFieldRecord Record(
        Guid eventId,
        TenantId tenantId,
        string field,
        FactDataType dataType,
        FieldOrderingDecision decision)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("A field record requires an event id.", nameof(eventId));
        }

        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("A field record requires a tenant.", nameof(tenantId));
        }

        if (!EventText.IsToken(field))
        {
            throw new ArgumentException("A field name must be a token.", nameof(field));
        }

        if (dataType == FactDataType.None || !Enum.IsDefined(dataType) || decision == FieldOrderingDecision.None || !Enum.IsDefined(decision))
        {
            throw new ArgumentException("A field record requires a data type and a decision.", nameof(decision));
        }

        return new EventLedgerFieldRecord(eventId, tenantId, field, dataType, decision);
    }
}
