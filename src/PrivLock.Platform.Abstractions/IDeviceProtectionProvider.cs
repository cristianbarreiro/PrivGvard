using PrivLock.Domain.Models;
using PrivLock.Domain.Results;

namespace PrivLock.Platform.Abstractions;

/// <summary>
/// Unified contract for managing two-tier device protection (Standard and Secure) on the host operating system.
/// </summary>
public interface IDeviceProtectionProvider
{
    /// <summary>
    /// Enables standard protection for the specified target (operates without administrator rights).
    /// </summary>
    Task<OperationResult> EnableStandardProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables standard protection for the specified target.
    /// </summary>
    Task<OperationResult> DisableStandardProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables secure/administrator protection for the specified target (requests on-demand elevation).
    /// Precondition: Standard protection must already be active.
    /// </summary>
    Task<OperationResult> EnableSecureProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables secure/administrator protection for the specified target.
    /// </summary>
    Task<OperationResult> DisableSecureProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the verified protection state for both Camera and Microphone.
    /// </summary>
    Task<FullProtectionState> GetProtectionStateAsync(CancellationToken cancellationToken = default);
}
