using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PrivLock.Platform.Windows.Privileged;

/// <summary>
/// Authenticates named-pipe peers using process IDs supplied by the Windows kernel, not caller data.
/// </summary>
internal static class WindowsPipePeerVerifier
{
    internal static int GetClientProcessId(NamedPipeServerStream server)
    {
        if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var processId))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not authenticate elevated pipe client PID.");
        return checked((int)processId);
    }

    internal static int GetServerProcessId(NamedPipeClientStream client)
    {
        if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not authenticate privileged pipe server PID.");
        return checked((int)processId);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);
}
