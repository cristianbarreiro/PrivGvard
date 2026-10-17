using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PrivLock.Platform.Abstractions;

namespace PrivLock.Platform.Windows.System;

[SupportedOSPlatform("windows10.0.19041")]
public sealed class WindowsAutostartChangeObserver : IAutostartChangeObserver
{
    private readonly Func<bool> _isPackaged;
    private readonly IWindowsRegistryChangeWaiter _waiter;

    public WindowsAutostartChangeObserver()
        : this(
            () => PackageIdentityHelper.IsRunningAsPackaged,
            new WindowsRegistryChangeWaiter())
    {
    }

    internal WindowsAutostartChangeObserver(
        Func<bool> isPackaged,
        IWindowsRegistryChangeWaiter waiter)
    {
        _isPackaged = isPackaged;
        _waiter = waiter;
    }

    public bool RequiresVisiblePolling => _isPackaged();

    public async Task ObserveChangesAsync(
        Action onChanged,
        Action<Exception> onError,
        CancellationToken cancellationToken)
    {
        if (RequiresVisiblePolling)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _waiter.WaitForChangeAsync(cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                    onChanged();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            onError(ex);
        }
    }
}

internal interface IWindowsRegistryChangeWaiter
{
    Task WaitForChangeAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsRegistryChangeWaiter : IWindowsRegistryChangeWaiter
{
    private const string WatchKeyPath = @"Software\Microsoft\Windows\CurrentVersion";
    private const uint NotifyChangeName = 0x00000001;
    private const uint NotifyChangeLastSet = 0x00000004;

    public Task WaitForChangeAsync(CancellationToken cancellationToken) =>
        Task.Run(() => WaitForChange(cancellationToken));

    private static void WaitForChange(CancellationToken cancellationToken)
    {
        using var key = Registry.CurrentUser.OpenSubKey(WatchKeyPath)
            ?? throw new InvalidOperationException(
                "The current user's Windows startup registry key could not be opened for change notifications.");
        using var changed = new EventWaitHandle(false, EventResetMode.AutoReset);

        var result = RegNotifyChangeKeyValue(
            key.Handle,
            watchSubtree: true,
            NotifyChangeName | NotifyChangeLastSet,
            changed.SafeWaitHandle,
            asynchronous: true);
        if (result != 0)
            throw new Win32Exception(result, "Windows could not register an autostart change notification.");

        WaitHandle.WaitAny([changed, cancellationToken.WaitHandle]);
    }

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        SafeWaitHandle eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
