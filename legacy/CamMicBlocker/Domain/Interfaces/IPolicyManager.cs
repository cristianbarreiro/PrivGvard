using CamMicBlocker.Domain.Models;

namespace CamMicBlocker.Domain.Interfaces;

/// <summary>
/// Manages Windows privacy policy settings in the registry.
/// Reading policy does NOT require admin. Writing does.
/// </summary>
public interface IPolicyManager
{
    /// <summary>
    /// Reads the current camera privacy policy from the registry.
    /// Can be called without admin privileges.
    /// </summary>
    BlockStatus GetCameraPolicyStatus();

    /// <summary>
    /// Reads the current microphone privacy policy from the registry.
    /// Can be called without admin privileges.
    /// </summary>
    BlockStatus GetMicrophonePolicyStatus();

    /// <summary>
    /// Sets the privacy policy to block/allow. Requires admin — triggers UAC.
    /// </summary>
    /// <param name="target">Which device(s) to apply the policy to.</param>
    /// <param name="status">Blocked (deny access) or Allowed (remove restriction).</param>
    Task<OperationResult> SetPolicyAsync(BlockTarget target, BlockStatus status);
}
