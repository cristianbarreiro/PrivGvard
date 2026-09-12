using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PrivLock.Infrastructure.Common.Logging;
using Serilog;

namespace PrivLock.Platform.Windows.Privileged;

/// <summary>
/// Short-lived elevated worker. It accepts commands only after bilateral PID verification and a
/// versioned nonce handshake with its launching standard-user process.
/// </summary>
public static class WindowsPrivilegedWorker
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(WindowsPrivilegedWorker));

    public static int Run(string pipeName, int parentProcessId, string sessionNonce)
    {
        if (!IsValidLaunchContext(pipeName, parentProcessId, sessionNonce))
            return 1;

        try
        {
            if (!IsCurrentProcessElevated())
            {
                Log.Error("Privileged worker rejected a non-elevated launch");
                return 1;
            }

            using var operationLease = WindowsPrivilegedExecutor.TryAcquirePrivilegedOperationLease(
                TimeSpan.FromSeconds(30));
            if (operationLease == null)
            {
                Log.Error("Elevated worker timed out waiting for the privileged-operation lease");
                return 1;
            }

            using var parent = Process.GetProcessById(parentProcessId);
            if (parent.HasExited || !IsSameExecutable(parent))
            {
                Log.Error("Privileged worker rejected a parent that was not the same PrivLock executable");
                return 1;
            }

            var ownerUserSid = GetProcessUserSid(parent);
            if (string.IsNullOrWhiteSpace(ownerUserSid))
            {
                Log.Error("Privileged worker could not establish its unelevated parent user identity");
                return 1;
            }

            using var pipeClient = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            pipeClient.Connect(10000);

            var serverPid = WindowsPipePeerVerifier.GetServerProcessId(pipeClient);
            if (serverPid != parentProcessId || parent.HasExited)
            {
                Log.Error("Privileged worker rejected a pipe whose kernel server PID did not match its parent");
                return 1;
            }

            using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var helloRequestId = Guid.NewGuid().ToString("N");
            PrivilegedPipeProtocol.WriteAsync(pipeClient, new PrivilegedPipeFrame
            {
                Version = PrivilegedPipeProtocol.CurrentVersion,
                Type = PrivilegedPipeProtocol.Hello,
                SessionNonce = sessionNonce,
                RequestId = helloRequestId,
                ProcessId = Environment.ProcessId
            }, handshakeTimeout.Token).GetAwaiter().GetResult();

            var ack = PrivilegedPipeProtocol.ReadAsync(pipeClient, handshakeTimeout.Token)
                .GetAwaiter().GetResult();
            if (ack.Version != PrivilegedPipeProtocol.CurrentVersion ||
                ack.Type != PrivilegedPipeProtocol.HelloAck ||
                ack.ProcessId != parentProcessId ||
                !string.Equals(ack.SessionNonce, sessionNonce, StringComparison.Ordinal) ||
                !string.Equals(ack.RequestId, helloRequestId, StringComparison.Ordinal))
            {
                Log.Error("Privileged worker rejected an invalid server handshake");
                return 1;
            }

            while (pipeClient.IsConnected && !parent.HasExited)
            {
                using var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                PrivilegedPipeFrame request;
                try
                {
                    request = PrivilegedPipeProtocol.ReadAsync(pipeClient, commandTimeout.Token)
                        .GetAwaiter().GetResult();
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    Log.Information("Elevated worker session timed out on idle; exiting cleanly");
                    break;
                }

                if (request.Type == PrivilegedPipeProtocol.Disconnect)
                {
                    Log.Information("Elevated worker received clean disconnect request");
                    break;
                }

                if (!IsValidAuthenticatedCommand(request, sessionNonce, parentProcessId))
                {
                    Log.Error("Privileged worker rejected an invalid authenticated command frame");
                    return 1;
                }

                var result = WindowsPrivilegedExecutor.ExecuteWorkerCommand(
                    request.Command!,
                    request.Argument!,
                    ownerUserSid);

                using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                PrivilegedPipeProtocol.WriteAsync(pipeClient, new PrivilegedPipeFrame
                {
                    Version = PrivilegedPipeProtocol.CurrentVersion,
                    Type = PrivilegedPipeProtocol.Response,
                    SessionNonce = sessionNonce,
                    RequestId = request.RequestId,
                    ProcessId = Environment.ProcessId,
                    Result = result
                }, responseTimeout.Token).GetAwaiter().GetResult();
            }

            return 0;
        }
        catch (OperationCanceledException ex)
        {
            CrashReporter.GenerateCrashReport(ex, "WindowsPrivilegedWorker.Timeout");
            Log.Error(ex, "Elevated worker IPC timed out");
            return 1;
        }
        catch (Exception ex)
        {
            CrashReporter.GenerateCrashReport(ex, "WindowsPrivilegedWorker.Run");
            Log.Error(ex, "Elevated worker encountered an error");
            return 1;
        }
    }

    public static bool IsValidLaunchContext(string pipeName, int parentProcessId, string sessionNonce)
    {
        const string prefix = "PrivLock_Pipe_";
        return parentProcessId > 0 &&
               pipeName.Length == prefix.Length + 32 &&
               pipeName.StartsWith(prefix, StringComparison.Ordinal) &&
               pipeName.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 &&
               sessionNonce.Length == 64 &&
               sessionNonce.AsSpan().IndexOfAnyExcept("0123456789ABCDEF") < 0;
    }

    private static bool IsValidAuthenticatedCommand(
        PrivilegedPipeFrame frame,
        string sessionNonce,
        int parentProcessId)
    {
        if (frame.Version != PrivilegedPipeProtocol.CurrentVersion ||
            frame.ProcessId != parentProcessId ||
            !string.Equals(frame.SessionNonce, sessionNonce, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(frame.RequestId))
        {
            return false;
        }

        return frame.Type == PrivilegedPipeProtocol.Command &&
               !string.IsNullOrWhiteSpace(frame.Command) &&
               frame.Command.Length <= 64 &&
               frame.Argument != null &&
               frame.Argument.Length <= 64 * 1024;
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsSameExecutable(Process parent)
    {
        var currentPath = Environment.ProcessPath;
        var parentPath = parent.MainModule?.FileName;
        return !string.IsNullOrWhiteSpace(currentPath) &&
               !string.IsNullOrWhiteSpace(parentPath) &&
               string.Equals(
                   Path.GetFullPath(currentPath),
                   Path.GetFullPath(parentPath),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetProcessUserSid(Process process)
    {
        if (!OpenProcessToken(process.Handle, TokenQuery, out var tokenHandle))
            return null;

        try
        {
            using var identity = new WindowsIdentity(tokenHandle);
            return identity.User?.Value;
        }
        finally
        {
            _ = CloseHandle(tokenHandle);
        }
    }

    private const uint TokenQuery = 0x0008;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
