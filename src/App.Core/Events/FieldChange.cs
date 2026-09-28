namespace WorkContinuity.Core.Events;

/// <summary>
/// One field's before/after change on the event's work object.
/// </summary>
/// <remarks>
/// <para>
/// Values are the source's values, carried as data and never interpreted as instructions
/// (invariant 25). <see cref="Before"/> is null when the source did not report the previous value;
/// <see cref="After"/> is null when the change cleared the field. A value that is present is bounded,
/// single-line text (<see cref="EventText.IsValue"/>), so any value a valid event carries can become a
/// Snapshot fact exactly as the source stated it.
/// </para>
/// <para>
/// Every change declares its <see cref="FactDataType"/>, which is what authority is resolved from.
/// Field names are tokens of the product's vocabulary; mapping a provider's fields onto them is the
/// connector's job (Gate 3 for monday).
/// </para>
/// </remarks>
public sealed record FieldChange
{
    /// <summary>Initializes a new instance of the <see cref="FieldChange"/> class.</summary>
    /// <exception cref="ArgumentNullException">The field name is null.</exception>
    public FieldChange(string field, FactDataType dataType, string? before, string? after)
    {
        ArgumentNullException.ThrowIfNull(field);

        Field = field;
        DataType = dataType;
        Before = before;
        After = after;
    }

    /// <summary>Gets the field name token, for example "status".</summary>
    public string Field { get; }

    /// <summary>Gets the field's data type, which determines its authority.</summary>
    public FactDataType DataType { get; }

    /// <summary>Gets the previous value, or null when the source did not report it.</summary>
    public string? Before { get; }

    /// <summary>Gets the new value, or null when the change cleared the field.</summary>
    public string? After { get; }

    /// <summary>Gets a value indicating whether the field name and data type are well formed.</summary>
    internal bool IsWellFormed =>
        EventText.IsToken(Field)
        && DataType != FactDataType.None
        && Enum.IsDefined(DataType);

    /// <summary>Gets a value indicating whether each value is absent or an acceptable field value.</summary>
    internal bool HasValidValues =>
        (Before is null || EventText.IsValue(Before))
        && (After is null || EventText.IsValue(After));
}
