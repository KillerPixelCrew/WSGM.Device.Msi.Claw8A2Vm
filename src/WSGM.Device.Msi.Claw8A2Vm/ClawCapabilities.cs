using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Msi.Claw8A2Vm;

internal sealed class ClawA2VmPowerCapability(IMsiWmiTransport transport)
{
    private readonly IMsiWmiTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<PowerPair> ReadAsync(CancellationToken cancellationToken)
    {
        byte[] sustained = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.PowerSustainedAddress,
            cancellationToken).ConfigureAwait(false);
        byte[] boost = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.PowerBoostAddress,
            cancellationToken).ConfigureAwait(false);
        byte[] scenario = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.ScenarioAddress,
            cancellationToken).ConfigureAwait(false);
        return new PowerPair(ReadInt32(sustained), ReadInt32(boost), scenario[1]);
    }

    public async ValueTask<CapabilityCommandResult> ApplySustainedAsync(
        CapabilityCommand command,
        int watts,
        CancellationToken cancellationToken)
    {
        PowerPair before = await ReadAsync(cancellationToken).ConfigureAwait(false);

        // PL1's ceiling is the same 37 W as PL2, raised from 30 W on the maintainer's instruction
        // for the A2VM. `_plan/claw-8-a2vm-plugin.md` recorded 8-30 W for EC 0x50 from the stock
        // read, which is the value the firmware ships with rather than the range it accepts.
        // ApplyPairCoreAsync reads the pair back and only reports success when the hardware took
        // the value, so a ceiling the EC actually refuses surfaces as a failed command rather than
        // a silent lie.
        if (watts is < 8 or > 37 || before.BoostWatts < watts || before.BoostWatts > 37)
        {
            return Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "PL1 must be 8-37 W and cannot exceed the current PL2 value.");
        }

        return await ApplyPairCoreAsync(command, before, watts, before.BoostWatts, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<CapabilityCommandResult> ApplyBoostAsync(
        CapabilityCommand command,
        int watts,
        CancellationToken cancellationToken)
    {
        PowerPair before = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (watts is < 8 or > 37 || watts < before.SustainedWatts)
        {
            return Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "PL2 must be 8-37 W and cannot be below the current PL1 value.");
        }

        return await ApplyPairCoreAsync(command, before, before.SustainedWatts, watts, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<bool> RestoreAsync(PowerPair snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        PowerPair current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        await WritePairOrderedAsync(current, snapshot.SustainedWatts, snapshot.BoostWatts, cancellationToken)
            .ConfigureAwait(false);
        if (current.Scenario != snapshot.Scenario)
        {
            await WriteDataAsync(ClawHardwareFacts.ScenarioAddress, snapshot.Scenario, cancellationToken)
                .ConfigureAwait(false);
        }

        PowerPair readback = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return readback == snapshot;
    }

    private async ValueTask<CapabilityCommandResult> ApplyPairCoreAsync(
        CapabilityCommand command,
        PowerPair before,
        int sustainedWatts,
        int boostWatts,
        CancellationToken cancellationToken)
    {
        try
        {
            await WritePairOrderedAsync(before, sustainedWatts, boostWatts, cancellationToken)
                .ConfigureAwait(false);
            PowerPair readback = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (readback.SustainedWatts == sustainedWatts && readback.BoostWatts == boostWatts)
            {
                int value = command.CapabilityId == CapabilityIds.PowerSustained
                    ? readback.SustainedWatts
                    : readback.BoostWatts;
                return Verified(command, Integer(value));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // At least one write may have reached firmware. Fall through to the exact captured-pair
            // rollback and report the independently verified restoration result.
        }

        RollbackResult rollback = await TryRestorePairAsync(before, CancellationToken.None).ConfigureAwait(false);
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "Power readback did not match the requested pair."),
            Rollback = rollback,
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    private async ValueTask WritePairOrderedAsync(
        PowerPair current,
        int sustainedWatts,
        int boostWatts,
        CancellationToken cancellationToken)
    {
        if (sustainedWatts > current.BoostWatts)
        {
            await WriteDataAsync(ClawHardwareFacts.PowerBoostAddress, boostWatts, cancellationToken)
                .ConfigureAwait(false);
            await WriteDataAsync(ClawHardwareFacts.PowerSustainedAddress, sustainedWatts, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await WriteDataAsync(ClawHardwareFacts.PowerSustainedAddress, sustainedWatts, cancellationToken)
                .ConfigureAwait(false);
            if (boostWatts != current.BoostWatts)
            {
                await WriteDataAsync(ClawHardwareFacts.PowerBoostAddress, boostWatts, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<RollbackResult> TryRestorePairAsync(
        PowerPair snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            PowerPair current = await ReadAsync(cancellationToken).ConfigureAwait(false);
            await WritePairOrderedAsync(current, snapshot.SustainedWatts, snapshot.BoostWatts, cancellationToken)
                .ConfigureAwait(false);
            PowerPair readback = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return readback.SustainedWatts == snapshot.SustainedWatts
                && readback.BoostWatts == snapshot.BoostWatts
                ? RollbackResult.RestoredVerified
                : RollbackResult.RestoredUnverified;
        }
        catch
        {
            return RollbackResult.RestoreFailed;
        }
    }

    private ValueTask WriteDataAsync(byte address, int value, CancellationToken cancellationToken)
    {
        byte[] package = new byte[ClawHardwareFacts.WmiPackageLength];
        package[0] = address;
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(1, sizeof(int)), value);
        return _transport.InvokeSetterAsync("Set_Data", package, cancellationToken);
    }

    private static int ReadInt32(byte[] response) =>
        BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(1, sizeof(int)));

    private static CapabilityCommandResult Verified(CapabilityCommand command, CapabilityValue value) => new()
    {
        CommandId = command.CommandId,
        Outcome = CommandOutcome.AppliedVerified,
        ReadbackValue = value,
        CompletedAt = DateTimeOffset.UtcNow,
    };

    private static CapabilityCommandResult Rejected(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail) => new()
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = new CapabilityReason(code, detail),
            CompletedAt = DateTimeOffset.UtcNow,
        };

    private static CapabilityValue Integer(int value) => new()
    {
        Kind = CapabilityValueKind.Integer,
        IntegerValue = value,
    };
}

internal sealed class ClawA2VmFanCapability(IMsiWmiTransport transport)
{
    private static readonly int[] TemperatureOffsets = [1, 4, 5, 6, 7, 8];
    private static readonly int[] DutyOffsets = [2, 3, 4, 5, 6, 7];
    private readonly IMsiWmiTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<FanSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        FanTable left = await ReadTableAsync(1, cancellationToken).ConfigureAwait(false);
        FanTable right = await ReadTableAsync(2, cancellationToken).ConfigureAwait(false);
        byte[] custom = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.FanCustomAddress,
            cancellationToken).ConfigureAwait(false);
        byte[] full = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.FanFullSpeedAddress,
            cancellationToken).ConfigureAwait(false);
        return new FanSnapshot(left, right, custom[1], full[1]);
    }

    public async ValueTask<FanTelemetry> ReadTelemetryAsync(CancellationToken cancellationToken)
    {
        byte[] fan = await _transport.InvokeGetterAsync("Get_Fan", 0, cancellationToken)
            .ConfigureAwait(false);
        byte[] temperature = await _transport.InvokeGetterAsync("Get_Temperature", 0, cancellationToken)
            .ConfigureAwait(false);
        return new FanTelemetry(
            DecodeRpm(fan[1], fan[2]),
            DecodeRpm(fan[3], fan[4]),
            temperature[1]);
    }

    public async ValueTask<CapabilityCommandResult> ApplyModeAsync(
        CapabilityCommand command,
        string mode,
        CancellationToken cancellationToken)
    {
        FanSnapshot before = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        (bool custom, bool full) = mode switch
        {
            "automatic" => (false, false),
            "custom" => (true, false),
            "full-speed" => (false, true),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        try
        {
            await WriteFlagAsync(
                ClawHardwareFacts.FanCustomAddress,
                before.CustomFlag,
                custom,
                cancellationToken).ConfigureAwait(false);
            await WriteFlagAsync(
                ClawHardwareFacts.FanFullSpeedAddress,
                before.FullSpeedFlag,
                full,
                cancellationToken).ConfigureAwait(false);
            FanSnapshot readback = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (Flag(readback.CustomFlag) == custom && Flag(readback.FullSpeedFlag) == full)
            {
                return Verified(command, Choice(mode));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The exact snapshot below is the recovery authority after any partial flag write.
        }

        RollbackResult rollback = await TryRestoreAsync(before, CancellationToken.None).ConfigureAwait(false);
        return Indeterminate(command, "Fan-mode readback did not match.", rollback);
    }

    public async ValueTask<CapabilityCommandResult> ApplyCurveAsync(
        CapabilityCommand command,
        int channel,
        IReadOnlyList<CurvePoint> curve,
        CancellationToken cancellationToken)
    {
        if (!TryValidateCurve(curve, out string? validationError))
        {
            return Rejected(command, validationError!);
        }

        FanSnapshot before = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        FanTable current = channel == 1 ? before.Left : before.Right;
        byte[] temperatures = [.. current.TemperatureBuffer];
        byte[] duties = [.. current.DutyBuffer];
        for (int i = 0; i < curve.Count; i++)
        {
            temperatures[TemperatureOffsets[i]] = checked((byte)curve[i].Input);
            duties[DutyOffsets[i]] = checked((byte)curve[i].Output);
        }

        try
        {
            await WriteTableAsync(channel, temperatures, duties, cancellationToken).ConfigureAwait(false);
            FanTable readback = await ReadTableAsync(checked((byte)channel), cancellationToken)
                .ConfigureAwait(false);
            if (CurveEquals(readback, curve))
            {
                return Verified(command, Curve(curve));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The exact two-channel snapshot below restores every touched byte and flag.
        }

        RollbackResult rollback = await TryRestoreAsync(before, CancellationToken.None).ConfigureAwait(false);
        return Indeterminate(command, "Fan-table readback did not match.", rollback);
    }

    public async ValueTask<bool> RestoreAsync(FanSnapshot snapshot, CancellationToken cancellationToken)
    {
        return await TryRestoreAsync(snapshot, cancellationToken).ConfigureAwait(false)
            is RollbackResult.RestoredVerified;
    }

    internal static bool TryValidateCurve(IReadOnlyList<CurvePoint> curve, out string? error)
    {
        if (curve.Count != 6)
        {
            error = "The A2VM firmware requires exactly six fan-curve points.";
            return false;
        }

        for (int i = 0; i < curve.Count; i++)
        {
            CurvePoint point = curve[i];
            if (point.Input is < 0 or > 100 || point.Output is < 0 or > 100)
            {
                error = "Fan temperatures and duties must be in the validated 0-100 range.";
                return false;
            }

            if (i > 0 && (point.Input < curve[i - 1].Input || point.Output < curve[i - 1].Output))
            {
                error = "Fan-curve temperatures and duties must be monotonic.";
                return false;
            }
        }

        error = null;
        return true;
    }

    internal static IReadOnlyList<CurvePoint> DecodeCurve(FanTable table) =>
        Enumerable.Range(0, TemperatureOffsets.Length)
            .Select(index => new CurvePoint(
                table.TemperatureBuffer[TemperatureOffsets[index]],
                table.DutyBuffer[DutyOffsets[index]]))
            .ToArray();

    private async ValueTask<FanTable> ReadTableAsync(byte channel, CancellationToken cancellationToken)
    {
        byte[] duties = await _transport.InvokeGetterAsync("Get_Fan", channel, cancellationToken)
            .ConfigureAwait(false);
        byte[] temperatures = await _transport.InvokeGetterAsync("Get_Temperature", channel, cancellationToken)
            .ConfigureAwait(false);
        return new FanTable(duties, temperatures);
    }

    private async ValueTask WriteTableAsync(
        int channel,
        byte[] temperatures,
        byte[] duties,
        CancellationToken cancellationToken)
    {
        byte[] temperaturePackage = [.. temperatures];
        byte[] dutyPackage = [.. duties];
        temperaturePackage[0] = checked((byte)channel);
        dutyPackage[0] = checked((byte)channel);
        await _transport.InvokeSetterAsync("Set_Temperature", temperaturePackage, cancellationToken)
            .ConfigureAwait(false);
        await _transport.InvokeSetterAsync("Set_Fan", dutyPackage, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<RollbackResult> TryRestoreAsync(
        FanSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteTableAsync(1, snapshot.Left.TemperatureBuffer, snapshot.Left.DutyBuffer, cancellationToken)
                .ConfigureAwait(false);
            await WriteTableAsync(2, snapshot.Right.TemperatureBuffer, snapshot.Right.DutyBuffer, cancellationToken)
                .ConfigureAwait(false);
            await WriteRawFlagAsync(
                ClawHardwareFacts.FanCustomAddress,
                snapshot.CustomFlag,
                cancellationToken).ConfigureAwait(false);
            await WriteRawFlagAsync(
                ClawHardwareFacts.FanFullSpeedAddress,
                snapshot.FullSpeedFlag,
                cancellationToken).ConfigureAwait(false);
            FanSnapshot readback = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return SnapshotEquals(snapshot, readback)
                ? RollbackResult.RestoredVerified
                : RollbackResult.RestoredUnverified;
        }
        catch
        {
            return RollbackResult.RestoreFailed;
        }
    }

    private ValueTask WriteFlagAsync(
        byte address,
        byte current,
        bool enabled,
        CancellationToken cancellationToken) =>
        WriteRawFlagAsync(address, enabled ? (byte)(current | 0x80) : (byte)(current & 0x7F), cancellationToken);

    private ValueTask WriteRawFlagAsync(byte address, byte value, CancellationToken cancellationToken)
    {
        byte[] package = new byte[ClawHardwareFacts.WmiPackageLength];
        package[0] = address;
        package[1] = value;
        return _transport.InvokeSetterAsync("Set_Data", package, cancellationToken);
    }

    private static bool CurveEquals(FanTable readback, IReadOnlyList<CurvePoint> curve) =>
        DecodeCurve(readback).SequenceEqual(curve);

    private static bool SnapshotEquals(FanSnapshot left, FanSnapshot right) =>
        left.Left.DutyBuffer.SequenceEqual(right.Left.DutyBuffer)
        && left.Left.TemperatureBuffer.SequenceEqual(right.Left.TemperatureBuffer)
        && left.Right.DutyBuffer.SequenceEqual(right.Right.DutyBuffer)
        && left.Right.TemperatureBuffer.SequenceEqual(right.Right.TemperatureBuffer)
        && left.CustomFlag == right.CustomFlag
        && left.FullSpeedFlag == right.FullSpeedFlag;

    private static int DecodeRpm(byte high, byte low)
    {
        int divisor = (high << 8) | low;
        return divisor == 0 ? 0 : 480_000 / divisor;
    }

    private static bool Flag(byte value) => (value & 0x80) != 0;

    private static CapabilityCommandResult Verified(CapabilityCommand command, CapabilityValue value) => new()
    {
        CommandId = command.CommandId,
        Outcome = CommandOutcome.AppliedVerified,
        ReadbackValue = value,
        CompletedAt = DateTimeOffset.UtcNow,
    };

    private static CapabilityCommandResult Rejected(CapabilityCommand command, string detail) => new()
    {
        CommandId = command.CommandId,
        Outcome = CommandOutcome.Rejected,
        Reason = new CapabilityReason(CapabilityReasonCode.ValueOutOfRange, detail),
        CompletedAt = DateTimeOffset.UtcNow,
    };

    private static CapabilityCommandResult Indeterminate(
        CapabilityCommand command,
        string detail,
        RollbackResult rollback) => new()
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail),
            Rollback = rollback,
            CompletedAt = DateTimeOffset.UtcNow,
        };

    private static CapabilityValue Choice(string value) => new()
    {
        Kind = CapabilityValueKind.Choice,
        ChoiceValue = value,
    };

    private static CapabilityValue Curve(IReadOnlyList<CurvePoint> value) => new()
    {
        Kind = CapabilityValueKind.Curve,
        CurveValue = [.. value],
    };
}

internal sealed class ClawA2VmLightingCapability(IClawMcuTransport transport)
{
    private static readonly TimeSpan MinimumPersistentWriteInterval = TimeSpan.FromSeconds(1);
    private readonly IClawMcuTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private LightingState? _observed;
    private DateTimeOffset _lastPersistentWrite;

    public async ValueTask<LightingState> ReadAsync(CancellationToken cancellationToken)
    {
        byte[] profile = await _transport.ReadProfileAsync(
            ClawHardwareFacts.LightingProfileAddress,
            32,
            cancellationToken).ConfigureAwait(false);
        if (profile.Length != 32 || profile[1] is not 1 || profile[2] is not 0x09)
        {
            throw new InvalidOperationException("The committed A2VM lighting profile has an unrecognized shape.");
        }

        _observed = new LightingState(
            profile[4],
            ReadColor(profile, 5),
            ReadColor(profile, 17),
            ReadColor(profile, 29));
        return _observed;
    }

    public async ValueTask<CapabilityCommandResult> ApplyAsync(
        CapabilityCommand command,
        Func<LightingState, LightingState> update,
        CancellationToken cancellationToken)
    {
        LightingState before = await ReadAsync(cancellationToken).ConfigureAwait(false);
        LightingState wanted = update(before);
        if (wanted == before)
        {
            CapabilityValue currentValue = command.CapabilityId == CapabilityIds.LightingBrightness
                ? Integer(before.Brightness)
                : Color(command.InstanceId switch
                {
                    CapabilityInstances.RightRing => before.RightRingColor,
                    CapabilityInstances.LeftRing => before.LeftRingColor,
                    CapabilityInstances.Buttons => before.ButtonsColor,
                    _ => throw new InvalidOperationException("Unknown lighting zone."),
                });
            return new CapabilityCommandResult
            {
                CommandId = command.CommandId,
                Outcome = CommandOutcome.AppliedVerified,
                ReadbackValue = currentValue,
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }

        if (wanted.Brightness is < 0 or > 100
            || !IsColor(wanted.RightRingColor)
            || !IsColor(wanted.LeftRingColor)
            || !IsColor(wanted.ButtonsColor))
        {
            return new CapabilityCommandResult
            {
                CommandId = command.CommandId,
                Outcome = CommandOutcome.Rejected,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.ValueOutOfRange,
                    "Lighting brightness or colour is outside the validated range."),
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }

        if (DateTimeOffset.UtcNow - _lastPersistentWrite < MinimumPersistentWriteInterval)
        {
            return new CapabilityCommandResult
            {
                CommandId = command.CommandId,
                Outcome = CommandOutcome.Rejected,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.ResourceConflict,
                    "Persistent lighting commits are limited to one per second.",
                    Retryable: true),
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }

        if (command.Deadline - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(2))
        {
            return new CapabilityCommandResult
            {
                CommandId = command.CommandId,
                Outcome = CommandOutcome.Rejected,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.Quiescing,
                    "Insufficient command budget for a persistent lighting write."),
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }

        byte[] payload = Encode(wanted);
        _lastPersistentWrite = DateTimeOffset.UtcNow;
        await _transport.WriteProfileAsync(
            ClawHardwareFacts.LightingProfileAddress,
            payload,
            cancellationToken).ConfigureAwait(false);
        LightingState readback = await ReadAsync(cancellationToken).ConfigureAwait(false);
        CapabilityValue value = command.CapabilityId == CapabilityIds.LightingBrightness
            ? Integer(readback.Brightness)
            : Color(command.InstanceId switch
            {
                CapabilityInstances.RightRing => readback.RightRingColor,
                CapabilityInstances.LeftRing => readback.LeftRingColor,
                CapabilityInstances.Buttons => readback.ButtonsColor,
                _ => throw new InvalidOperationException("Unknown lighting zone."),
            });
        return readback == wanted
            ? new CapabilityCommandResult
            {
                CommandId = command.CommandId,
                Outcome = CommandOutcome.AppliedVerified,
                ReadbackValue = value,
                CompletedAt = DateTimeOffset.UtcNow,
            }
            : new CapabilityCommandResult
            {
                CommandId = command.CommandId,
                Outcome = CommandOutcome.Indeterminate,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "Persistent lighting readback did not match the committed profile."),
                Rollback = RollbackResult.NotRequired,
                CompletedAt = DateTimeOffset.UtcNow,
            };
    }

    internal static byte[] Encode(LightingState state)
    {
        byte[] payload = new byte[32];
        payload[0] = 0;
        payload[1] = 1;
        payload[2] = 0x09;
        payload[3] = 0x03;
        payload[4] = checked((byte)state.Brightness);
        for (int zone = 0; zone < 4; zone++)
        {
            WriteColor(payload, 5 + (zone * 3), state.RightRingColor);
            WriteColor(payload, 17 + (zone * 3), state.LeftRingColor);
        }

        WriteColor(payload, 29, state.ButtonsColor);
        return payload;
    }

    private static int ReadColor(byte[] payload, int offset) =>
        (payload[offset] << 16) | (payload[offset + 1] << 8) | payload[offset + 2];

    private static void WriteColor(byte[] payload, int offset, int color)
    {
        payload[offset] = checked((byte)((color >> 16) & 0xFF));
        payload[offset + 1] = checked((byte)((color >> 8) & 0xFF));
        payload[offset + 2] = checked((byte)(color & 0xFF));
    }

    private static bool IsColor(int color) => color is >= 0 and <= 0xFFFFFF;

    private static CapabilityValue Integer(int value) => new()
    {
        Kind = CapabilityValueKind.Integer,
        IntegerValue = value,
    };

    private static CapabilityValue Color(int value) => new()
    {
        Kind = CapabilityValueKind.Color,
        ColorValue = value,
    };
}

internal static class CapabilityIds
{
    public const string PowerSustained = "power.primary-limit";
    public const string PowerBoost = "power.boost-limit";
    public const string Scenario = "power.scenario";
    public const string FanMode = "fan.mode";
    public const string FanCurve = "fan.curve";
    public const string FanRpm = "fan.measured-rpm";
    public const string Temperature = "telemetry.temperature";
    public const string LightingBrightness = "lighting.brightness";
    public const string LightingColor = "lighting.zone-color";
    public const string Controller = "controller.source";
    public const string Motion = "motion.source";
    public const string Rumble = "haptic.rumble";
}

internal static class CapabilityInstances
{
    public const string Left = "left";
    public const string Right = "right";
    public const string RightRing = "right-ring";
    public const string LeftRing = "left-ring";
    public const string Buttons = "buttons";
}
