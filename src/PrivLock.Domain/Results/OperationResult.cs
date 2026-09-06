namespace PrivLock.Domain.Results;

/// <summary>
/// Detail of an operation performed on a specific hardware device or endpoint.
/// </summary>
public sealed class DeviceOperationDetail
{
    public required string DeviceId { get; init; }
    public required string FriendlyName { get; init; }
    public bool Success { get; init; }
    public bool OutcomeUncertain { get; init; }
    public bool ExecutionStillInFlight { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of a protection or system operation.
/// </summary>
public sealed class OperationResult
{
    public bool Success { get; init; }
    /// <summary>
    /// True when dispatch may have reached the OS but no authoritative completion response was
    /// obtained. Callers must preserve the WAL entry and avoid claiming ownership/completion.
    /// </summary>
    public bool OutcomeUncertain { get; init; }
    /// <summary>
    /// The elevated/native actor could not be proven stopped. Reconciliation must not observe or
    /// terminalize the WAL until a later startup barrier proves quiescence.
    /// </summary>
    public bool ExecutionStillInFlight { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<DeviceOperationDetail> Details { get; init; } = [];

    public static OperationResult Ok(IReadOnlyList<DeviceOperationDetail>? details = null) =>
        new() { Success = true, Details = details ?? [] };

    public static OperationResult Fail(
        string error,
        IReadOnlyList<DeviceOperationDetail>? details = null,
        bool outcomeUncertain = false,
        bool executionStillInFlight = false) =>
        new()
        {
            Success = false,
            OutcomeUncertain = outcomeUncertain,
            ExecutionStillInFlight = executionStillInFlight,
            ErrorMessage = error,
            Details = details ?? []
        };
}
