using System.Buffers.Binary;
using System.Text.Json;
using PrivLock.Domain.Results;

namespace PrivLock.Platform.Windows.Privileged;

/// <summary>
/// Bounded, correlated framing for the transient elevated worker. The nonce is a session
/// correlation value; trust comes from the kernel-reported peer PIDs and the pipe ACL.
/// </summary>
internal static class PrivilegedPipeProtocol
{
    internal const int CurrentVersion = 1;
    internal const int MaxFrameBytes = 128 * 1024;
    internal const string Hello = "hello";
    internal const string HelloAck = "hello-ack";
    internal const string Command = "command";
    internal const string Response = "response";
    internal const string Disconnect = "disconnect";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task WriteAsync(
        Stream stream,
        PrivilegedPipeFrame frame,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        if (payload.Length == 0 || payload.Length > MaxFrameBytes)
            throw new InvalidDataException("Privileged IPC frame exceeds the allowed size.");

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static async Task<PrivilegedPipeFrame> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
            throw new InvalidDataException("Privileged IPC frame length is invalid.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<PrivilegedPipeFrame>(payload, JsonOptions)
            ?? throw new InvalidDataException("Privileged IPC frame could not be deserialized.");
    }
}

internal sealed class PrivilegedPipeFrame
{
    public int Version { get; init; }
    public required string Type { get; init; }
    public required string SessionNonce { get; init; }
    public required string RequestId { get; init; }
    public int ProcessId { get; init; }
    public string? Command { get; init; }
    public string? Argument { get; init; }
    public OperationResult? Result { get; init; }
}
