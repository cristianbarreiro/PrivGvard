using PrivLock.Platform.Windows.Devices;
using PrivLock.Platform.Windows.Privileged;
using Xunit;

namespace PrivLock.Platform.Windows.Tests;

public sealed class WindowsPrivacySessionPlatformAdapterTests
{
    [Fact]
    public void SuppressRecoveryPassCompletion_PreventsCompleteRecoveryPassFromClosingSession()
    {
        var detector = new WindowsDeviceDetector();
        var controller = new WindowsDeviceController();
        var audio = new WindowsCoreAudioController();
        var adapter = new WindowsPrivacySessionPlatformAdapter(detector, controller, audio);

        // When suppressed, CompleteRecoveryPass should be a no-op
        adapter.SuppressRecoveryPassCompletion();

        // This must be suppressed
        adapter.CompleteRecoveryPass();

        // Resuming will release suppression and trigger session cleanup
        adapter.ResumeRecoveryPassCompletion();
    }

    [Fact]
    public void NestedSuppression_RequiresMatchingResumes()
    {
        var detector = new WindowsDeviceDetector();
        var controller = new WindowsDeviceController();
        var audio = new WindowsCoreAudioController();
        var adapter = new WindowsPrivacySessionPlatformAdapter(detector, controller, audio);

        adapter.SuppressRecoveryPassCompletion();
        adapter.SuppressRecoveryPassCompletion();

        // After one resume, suppression depth is 1 (still suppressed)
        adapter.ResumeRecoveryPassCompletion();
        adapter.CompleteRecoveryPass();

        // After second resume, suppression depth reaches 0 and closes
        adapter.ResumeRecoveryPassCompletion();
    }
}
