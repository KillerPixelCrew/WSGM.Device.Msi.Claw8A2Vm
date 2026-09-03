using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

internal static class ClawDiagnosticText
{
    public static string FromException(string context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string message = $"{context} ({exception.GetType().Name}): {exception.Message}";
        int length = Math.Min(message.Length, PluginTrace.MaxMessageLength);
        char[] bounded = new char[length];
        for (int index = 0; index < bounded.Length; index++)
        {
            char character = message[index];
            bounded[index] = PlainText.IsUnsafe(character) ? ' ' : character;
        }

        return new string(bounded);
    }
}

internal enum ClawServiceState
{
    Idle,
    Acquiring,
    Owned,
    Passive,
    Degraded,
    Releasing,
    ReleasedUnverified,
    Faulted,
}

internal readonly record struct ClawCycleContext(
    long CycleGeneration,
    DateTimeOffset Deadline);

internal sealed record ClawServiceResult(
    ClawServiceState State,
    CapabilityReason? Reason = null);

internal abstract class ClawServiceStatus(string serviceId)
{
    public string ServiceId { get; } = serviceId;

    public ClawServiceState State { get; protected set; } = ClawServiceState.Idle;

    public CapabilityReason? Reason { get; private set; }

    public CapabilityReason? ReconciliationBlockReason { get; set; }

    internal void ApplyResult(ClawServiceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        State = result.State;
        Reason = result.Reason;
    }

    public void Fault(CapabilityReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ReconciliationBlockReason = reason;
        _ = Set(ClawServiceState.Faulted, reason);
    }

    protected ClawServiceResult Set(
        ClawServiceState state,
        CapabilityReason? reason = null)
    {
        State = state;
        Reason = reason;
        return new ClawServiceResult(state, reason);
    }

}

internal sealed class OemEventService(
    IMsiOemEventSource source,
    IPluginHostAdapter host,
    ClawOemButtonLatch oemButtons) : ClawServiceStatus(ServiceIds.OemEvents)
{
    private readonly IMsiOemEventSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly IPluginHostAdapter _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly ClawOemButtonLatch _oemButtons =
        oemButtons ?? throw new ArgumentNullException(nameof(oemButtons));
    private long _cycleGeneration;

    public async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        _cycleGeneration = context.CycleGeneration;
        bool started = await _source.StartAsync(PublishAsync, cancellationToken).ConfigureAwait(false);
        return started
            ? Set(ClawServiceState.Owned)
            : Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The MSI_Event provider was unavailable."));
    }

    public async ValueTask<ClawServiceResult> SuspendAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Idle);
    }

    public ValueTask<ClawServiceResult> ResumeAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken) => AcquireAsync(context, cancellationToken);

    public async ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Idle);
    }

    private ValueTask PublishAsync(byte code, DateTimeOffset timestamp)
    {
        (string controlId, OemPressKind press)? mapped = code switch
        {
            0x29 => ("oem1", OemPressKind.Short),
            0x58 => ("oem2", OemPressKind.Short),
            0x2A => ("oem2", OemPressKind.Long),
            _ => null,
        };
        if (mapped is null)
        {
            return ValueTask.CompletedTask;
        }

        // The buttons the device is printed for: the left one is the virtual target's Steam button,
        // the right one its Quick Access button. Latched into the controller sample so Steam sees
        // its own controller press them, rather than WSGM acting on the user's behalf. A long press
        // on OEM2 is still only that button — the duration belongs to whatever reads it.
        CanonicalButtons button = mapped.Value.controlId switch
        {
            "oem1" => CanonicalButtons.Guide,
            "oem2" => CanonicalButtons.QuickAccess,
            _ => CanonicalButtons.None,
        };
        if (button != CanonicalButtons.None)
        {
            _oemButtons.Press(button, timestamp);
        }

        return _host.PublishOemEventAsync(
            new OemControlEvent(
                mapped.Value.controlId,
                mapped.Value.press,
                _cycleGeneration,
                timestamp,
                $"msi-event-{code:X2}-{timestamp.UtcTicks}"),
            CancellationToken.None);
    }
}

internal sealed class PowerService(
    IClawIdentityReader identity,
    ClawA2VmPowerCapability capability,
    ClawRecoveryJournal journal) : ClawServiceStatus(ServiceIds.Power)
{
    private readonly IClawIdentityReader _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly ClawA2VmPowerCapability _capability = capability ?? throw new ArgumentNullException(nameof(capability));
    private readonly ClawRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    public PowerPair? LastObserved { get; private set; }

    public async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(ClawServiceState.Faulted, ReconciliationBlockReason);
        }

        ClawIdentityState identity = await _identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch || !identity.WmiFirmwareVerified)
        {
            return Set(ClawServiceState.Passive, FirmwareReason(identity));
        }

        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Owned);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken) =>
        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(ClawServiceState.Faulted, ReconciliationBlockReason);
        }

        if (State is not ClawServiceState.Owned || !_journal.HasUnrestoredMutation(ServiceId))
        {
            return Set(ClawServiceState.Idle);
        }

        if (!ClawRecoveryValues.TryPower(_journal.OriginalStateFor(ServiceId), out PowerPair? restoreSnapshot)
            || restoreSnapshot is null)
        {
            return Set(ClawServiceState.Faulted, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The power recovery record did not contain its pre-mutation snapshot."));
        }

        ClawWriteBudget.Require(context.Deadline, "journalled power restoration");
        bool restored;
        try
        {
            restored = await _capability.RestoreAsync(restoreSnapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await _journal.CompleteServiceRestorationAsync(
                ServiceId,
                ClawRecoveryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await _journal.CompleteServiceRestorationAsync(
            ServiceId,
            restored ? ClawRecoveryStatus.RestoredVerified : ClawRecoveryStatus.RestoredUnverified,
            cancellationToken).ConfigureAwait(false);
        return restored
            ? Set(ClawServiceState.Idle)
            : Set(ClawServiceState.ReleasedUnverified, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The captured power pair or scenario could not be verified after restoration."));
    }

    private static CapabilityReason FirmwareReason(ClawIdentityState identity) => identity.ExactMachineMatch
        ? new CapabilityReason(
            CapabilityReasonCode.FirmwareNotVerified,
            "MSI_ACPI interface 8.0 and EC firmware 1T52EMS1.109 were not both verified.")
        : new CapabilityReason(
            CapabilityReasonCode.GenerationChanged,
            "The exact MS-1T52 identity no longer matches.");
}

internal sealed class ChargeLimitService(
    IClawIdentityReader identity,
    ClawA2VmChargeLimitCapability capability) : ClawServiceStatus(ServiceIds.ChargeLimit)
{
    private readonly IClawIdentityReader _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly ClawA2VmChargeLimitCapability _capability = capability
        ?? throw new ArgumentNullException(nameof(capability));

    public ChargeLimitState? LastObserved { get; private set; }

    public async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        ClawIdentityState identity = await _identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch || !identity.WmiFirmwareVerified)
        {
            return Set(ClawServiceState.Passive, FirmwareReason(identity));
        }

        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Owned);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken) =>
        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);

    public ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastObserved = null;
        return ValueTask.FromResult(Set(ClawServiceState.Idle));
    }

    private static CapabilityReason FirmwareReason(ClawIdentityState identity) => identity.ExactMachineMatch
        ? new CapabilityReason(
            CapabilityReasonCode.FirmwareNotVerified,
            "MSI_ACPI interface 8.0 and EC firmware 1T52EMS1.109 were not both verified.")
        : new CapabilityReason(
            CapabilityReasonCode.GenerationChanged,
            "The exact MS-1T52 identity no longer matches.");
}

internal sealed class FanService(
    IClawIdentityReader identity,
    ClawA2VmFanCapability capability,
    ClawRecoveryJournal journal) : ClawServiceStatus(ServiceIds.Fans)
{
    private readonly IClawIdentityReader _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly ClawA2VmFanCapability _capability = capability ?? throw new ArgumentNullException(nameof(capability));
    private readonly ClawRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    public FanSnapshot? LastObserved { get; private set; }

    public async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(ClawServiceState.Faulted, ReconciliationBlockReason);
        }

        ClawIdentityState identity = await _identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch || !identity.WmiFirmwareVerified)
        {
            return Set(ClawServiceState.Passive, new CapabilityReason(
                identity.ExactMachineMatch
                    ? CapabilityReasonCode.FirmwareNotVerified
                    : CapabilityReasonCode.GenerationChanged,
                "The exact board, EC firmware, and MSI_ACPI interface were not verified."));
        }

        LastObserved = await _capability.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Owned);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken) =>
        LastObserved = await _capability.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(ClawServiceState.Faulted, ReconciliationBlockReason);
        }

        if (State is not ClawServiceState.Owned || !_journal.HasUnrestoredMutation(ServiceId))
        {
            return Set(ClawServiceState.Idle);
        }

        if (!ClawRecoveryValues.TryFans(_journal.OriginalStateFor(ServiceId), out FanSnapshot? restoreSnapshot)
            || restoreSnapshot is null)
        {
            return Set(ClawServiceState.Faulted, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The fan recovery record did not contain its pre-mutation snapshot."));
        }

        ClawWriteBudget.Require(context.Deadline, "journalled fan restoration");
        bool restored;
        try
        {
            restored = await _capability.RestoreAsync(restoreSnapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await _journal.CompleteServiceRestorationAsync(
                ServiceId,
                ClawRecoveryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await _journal.CompleteServiceRestorationAsync(
            ServiceId,
            restored ? ClawRecoveryStatus.RestoredVerified : ClawRecoveryStatus.RestoredUnverified,
            cancellationToken).ConfigureAwait(false);
        return restored
            ? Set(ClawServiceState.Idle)
            : Set(ClawServiceState.ReleasedUnverified, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The captured left/right fan tables or flags could not be verified after restoration."));
    }
}

internal sealed class TelemetryService(
    IClawIdentityReader identity,
    ClawA2VmFanCapability capability) : ClawServiceStatus(ServiceIds.Telemetry)
{
    private readonly IClawIdentityReader _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly ClawA2VmFanCapability _capability = capability ?? throw new ArgumentNullException(nameof(capability));

    public FanTelemetry? LastTelemetry { get; private set; }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken) =>
        LastTelemetry = await _capability.ReadTelemetryAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        ClawIdentityState identity = await _identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch || !identity.WmiFirmwareVerified)
        {
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "MSI telemetry requires the exact reviewed MSI_ACPI provider."));
        }

        LastTelemetry = await _capability.ReadTelemetryAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Owned);
    }

    public ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastTelemetry = null;
        return ValueTask.FromResult(Set(ClawServiceState.Idle));
    }
}

internal sealed class LightingService(
    IClawIdentityReader identity,
    IClawMcuTransport transport,
    ClawA2VmLightingCapability capability) : ClawServiceStatus(ServiceIds.Lighting)
{
    private readonly IClawIdentityReader _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly IClawMcuTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly ClawA2VmLightingCapability _capability = capability ?? throw new ArgumentNullException(nameof(capability));

    public LightingState? LastObserved { get; private set; }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken) =>
        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        ClawIdentityState identity = await _identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch || !identity.McuFirmwareVerified)
        {
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "Lighting writes are gated to controller firmware 0x0229 and profile base 0x024A."));
        }

        if (!await _transport.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The reviewed MCU HID collection was unavailable."));
        }

        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Owned);
    }

    public ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // RGB profile writes are intentionally persistent user choices. Deactivation never rewrites
        // the captured profile and therefore cannot silently undo the requested setting.
        return ValueTask.FromResult(Set(ClawServiceState.Idle));
    }
}

internal sealed class MotionService(IClawMotionSource source) : ClawServiceStatus(ServiceIds.Motion)
{
    private readonly IClawMotionSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private MotionSample? _latest;
    private int _staleReported;

    /// <summary>How long a sensor reading may still be attached to a controller sample.</summary>
    /// <remarks>
    /// The gyrometer reports at tens of hertz while the controller reader runs at about 125 Hz, so
    /// the same reading legitimately rides several samples. What is not legitimate is replaying it
    /// after the sensor stopped: a last non-zero angular velocity attached to every following
    /// sample produces continuous gyro movement on a device that is lying still. A quarter of a
    /// second is several sensor periods and far short of anything a player would notice.
    /// </remarks>
    internal static readonly TimeSpan MaximumMotionAge = TimeSpan.FromMilliseconds(250);

    private readonly GyroFrameResampler _resampler = new();

    public MotionSample? Latest => Volatile.Read(ref _latest);

    /// <summary>The motion to attach to the controller sample being published now.</summary>
    /// <param name="now">Current time, from the caller's clock.</param>
    /// <returns>
    /// The last reading with its angular velocity replaced by the frame-average since the previous
    /// call, or null before the first reading arrives.
    /// </returns>
    /// <remarks>
    /// The sensor updates at 100 Hz under a ~125 Hz controller reader, so raw values ride frames
    /// unevenly in a repeating beat that Steam integrates as jagged angular steps. The resampled
    /// average preserves the exact integrated angle per frame and decays to zero when the sensor
    /// goes quiet, which is also what keeps a still device from ever reading as freefall.
    /// </remarks>
    public MotionSample? Current(DateTimeOffset now)
    {
        if (Volatile.Read(ref _latest) is not { } sample)
        {
            return null;
        }

        // A source that supplies no timestamp cannot be aged; it is passed through as before.
        if (sample.SensorTimestamp is not { } stamp)
        {
            return sample;
        }

        if (now - stamp > MaximumMotionAge)
        {
            // Once per quiet stretch, never per sample: this is called from the controller reader
            // at about 125 Hz. A quiet gyrometer is a still device (the ISH suppresses unchanged
            // readings); the resampled average below decays to zero on its own while the last
            // measured acceleration keeps Steam's fusion anchored, so nothing is dropped here —
            // publishing nothing turned every pause into freefall and made the crosshair jump when
            // the player stopped moving (device-observed 2026-09-02).
            if (Interlocked.Exchange(ref _staleReported, 1) == 0)
            {
                PluginTrace.Info(
                    "motion",
                    $"Gyroscope reading is {(now - stamp).TotalMilliseconds:F0} ms old; holding "
                    + "rest (decayed angular velocity, last measured acceleration) until the sensor reports again.");
            }
        }

        Vector3 average = _resampler.FrameAverage(now);
        return sample with
        {
            GyroX = average.X,
            GyroY = average.Y,
            GyroZ = average.Z,
        };
    }

    public async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        bool started = await _source.StartAsync(
            sample =>
            {
                Volatile.Write(ref _latest, sample);
                if (sample.SensorTimestamp is { } stamp)
                {
                    _resampler.OnReading(
                        new Vector3(sample.GyroX, sample.GyroY, sample.GyroZ),
                        stamp);
                }

                // A fresh reading ends the quiet stretch, and re-arms its one-shot report.
                if (Interlocked.Exchange(ref _staleReported, 0) != 0)
                {
                    PluginTrace.Info("motion", "Gyroscope readings resumed.");
                }

                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return started
            ? Set(ClawServiceState.Owned)
            : Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The Intel ISS gyrometer or legacy physical accelerometer was unavailable; no synthetic fallback exists."));
    }

    public async ValueTask<ClawServiceResult> SuspendAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _latest, null);
        return Set(ClawServiceState.Idle);
    }

    public ValueTask<ClawServiceResult> ResumeAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken) => AcquireAsync(context, cancellationToken);

    public async ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _latest, null);
        return Set(ClawServiceState.Idle);
    }
}

/// <summary>
/// Area-preserving resampler from the gyrometer's 100 Hz cadence onto the controller frames.
/// </summary>
/// <remarks>
/// Attaching raw readings to ~125 Hz frames makes some frames repeat a stale value and others jump
/// two sensor periods, in a repeating 40 ms beat that integrates as jagged angular steps. Each
/// frame instead reports the average angular velocity over exactly the interval since the previous
/// frame, computed from the zero-order-held sensor integral: the total rotation Steam integrates
/// stays exact, the beat disappears, and no latency is added. A reading older than
/// <see cref="MotionService.MaximumMotionAge"/> stops contributing, so the average decays to zero
/// on a quiet (still) sensor rather than replaying the last angular velocity forever.
/// </remarks>
internal sealed class GyroFrameResampler
{
    private readonly object _gate = new();
    private Vector3 _omega;
    private DateTimeOffset _quietCap;
    private DateTimeOffset? _accountedTo;
    private Vector3 _pendingDegrees;
    private DateTimeOffset? _lastFrame;
    private Vector3 _lastAverage;

    /// <summary>Feeds one sensor reading, accumulating the angle the previous one covered.</summary>
    /// <param name="omegaDegreesPerSecond">Angular velocity in the published basis.</param>
    /// <param name="stamp">The reading's sensor timestamp.</param>
    public void OnReading(Vector3 omegaDegreesPerSecond, DateTimeOffset stamp)
    {
        lock (_gate)
        {
            AdvanceUnderGate(stamp);
            _omega = omegaDegreesPerSecond;
            _quietCap = stamp + MotionService.MaximumMotionAge;
        }
    }

    /// <summary>Returns the average angular velocity since the previous frame.</summary>
    /// <param name="now">The controller frame's timestamp.</param>
    /// <returns>Degrees per second whose integral over the frame equals the sensor's.</returns>
    public Vector3 FrameAverage(DateTimeOffset now)
    {
        lock (_gate)
        {
            AdvanceUnderGate(now);
            if (_lastFrame is not { } previous || now <= previous)
            {
                // First frame, or a non-advancing frame clock: the held reading is the only
                // defensible answer, and pending angle stays banked for the next real frame.
                _lastFrame ??= now;
                return _lastAverage = _omega;
            }

            _lastAverage = _pendingDegrees / (float)(now - previous).TotalSeconds;
            _pendingDegrees = Vector3.Zero;
            _lastFrame = now;
            return _lastAverage;
        }
    }

    private void AdvanceUnderGate(DateTimeOffset to)
    {
        if (_accountedTo is not { } from)
        {
            _accountedTo = to;
            return;
        }

        if (to <= from)
        {
            return;
        }

        // The held velocity covers time only up to the quiet cap; beyond it the sensor's silence
        // means stillness and the integral stops growing.
        DateTimeOffset covered = to < _quietCap ? to : _quietCap;
        if (covered > from)
        {
            _pendingDegrees += _omega * (float)(covered - from).TotalSeconds;
        }

        _accountedTo = to;
    }
}

internal sealed class ControllerService(
    IClawIdentityReader identity,
    IClawMcuTransport mcu,
    IClawControllerSource source,
    MotionService motion,
    IPluginHostAdapter host,
    ClawRecoveryJournal journal) : ClawServiceStatus(ServiceIds.Controller)
{
    private readonly IClawIdentityReader _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly IClawMcuTransport _mcu = mcu ?? throw new ArgumentNullException(nameof(mcu));
    private readonly IClawControllerSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly MotionService _motion = motion ?? throw new ArgumentNullException(nameof(motion));
    private readonly IPluginHostAdapter _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly ClawRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private ControllerTopology? _original;
    private ControllerTopology? _current;
    private CanonicalButtons _rearButtons;
    private readonly object _hapticGate = new();
    private readonly SemaphoreSlim _outputSerializer = new(1, 1);
    private byte _lastWeak;
    private byte _lastStrong;
    private DateTimeOffset _lastHapticWrite;

    public bool Enabled { get; set; }

    public ControllerTopology? CurrentTopology => _current;

    public IReadOnlyList<PhysicalDeviceIdentity> LastReleasedDevices { get; private set; } = [];

    public async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.ResourceReleased,
                "Controller management is disabled."));
        }

        LastReleasedDevices = [];

        if (ReconciliationBlockReason is not null)
        {
            return Set(ClawServiceState.Faulted, ReconciliationBlockReason);
        }

        ClawIdentityState identity = await _identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch || !identity.McuFirmwareVerified)
        {
            _host.Trace(
                DeviceTraceLevel.Warn,
                "controller",
                "acquire refused at the identity gate: "
                    + $"exactMachine={identity.ExactMachineMatch}, "
                    + $"mcuVerified={identity.McuFirmwareVerified}.");
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "Controller ownership is gated to exact MS-1T52 firmware 0x0229."));
        }

        ControllerTopology? observed = await _source.DiscoverAsync(cancellationToken).ConfigureAwait(false);

        // Discovery is where the answer to "why didn't it switch to DirectInput?" lives, and it was
        // invisible: the mode, the product id and the endpoint list are all decided here and none
        // of them survived into any reason string. Traced unconditionally, before the gates below
        // get a chance to turn all of it into one sentence about a prerequisite.
        _host.Trace(
            observed is null ? DeviceTraceLevel.Warn : DeviceTraceLevel.Info,
            "controller",
            observed is null
                ? "discovery found no Claw controller topology."
                : $"discovered mode={observed.Mode}, product=0x{observed.ProductId:X4}, "
                    + $"location='{observed.PhysicalLocation}', "
                    + $"physicalDevices={observed.PhysicalDevices.Count}, "
                    + $"endpoints=[{observed.ObservedEndpoints}]");
        _original ??= observed;
        if (_original is null || observed is null
            || string.IsNullOrWhiteSpace(_original.PhysicalLocation)
            || !HidEndpointEnumerator.SamePhysicalLocation(
                observed.PhysicalLocation,
                _original.PhysicalLocation))
        {
            _host.Trace(
                DeviceTraceLevel.Warn,
                "controller",
                "acquire refused: composite USB location did not match the one first observed. "
                    + $"original='{_original?.PhysicalLocation}', "
                    + $"observed='{observed?.PhysicalLocation}'.");
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The physical controller or its composite USB location was unavailable."));
        }

        _current = observed;
        if (_current.Mode is not ClawControllerMode.DirectInput)
        {
            ClawWriteBudget.Require(context.Deadline, "controller mode acquisition");
            _ = await _journal.BeginAsync(
                ServiceId,
                CapabilityIds.Controller,
                ClawFirmwareIdentities.Mcu,
                ClawRecoveryValues.ControllerMode(_original.Mode),
                cancellationToken).ConfigureAwait(false);
            _host.Trace(
                DeviceTraceLevel.Info,
                "controller",
                $"switching MCU mode {_current.Mode} -> DirectInput at '{_original.PhysicalLocation}'.");
            try
            {
                _current = await _mcu.SwitchModeAsync(
                    ClawControllerMode.DirectInput,
                    _original.PhysicalLocation,
                    context.Deadline,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _host.Trace(
                    DeviceTraceLevel.Error,
                    "controller",
                    $"mode switch to DirectInput failed: {ex.GetType().Name}: {ex.Message}");
                await RestoreAfterFailedAcquireAsync(context.Deadline).ConfigureAwait(false);
                throw;
            }

            // The switch reports success by returning a topology, but the topology is what the
            // hardware actually settled into. Those differed on the reference unit, and nothing
            // said so.
            _host.Trace(
                _current.Mode is ClawControllerMode.DirectInput
                    ? DeviceTraceLevel.Info
                    : DeviceTraceLevel.Warn,
                "controller",
                $"mode switch settled at {_current.Mode}, product=0x{_current.ProductId:X4}, "
                    + $"physicalDevices={_current.PhysicalDevices.Count}, "
                    + $"endpoints=[{_current.ObservedEndpoints}]");
        }
        else
        {
            _host.Trace(
                DeviceTraceLevel.Info,
                "controller",
                "controller already in DirectInput; no mode switch needed.");
        }

        if (_current.PhysicalDevices.Count == 0)
        {
            // Captured before the restore, which clears _current: the whole point of this reason is
            // to say what was observed, and reading it afterwards is reading nothing.
            string detail = "No exact DirectInput physical interface identity was available for "
                + $"handoff. Mode={_current.Mode}, product={_current.ProductId}, "
                + $"endpoints=[{_current.ObservedEndpoints}]";
            _host.Trace(DeviceTraceLevel.Warn, "controller", detail);
            await RestoreAfterFailedAcquireAsync(context.Deadline).ConfigureAwait(false);
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                detail));
        }

        try
        {
            await _source.StartAsync(
                context.CycleGeneration,
                PublishControllerSampleAsync,
                ReportControllerReaderFault,
                cancellationToken).ConfigureAwait(false);

            await _host.PublishPhysicalDevicesAsync(
                _current.PhysicalDevices,
                OutputCapabilities,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host.Trace(
                DeviceTraceLevel.Error,
                "controller",
                $"controller acquisition failed before physical-device handoff completed: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            await RestoreAfterFailedAcquireAsync(context.Deadline).ConfigureAwait(false);
            throw;
        }
        _host.Trace(
            DeviceTraceLevel.Info,
            "controller",
            $"owned: published {_current.PhysicalDevices.Count} physical identities for hiding, "
                + $"haptics={OutputCapabilities is not null}.");
        return Set(ClawServiceState.Owned);
    }

    private void ReportControllerReaderFault(Exception exception)
    {
        string detail = ClawDiagnosticText.FromException(
            "The controller reader stopped",
            exception);
        CapabilityReason reason = new(
            CapabilityReasonCode.TransportFaulted,
            detail);
        Fault(reason);
        _host.ReportFault("controller", detail);
    }

    public async ValueTask<ClawServiceResult> SuspendAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        CapabilityReason? stopFailure = await StopOutputAndAcquisitionAsync(cancellationToken)
            .ConfigureAwait(false);
        return stopFailure is null
            ? Set(ClawServiceState.Idle)
            : Set(ClawServiceState.Faulted, stopFailure);
    }

    public ValueTask<ClawServiceResult> ResumeAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken) => AcquireAsync(context, cancellationToken);

    public async ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        ControllerHandoffResult result = await ReleaseControllerAsync(context.Deadline, cancellationToken)
            .ConfigureAwait(false);
        return result is ControllerHandoffResult.ReleasedVerified
            ? Set(ClawServiceState.Idle)
            : Set(ClawServiceState.ReleasedUnverified, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "Controller mode or physical USB topology restoration was not verified."));
    }

    public async ValueTask<ControllerHandoffResult> ReleaseControllerAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        _ = Set(ClawServiceState.Releasing);
        CapabilityReason? stopFailure = await StopOutputAndAcquisitionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (ReconciliationBlockReason is not null)
        {
            _ = Set(ClawServiceState.ReleasedUnverified, ReconciliationBlockReason);
            return ControllerHandoffResult.ReleasedUnverified;
        }

        if (_original is null || _current is null)
        {
            _current = null;
            LastReleasedDevices = [];
            if (_journal.HasUnrestoredMutation(ServiceId))
            {
                _ = Set(ClawServiceState.ReleasedUnverified, new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "Controller topology disappeared before its original mode could be verified."));
                await _journal.CompleteServiceRestorationAsync(
                    ServiceId,
                    ClawRecoveryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
                return ControllerHandoffResult.ReleasedUnverified;
            }

            if (stopFailure is not null)
            {
                _ = Set(ClawServiceState.ReleasedUnverified, stopFailure);
                return ControllerHandoffResult.ReleasedUnverified;
            }

            _ = Set(ClawServiceState.Idle);
            return ControllerHandoffResult.ReleasedVerified;
        }

        if (_current.Mode != _original.Mode)
        {
            ClawWriteBudget.Require(deadline, "controller mode restoration");
            ControllerTopology restored;
            try
            {
                restored = await _mcu.SwitchModeAsync(
                    _original.Mode,
                    _original.PhysicalLocation,
                    deadline,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                CapabilityReason reason = new(
                    CapabilityReasonCode.TransportFaulted,
                    "The original controller mode could not be restored.");
                Fault(reason);
                await _journal.CompleteServiceRestorationAsync(
                    ServiceId,
                    ClawRecoveryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            if (!string.Equals(
                    restored.PhysicalLocation,
                    _original.PhysicalLocation,
                    StringComparison.OrdinalIgnoreCase)
                || restored.Mode != _original.Mode)
            {
                _current = restored;
                LastReleasedDevices = restored.PhysicalDevices;
                _ = Set(ClawServiceState.ReleasedUnverified, new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "The original controller topology was not verified after restoration."));
                await _journal.CompleteServiceRestorationAsync(
                    ServiceId,
                    ClawRecoveryStatus.RestoredUnverified,
                    cancellationToken).ConfigureAwait(false);
                return ControllerHandoffResult.ReleasedUnverified;
            }

            _current = restored;
        }

        LastReleasedDevices = _current.PhysicalDevices;
        _current = null;
        _rearButtons = CanonicalButtons.None;
        await _journal.CompleteServiceRestorationAsync(
            ServiceId,
            ClawRecoveryStatus.RestoredVerified,
            cancellationToken).ConfigureAwait(false);
        if (stopFailure is not null)
        {
            _ = Set(ClawServiceState.ReleasedUnverified, stopFailure);
            return ControllerHandoffResult.ReleasedUnverified;
        }

        _ = Set(ClawServiceState.Idle);
        return ControllerHandoffResult.ReleasedVerified;
    }

    /// <summary>
    /// What the Claw's MCU can actually do with output.
    /// </summary>
    /// <remarks>
    /// Two motors and no trigger haptics: the rumble report
    /// (<see cref="ClawControllerCodec.EncodeRumble"/>) carries one weak and one strong byte and
    /// nothing else. The frame rate matches this service's own 4 ms write gate below, so WSGM's
    /// output router paces frames before they reach this device boundary.
    /// </remarks>
    private static readonly HapticCapabilities OutputCapabilities = new()
    {
        LowFrequency = OutputChannelSupport.Native,
        HighFrequency = OutputChannelSupport.Native,
        LeftTrigger = OutputChannelSupport.Unsupported,
        RightTrigger = OutputChannelSupport.Unsupported,
        MaxFramesPerSecond = 250,
        // ERM motors: LRA-grade haptic ticks must be floored and stretched by the host to be
        // perceptible at all. Device-measured with the attended A-button sweep (2026-09-02):
        // 30 ms ticks are felt down to 56/255 and vanish at 48; full-strength pulses stay
        // reliable to about 10 ms (below that the sleep granularity dominates); continuous
        // rumble is felt down to 24/255, which is why the floor applies to bounded events only.
        MinimumStartIntensity = 56f / 255f,
        MinimumPulse = TimeSpan.FromMilliseconds(10),
    };

    public async ValueTask ApplyHapticsAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken)
    {
        await _outputSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not ClawServiceState.Owned)
            {
                return;
            }

            byte weak = ToByte(frame.HighFrequency);
            byte strong = ToByte(frame.LowFrequency);
            DateTimeOffset now;
            lock (_hapticGate)
            {
                if (weak == _lastWeak && strong == _lastStrong)
                {
                    return;
                }

                now = DateTimeOffset.UtcNow;
                if ((weak != 0 || strong != 0)
                    && now - _lastHapticWrite < TimeSpan.FromMilliseconds(4))
                {
                    return;
                }
            }

            await _source.WriteRumbleAsync(weak, strong, cancellationToken).ConfigureAwait(false);
            lock (_hapticGate)
            {
                _lastWeak = weak;
                _lastStrong = strong;
                _lastHapticWrite = now;
            }
        }
        finally
        {
            _outputSerializer.Release();
        }
    }

    private async ValueTask<CapabilityReason?> StopOutputAndAcquisitionAsync(
        CancellationToken cancellationToken)
    {
        CapabilityReason? failure = null;
        bool ownsOutput = false;
        try
        {
            await _outputSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
            ownsOutput = true;
            try
            {
                await _source.WriteRumbleAsync(0, 0, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The device may already be gone; acquisition cleanup and mode restoration continue.
                _host.Trace(
                    DeviceTraceLevel.Warn,
                    "controller",
                    $"zero-rumble write during release failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // StopAsync below closes the stream to abort the in-flight output at the lifecycle deadline.
        }
        finally
        {
            if (ownsOutput)
            {
                _outputSerializer.Release();
            }
        }

        try
        {
            await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The physical source may have vanished. Continue to the independently bounded mode
            // restoration, which is the cleanup step that keeps external input usable.
            failure = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                $"The controller source did not stop cleanly ({ex.GetType().Name}: {ex.Message}).");
            _host.Trace(
                DeviceTraceLevel.Error,
                "controller",
                failure.Detail ?? "The controller source did not stop cleanly.");
        }
        lock (_hapticGate)
        {
            _lastWeak = 0;
            _lastStrong = 0;
            _lastHapticWrite = DateTimeOffset.UtcNow;
        }

        return failure;
    }

    private async ValueTask PublishControllerSampleAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        CanonicalButtons current = sample.Buttons
            & (CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2);
        CanonicalButtons changed = current ^ _rearButtons;
        if ((changed & CanonicalButtons.RearPaddle1) != 0)
        {
            await PublishRearEventAsync(
                "oem3",
                (current & CanonicalButtons.RearPaddle1) != 0,
                sample,
                cancellationToken).ConfigureAwait(false);
        }

        if ((changed & CanonicalButtons.RearPaddle2) != 0)
        {
            await PublishRearEventAsync(
                "oem4",
                (current & CanonicalButtons.RearPaddle2) != 0,
                sample,
                cancellationToken).ConfigureAwait(false);
        }

        _rearButtons = current;
        // Aged rather than taken blindly. If the WinRT gyrometer stops raising ReadingChanged while
        // the controller keeps reporting, the last reading would otherwise ride every following
        // sample and replay a non-zero angular velocity through the virtual Deck indefinitely.
        await _host.PublishControllerSampleAsync(
            sample with { Motion = _motion.Current(sample.Timestamp) },
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask PublishRearEventAsync(
        string controlId,
        bool pressed,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken) =>
        _host.PublishOemEventAsync(
            new OemControlEvent(
                controlId,
                OemPressKind.Short,
                sample.CycleGeneration,
                sample.Timestamp,
                $"claw-hid-{controlId}-{sample.Sequence}",
                pressed ? OemControlEdge.Pressed : OemControlEdge.Released),
            cancellationToken);

    private async ValueTask RestoreAfterFailedAcquireAsync(DateTimeOffset deadline)
    {
        try
        {
            ControllerTopology? observed = await _source.DiscoverAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (observed is null)
            {
                _current = null;
                CapabilityReason reason = new(
                    CapabilityReasonCode.TransportFaulted,
                    "Controller topology vanished during acquisition rollback.");
                Fault(reason);
                await _journal.CompleteServiceRestorationAsync(
                    ServiceId,
                    ClawRecoveryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            _current = observed;
            _ = await ReleaseControllerAsync(deadline, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            Fault(new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "Controller acquisition rollback failed."));
            await _journal.CompleteServiceRestorationAsync(
                ServiceId,
                ClawRecoveryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static byte ToByte(float value) =>
        checked((byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue));
}

internal sealed class ChordSuppressorService(
    IFirmwareChordSuppressor suppressor,
    OemEventService oemEvents,
    IPluginHostAdapter host) : ClawServiceStatus(ServiceIds.ChordSuppressor)
{
    private readonly IFirmwareChordSuppressor _suppressor = suppressor
        ?? throw new ArgumentNullException(nameof(suppressor));
    private readonly OemEventService _oemEvents = oemEvents
        ?? throw new ArgumentNullException(nameof(oemEvents));
    private readonly IPluginHostAdapter _host = host ?? throw new ArgumentNullException(nameof(host));

    public async ValueTask<ClawServiceResult> AcquireAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        if (_oemEvents.State is not ClawServiceState.Owned)
        {
            return Set(ClawServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "Chord suppression starts only when the device-identified MSI OEM event source is healthy."));
        }

        bool started = await _suppressor.StartAsync(ReportFault, cancellationToken).ConfigureAwait(false);
        return started
            ? Set(ClawServiceState.Owned)
            : Set(ClawServiceState.Degraded, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The bounded low-level keyboard hook could not be installed."));
    }

    private void ReportFault(Exception exception)
    {
        string detail = ClawDiagnosticText.FromException(
            "The firmware chord suppressor stopped",
            exception);
        CapabilityReason reason = new(
            CapabilityReasonCode.TransportFaulted,
            detail);
        Fault(reason);
        _host.ReportFault(ServiceId, detail);
    }

    public async ValueTask<ClawServiceResult> SuspendAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        await _suppressor.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Idle);
    }

    public ValueTask<ClawServiceResult> ResumeAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken) => AcquireAsync(context, cancellationToken);

    public async ValueTask<ClawServiceResult> ReleaseAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        await _suppressor.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(ClawServiceState.Idle);
    }
}

/// <summary>Owns the panel's variable-refresh state for one cycle.</summary>
/// <remarks>
/// A service of its own rather than a corner of the lighting or power services, because it is the
/// only capability driven by the GPU driver rather than by MSI's firmware: it has no firmware
/// identity to verify, and it must be restored on make-safe even when every WMI and MCU path failed.
/// </remarks>
internal sealed class DisplayService : ClawServiceStatus, IDisposable
{
    private readonly ArcSyncTransport _arcSync = new();
    private bool _disposed;

    /// <summary>Creates the service without touching the driver.</summary>
    public DisplayService()
        : base(ServiceIds.Display)
    {
    }

    /// <summary>Whether a variable-refresh capable panel answered.</summary>
    public bool IsAvailable => _arcSync.IsAvailable;

    /// <summary>Opens the driver and selects the panel, capturing the profile to restore later.</summary>
    /// <returns><see langword="true"/> when variable refresh can be driven.</returns>
    public bool TryAcquire()
    {
        bool available = _arcSync.TryOpen();

        // Passive rather than Faulted when no capable panel answered: nothing went wrong, the
        // device simply does not have the feature, and Faulted would report a defect that is not one.
        State = available ? ClawServiceState.Owned : ClawServiceState.Passive;
        return available;
    }

    /// <summary>Reads the current state, or null when it cannot be read.</summary>
    /// <returns>The panel's variable-refresh state.</returns>
    public ArcSyncState? Read() => _arcSync.Read();

    /// <summary>Turns variable refresh on or off, verifying the result.</summary>
    /// <param name="enabled">Whether variable refresh should be active.</param>
    /// <returns><see langword="true"/> when the panel reports the requested state afterwards.</returns>
    public bool TryWrite(bool enabled) => _arcSync.TryWrite(enabled);

    /// <summary>Restores the profile captured when the cycle started.</summary>
    /// <returns><see langword="true"/> when nothing was left changed.</returns>
    public bool Restore() => _arcSync.TryRestore();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _arcSync.Dispose();
        State = ClawServiceState.Idle;
    }
}

internal static class ServiceIds
{
    public const string OemEvents = "msi-oem-events";
    public const string Power = "msi-power";
    public const string ChargeLimit = "msi-charge-limit";
    public const string Fans = "msi-fans";
    public const string Telemetry = "msi-telemetry";
    public const string Lighting = "claw-lighting";
    public const string Motion = "claw-motion";
    public const string Controller = "physical-controller";
    public const string ChordSuppressor = "firmware-chord-suppressor";
    public const string Display = "claw-display";
}

internal static class ClawFirmwareIdentities
{
    public const string Wmi = "ec:1T52EMS1.109;msi-acpi:8.0";
    public const string Mcu = "mcu:0229";
}
