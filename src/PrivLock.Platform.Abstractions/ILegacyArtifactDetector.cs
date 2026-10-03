namespace PrivLock.Platform.Abstractions;

/// <summary>
/// Detects explicit legacy artifacts, markers, or unmanaged historical recovery state
/// on the local system without relying on user DesiredState as historical evidence.
/// </summary>
public interface ILegacyArtifactDetector
{
    bool HasLegacyEvidence();
}
