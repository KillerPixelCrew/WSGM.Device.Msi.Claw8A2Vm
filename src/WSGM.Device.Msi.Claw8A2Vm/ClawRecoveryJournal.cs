using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

internal sealed class ClawRecoveryJournal
{
    private static long _lastSequence;
    private readonly object _gate = new();
    private readonly IPluginHostAdapter _host;
    private readonly List<RecoveryJournalEntry> _currentEntries = [];

    public ClawRecoveryJournal(
        IPluginHostAdapter host,
        IReadOnlyList<RecoveryJournalEntry> outstandingEntries)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentNullException.ThrowIfNull(outstandingEntries);
        long previous = outstandingEntries.Count == 0
            ? 0
            : outstandingEntries.Max(entry => entry.Sequence);
        AdvanceSequenceFloor(previous);
    }

    public async ValueTask<RecoveryJournalEntry> BeginAsync(
        string resourceId,
        string? capabilityId,
        string firmwareIdentity,
        CapabilityValue originalValue,
        CapabilityValue? plannedValue,
        CancellationToken cancellationToken)
    {
        DateTimeOffset openedAt = DateTimeOffset.UtcNow;
        RecoveryJournalEntry entry = new()
        {
            Sequence = NextSequence(),
            PackageId = ClawHardwareFacts.PackageId,
            DeviceId = ClawHardwareFacts.DeviceDefinitionId,
            HostGeneration = _host.HostGeneration,
            DeviceGeneration = _host.DeviceGeneration,
            ResourceId = resourceId,
            CapabilityId = capabilityId,
            FirmwareIdentity = firmwareIdentity,
            OriginalValue = originalValue,
            PlannedValue = plannedValue,
            Status = JournalEntryStatus.Planned,
            OpenedAt = openedAt,
        };
        await _host.PersistRecoveryJournalEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _currentEntries.Add(entry);
        }

        return entry;
    }

    public bool HasUnrestoredMutation(string resourceId)
    {
        lock (_gate)
        {
            return _currentEntries.Any(entry => entry.ResourceId == resourceId
                && entry.Status is not (JournalEntryStatus.Planned
                    or JournalEntryStatus.RestoredVerified
                    or JournalEntryStatus.RestoreFailed));
        }
    }

    public ValueTask<RecoveryJournalEntry> MarkApplyingAsync(
        RecoveryJournalEntry entry,
        CancellationToken cancellationToken) =>
        ReplaceAsync(entry with { Status = JournalEntryStatus.Applying }, cancellationToken);

    public ValueTask<RecoveryJournalEntry> CompleteApplicationAsync(
        RecoveryJournalEntry entry,
        CapabilityCommandResult result,
        CancellationToken cancellationToken)
    {
        JournalEntryStatus status = result.Outcome switch
        {
            CommandOutcome.AppliedVerified => JournalEntryStatus.AppliedVerified,
            CommandOutcome.AppliedUnverified => JournalEntryStatus.AppliedUnverified,
            CommandOutcome.Rejected => JournalEntryStatus.RestoredVerified,
            _ => result.Rollback switch
            {
                RollbackResult.RestoredVerified => JournalEntryStatus.RestoredVerified,
                RollbackResult.RestoredUnverified => JournalEntryStatus.RestoredUnverified,
                RollbackResult.RestoreFailed => JournalEntryStatus.RestoreFailed,
                _ => JournalEntryStatus.AppliedUnverified,
            },
        };
        DateTimeOffset? closedAt = status is JournalEntryStatus.RestoredVerified
            ? DateTimeOffset.UtcNow
            : null;
        return ReplaceAsync(entry with
        {
            AppliedValue = result.ReadbackValue,
            Status = status,
            ClosedAt = closedAt,
        }, cancellationToken);
    }

    public ValueTask<RecoveryJournalEntry> MarkAppliedAsync(
        RecoveryJournalEntry entry,
        CapabilityValue appliedValue,
        bool verified,
        CancellationToken cancellationToken) =>
        ReplaceAsync(entry with
        {
            AppliedValue = appliedValue,
            Status = verified
                ? JournalEntryStatus.AppliedVerified
                : JournalEntryStatus.AppliedUnverified,
        }, cancellationToken);

    public async ValueTask CompleteResourceRestorationAsync(
        string resourceId,
        JournalEntryStatus status,
        CancellationToken cancellationToken)
    {
        if (status is not (JournalEntryStatus.RestoredVerified
            or JournalEntryStatus.RestoredUnverified
            or JournalEntryStatus.RestoreFailed))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        RecoveryJournalEntry[] entries;
        lock (_gate)
        {
            entries = _currentEntries
                .Where(entry => entry.ResourceId == resourceId
                    && entry.Status is not (JournalEntryStatus.Planned
                        or JournalEntryStatus.RestoredVerified
                        or JournalEntryStatus.RestoreFailed))
                .OrderByDescending(entry => entry.Sequence)
                .ToArray();
        }

        foreach (RecoveryJournalEntry entry in entries)
        {
            await ReplaceAsync(entry with
            {
                Status = status,
                ClosedAt = status is JournalEntryStatus.RestoredVerified
                    ? DateTimeOffset.UtcNow
                    : null,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask<RecoveryJournalEntry> CompleteExistingAsync(
        RecoveryJournalEntry entry,
        JournalEntryStatus status,
        CancellationToken cancellationToken)
    {
        if (status is not (JournalEntryStatus.RestoredVerified
            or JournalEntryStatus.RestoredUnverified
            or JournalEntryStatus.RestoreFailed))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return PersistReplacementAsync(entry with
        {
            Status = status,
            ClosedAt = status is JournalEntryStatus.RestoredVerified
                ? DateTimeOffset.UtcNow
                : null,
        }, cancellationToken);
    }

    private async ValueTask<RecoveryJournalEntry> ReplaceAsync(
        RecoveryJournalEntry replacement,
        CancellationToken cancellationToken)
    {
        await _host.PersistRecoveryJournalEntryAsync(replacement, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            int index = _currentEntries.FindIndex(entry => entry.Sequence == replacement.Sequence);
            if (index >= 0)
            {
                _currentEntries[index] = replacement;
            }
        }

        return replacement;
    }

    private async ValueTask<RecoveryJournalEntry> PersistReplacementAsync(
        RecoveryJournalEntry replacement,
        CancellationToken cancellationToken)
    {
        await _host.PersistRecoveryJournalEntryAsync(replacement, cancellationToken).ConfigureAwait(false);
        return replacement;
    }

    private static long NextSequence()
    {
        while (true)
        {
            long previous = Interlocked.Read(ref _lastSequence);
            long candidate = Math.Max(DateTimeOffset.UtcNow.UtcTicks, previous + 1);
            if (Interlocked.CompareExchange(ref _lastSequence, candidate, previous) == previous)
            {
                return candidate;
            }
        }
    }

    private static void AdvanceSequenceFloor(long floor)
    {
        while (true)
        {
            long previous = Interlocked.Read(ref _lastSequence);
            if (previous >= floor
                || Interlocked.CompareExchange(ref _lastSequence, floor, previous) == previous)
            {
                return;
            }
        }
    }
}

internal static class ClawRecoveryValues
{
    private const int PowerSustainedIndex = 0;
    private const int PowerBoostIndex = 1;
    private const int PowerScenarioIndex = 2;
    private const int FanLeftStart = 0;
    private const int FanRightStart = 32;
    private const int FanFlagsIndex = 64;

    public static CapabilityValue Power(PowerPair snapshot) => new()
    {
        Kind = CapabilityValueKind.Curve,
        CurveValue =
        [
            new CurvePoint(PowerSustainedIndex, snapshot.SustainedWatts),
            new CurvePoint(PowerBoostIndex, snapshot.BoostWatts),
            new CurvePoint(PowerScenarioIndex, snapshot.Scenario),
        ],
    };

    public static bool TryPower(CapabilityValue? value, out PowerPair? snapshot)
    {
        snapshot = null;
        if (value?.Kind is not CapabilityValueKind.Curve || value.CurveValue.Count != 3
            || value.CurveValue[0].Input != PowerSustainedIndex
            || value.CurveValue[1].Input != PowerBoostIndex
            || value.CurveValue[2].Input != PowerScenarioIndex
            || value.CurveValue[2].Output is < byte.MinValue or > byte.MaxValue)
        {
            return false;
        }

        snapshot = new PowerPair(
            value.CurveValue[0].Output,
            value.CurveValue[1].Output,
            checked((byte)value.CurveValue[2].Output));
        return true;
    }

    public static CapabilityValue Fans(FanSnapshot snapshot)
    {
        var points = new List<CurvePoint>(65);
        AddFanTable(points, FanLeftStart, snapshot.Left);
        AddFanTable(points, FanRightStart, snapshot.Right);
        points.Add(new CurvePoint(
            FanFlagsIndex,
            (snapshot.CustomFlag << 8) | snapshot.FullSpeedFlag));
        return new CapabilityValue
        {
            Kind = CapabilityValueKind.Curve,
            CurveValue = points,
        };
    }

    public static bool TryFans(CapabilityValue? value, out FanSnapshot? snapshot)
    {
        snapshot = null;
        if (value?.Kind is not CapabilityValueKind.Curve || value.CurveValue.Count != 65)
        {
            return false;
        }

        byte[] leftTemperatures = new byte[32];
        byte[] leftDuties = new byte[32];
        byte[] rightTemperatures = new byte[32];
        byte[] rightDuties = new byte[32];
        for (int i = 0; i < 64; i++)
        {
            CurvePoint point = value.CurveValue[i];
            if (point.Input != i || point.Output is < 0 or > ushort.MaxValue)
            {
                return false;
            }

            byte temperature = checked((byte)(point.Output >> 8));
            byte duty = checked((byte)(point.Output & 0xFF));
            if (i < FanRightStart)
            {
                leftTemperatures[i] = temperature;
                leftDuties[i] = duty;
            }
            else
            {
                int offset = i - FanRightStart;
                rightTemperatures[offset] = temperature;
                rightDuties[offset] = duty;
            }
        }

        CurvePoint flags = value.CurveValue[FanFlagsIndex];
        if (flags.Input != FanFlagsIndex || flags.Output is < 0 or > ushort.MaxValue)
        {
            return false;
        }

        snapshot = new FanSnapshot(
            new FanTable(leftDuties, leftTemperatures),
            new FanTable(rightDuties, rightTemperatures),
            checked((byte)(flags.Output >> 8)),
            checked((byte)(flags.Output & 0xFF)));
        return true;
    }

    public static CapabilityValue ControllerMode(ClawControllerMode mode) => new()
    {
        Kind = CapabilityValueKind.Choice,
        ChoiceValue = mode switch
        {
            ClawControllerMode.XInput => "xinput",
            ClawControllerMode.DirectInput => "directinput",
            _ => "offline",
        },
    };

    public static bool TryControllerMode(CapabilityValue? value, out ClawControllerMode mode)
    {
        mode = value?.ChoiceValue switch
        {
            "xinput" => ClawControllerMode.XInput,
            "directinput" => ClawControllerMode.DirectInput,
            _ => ClawControllerMode.Offline,
        };
        return value?.Kind is CapabilityValueKind.Choice
            && mode is ClawControllerMode.XInput or ClawControllerMode.DirectInput;
    }

    private static void AddFanTable(List<CurvePoint> points, int start, FanTable table)
    {
        if (table.DutyBuffer.Length != 32 || table.TemperatureBuffer.Length != 32)
        {
            throw new InvalidOperationException("A recovery fan table must retain both 32-byte buffers.");
        }

        for (int i = 0; i < 32; i++)
        {
            points.Add(new CurvePoint(
                start + i,
                (table.TemperatureBuffer[i] << 8) | table.DutyBuffer[i]));
        }
    }
}
