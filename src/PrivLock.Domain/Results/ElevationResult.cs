namespace PrivLock.Domain.Results;

/// <summary>
/// Result of an elevation request.
/// </summary>
public sealed class ElevationResult
{
    public bool IsElevated { get; init; }
    public bool CancelledByUser { get; init; }
    public string? ErrorMessage { get; init; }

    public static ElevationResult Success() =>
        new() { IsElevated = true };

    public static ElevationResult Cancelled() =>
        new() { IsElevated = false, CancelledByUser = true, ErrorMessage = "Elevation cancelled by user." };

    public static ElevationResult Fail(string error) =>
        new() { IsElevated = false, ErrorMessage = error };
}
