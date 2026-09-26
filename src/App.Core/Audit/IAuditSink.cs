namespace WorkContinuity.Core.Audit;

/// <summary>
/// Accepts security/audit records.
/// </summary>
/// <remarks>
/// Gate 1C defines this contract only. No implementation ships yet, and none is registered, so
/// there is intentionally no way to persist an audit record until the storage Gate provides a sink
/// with its own retention lifecycle — separate from raw work data.
/// </remarks>
public interface IAuditSink
{
    /// <summary>Records one audit event.</summary>
    Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default);
}
