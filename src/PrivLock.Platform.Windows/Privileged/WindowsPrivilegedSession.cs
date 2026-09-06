using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using PrivLock.Domain.Results;
using Serilog;

namespace PrivLock.Platform.Windows.Privileged;

/// <summary>
/// Client-side manager for one operation-scoped elevated worker. The pipe is ACL-restricted and
/// both endpoints authenticate the other process through kernel-reported PIDs before dispatch.
/// </summary>
public sealed class WindowsPrivilegedSession : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsPrivilegedSession>();
    private static readonly Lazy<WindowsPrivilegedSession> InstanceLazy = new(() => new());

    private readonly object _lock = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private NamedPipeServerStream? _pipeServer;
    private Process? _workerProcess;
    private string? _sessionNonce;
    private long _requestSequence;
    private bool _isElevatedSessionActive;

    public static WindowsPrivilegedSession Instance => InstanceLazy.Value;

    public bool IsSessionActive
    {
        get
        {
            lock (_lock)
            {
                return _isElevatedSessionActive &&
                       _pipeServer is { IsConnected: true } &&
                       _workerProcess is { HasExited: false };
            }
        }
    }

    public async Task<OperationResult> ExecuteCommandAsync(string command, string argument)
    {
        if (!IsValidCommand(command, argument))
            return OperationResult.Fail("Invalid elevated command framing.");

        await _commandGate.WaitAsync();
        var dispatchStarted = false;
        OperationResult result;
        var quiesced = false;
        try
        {
            await EnsureSessionActiveAsync();

            NamedPipeServerStream pipe;
            string nonce;
            lock (_lock)
            {
                if (_pipeServer is not { IsConnected: true } || string.IsNullOrEmpty(_sessionNonce))
                    throw new InvalidOperationException("Elevated worker is not connected.");
                pipe = _pipeServer;
                nonce = _sessionNonce;
            }

            var requestId = Interlocked.Increment(ref _requestSequence).ToString(
                global::System.Globalization.CultureInfo.InvariantCulture);
            var request = new PrivilegedPipeFrame
            {
                Version = PrivilegedPipeProtocol.CurrentVersion,
                Type = PrivilegedPipeProtocol.Command,
                SessionNonce = nonce,
                RequestId = requestId,
                ProcessId = Environment.ProcessId,
                Command = command,
                Argument = argument
            };

            using var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            dispatchStarted = true;
            await PrivilegedPipeProtocol.WriteAsync(pipe, request, commandTimeout.Token);
            var response = await PrivilegedPipeProtocol.ReadAsync(pipe, commandTimeout.Token);
            if (response.Version != PrivilegedPipeProtocol.CurrentVersion ||
                response.Type != PrivilegedPipeProtocol.Response ||
                !string.Equals(response.SessionNonce, nonce, StringComparison.Ordinal) ||
                !string.Equals(response.RequestId, requestId, StringComparison.Ordinal) ||
                response.Result == null)
            {
                result = OperationResult.Fail(
                    "Elevated worker returned an unauthenticated or mismatched response.",
                    outcomeUncertain: true,
                    executionStillInFlight: false);
            }
            else
            {
                result = response.Result;
            }
        }
        catch (global::System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warning("User cancelled UAC elevation prompt for elevated session");
            result = OperationResult.Fail("Operation cancelled: Administrator permissions were denied.");
        }
        catch (OperationCanceledException ex)
        {
            Log.Error(ex, "Elevated command timed out: Command={Command}", command);
            result = OperationResult.Fail(
                "Elevated operation timed out; helper quiescence is being enforced.",
                outcomeUncertain: dispatchStarted,
                executionStillInFlight: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to execute command '{Command}' via elevated session", command);
            result = OperationResult.Fail(
                $"Elevated worker error: {ex.Message}",
                outcomeUncertain: dispatchStarted,
                executionStillInFlight: false);
        }
        finally
        {
            // The elevated worker is single-command by contract. Prove it has exited before
            // allowing another command to launch a new worker.
            quiesced = CloseSessionCore();
            if (!quiesced)
                Log.Error("Single-command elevated worker did not quiesce after command completion");
            _commandGate.Release();
        }

        return quiesced
            ? result
            : OperationResult.Fail(
                "Single-command elevated worker did not exit; mutation reconciliation is deferred until startup quiescence.",
                result.Details,
                outcomeUncertain: dispatchStarted || result.OutcomeUncertain,
                executionStillInFlight: true);
    }

    private async Task EnsureSessionActiveAsync()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            throw new InvalidOperationException("Cannot locate current executable path for privileged session.");

        // Keep the exact executable path non-writable/non-replaceable until the child has loaded
        // and authenticated. The release installer additionally places it under Program Files.
        using var executableLease = new FileStream(
            exePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        CloseSessionCore();

        var pipeName = $"PrivLock_Pipe_{Guid.NewGuid():N}";
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        NamedPipeServerStream? candidatePipe = null;
        Process? candidateProcess = null;
        try
        {
            candidatePipe = CreateSecuredPipe(pipeName);
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--privileged-worker");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(nonce);

            candidateProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to launch elevated session worker process.");

            using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await AcceptAuthenticatedWorkerAsync(candidatePipe, candidateProcess, nonce, connectionTimeout.Token);

            lock (_lock)
            {
                _pipeServer = candidatePipe;
                _workerProcess = candidateProcess;
                _sessionNonce = nonce;
                _isElevatedSessionActive = true;
            }

            candidatePipe = null;
            candidateProcess = null;
            Log.Information("Authenticated single-command elevated worker established");
        }
        catch
        {
            CleanupFailedStartup(candidatePipe, candidateProcess);
            throw;
        }
    }

    private static NamedPipeServerStream CreateSecuredPipe(string pipeName)
    {
        var userSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(userSid);
        security.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(administrators, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            0,
            0,
            security,
            HandleInheritability.None,
            (PipeAccessRights)0);
    }

    private static async Task AcceptAuthenticatedWorkerAsync(
        NamedPipeServerStream pipe,
        Process worker,
        string nonce,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
            var clientPid = WindowsPipePeerVerifier.GetClientProcessId(pipe);
            if (clientPid != worker.Id || worker.HasExited)
            {
                Log.Warning("Rejected a named-pipe client whose kernel PID did not match the elevated worker");
                pipe.Disconnect();
                continue;
            }

            var hello = await PrivilegedPipeProtocol.ReadAsync(pipe, cancellationToken);
            if (hello.Version != PrivilegedPipeProtocol.CurrentVersion ||
                hello.Type != PrivilegedPipeProtocol.Hello ||
                hello.ProcessId != worker.Id ||
                !string.Equals(hello.SessionNonce, nonce, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(hello.RequestId))
            {
                throw new InvalidDataException("Elevated worker handshake was invalid.");
            }

            await PrivilegedPipeProtocol.WriteAsync(pipe, new PrivilegedPipeFrame
            {
                Version = PrivilegedPipeProtocol.CurrentVersion,
                Type = PrivilegedPipeProtocol.HelloAck,
                SessionNonce = nonce,
                RequestId = hello.RequestId,
                ProcessId = Environment.ProcessId
            }, cancellationToken);
            return;
        }
    }

    /// <summary>
    /// Ends the helper and proves process/lease quiescence before callers reconcile OS state.
    /// </summary>
    public bool CloseSession()
    {
        _commandGate.Wait();
        try
        {
            return CloseSessionCore();
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private bool CloseSessionCore()
    {
        NamedPipeServerStream? pipe;
        Process? worker;
        lock (_lock)
        {
            _isElevatedSessionActive = false;
            pipe = _pipeServer;
            worker = _workerProcess;
            _pipeServer = null;
            _workerProcess = null;
            _sessionNonce = null;
        }

        DisposeWithLogging(pipe, "pipe server");
        var processStopped = StopAndDisposeWorker(worker);
        var leaseReleased = WindowsPrivilegedExecutor
            .WaitForPreviousPrivilegedOperation(TimeSpan.FromSeconds(5))
            .Success;
        return processStopped && leaseReleased;
    }

    public void Dispose() => CloseSession();

    private static bool StopAndDisposeWorker(Process? process)
    {
        if (process == null)
            return true;

        var stopped = false;
        try
        {
            if (!process.HasExited && !process.WaitForExit(2000))
                process.Kill(entireProcessTree: false);
            if (!process.HasExited)
                process.WaitForExit(3000);
            stopped = process.HasExited;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not terminate or wait for elevated worker process");
        }
        finally
        {
            DisposeWithLogging(process, "elevated worker process");
        }
        return stopped;
    }

    private static void DisposeWithLogging(IDisposable? disposable, string component)
    {
        if (disposable == null)
            return;
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not dispose elevated session {Component}", component);
        }
    }

    private static void CleanupFailedStartup(NamedPipeServerStream? pipe, Process? process)
    {
        DisposeWithLogging(pipe, "unconnected pipe server");
        StopAndDisposeWorker(process);
    }

    private static bool IsValidCommand(string command, string argument) =>
        !string.IsNullOrWhiteSpace(command) &&
        command.Length <= 64 &&
        command.IndexOfAny(['\t', '\r', '\n']) < 0 &&
        argument.IndexOfAny(['\t', '\r', '\n']) < 0 &&
        argument.Length <= 64 * 1024;
}
