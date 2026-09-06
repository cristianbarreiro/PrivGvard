using System.Buffers.Binary;
using System.IO.Pipes;
using PrivLock.Domain.Models;
using PrivLock.Platform.Windows.Devices;
using PrivLock.Platform.Windows.Privileged;
using Xunit;

namespace PrivLock.Platform.Windows.Tests;

public sealed class PrivilegedProtocolTests
{
    [Fact]
    public async Task Frame_RoundTripsWithCorrelationFieldsIntact()
    {
        var frame = new PrivilegedPipeFrame
        {
            Version = PrivilegedPipeProtocol.CurrentVersion,
            Type = PrivilegedPipeProtocol.Command,
            SessionNonce = new string('A', 64),
            RequestId = "request-1",
            ProcessId = 123,
            Command = "ping",
            Argument = "payload"
        };
        await using var stream = new MemoryStream();

        await PrivilegedPipeProtocol.WriteAsync(stream, frame, CancellationToken.None);
        stream.Position = 0;
        var decoded = await PrivilegedPipeProtocol.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(frame.Version, decoded.Version);
        Assert.Equal(frame.Type, decoded.Type);
        Assert.Equal(frame.SessionNonce, decoded.SessionNonce);
        Assert.Equal(frame.RequestId, decoded.RequestId);
        Assert.Equal(frame.ProcessId, decoded.ProcessId);
        Assert.Equal(frame.Command, decoded.Command);
        Assert.Equal(frame.Argument, decoded.Argument);
    }

    [Fact]
    public async Task Read_RejectsOversizedFrameBeforeAllocatingPayload()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, PrivilegedPipeProtocol.MaxFrameBytes + 1);
        await using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PrivilegedPipeProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Write_RejectsOversizedSerializedFrame()
    {
        var frame = new PrivilegedPipeFrame
        {
            Version = PrivilegedPipeProtocol.CurrentVersion,
            Type = PrivilegedPipeProtocol.Command,
            SessionNonce = new string('A', 64),
            RequestId = "request-large",
            ProcessId = 123,
            Command = "apply-policy",
            Argument = new string('X', PrivilegedPipeProtocol.MaxFrameBytes)
        };
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PrivilegedPipeProtocol.WriteAsync(stream, frame, CancellationToken.None));
    }

    [Fact]
    public void WorkerLaunchContext_RequiresCanonicalRandomIdentifiers()
    {
        Assert.True(WindowsPrivilegedWorker.IsValidLaunchContext(
            "PrivLock_Pipe_0123456789abcdef0123456789abcdef",
            42,
            new string('A', 64)));
        Assert.False(WindowsPrivilegedWorker.IsValidLaunchContext(
            "PrivLock_Pipe_../../unsafe",
            42,
            new string('A', 64)));
        Assert.False(WindowsPrivilegedWorker.IsValidLaunchContext(
            "PrivLock_Pipe_0123456789abcdef0123456789abcdef",
            0,
            new string('A', 64)));
        Assert.False(WindowsPrivilegedWorker.IsValidLaunchContext(
            "PrivLock_Pipe_0123456789abcdef0123456789abcdef",
            42,
            new string('G', 64)));
    }

    [Fact]
    public void ElevatedWorker_CommandAdmission_IsStrictlySingleUse()
    {
        var admission = new SinglePrivilegedCommandAdmission();

        Assert.True(admission.TryAdmit());
        Assert.False(admission.TryAdmit());
    }

    [Fact]
    public void TrustedDispatcher_RejectsMalformedPayloadsWithoutNativeMutation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.False(WindowsPrivilegedExecutor
            .ExecuteWorkerCommand("apply-policy", "not-base64", "S-1-5-21-1-2-3-1001").Success);
        Assert.False(WindowsPrivilegedExecutor
            .ExecuteWorkerCommand("restore-device", "not-base64", "S-1-5-21-1-2-3-1001").Success);
        Assert.False(WindowsPrivilegedExecutor
            .ExecuteWorkerCommand("not-whitelisted", "ignored", "S-1-5-21-1-2-3-1001").Success);
    }

    [Fact]
    public async Task KernelPipePeerQueries_ReturnActualConnectedProcessIds()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var pipeName = $"PrivLock_Test_{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        var connect = client.ConnectAsync(5000);
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await connect;

        Assert.Equal(Environment.ProcessId, WindowsPipePeerVerifier.GetClientProcessId(server));
        Assert.Equal(Environment.ProcessId, WindowsPipePeerVerifier.GetServerProcessId(client));
    }

    [Fact]
    public void SecureState_RequiresPolicyAndEveryDetectedDeviceToBeBlocked()
    {
        Assert.Equal(
            (true, false),
            WindowsProtectionProvider.EvaluateSecureState(BlockStatus.Blocked, 2, true, true, false));
        Assert.Equal(
            (false, true),
            WindowsProtectionProvider.EvaluateSecureState(BlockStatus.Blocked, 2, true, false, true));
        Assert.Equal(
            (false, true),
            WindowsProtectionProvider.EvaluateSecureState(BlockStatus.Blocked, 0, true, false, false));
        Assert.Equal(
            (false, true),
            WindowsProtectionProvider.EvaluateSecureState(BlockStatus.Allowed, 2, true, false, false));
    }

    [Fact]
    public void MicrophoneStandardState_RequiresConsentAndEveryEndpointMuteToAgree()
    {
        var muted = new WindowsAudioEndpointMuteState("endpoint-a", true);
        var unmuted = new WindowsAudioEndpointMuteState("endpoint-b", false);

        Assert.Equal(
            BlockStatus.Blocked,
            WindowsProtectionProvider.EvaluateMicrophoneStandardStatus(
                BlockStatus.Blocked, true, [muted]));
        Assert.Equal(
            BlockStatus.Unknown,
            WindowsProtectionProvider.EvaluateMicrophoneStandardStatus(
                BlockStatus.Blocked, true, [muted, unmuted]));
        Assert.Equal(
            BlockStatus.Unknown,
            WindowsProtectionProvider.EvaluateMicrophoneStandardStatus(
                BlockStatus.Blocked, true, []));
    }
}
