using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>Keeps only the original temporary state WSGM must restore after a crash.</summary>
internal sealed class ClawRecoveryJournal : IAsyncDisposable
{
    private const int CurrentVersion = 1;
    private const int MaxBytes = 16 * 1024;
    private const string FileName = "temporary-state.v1.json";
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string? _path;
    private List<ClawRecoveryEntry> _entries = [];

    private ClawRecoveryJournal(string? path, CapabilityReason? failureReason)
    {
        _path = path;
        FailureReason = failureReason;
    }

    public CapabilityReason? FailureReason { get; private set; }

    public IReadOnlyList<ClawRecoveryEntry> OutstandingEntries => [.. _entries];

    public static async ValueTask<ClawRecoveryJournal> OpenAsync(
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            return Failed("DeviceHost did not provide a writable plugin state directory.");
        }

        string path;
        try
        {
            string root = Path.GetFullPath(stateDirectory);
            Directory.CreateDirectory(root);
            path = Path.Combine(root, FileName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed($"The plugin recovery directory is unavailable ({ex.GetType().Name}).");
        }

        var journal = new ClawRecoveryJournal(path, failureReason: null);
        await journal.LoadAsync(cancellationToken).ConfigureAwait(false);
        _ = await journal.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        return journal;
    }

    public async ValueTask<ClawRecoveryOperation> BeginAsync(
        string serviceId,
        string capabilityId,
        string firmwareIdentity,
        ClawRecoveryState originalState,
        CancellationToken cancellationToken)
    {
        ValidateEntry(new ClawRecoveryEntry
        {
            ServiceId = serviceId,
            CapabilityId = capabilityId,
            FirmwareIdentity = firmwareIdentity,
            OriginalState = originalState,
            Status = ClawRecoveryStatus.Pending,
        });
        ThrowIfUnavailable();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClawRecoveryEntry? existing = _entries.SingleOrDefault(entry =>
                string.Equals(entry.ServiceId, serviceId, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (existing.Status is ClawRecoveryStatus.RestoredUnverified
                    or ClawRecoveryStatus.RestoreFailed)
                {
                    throw new InvalidOperationException(
                        $"Recovery for service '{serviceId}' is unresolved.");
                }

                return new ClawRecoveryOperation(existing, Opened: false);
            }

            var entry = new ClawRecoveryEntry
            {
                ServiceId = serviceId,
                CapabilityId = capabilityId,
                FirmwareIdentity = firmwareIdentity,
                OriginalState = originalState,
                Status = ClawRecoveryStatus.Pending,
            };
            List<ClawRecoveryEntry> entries = [.. _entries, entry];
            await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
            return new ClawRecoveryOperation(entry, Opened: true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public bool HasUnrestoredMutation(string serviceId) =>
        _entries.Any(entry => string.Equals(entry.ServiceId, serviceId, StringComparison.Ordinal));

    public async ValueTask<CapabilityReason?> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (FailureReason is not null)
        {
            return FailureReason;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveAsync([.. _entries], cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailureReason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                $"The plugin recovery record is not writable ({ex.GetType().Name}).");
        }
        finally
        {
            _writeGate.Release();
        }

        return FailureReason;
    }

    public ValueTask<ClawRecoveryOperation> CompleteCommandAsync(
        ClawRecoveryOperation operation,
        CapabilityCommandResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Rollback is RollbackResult.RestoreFailed)
        {
            return ReplaceAsync(operation, ClawRecoveryStatus.RestoreFailed, cancellationToken);
        }

        if (result.Rollback is RollbackResult.RestoredUnverified)
        {
            return ReplaceAsync(operation, ClawRecoveryStatus.RestoredUnverified, cancellationToken);
        }

        if (operation.Opened
            && (result.Outcome is CommandOutcome.Rejected
                || result.Rollback is RollbackResult.RestoredVerified))
        {
            return RemoveAsync(operation, cancellationToken);
        }

        return ValueTask.FromResult(operation);
    }

    public async ValueTask CompleteServiceRestorationAsync(
        string serviceId,
        ClawRecoveryStatus status,
        CancellationToken cancellationToken)
    {
        ValidateRestorationStatus(status);
        ThrowIfUnavailable();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ClawRecoveryEntry> entries = [.. _entries];
            int index = entries.FindIndex(entry => entry.ServiceId == serviceId);
            if (index < 0)
            {
                return;
            }

            if (status is ClawRecoveryStatus.RestoredVerified)
            {
                entries.RemoveAt(index);
            }
            else
            {
                entries[index] = entries[index] with { Status = status };
            }

            await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<ClawRecoveryEntry> CompleteExistingAsync(
        ClawRecoveryEntry entry,
        ClawRecoveryStatus status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await CompleteServiceRestorationAsync(entry.ServiceId, status, cancellationToken)
            .ConfigureAwait(false);
        return entry with { Status = status };
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static ClawReconciliationAction Decide(
        ClawRecoveryEntry entry,
        string? currentFirmwareIdentity)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Status is ClawRecoveryStatus.RestoreFailed)
        {
            return ClawReconciliationAction.Block;
        }

        return string.Equals(entry.FirmwareIdentity, currentFirmwareIdentity, StringComparison.Ordinal)
            ? ClawReconciliationAction.Restore
            : ClawReconciliationAction.ReportOnly;
    }

    private static ClawRecoveryJournal Failed(string detail) => new(
        path: null,
        new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));

    private async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            await using FileStream stream = new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaxBytes)
            {
                throw new InvalidDataException("The Claw recovery record exceeds 16 KiB.");
            }

            ClawRecoveryDocument document = await JsonSerializer.DeserializeAsync(
                stream,
                ClawRecoveryJsonContext.Default.ClawRecoveryDocument,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The Claw recovery record was empty.");
            ValidateDocument(document);
            _entries = [.. document.Entries];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailureReason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                $"The plugin recovery record is unavailable or invalid ({ex.GetType().Name}).");
            _entries = [];
        }
    }

    private async ValueTask<ClawRecoveryOperation> ReplaceAsync(
        ClawRecoveryOperation operation,
        ClawRecoveryStatus status,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ClawRecoveryEntry> entries = [.. _entries];
            int index = entries.FindIndex(entry => entry.ServiceId == operation.Entry.ServiceId);
            if (index < 0)
            {
                throw new InvalidDataException("The recovery operation is no longer current.");
            }

            ClawRecoveryEntry replacement = entries[index] with { Status = status };
            entries[index] = replacement;
            await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
            return operation with { Entry = replacement };
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask<ClawRecoveryOperation> RemoveAsync(
        ClawRecoveryOperation operation,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ClawRecoveryEntry> entries = [.. _entries];
            int removed = entries.RemoveAll(entry => entry.ServiceId == operation.Entry.ServiceId);
            if (removed != 1)
            {
                throw new InvalidDataException("The recovery operation is no longer current.");
            }

            await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
            return operation with
            {
                Entry = operation.Entry with { Status = ClawRecoveryStatus.RestoredVerified },
            };
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask SaveAsync(
        List<ClawRecoveryEntry> entries,
        CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            throw new InvalidOperationException("The plugin recovery path is unavailable.");
        }

        var document = new ClawRecoveryDocument
        {
            Version = CurrentVersion,
            Entries = entries.OrderBy(entry => entry.ServiceId, StringComparer.Ordinal).ToArray(),
        };
        ValidateDocument(document);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            ClawRecoveryJsonContext.Default.ClawRecoveryDocument);
        if (bytes.Length > MaxBytes)
        {
            throw new InvalidDataException("The Claw recovery record exceeds 16 KiB.");
        }

        string temporary = _path + ".tmp";
        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
            _entries = entries;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        if (FailureReason is not null || _path is null)
        {
            throw new InvalidOperationException(
                FailureReason?.Detail ?? "The plugin recovery record is unavailable.");
        }
    }

    private static void ValidateDocument(ClawRecoveryDocument document)
    {
        if (document.Version != CurrentVersion || document.Entries.Count > 3)
        {
            throw new InvalidDataException("The Claw recovery record header is invalid.");
        }

        var services = new HashSet<string>(StringComparer.Ordinal);
        foreach (ClawRecoveryEntry entry in document.Entries)
        {
            ValidateEntry(entry);
            if (!services.Add(entry.ServiceId))
            {
                throw new InvalidDataException("The Claw recovery record has duplicate services.");
            }
        }
    }

    private static void ValidateEntry(ClawRecoveryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.ServiceId)
            || string.IsNullOrWhiteSpace(entry.CapabilityId)
            || string.IsNullOrWhiteSpace(entry.FirmwareIdentity))
        {
            throw new InvalidDataException("A Claw recovery entry is incomplete.");
        }

        ValidateOriginalState(entry.ServiceId, entry.OriginalState);
        string expectedFirmware = entry.ServiceId switch
        {
            ServiceIds.Power or ServiceIds.Fans => ClawFirmwareIdentities.Wmi,
            ServiceIds.Controller => ClawFirmwareIdentities.Mcu,
            _ => throw new InvalidDataException("A recovery entry names a non-restorable service."),
        };
        if (!string.Equals(entry.FirmwareIdentity, expectedFirmware, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A recovery entry has an unexpected firmware identity.");
        }
    }

    private static void ValidateOriginalState(string serviceId, ClawRecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool valid = serviceId switch
        {
            ServiceIds.Power => state.Kind is ClawRecoveryStateKind.Power
                && state.SustainedWatts is >= byte.MinValue and <= byte.MaxValue
                && state.BoostWatts is >= byte.MinValue and <= byte.MaxValue
                && state.Scenario is not null,
            ServiceIds.Fans => state.Kind is ClawRecoveryStateKind.Fans
                && state.LeftDuty.Length == 32
                && state.LeftTemperature.Length == 32
                && state.RightDuty.Length == 32
                && state.RightTemperature.Length == 32
                && state.CustomFlag is not null
                && state.FullSpeedFlag is not null,
            ServiceIds.Controller => state.Kind is ClawRecoveryStateKind.ControllerMode
                && state.ControllerMode is ClawControllerMode.XInput or ClawControllerMode.DirectInput,
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException($"Recovery state does not match service '{serviceId}'.");
        }
    }

    private static void ValidateRestorationStatus(ClawRecoveryStatus status)
    {
        if (status is not (ClawRecoveryStatus.RestoredVerified
            or ClawRecoveryStatus.RestoredUnverified
            or ClawRecoveryStatus.RestoreFailed))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
    }
}

internal sealed record ClawRecoveryDocument
{
    public required int Version { get; init; }

    public IReadOnlyList<ClawRecoveryEntry> Entries { get; init; } = [];
}

internal sealed record ClawRecoveryEntry
{
    public required string ServiceId { get; init; }

    public required string CapabilityId { get; init; }

    public required string FirmwareIdentity { get; init; }

    public required ClawRecoveryState OriginalState { get; init; }

    public required ClawRecoveryStatus Status { get; init; }
}

internal sealed record ClawRecoveryOperation(
    ClawRecoveryEntry Entry,
    bool Opened);

[JsonConverter(typeof(JsonStringEnumConverter<ClawRecoveryStatus>))]
internal enum ClawRecoveryStatus
{
    Pending,
    RestoredVerified,
    RestoredUnverified,
    RestoreFailed,
}

internal enum ClawReconciliationAction
{
    Restore,
    ReportOnly,
    Block,
}

internal sealed record ClawRecoveryState
{
    public required ClawRecoveryStateKind Kind { get; init; }

    public int? SustainedWatts { get; init; }

    public int? BoostWatts { get; init; }

    public byte? Scenario { get; init; }

    public byte[] LeftDuty { get; init; } = [];

    public byte[] LeftTemperature { get; init; } = [];

    public byte[] RightDuty { get; init; } = [];

    public byte[] RightTemperature { get; init; } = [];

    public byte? CustomFlag { get; init; }

    public byte? FullSpeedFlag { get; init; }

    public ClawControllerMode? ControllerMode { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ClawRecoveryStateKind>))]
internal enum ClawRecoveryStateKind
{
    Power,
    Fans,
    ControllerMode,
}

internal static class ClawRecoveryValues
{
    public static ClawRecoveryState Power(PowerPair snapshot) => new()
    {
        Kind = ClawRecoveryStateKind.Power,
        SustainedWatts = snapshot.SustainedWatts,
        BoostWatts = snapshot.BoostWatts,
        Scenario = snapshot.Scenario,
    };

    public static bool TryPower(ClawRecoveryState? value, out PowerPair? snapshot)
    {
        snapshot = null;
        if (value?.Kind is not ClawRecoveryStateKind.Power
            || value.SustainedWatts is not { } sustained
            || value.BoostWatts is not { } boost
            || value.Scenario is not { } scenario)
        {
            return false;
        }

        snapshot = new PowerPair(sustained, boost, scenario);
        return true;
    }

    public static ClawRecoveryState Fans(FanSnapshot snapshot) => new()
    {
        Kind = ClawRecoveryStateKind.Fans,
        LeftDuty = [.. snapshot.Left.DutyBuffer],
        LeftTemperature = [.. snapshot.Left.TemperatureBuffer],
        RightDuty = [.. snapshot.Right.DutyBuffer],
        RightTemperature = [.. snapshot.Right.TemperatureBuffer],
        CustomFlag = snapshot.CustomFlag,
        FullSpeedFlag = snapshot.FullSpeedFlag,
    };

    public static bool TryFans(ClawRecoveryState? value, out FanSnapshot? snapshot)
    {
        snapshot = null;
        if (value?.Kind is not ClawRecoveryStateKind.Fans
            || value.LeftDuty.Length != 32
            || value.LeftTemperature.Length != 32
            || value.RightDuty.Length != 32
            || value.RightTemperature.Length != 32
            || value.CustomFlag is not { } customFlag
            || value.FullSpeedFlag is not { } fullSpeedFlag)
        {
            return false;
        }

        snapshot = new FanSnapshot(
            new FanTable([.. value.LeftDuty], [.. value.LeftTemperature]),
            new FanTable([.. value.RightDuty], [.. value.RightTemperature]),
            customFlag,
            fullSpeedFlag);
        return true;
    }

    public static ClawRecoveryState ControllerMode(ClawControllerMode mode) => new()
    {
        Kind = ClawRecoveryStateKind.ControllerMode,
        ControllerMode = mode,
    };

    public static bool TryControllerMode(ClawRecoveryState? value, out ClawControllerMode mode)
    {
        mode = value?.ControllerMode ?? ClawControllerMode.Offline;
        return value?.Kind is ClawRecoveryStateKind.ControllerMode
            && mode is ClawControllerMode.XInput or ClawControllerMode.DirectInput;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ClawRecoveryDocument))]
internal sealed partial class ClawRecoveryJsonContext : JsonSerializerContext;
