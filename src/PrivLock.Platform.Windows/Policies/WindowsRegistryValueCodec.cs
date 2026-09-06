using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Infrastructure.Common.Logging;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.Windows.Policies;

/// <summary>
/// Captures, compares, and restores Registry values without collapsing absent/type/value states.
/// </summary>
internal static class WindowsRegistryValueCodec
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(WindowsRegistryValueCodec));

    internal static OriginalPolicyState Capture(
        PrivacyRegistryHive hive,
        PrivacyRegistryView view,
        string path,
        string valueName,
        ProtectionLayer layer,
        BlockTarget target,
        string operationId,
        PrivacyRegistryValueKind protectedKind,
        string protectedValue)
    {
        var original = Read(hive, view, path, valueName);
        var now = DateTimeOffset.UtcNow;
        return new OriginalPolicyState
        {
            ResourceId = $"registry:{hive}:{view}:{path}:{valueName}",
            OperationId = operationId,
            Layer = layer,
            Target = target,
            CapturedAtUtc = now,
            LastUpdatedAtUtc = now,
            RegistryHive = hive,
            RegistryView = view,
            RegistryPath = path,
            ValueName = valueName,
            ValueExisted = original.Exists,
            OriginalValueKind = original.Kind,
            OriginalValue = original.CanonicalValue,
            ProtectedValueExists = true,
            ProtectedValueKind = protectedKind,
            ProtectedValue = protectedValue
        };
    }

    internal static PrivacyResourceObservation Observe(OriginalPolicyState policy)
    {
        try
        {
            var current = Read(policy.RegistryHive, policy.RegistryView, policy.RegistryPath, policy.ValueName);
            if (MatchesOriginal(current, policy))
                return new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesOriginal);
            if (MatchesProtected(current, policy))
                return new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesProtected);
            return new PrivacyResourceObservation(
                PrivacyResourceObservationKind.Conflict,
                "Registry value differs from both the captured original and the PrivLock-applied value.");
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            global::System.Security.SecurityException or
            ArgumentException or
            FormatException or
            JsonException or
            OverflowException)
        {
            Log.Error(ex, "Failed to observe Registry value {Hive}\\{Path}\\{ValueName}", policy.RegistryHive, policy.RegistryPath, policy.ValueName);
            CrashReporter.GenerateCrashReport(ex, "WindowsRegistryValueCodec.Observe");
            return new PrivacyResourceObservation(PrivacyResourceObservationKind.Error, ex.Message);
        }
    }

    /// <summary>
    /// Performs the final compare immediately before applying the protected value. A value that
    /// became protected after a fresh snapshot is treated as externally owned, not as our success.
    /// </summary>
    internal static OperationResult CompareAndApplyProtected(OriginalPolicyState policy)
    {
        var mutationStarted = false;
        try
        {
            var current = Read(policy.RegistryHive, policy.RegistryView, policy.RegistryPath, policy.ValueName);
            if (MatchesProtected(current, policy))
            {
                return policy.JournalState == PrivacyResourceJournalState.Applied &&
                       policy.ModifiedByPrivLock &&
                       !policy.OwnershipUncertain
                    ? OperationResult.Ok()
                    : OperationResult.Fail(
                        "Registry apply conflict: value reached the protected state after snapshot and was preserved as externally owned.");
            }
            if (!MatchesOriginal(current, policy))
                return OperationResult.Fail("Registry apply conflict: current value changed after snapshot.");

            mutationStarted = true;
            WriteValue(policy, useOriginal: false);
            var verified = Read(policy.RegistryHive, policy.RegistryView, policy.RegistryPath, policy.ValueName);
            return MatchesProtected(verified, policy)
                ? OperationResult.Ok()
                : ReportMutationFailure(
                    "Registry protected value could not be verified after application.",
                    "WindowsRegistryValueCodec.ApplyVerify");
        }
        catch (Exception ex) when (IsRegistryOperationException(ex))
        {
            Log.Error(ex, "Failed to conditionally apply Registry value {Hive}\\{Path}\\{ValueName}", policy.RegistryHive, policy.RegistryPath, policy.ValueName);
            CrashReporter.GenerateCrashReport(ex, "WindowsRegistryValueCodec.Apply");
            return OperationResult.Fail(
                $"Registry protected-value application failed: {ex.Message}",
                outcomeUncertain: mutationStarted);
        }
    }

    /// <summary>
    /// Repeats compare-and-restore immediately before writing. The Registry API has no atomic
    /// compare-exchange for arbitrary values, so an irreducible sub-call TOCTOU window remains.
    /// </summary>
    internal static OperationResult CompareAndRestore(OriginalPolicyState policy)
    {
        var mutationStarted = false;
        try
        {
            var current = Read(policy.RegistryHive, policy.RegistryView, policy.RegistryPath, policy.ValueName);
            if (MatchesOriginal(current, policy))
                return OperationResult.Ok();
            if (!MatchesProtected(current, policy))
                return OperationResult.Fail("Registry restore conflict: current value is externally owned and was preserved.");

            mutationStarted = true;
            WriteValue(policy, useOriginal: true);

            var verified = Read(policy.RegistryHive, policy.RegistryView, policy.RegistryPath, policy.ValueName);
            return MatchesOriginal(verified, policy)
                ? OperationResult.Ok()
                : ReportMutationFailure(
                    "Registry original value could not be verified after restoration.",
                    "WindowsRegistryValueCodec.RestoreVerify");
        }
        catch (Exception ex) when (IsRegistryOperationException(ex))
        {
            Log.Error(ex, "Failed to restore Registry value {Hive}\\{Path}\\{ValueName}", policy.RegistryHive, policy.RegistryPath, policy.ValueName);
            CrashReporter.GenerateCrashReport(ex, "WindowsRegistryValueCodec.Restore");
            return OperationResult.Fail(
                $"Registry restoration failed: {ex.Message}",
                outcomeUncertain: mutationStarted);
        }
    }

    internal static bool IsSupportedValue(OriginalPolicyState policy)
    {
        if (!policy.ValueExisted)
            return policy.OriginalValueKind == PrivacyRegistryValueKind.None && policy.OriginalValue == null;

        try
        {
            _ = Decode(policy.OriginalValueKind, policy.OriginalValue);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or OverflowException or ArgumentException)
        {
            Log.Warning(ex, "Rejected malformed serialized Registry value for {ValueName}", policy.ValueName);
            return false;
        }
    }

    private static OperationResult ReportMutationFailure(string error, string context)
    {
        CrashReporter.GenerateCrashReport(new InvalidOperationException(error), context);
        return OperationResult.Fail(error, outcomeUncertain: true);
    }

    private static RegistryValueSnapshot Read(
        PrivacyRegistryHive hive,
        PrivacyRegistryView view,
        string path,
        string valueName)
    {
        using var baseKey = OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(path, writable: false);
        if (key == null || !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
            return RegistryValueSnapshot.Missing();

        var registryKind = key.GetValueKind(valueName);
        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value == null)
            throw new InvalidDataException("Registry reported an existing value with null data.");

        var kind = FromRegistryKind(registryKind);
        return new RegistryValueSnapshot(true, kind, Encode(kind, value));
    }

    private static void WriteValue(OriginalPolicyState policy, bool useOriginal)
    {
        var exists = useOriginal ? policy.ValueExisted : policy.ProtectedValueExists;
        var kind = useOriginal ? policy.OriginalValueKind : policy.ProtectedValueKind;
        var value = useOriginal ? policy.OriginalValue : policy.ProtectedValue;

        using var baseKey = OpenBaseKey(policy.RegistryHive, policy.RegistryView);
        if (!exists)
        {
            using var key = baseKey.OpenSubKey(policy.RegistryPath, writable: true);
            key?.DeleteValue(policy.ValueName, throwOnMissingValue: false);
            return;
        }

        using var writableKey = baseKey.CreateSubKey(policy.RegistryPath, writable: true)
            ?? throw new IOException("Could not create/open Registry key for update.");
        writableKey.SetValue(policy.ValueName, Decode(kind, value), ToRegistryKind(kind));
    }

    private static RegistryKey OpenBaseKey(PrivacyRegistryHive hive, PrivacyRegistryView view)
    {
        var nativeHive = hive switch
        {
            PrivacyRegistryHive.CurrentUser => RegistryHive.CurrentUser,
            PrivacyRegistryHive.LocalMachine => RegistryHive.LocalMachine,
            _ => throw new ArgumentOutOfRangeException(nameof(hive))
        };
        var nativeView = view switch
        {
            PrivacyRegistryView.Default => RegistryView.Default,
            PrivacyRegistryView.Registry32 => RegistryView.Registry32,
            PrivacyRegistryView.Registry64 => RegistryView.Registry64,
            _ => throw new ArgumentOutOfRangeException(nameof(view))
        };
        return RegistryKey.OpenBaseKey(nativeHive, nativeView);
    }

    private static bool MatchesOriginal(RegistryValueSnapshot current, OriginalPolicyState policy) =>
        current.Exists == policy.ValueExisted &&
        current.Kind == policy.OriginalValueKind &&
        string.Equals(current.CanonicalValue, policy.OriginalValue, StringComparison.Ordinal);

    private static bool MatchesProtected(RegistryValueSnapshot current, OriginalPolicyState policy) =>
        current.Exists == policy.ProtectedValueExists &&
        current.Kind == policy.ProtectedValueKind &&
        string.Equals(current.CanonicalValue, policy.ProtectedValue, StringComparison.Ordinal);

    private static string Encode(PrivacyRegistryValueKind kind, object value) => kind switch
    {
        PrivacyRegistryValueKind.String or PrivacyRegistryValueKind.ExpandString => (string)value,
        PrivacyRegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
        PrivacyRegistryValueKind.DWord => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        PrivacyRegistryValueKind.MultiString => JsonSerializer.Serialize((string[])value),
        PrivacyRegistryValueKind.QWord => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Registry value kind.")
    };

    private static object Decode(PrivacyRegistryValueKind kind, string? value) => kind switch
    {
        PrivacyRegistryValueKind.String or PrivacyRegistryValueKind.ExpandString =>
            value ?? throw new FormatException("String Registry value is null."),
        PrivacyRegistryValueKind.Binary => Convert.FromBase64String(value ?? throw new FormatException("Binary Registry value is null.")),
        PrivacyRegistryValueKind.DWord => int.Parse(value ?? throw new FormatException("DWORD Registry value is null."), CultureInfo.InvariantCulture),
        PrivacyRegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(value ?? throw new FormatException("Multi-string Registry value is null."))
            ?? throw new FormatException("Multi-string Registry value is invalid."),
        PrivacyRegistryValueKind.QWord => long.Parse(value ?? throw new FormatException("QWORD Registry value is null."), CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Registry value kind.")
    };

    private static PrivacyRegistryValueKind FromRegistryKind(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String => PrivacyRegistryValueKind.String,
        RegistryValueKind.ExpandString => PrivacyRegistryValueKind.ExpandString,
        RegistryValueKind.Binary => PrivacyRegistryValueKind.Binary,
        RegistryValueKind.DWord => PrivacyRegistryValueKind.DWord,
        RegistryValueKind.MultiString => PrivacyRegistryValueKind.MultiString,
        RegistryValueKind.QWord => PrivacyRegistryValueKind.QWord,
        _ => throw new InvalidDataException($"Unsupported Registry value kind: {kind}.")
    };

    private static RegistryValueKind ToRegistryKind(PrivacyRegistryValueKind kind) => kind switch
    {
        PrivacyRegistryValueKind.String => RegistryValueKind.String,
        PrivacyRegistryValueKind.ExpandString => RegistryValueKind.ExpandString,
        PrivacyRegistryValueKind.Binary => RegistryValueKind.Binary,
        PrivacyRegistryValueKind.DWord => RegistryValueKind.DWord,
        PrivacyRegistryValueKind.MultiString => RegistryValueKind.MultiString,
        PrivacyRegistryValueKind.QWord => RegistryValueKind.QWord,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Registry value kind.")
    };

    private static bool IsRegistryOperationException(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        global::System.Security.SecurityException or
        ArgumentException or
        FormatException or
        JsonException or
        OverflowException;

    private sealed record RegistryValueSnapshot(bool Exists, PrivacyRegistryValueKind Kind, string? CanonicalValue)
    {
        public static RegistryValueSnapshot Missing() => new(false, PrivacyRegistryValueKind.None, null);
    }
}
