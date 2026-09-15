using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace PrivLock.Platform.Windows.System;

/// <summary>
/// Provides helpers to detect if the current application is running inside a packaged MSIX / AppX container.
/// Uses native Win32 GetCurrentPackageFullName from kernel32.dll with zero external dependencies.
/// </summary>
public static class PackageIdentityHelper
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(PackageIdentityHelper));

    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    private static readonly Lazy<bool> _isPackaged = new(DetectPackagedIdentity);
    private static readonly Lazy<string?> _packageFullName = new(QueryPackageFullName);

    /// <summary>
    /// Gets whether the current process is executing with Windows package identity (MSIX/AppX).
    /// </summary>
    public static bool IsRunningAsPackaged => _isPackaged.Value;

    /// <summary>
    /// Gets the full package name if executing with package identity, or null if unpackaged.
    /// </summary>
    public static string? PackageFullName => _packageFullName.Value;

    private static bool DetectPackagedIdentity()
    {
        try
        {
            int length = 0;
            int result = GetCurrentPackageFullName(ref length, null);

            // If the buffer is insufficient, the package identity exists and requires `length` chars.
            if (result == ERROR_INSUFFICIENT_BUFFER || result == ERROR_SUCCESS)
            {
                Log.Debug("Application is running with Windows MSIX package identity");
                return true;
            }

            if (result == APPMODEL_ERROR_NO_PACKAGE)
            {
                Log.Debug("Application is running unpackaged (standard Win32)");
                return false;
            }

            Log.Warning("Unexpected result checking package identity: {ErrorCode}", result);
            return false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to query package identity via GetCurrentPackageFullName");
            return false;
        }
    }

    private static string? QueryPackageFullName()
    {
        try
        {
            int length = 0;
            int result = GetCurrentPackageFullName(ref length, null);

            if (result == ERROR_INSUFFICIENT_BUFFER && length > 0)
            {
                var sb = new StringBuilder(length);
                result = GetCurrentPackageFullName(ref length, sb);
                if (result == ERROR_SUCCESS)
                {
                    return sb.ToString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to retrieve package full name string");
            return null;
        }
    }
}
