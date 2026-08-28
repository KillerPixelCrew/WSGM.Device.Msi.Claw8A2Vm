using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Input;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

internal abstract class ClawResourceBase(string resourceId) : IPluginResource
{
    public string ResourceId { get; } = resourceId;

    public ResourceState State { get; protected set; } = ResourceState.Idle;

    public CapabilityReason? Reason { get; private set; }

    public CapabilityReason? ReconciliationBlockReason { get; set; }

    public void Quarantine(CapabilityReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ReconciliationBlockReason = reason;
        _ = Set(ResourceState.Faulted, reason);
    }

    public abstract ValueTask<PluginResourceOperationResult> AcquireAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken);

    public virtual ValueTask<PluginResourceOperationResult> SuspendAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Result(State));
    }

    public virtual ValueTask<PluginResourceOperationResult> ResumeAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken) => AcquireAsync(context, cancellationToken);

    public abstract ValueTask<PluginResourceOperationResult> ReleaseAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken);

    protected PluginResourceOperationResult Set(
        ResourceState state,
        CapabilityReason? reason = null)
    {
        State = state;
        Reason = reason;
        return new PluginResourceOperationResult(state, reason);
    }

    protected PluginResourceOperationResult Result(ResourceState state, CapabilityReason? reason = null) =>
        new(state, reason);

    protected static void RequireWriteBudget(DateTimeOffset deadline)
    {
        if (deadline - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(2))
        {
            throw new OperationCanceledException("Insufficient lifecycle budget for a journalled hardware write.");
        }
    }
}

internal sealed class OemEventResource(
    IMsiOemEventSource source,
    IPluginHostAdapter host) : ClawResourceBase(ResourceIds.OemEvents)
{
    private readonly IMsiOemEventSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly IPluginHostAdapter _host = host ?? throw new ArgumentNullException(nameof(host));
    private long _deviceGeneration;

    public override async ValueTask<PluginResourceOperationResult> AcquireAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        _deviceGeneration = context.DeviceGeneration;
        bool started = await _source.StartAsync(PublishAsync, cancellationToken).ConfigureAwait(false);
        return started
            ? Set(ResourceState.Owned)
            : Set(ResourceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The MSI_Event provider was unavailable."));
    }

    public override async ValueTask<PluginResourceOperationResult> SuspendAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(ResourceState.Idle);
    }

    public override ValueTask<PluginResourceOperationResult> ResumeAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken) => AcquireAsync(context, cancellationToken);

    public override async ValueTask<PluginResourceOperationResult> ReleaseAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(ResourceState.Idle);
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

        return _host.PublishOemEventAsync(
            new OemControlEvent(
                mapped.Value.controlId,
                mapped.Value.press,
                _deviceGeneration,
                timestamp,
                $"msi-event-{code:X2}-{timestamp.UtcTicks}"),
            CancellationToken.None);
    }
}

internal sealed class PowerResource(
    IClawIdentityReader identity,
    ClawA2VmPowerCapability capability,
    ClawRecoveryJournal journal) : ClawResourceBase(ResourceIds.Power)
{
    private readonly IClawIdentityReader _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly ClawA2VmPowerCapability _capability = capability ?? throw new ArgumentNullException(nameof(capability));
    private readonly ClawRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private PowerPair? _restoreSnapshot;

    public PowerPair? LastObserved { get; private set; }

    public override async ValueTask<PluginResourceOperationResult> AcquireAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(ResourceState.Faulted, ReconciliationBlockReason);
        }

        ClawIdentityState identity = await _identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch || !identity.WmiFirmwareVerified)
        {
            return Set(ResourceState.Passive, FirmwareReason(identity));
        }

        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);
        _restoreSnapshot ??= LastObserved;
        return Set(ResourceState.Owned);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken) =>
        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);

    public override async ValueTask<PluginResourceOperationResult> ReleaseAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(ResourceState.Faulted, ReconciliationBlockReason);
        }

        if (_restoreSnapshot is null || State is not ResourceState.Owned
            || !_journal.HasUnrestoredMutation(ResourceId))
        {
            return Set(ResourceState.Idle);
        }

        RequireWriteBudget(context.Deadline);
        bool restored;
        try
        {
            restored = await _capability.RestoreAsync(_restoreSnapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await _journal.CompleteResourceRestorationAsync(
                ResourceId,
                JournalEntryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await _journal.CompleteResourceRestorationAsync(
            ResourceId,
            restored ? JournalEntryStatus.RestoredVerified : JournalEntryStatus.RestoredUnverified,
            cancellationToken).ConfigureAwait(false);
        return restored
            ? Set(ResourceState.Idle)
            : Set(ResourceState.ReleasedUnverified, new CapabilityReason(
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

internal sealed class FanResource(
    IClawIdentityReader identity,
    ClawA2VmFanCapability capability,
    ClawRecoveryJournal journal) : ClawResourceBase(ResourceIds.Fans)
{
    private readonly IClawIdentityReader _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly ClawA2VmFanCapability _capability = capability ?? throw new ArgumentNullException(nameof(capability));
    private readonly ClawRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private FanSnapshot? _restoreSnapshot;

    public FanSnapshot? LastObserved { get; private set; }

    public override async ValueTask<PluginResourceOperationResult> AcquireAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(ResourceState.Faulted, ReconciliationBlockReason);
        }

        ClawIdentityState identity = await _identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch || !identity.WmiFirmwareVerified)
        {
            return Set(ResourceState.Passive, new CapabilityReason(
                identity.ExactMachineMatch
                    ? CapabilityReasonCode.FirmwareNotVerified
                    : CapabilityReasonCode.GenerationChanged,
                "The exact board, EC firmware, and MSI_ACPI interface were not verified."));
        }

        LastObserved = await _capability.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        _restoreSnapshot ??= LastObserved;
        return Set(ResourceState.Owned);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken) =>
        LastObserved = await _capability.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);

    public override async ValueTask<PluginResourceOperationResult> ReleaseAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        if (ReconciliationBlockReason is not null)
        {
            return Set(ResourceState.Faulted, ReconciliationBlockReason);
        }

        if (_restoreSnapshot is null || State is not ResourceState.Owned
            || !_journal.HasUnrestoredMutation(ResourceId))
        {
            return Set(ResourceState.Idle);
        }

        RequireWriteBudget(context.Deadline);
        bool restored;
        try
        {
            restored = await _capability.RestoreAsync(_restoreSnapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await _journal.CompleteResourceRestorationAsync(
                ResourceId,
                JournalEntryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await _journal.CompleteResourceRestorationAsync(
            ResourceId,
            restored ? JournalEntryStatus.RestoredVerified : JournalEntryStatus.RestoredUnverified,
            cancellationToken).ConfigureAwait(false);
        return restored
            ? Set(ResourceState.Idle)
            : Set(ResourceState.ReleasedUnverified, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The captured left/right fan tables or flags could not be verified after restoration."));
    }
}

internal sealed class TelemetryResource(
    IClawIdentityReader identity,
    ClawA2VmFanCapability capability) : ClawResourceBase(ResourceIds.Telemetry)
{
    private readonly IClawIdentityReader _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly ClawA2VmFanCapability _capability = capability ?? throw new ArgumentNullException(nameof(capability));

    public FanTelemetry? LastTelemetry { get; private set; }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken) =>
        LastTelemetry = await _capability.ReadTelemetryAsync(cancellationToken).ConfigureAwait(false);

    public override async ValueTask<PluginResourceOperationResult> AcquireAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        ClawIdentityState identity = await _identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch || !identity.WmiFirmwareVerified)
        {
            return Set(ResourceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "MSI telemetry requires the exact reviewed MSI_ACPI provider."));
        }

        LastTelemetry = await _capability.ReadTelemetryAsync(cancellationToken).ConfigureAwait(false);
        return Set(ResourceState.Owned);
    }

    public override ValueTask<PluginResourceOperationResult> ReleaseAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastTelemetry = null;
        return ValueTask.FromResult(Set(ResourceState.Idle));
    }
}

internal sealed class LightingResource(
    IClawIdentityReader identity,
    IClawMcuTransport transport,
    ClawA2VmLightingCapability capability) : ClawResourceBase(ResourceIds.Lighting)
{
    private readonly IClawIdentityReader _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly IClawMcuTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly ClawA2VmLightingCapability _capability = capability ?? throw new ArgumentNullException(nameof(capability));

    public LightingState? LastObserved { get; private set; }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken) =>
        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);

    public override async ValueTask<PluginResourceOperationResult> AcquireAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        ClawIdentityState identity = await _identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch || !identity.McuFirmwareVerified)
        {
            return Set(ResourceState.Passive, new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "Lighting writes are gated to controller firmware 0x0229 and profile base 0x024A."));
        }

        if (!await _transport.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return Set(ResourceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The reviewed MCU HID collection was unavailable."));
        }

        LastObserved = await _capability.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Set(ResourceState.Owned);
    }

    public override ValueTask<PluginResourceOperationResult> ReleaseAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // RGB profile writes are intentionally persistent user choices. Deactivation never rewrites
        // the captured profile and therefore cannot silently undo the requested setting.
        return ValueTask.FromResult(Set(ResourceState.Idle));
    }
}

internal sealed class MotionResource(IClawMotionSource source) : ClawResourceBase(ResourceIds.Motion)
{
    private readonly IClawMotionSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public MotionSample? Latest { get; private set; }

    public override async ValueTask<PluginResourceOperationResult> AcquireAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        bool started = await _source.StartAsync(
            sample =>
            {
                Latest = sample;
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return started
            ? Set(ResourceState.Owned)
            : Set(ResourceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The Intel ISS gyrometer was unavailable; no accelerometer fallback exists."));
    }

    public override async ValueTask<PluginResourceOperationResult> SuspendAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        Latest = null;
        return Set(ResourceState.Idle);
    }

    public override ValueTask<PluginResourceOperationResult> ResumeAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken) => AcquireAsync(context, cancellationToken);

    public override async ValueTask<PluginResourceOperationResult> ReleaseAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        Latest = null;
        return Set(ResourceState.Idle);
    }
}

internal sealed class ControllerResource(
    IClawIdentityReader identity,
    IClawMcuTransport mcu,
    IClawControllerSource source,
    MotionResource motion,
    IPluginHostAdapter host,
    ClawRecoveryJournal journal) : ClawResourceBase(ResourceIds.Controller)
{
    private readonly IClawIdentityReader _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly IClawMcuTransport _mcu = mcu ?? throw new ArgumentNullException(nameof(mcu));
    private readonly IClawControllerSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly MotionResource _motion = motion ?? throw new ArgumentNullException(nameof(motion));
    private readonly IPluginHostAdapter _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly ClawRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private ControllerTopology? _original;
    private ControllerTopology? _current;
    private CanonicalButtons _rearButtons;
    private RecoveryJournalEntry? _modeJournalEntry;
    private readonly object _hapticGate = new();
    private readonly SemaphoreSlim _outputSerializer = new(1, 1);
    private byte _lastWeak;
    private byte _lastStrong;
    private DateTimeOffset _lastHapticWrite;

    public bool Enabled { get; set; }

    public ControllerTopology? CurrentTopology => _current;

    public IReadOnlyList<PhysicalDeviceIdentity> LastReleasedDevices { get; private set; } = [];

    public override async ValueTask<PluginResourceOperationResult> AcquireAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return Set(ResourceState.Passive, new CapabilityReason(
                CapabilityReasonCode.ResourceReleased,
                "Controller management is disabled."));
        }

        LastReleasedDevices = [];

        if (ReconciliationBlockReason is not null)
        {
            return Set(ResourceState.Faulted, ReconciliationBlockReason);
        }

        ClawIdentityState identity = await _identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch || !identity.McuFirmwareVerified)
        {
            return Set(ResourceState.Passive, new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "Controller ownership is gated to exact MS-1T52 firmware 0x0229."));
        }

        ControllerTopology? observed = await _source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        _original ??= observed;
        if (_original is null || observed is null
            || string.IsNullOrWhiteSpace(_original.PhysicalLocation)
            || !HidEndpointEnumerator.SamePhysicalLocation(
                observed.PhysicalLocation,
                _original.PhysicalLocation))
        {
            return Set(ResourceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The physical controller or its composite USB location was unavailable."));
        }

        _current = observed;
        if (_current.Mode is not ClawControllerMode.DirectInput)
        {
            RequireWriteBudget(context.Deadline);
            _modeJournalEntry = await _journal.BeginAsync(
                ResourceId,
                CapabilityIds.Controller,
                ClawFirmwareIdentities.Mcu,
                ClawRecoveryValues.ControllerMode(_original.Mode),
                ClawRecoveryValues.ControllerMode(ClawControllerMode.DirectInput),
                cancellationToken).ConfigureAwait(false);
            _modeJournalEntry = await _journal.MarkApplyingAsync(_modeJournalEntry, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                _current = await _mcu.SwitchModeAsync(
                    ClawControllerMode.DirectInput,
                    _original.PhysicalLocation,
                    context.Deadline,
                    cancellationToken).ConfigureAwait(false);
                _modeJournalEntry = await _journal.MarkAppliedAsync(
                    _modeJournalEntry,
                    ClawRecoveryValues.ControllerMode(_current.Mode),
                    verified: true,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RestoreAfterFailedAcquireAsync(context.Deadline).ConfigureAwait(false);
                throw;
            }
        }

        if (_current.PhysicalDevices.Count == 0)
        {
            await RestoreAfterFailedAcquireAsync(context.Deadline).ConfigureAwait(false);
            return Set(ResourceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "No exact DirectInput physical interface identity was available for handoff."));
        }

        try
        {
            await _source.StartAsync(
                context.DeviceGeneration,
                PublishControllerSampleAsync,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RestoreAfterFailedAcquireAsync(context.Deadline).ConfigureAwait(false);
            throw;
        }

        await _host.PublishPhysicalDevicesAsync(_current.PhysicalDevices, cancellationToken)
            .ConfigureAwait(false);
        return Set(ResourceState.Owned);
    }

    public override async ValueTask<PluginResourceOperationResult> SuspendAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        await StopOutputAndAcquisitionAsync(cancellationToken).ConfigureAwait(false);
        return Set(ResourceState.Idle);
    }

    public override ValueTask<PluginResourceOperationResult> ResumeAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken) => AcquireAsync(context, cancellationToken);

    public override async ValueTask<PluginResourceOperationResult> ReleaseAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        ControllerHandoffResult result = await ReleaseControllerAsync(context.Deadline, cancellationToken)
            .ConfigureAwait(false);
        return result is ControllerHandoffResult.ReleasedVerified
            ? Set(ResourceState.Idle)
            : Set(ResourceState.ReleasedUnverified, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "Controller mode or physical USB topology restoration was not verified."));
    }

    public async ValueTask<ControllerHandoffResult> ReleaseControllerAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        _ = Set(ResourceState.Releasing);
        await StopOutputAndAcquisitionAsync(cancellationToken).ConfigureAwait(false);
        if (ReconciliationBlockReason is not null)
        {
            _ = Set(ResourceState.ReleasedUnverified, ReconciliationBlockReason);
            return ControllerHandoffResult.ReleasedUnverified;
        }

        if (_original is null || _current is null)
        {
            _current = null;
            LastReleasedDevices = [];
            if (_journal.HasUnrestoredMutation(ResourceId))
            {
                _ = Set(ResourceState.ReleasedUnverified, new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "Controller topology disappeared before its original mode could be verified."));
                await _journal.CompleteResourceRestorationAsync(
                    ResourceId,
                    JournalEntryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
                return ControllerHandoffResult.ReleasedUnverified;
            }

            _ = Set(ResourceState.Idle);
            return ControllerHandoffResult.ReleasedVerified;
        }

        if (_current.Mode != _original.Mode)
        {
            RequireWriteBudget(deadline);
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
                Quarantine(reason);
                await _journal.CompleteResourceRestorationAsync(
                    ResourceId,
                    JournalEntryStatus.RestoreFailed,
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
                _ = Set(ResourceState.ReleasedUnverified, new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "The original controller topology was not verified after restoration."));
                await _journal.CompleteResourceRestorationAsync(
                    ResourceId,
                    JournalEntryStatus.RestoredUnverified,
                    cancellationToken).ConfigureAwait(false);
                return ControllerHandoffResult.ReleasedUnverified;
            }

            _current = restored;
        }

        LastReleasedDevices = _current.PhysicalDevices;
        _current = null;
        _ = Set(ResourceState.Idle);
        _rearButtons = CanonicalButtons.None;
        await _journal.CompleteResourceRestorationAsync(
            ResourceId,
            JournalEntryStatus.RestoredVerified,
            cancellationToken).ConfigureAwait(false);
        return ControllerHandoffResult.ReleasedVerified;
    }

    public async ValueTask ApplyHapticsAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken)
    {
        await _outputSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not ResourceState.Owned)
            {
                return;
            }

            byte weak = ToByte(frame.HighFrequency);
            byte strong = ToByte(frame.LowFrequency);
            lock (_hapticGate)
            {
                if (weak == _lastWeak && strong == _lastStrong)
                {
                    return;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if ((weak != 0 || strong != 0)
                    && now - _lastHapticWrite < TimeSpan.FromMilliseconds(4))
                {
                    return;
                }

                _lastWeak = weak;
                _lastStrong = strong;
                _lastHapticWrite = now;
            }

            await _source.WriteRumbleAsync(weak, strong, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _outputSerializer.Release();
        }
    }

    private async ValueTask StopOutputAndAcquisitionAsync(CancellationToken cancellationToken)
    {
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
        }
        lock (_hapticGate)
        {
            _lastWeak = 0;
            _lastStrong = 0;
            _lastHapticWrite = DateTimeOffset.UtcNow;
        }
    }

    private async ValueTask PublishControllerSampleAsync(CanonicalControllerSample sample)
    {
        CanonicalButtons current = sample.Buttons
            & (CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2);
        CanonicalButtons changed = current ^ _rearButtons;
        if ((changed & CanonicalButtons.RearPaddle1) != 0)
        {
            await PublishRearEventAsync(
                "oem3",
                (current & CanonicalButtons.RearPaddle1) != 0,
                sample).ConfigureAwait(false);
        }

        if ((changed & CanonicalButtons.RearPaddle2) != 0)
        {
            await PublishRearEventAsync(
                "oem4",
                (current & CanonicalButtons.RearPaddle2) != 0,
                sample).ConfigureAwait(false);
        }

        _rearButtons = current;
        await _host.PublishControllerSampleAsync(
            sample with { Motion = _motion.Latest },
            CancellationToken.None).ConfigureAwait(false);
    }

    private ValueTask PublishRearEventAsync(
        string controlId,
        bool pressed,
        CanonicalControllerSample sample) =>
        _host.PublishOemEventAsync(
            new OemControlEvent(
                controlId,
                OemPressKind.Short,
                sample.DeviceGeneration,
                sample.Timestamp,
                $"claw-hid-{controlId}-{sample.Sequence}",
                pressed ? OemControlEdge.Pressed : OemControlEdge.Released),
            CancellationToken.None);

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
                Quarantine(reason);
                await _journal.CompleteResourceRestorationAsync(
                    ResourceId,
                    JournalEntryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            _current = observed;
            _ = await ReleaseControllerAsync(deadline, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            Quarantine(new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "Controller acquisition rollback failed."));
            await _journal.CompleteResourceRestorationAsync(
                ResourceId,
                JournalEntryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static byte ToByte(float value) =>
        checked((byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue));
}

internal sealed class ChordSuppressorResource(
    IFirmwareChordSuppressor suppressor,
    OemEventResource oemEvents) : ClawResourceBase(ResourceIds.ChordSuppressor)
{
    private readonly IFirmwareChordSuppressor _suppressor = suppressor
        ?? throw new ArgumentNullException(nameof(suppressor));
    private readonly OemEventResource _oemEvents = oemEvents
        ?? throw new ArgumentNullException(nameof(oemEvents));

    public override async ValueTask<PluginResourceOperationResult> AcquireAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        if (_oemEvents.State is not ResourceState.Owned)
        {
            return Set(ResourceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "Chord suppression starts only when the device-identified MSI OEM event source is healthy."));
        }

        bool started = await _suppressor.StartAsync(cancellationToken).ConfigureAwait(false);
        return started
            ? Set(ResourceState.Owned)
            : Set(ResourceState.Degraded, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The bounded low-level keyboard hook could not be installed."));
    }

    public override async ValueTask<PluginResourceOperationResult> SuspendAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        await _suppressor.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(ResourceState.Idle);
    }

    public override ValueTask<PluginResourceOperationResult> ResumeAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken) => AcquireAsync(context, cancellationToken);

    public override async ValueTask<PluginResourceOperationResult> ReleaseAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        await _suppressor.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(ResourceState.Idle);
    }
}

internal static class ResourceIds
{
    public const string OemEvents = "msi-oem-events";
    public const string Power = "msi-power";
    public const string Fans = "msi-fans";
    public const string Telemetry = "msi-telemetry";
    public const string Lighting = "claw-lighting";
    public const string Motion = "claw-motion";
    public const string Controller = "physical-controller";
    public const string ChordSuppressor = "firmware-chord-suppressor";
}

internal static class ClawFirmwareIdentities
{
    public const string Wmi = "ec:1T52EMS1.109;msi-acpi:8.0";
    public const string Mcu = "mcu:0229";
}
