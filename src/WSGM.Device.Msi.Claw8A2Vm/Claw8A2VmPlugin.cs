using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Input;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>The exact-device plugin for the MSI Claw 8 AI+ A2VM board <c>MS-1T52</c>.</summary>
public sealed class Claw8A2VmPlugin : IDevicePlugin
{
    private readonly ClawHardwareServices _services;
    private readonly Dictionary<string, CapabilityCommandResult> _idempotency = new(StringComparer.Ordinal);
    private readonly Queue<string> _idempotencyOrder = new();
    private readonly object _commandGate = new();
    private readonly SemaphoreSlim _commandSerializer = new(1, 1);
    private IPluginHostAdapter? _host;
    private PluginResourceCoordinator? _coordinator;
    private CapabilityRegistry? _registry;
    private OemEventResource? _oem;
    private PowerResource? _power;
    private FanResource? _fans;
    private TelemetryResource? _telemetry;
    private LightingResource? _lighting;
    private MotionResource? _motion;
    private ControllerResource? _controller;
    private ChordSuppressorResource? _suppressor;
    private ClawRecoveryJournal? _journal;
    private ClawA2VmPowerCapability? _powerCapability;
    private ClawA2VmFanCapability? _fanCapability;
    private ClawA2VmLightingCapability? _lightingCapability;
    private long _hostGeneration;
    private long _deviceGeneration;
    private bool _active;
    private bool _quiescing;
    private bool _disposed;

    /// <summary>Creates the production plugin with Windows hardware transports.</summary>
    public Claw8A2VmPlugin()
        : this(CreateWindowsServices())
    {
    }

    internal Claw8A2VmPlugin(ClawHardwareServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public string PackageId => ClawHardwareFacts.PackageId;

    /// <inheritdoc />
    public ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        bool matched = WindowsClawIdentityReader.IsExactMachine(context.Identity);
        return ValueTask.FromResult(new PluginDetectionResult
        {
            Matched = matched,
            DeviceDefinitionId = matched ? ClawHardwareFacts.DeviceDefinitionId : null,
            Reason = matched
                ? null
                : new CapabilityReason(
                    CapabilityReasonCode.Unsupported,
                    "This package requires manufacturer Micro-Star, baseboard MS-1T52, and SKU 1T52.1."),
        });
    }

    /// <inheritdoc />
    public async ValueTask ActivateAsync(
        PluginActivationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active)
        {
            throw new InvalidOperationException("The plugin already owns a device cycle.");
        }

        if (!string.Equals(
                context.DeviceDefinitionId,
                ClawHardwareFacts.DeviceDefinitionId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DeviceHost supplied a different device definition.");
        }

        if (context.HostGeneration != context.Host.HostGeneration
            || context.DeviceGeneration != context.Host.DeviceGeneration)
        {
            throw new InvalidOperationException("DeviceHost supplied inconsistent activation generations.");
        }

        ClawIdentityState identity = await _services.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch)
        {
            throw new InvalidOperationException("The exact MS-1T52 activation gate no longer matches.");
        }

        _host = context.Host;
        _hostGeneration = context.HostGeneration;
        _deviceGeneration = context.DeviceGeneration;
        _quiescing = false;
        lock (_commandGate)
        {
            _idempotency.Clear();
            _idempotencyOrder.Clear();
        }

        var powerCapability = new ClawA2VmPowerCapability(_services.Wmi);
        var fanCapability = new ClawA2VmFanCapability(_services.Wmi);
        var lightingCapability = new ClawA2VmLightingCapability(_services.Mcu);
        _powerCapability = powerCapability;
        _fanCapability = fanCapability;
        _lightingCapability = lightingCapability;
        _journal = new ClawRecoveryJournal(context.Host, context.OutstandingJournalEntries);
        _oem = new OemEventResource(_services.OemEvents, context.Host);
        _power = new PowerResource(_services.Identity, powerCapability, _journal);
        _fans = new FanResource(_services.Identity, fanCapability, _journal);
        _telemetry = new TelemetryResource(_services.Identity, fanCapability);
        _lighting = new LightingResource(_services.Identity, _services.Mcu, lightingCapability);
        _motion = new MotionResource(_services.Motion);
        _controller = new ControllerResource(
            _services.Identity,
            _services.Mcu,
            _services.Controller,
            _motion,
            context.Host,
            _journal)
        {
            Enabled = context.ControllerManagementEnabled,
        };
        _suppressor = new ChordSuppressorResource(_services.ChordSuppressor, _oem);

        ClawResourceBase[] resources =
        [
            _oem,
            _power,
            _fans,
            _telemetry,
            _lighting,
            _motion,
            _controller,
            _suppressor,
        ];
        _coordinator = new PluginResourceCoordinator(context.Host, resources);
        _registry = BuildRegistry(powerCapability, fanCapability, lightingCapability);

        await ReconcileOutstandingAsync(
            context.OutstandingJournalEntries,
            identity,
            powerCapability,
            fanCapability,
            cancellationToken).ConfigureAwait(false);

        await context.Host.PublishDescriptorsAsync(_registry.DescriptorSet, cancellationToken)
            .ConfigureAwait(false);
        await context.Host.PublishOemControlsAsync(CreateOemControls(), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _coordinator.ActivateAsync(
                new PluginResourceOperationContext(
                    context.HostGeneration,
                    context.DeviceGeneration,
                    DateTimeOffset.UtcNow.AddSeconds(15)),
                cancellationToken).ConfigureAwait(false);
            _active = true;
            await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _quiescing = true;
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_active || _registry is null || _quiescing)
        {
            return Rejected(
                command,
                CapabilityReasonCode.Quiescing,
                "The Claw device cycle is inactive or quiescing.");
        }

        try
        {
            await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Rejected(
                command,
                CapabilityReasonCode.Quiescing,
                "The command was cancelled before its serialized hardware turn began.");
        }

        try
        {
            CapabilityRegistry? registry = _registry;
            if (!_active || registry is null || _quiescing)
            {
                return Rejected(
                    command,
                    CapabilityReasonCode.Quiescing,
                    "The Claw device cycle started quiescing before this command could run.");
            }

            lock (_commandGate)
            {
                if (_idempotency.TryGetValue(command.IdempotencyKey, out CapabilityCommandResult? cached))
                {
                    return cached with { CommandId = command.CommandId };
                }
            }

            CapabilityCommandResult result = await registry.ExecuteAsync(command, cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome is not CommandOutcome.Accepted)
            {
                Remember(command.IdempotencyKey, result);
            }

            if (_host is not null && result.Outcome is CommandOutcome.AppliedVerified)
            {
                await RefreshObservedAsync(command.CapabilityId, CancellationToken.None).ConfigureAwait(false);
                await PublishCapabilityStatesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask SuspendAsync(
        PluginQuiesceContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_coordinator is null)
        {
            return;
        }

        _quiescing = true;
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _coordinator.SuspendAsync(
                OperationContext(context.Deadline),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_coordinator is null)
        {
            return;
        }

        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _deviceGeneration = context.DeviceGeneration;
            await _coordinator.ResumeAsync(
                OperationContext(context.Deadline),
                cancellationToken).ConfigureAwait(false);
            if (_host is null || _powerCapability is null || _fanCapability is null
                || _lightingCapability is null)
            {
                throw new InvalidOperationException("Resume cannot rebuild the capability surface.");
            }

            _registry = BuildRegistry(
                _powerCapability,
                _fanCapability,
                _lightingCapability);
            await _host.PublishDescriptorsAsync(_registry.DescriptorSet, cancellationToken)
                .ConfigureAwait(false);
            _quiescing = false;
            await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _controller is null || _quiescing
            ? ValueTask.CompletedTask
            : _controller.ApplyHapticsAsync(frame, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<PluginControllerRelease> ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReleaseControllerCoreAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_controller is null)
            {
                return;
            }

            _controller.Enabled = context.Enabled;
            _deviceGeneration = context.DeviceGeneration;
            if (context.Enabled)
            {
                PluginResourceOperationResult result = await _controller.AcquireAsync(
                    OperationContext(context.Deadline),
                    cancellationToken).ConfigureAwait(false);
                await PublishResourceStateAsync(
                    _controller,
                    result.State,
                    cancellationToken,
                    result.Reason).ConfigureAwait(false);
            }
            else
            {
                _ = await ReleaseControllerCoreAsync(
                    new PluginControllerReleaseContext(HandoffScope.ControllerOnly, context.Deadline),
                    cancellationToken).ConfigureAwait(false);
            }

            await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    private async ValueTask<PluginControllerRelease> ReleaseControllerCoreAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken)
    {
        if (_controller is null)
        {
            return new PluginControllerRelease
            {
                Step = ControllerHandoffStep.TopologyVerified,
                Result = ControllerHandoffResult.ReleasedVerified,
            };
        }

        ControllerHandoffResult result = await _controller.ReleaseControllerAsync(
            context.Deadline,
            cancellationToken).ConfigureAwait(false);
        await PublishResourceStateAsync(
            _controller,
            result is ControllerHandoffResult.ReleasedVerified
                ? ResourceState.Idle
                : ResourceState.ReleasedUnverified,
            cancellationToken).ConfigureAwait(false);
        return new PluginControllerRelease
        {
            Step = result is ControllerHandoffResult.ReleasedVerified
                ? ControllerHandoffStep.TopologyVerified
                : ControllerHandoffStep.TopologyUnverified,
            Result = result,
            ReleasedDevices = _controller.LastReleasedDevices,
        };
    }

    /// <inheritdoc />
    public async ValueTask DeactivateAsync(
        PluginDeactivationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _quiescing = true;
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_coordinator is not null)
            {
                await _coordinator.ReleaseAsync(OperationContext(context.Deadline), cancellationToken)
                    .ConfigureAwait(false);
            }

            _active = false;
            _registry = null;
            _coordinator = null;
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_active)
        {
            await DeactivateAsync(
                new PluginDeactivationContext(
                    PluginDeactivationReason.WsgmExiting,
                    DateTimeOffset.UtcNow.AddSeconds(12)),
                CancellationToken.None).ConfigureAwait(false);
        }

        await _services.ChordSuppressor.DisposeAsync().ConfigureAwait(false);
        await _services.Motion.DisposeAsync().ConfigureAwait(false);
        await _services.Controller.DisposeAsync().ConfigureAwait(false);
        await _services.Mcu.DisposeAsync().ConfigureAwait(false);
        await _services.OemEvents.DisposeAsync().ConfigureAwait(false);
        await _services.Wmi.DisposeAsync().ConfigureAwait(false);
        _commandSerializer.Dispose();
    }

    private CapabilityRegistry BuildRegistry(
        ClawA2VmPowerCapability powerCapability,
        ClawA2VmFanCapability fanCapability,
        ClawA2VmLightingCapability lightingCapability)
    {
        if (_power is null || _fans is null || _telemetry is null || _lighting is null
            || _motion is null || _controller is null)
        {
            throw new InvalidOperationException("Resources must exist before descriptors are built.");
        }

        IReadOnlyList<CapabilityRegistration> registrations =
        [
            Register(
                ResourceIds.Power,
                IntegerDescriptor(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit,
                    DisplayKey.SustainedPowerLimit, 8, 30, CapabilityUnit.Watt, writable: true),
                _power,
                async token => Integer((await powerCapability.ReadAsync(token).ConfigureAwait(false)).SustainedWatts),
                (execution, token) => JournalCommandAsync(
                    ResourceIds.Power,
                    ClawFirmwareIdentities.Wmi,
                    execution,
                    async commandToken => ClawRecoveryValues.Power(
                        await powerCapability.ReadAsync(commandToken).ConfigureAwait(false)),
                    (commandExecution, commandToken) => powerCapability.ApplySustainedAsync(
                        commandExecution.Command,
                        commandExecution.Command.RequestedValue!.IntegerValue!.Value,
                        commandToken),
                    token)),
            Register(
                ResourceIds.Power,
                IntegerDescriptor(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit,
                    DisplayKey.BoostPowerLimit, 8, 37, CapabilityUnit.Watt, writable: true),
                _power,
                async token => Integer((await powerCapability.ReadAsync(token).ConfigureAwait(false)).BoostWatts),
                (execution, token) => JournalCommandAsync(
                    ResourceIds.Power,
                    ClawFirmwareIdentities.Wmi,
                    execution,
                    async commandToken => ClawRecoveryValues.Power(
                        await powerCapability.ReadAsync(commandToken).ConfigureAwait(false)),
                    (commandExecution, commandToken) => powerCapability.ApplyBoostAsync(
                        commandExecution.Command,
                        commandExecution.Command.RequestedValue!.IntegerValue!.Value,
                        commandToken),
                    token)),
            Register(
                ResourceIds.Power,
                ChoiceDescriptor(
                    CapabilityIds.Scenario,
                    CapabilityRole.ScenarioMode,
                    DisplayKey.PerformanceProfile,
                    ["comfort", "green", "eco", "user", "sport"],
                    writable: false),
                _power,
                async token => Scenario((await powerCapability.ReadAsync(token).ConfigureAwait(false)).Scenario),
                ReadOnlyHandler),
            Register(
                ResourceIds.Fans,
                ChoiceDescriptor(
                    CapabilityIds.FanMode,
                    CapabilityRole.FanMode,
                    DisplayKey.FanMode,
                    ["automatic", "custom", "full-speed"],
                    writable: true),
                _fans,
                async token => FanMode(await fanCapability.ReadSnapshotAsync(token).ConfigureAwait(false)),
                (execution, token) => JournalCommandAsync(
                    ResourceIds.Fans,
                    ClawFirmwareIdentities.Wmi,
                    execution,
                    async commandToken => ClawRecoveryValues.Fans(
                        await fanCapability.ReadSnapshotAsync(commandToken).ConfigureAwait(false)),
                    (commandExecution, commandToken) => fanCapability.ApplyModeAsync(
                        commandExecution.Command,
                        commandExecution.Command.RequestedValue!.ChoiceValue!,
                        commandToken),
                    token)),
            RegisterFanCurve(CapabilityInstances.Left, fanCapability),
            RegisterFanCurve(CapabilityInstances.Right, fanCapability),
            Register(
                ResourceIds.Telemetry,
                IntegerDescriptor(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                    DisplayKey.FanLeft, 0, 10_000, CapabilityUnit.Rpm, writable: false,
                    CapabilityInstances.Left),
                _telemetry,
                async token => Integer((await fanCapability.ReadTelemetryAsync(token).ConfigureAwait(false)).LeftRpm),
                ReadOnlyHandler),
            Register(
                ResourceIds.Telemetry,
                IntegerDescriptor(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                    DisplayKey.FanRight, 0, 10_000, CapabilityUnit.Rpm, writable: false,
                    CapabilityInstances.Right),
                _telemetry,
                async token => Integer((await fanCapability.ReadTelemetryAsync(token).ConfigureAwait(false)).RightRpm),
                ReadOnlyHandler),
            Register(
                ResourceIds.Telemetry,
                IntegerDescriptor(CapabilityIds.Temperature, CapabilityRole.Telemetry,
                    DisplayKey.CpuTemperature, 0, 110, CapabilityUnit.Celsius, writable: false),
                _telemetry,
                async token => Integer((await fanCapability.ReadTelemetryAsync(token).ConfigureAwait(false)).TemperatureCelsius),
                ReadOnlyHandler),
            RegisterLightingBrightness(lightingCapability),
            RegisterLightingColor(CapabilityInstances.RightRing, "Right ring", lightingCapability),
            RegisterLightingColor(CapabilityInstances.LeftRing, "Left ring", lightingCapability),
            RegisterLightingColor(CapabilityInstances.Buttons, "Buttons", lightingCapability),
            Register(
                ResourceIds.Controller,
                BooleanDescriptor(CapabilityIds.Controller, CapabilityRole.ControllerSource,
                    DisplayKey.Controller, writable: false),
                _controller,
                _ => ValueTask.FromResult<CapabilityValue?>(
                    Boolean(_controller.State is ResourceState.Owned)),
                ReadOnlyHandler,
                firmwareKind: FirmwareKind.Mcu),
            Register(
                ResourceIds.Motion,
                BooleanDescriptor(CapabilityIds.Motion, CapabilityRole.MotionSource,
                    DisplayKey.Motion, writable: false),
                _motion,
                _ => ValueTask.FromResult<CapabilityValue?>(
                    Boolean(_motion.State is ResourceState.Owned)),
                ReadOnlyHandler,
                firmwareKind: FirmwareKind.None),
            Register(
                ResourceIds.Controller,
                BooleanDescriptor(CapabilityIds.Rumble, CapabilityRole.HapticSink,
                    DisplayKey.Rumble, writable: false),
                _controller,
                _ => ValueTask.FromResult<CapabilityValue?>(
                    Boolean(_controller.State is ResourceState.Owned)),
                ReadOnlyHandler,
                firmwareKind: FirmwareKind.Mcu),
        ];

        return new CapabilityRegistry(1, _deviceGeneration, registrations);
    }

    private CapabilityRegistration RegisterFanCurve(
        string instance,
        ClawA2VmFanCapability capability)
    {
        FanResource resource = _fans!;
        int channel = instance == CapabilityInstances.Left ? 1 : 2;
        CapabilityDescriptor descriptor = new()
        {
            CapabilityId = CapabilityIds.FanCurve,
            InstanceId = instance,
            Role = CapabilityRole.FanCurve,
            ValueKind = CapabilityValueKind.Curve,
            Display = new CapabilityDisplay
            {
                Key = instance == CapabilityInstances.Left ? DisplayKey.FanLeft : DisplayKey.FanRight,
            },
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile,
        };
        return Register(
            ResourceIds.Fans,
            descriptor,
            resource,
            async token =>
            {
                FanSnapshot snapshot = await capability.ReadSnapshotAsync(token).ConfigureAwait(false);
                return Curve(channel == 1 ? snapshot.Left : snapshot.Right);
            },
            (execution, token) => JournalCommandAsync(
                ResourceIds.Fans,
                ClawFirmwareIdentities.Wmi,
                execution,
                async commandToken => ClawRecoveryValues.Fans(
                    await capability.ReadSnapshotAsync(commandToken).ConfigureAwait(false)),
                (commandExecution, commandToken) => capability.ApplyCurveAsync(
                    commandExecution.Command,
                    channel,
                    commandExecution.Command.RequestedValue!.CurveValue,
                    commandToken),
                token));
    }

    private CapabilityRegistration RegisterLightingBrightness(ClawA2VmLightingCapability capability) =>
        Register(
            ResourceIds.Lighting,
            IntegerDescriptor(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
                DisplayKey.Brightness, 0, 100, CapabilityUnit.Percent, writable: true,
                persistence: CapabilityPersistence.DevicePersistent),
            _lighting!,
            async token => Integer((await capability.ReadAsync(token).ConfigureAwait(false)).Brightness),
            (execution, token) => capability.ApplyAsync(
                execution.Command,
                state => state with
                {
                    Brightness = execution.Command.RequestedValue!.IntegerValue!.Value,
                },
                token),
            firmwareKind: FirmwareKind.Mcu);

    private CapabilityRegistration RegisterLightingColor(
        string instance,
        string label,
        ClawA2VmLightingCapability capability)
    {
        CapabilityDescriptor descriptor = new()
        {
            CapabilityId = CapabilityIds.LightingColor,
            InstanceId = instance,
            Role = CapabilityRole.LightingZoneColor,
            ValueKind = CapabilityValueKind.Color,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label },
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent,
        };
        return Register(
            ResourceIds.Lighting,
            descriptor,
            _lighting!,
            async token =>
            {
                LightingState state = await capability.ReadAsync(token).ConfigureAwait(false);
                return Color(instance switch
                {
                    CapabilityInstances.RightRing => state.RightRingColor,
                    CapabilityInstances.LeftRing => state.LeftRingColor,
                    CapabilityInstances.Buttons => state.ButtonsColor,
                    _ => throw new InvalidOperationException("Unknown lighting zone."),
                });
            },
            (execution, token) => capability.ApplyAsync(
                execution.Command,
                state => instance switch
                {
                    CapabilityInstances.RightRing => state with
                    {
                        RightRingColor = execution.Command.RequestedValue!.ColorValue!.Value,
                    },
                    CapabilityInstances.LeftRing => state with
                    {
                        LeftRingColor = execution.Command.RequestedValue!.ColorValue!.Value,
                    },
                    CapabilityInstances.Buttons => state with
                    {
                        ButtonsColor = execution.Command.RequestedValue!.ColorValue!.Value,
                    },
                    _ => state,
                },
                token),
            firmwareKind: FirmwareKind.Mcu);
    }

    private CapabilityRegistration Register(
        string resourceId,
        CapabilityDescriptor descriptor,
        ClawResourceBase resource,
        Func<CancellationToken, ValueTask<CapabilityValue?>> readCurrent,
        PluginCommandHandler handler,
        FirmwareKind firmwareKind = FirmwareKind.Wmi) =>
        new(
            resourceId,
            descriptor,
            async token =>
            {
                ClawIdentityState identity = await _services.Identity.ReadAsync(token).ConfigureAwait(false);
                CapabilityValue? current = resource.State is ResourceState.Owned
                    && identity.ExactMachineMatch
                    && (firmwareKind switch
                    {
                        FirmwareKind.Wmi => identity.WmiFirmwareVerified,
                        FirmwareKind.Mcu => identity.McuFirmwareVerified,
                        _ => true,
                    })
                    ? await readCurrent(token).ConfigureAwait(false)
                    : null;
                return new PluginCommandSnapshot
                {
                    IdentityVerified = identity.ExactMachineMatch,
                    FirmwareVerified = firmwareKind switch
                    {
                        FirmwareKind.Wmi => identity.WmiFirmwareVerified,
                        FirmwareKind.Mcu => identity.McuFirmwareVerified,
                        _ => true,
                    },
                    ResourceState = resource.State,
                    DescriptorGeneration = _registry?.DescriptorSet.Generation ?? 1,
                    DeviceGeneration = _deviceGeneration,
                    OnAcPower = identity.OnAcPower,
                    CurrentValue = current,
                };
            },
            handler);

    private async ValueTask PublishCapabilityStatesAsync(CancellationToken cancellationToken)
    {
        if (_host is null || _registry is null)
        {
            return;
        }

        foreach (CapabilityDescriptor descriptor in _registry.DescriptorSet.Descriptors)
        {
            (ClawResourceBase resource, CapabilityValue? value) = CurrentState(descriptor);
            await _host.PublishCapabilityStateAsync(
                new CapabilityState
                {
                    CapabilityId = descriptor.CapabilityId,
                    InstanceId = descriptor.InstanceId,
                    Available = resource.State is ResourceState.Owned,
                    Reason = resource.Reason ?? ReasonFor(resource.State),
                    ObservedValue = value,
                    Quality = value is null
                        ? HardwareStateQuality.Unknown
                        : HardwareStateQuality.Observed,
                    ObservedAt = value is null ? null : DateTimeOffset.UtcNow,
                    DescriptorGeneration = _registry.DescriptorSet.Generation,
                    DeviceGeneration = _deviceGeneration,
                    HostGeneration = _hostGeneration,
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private (ClawResourceBase Resource, CapabilityValue? Value) CurrentState(CapabilityDescriptor descriptor)
    {
        if (descriptor.CapabilityId == CapabilityIds.PowerSustained)
        {
            return (_power!, _power!.LastObserved is { } value ? Integer(value.SustainedWatts) : null);
        }

        if (descriptor.CapabilityId == CapabilityIds.PowerBoost)
        {
            return (_power!, _power!.LastObserved is { } value ? Integer(value.BoostWatts) : null);
        }

        if (descriptor.CapabilityId == CapabilityIds.Scenario)
        {
            return (_power!, _power!.LastObserved is { } value ? Scenario(value.Scenario) : null);
        }

        if (descriptor.CapabilityId == CapabilityIds.FanMode)
        {
            return (_fans!, _fans!.LastObserved is { } value ? FanMode(value) : null);
        }

        if (descriptor.CapabilityId == CapabilityIds.FanCurve)
        {
            FanSnapshot? value = _fans!.LastObserved;
            return (_fans, value is null
                ? null
                : Curve(descriptor.InstanceId == CapabilityInstances.Left ? value.Left : value.Right));
        }

        if (descriptor.CapabilityId == CapabilityIds.FanRpm)
        {
            FanTelemetry? value = _telemetry!.LastTelemetry;
            return (_telemetry, value is null
                ? null
                : Integer(descriptor.InstanceId == CapabilityInstances.Left ? value.LeftRpm : value.RightRpm));
        }

        if (descriptor.CapabilityId == CapabilityIds.Temperature)
        {
            return (_telemetry!, _telemetry!.LastTelemetry is { } value
                ? Integer(value.TemperatureCelsius)
                : null);
        }

        if (descriptor.CapabilityId == CapabilityIds.LightingBrightness)
        {
            return (_lighting!, _lighting!.LastObserved is { } value ? Integer(value.Brightness) : null);
        }

        if (descriptor.CapabilityId == CapabilityIds.LightingColor)
        {
            LightingState? value = _lighting!.LastObserved;
            return (_lighting, value is null
                ? null
                : Color(descriptor.InstanceId switch
                {
                    CapabilityInstances.RightRing => value.RightRingColor,
                    CapabilityInstances.LeftRing => value.LeftRingColor,
                    CapabilityInstances.Buttons => value.ButtonsColor,
                    _ => 0,
                }));
        }

        if (descriptor.CapabilityId == CapabilityIds.Motion)
        {
            return (_motion!, Boolean(_motion!.State is ResourceState.Owned));
        }

        return (_controller!, Boolean(_controller!.State is ResourceState.Owned));
    }

    private async ValueTask PublishResourceStateAsync(
        ClawResourceBase resource,
        ResourceState state,
        CancellationToken cancellationToken,
        CapabilityReason? reason = null)
    {
        if (_host is null)
        {
            return;
        }

        await _host.PublishResourceStateAsync(
            new PluginResourceState
            {
                ResourceId = resource.ResourceId,
                State = state,
                Reason = reason ?? resource.Reason,
                DeviceGeneration = _deviceGeneration,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RefreshObservedAsync(
        string capabilityId,
        CancellationToken cancellationToken)
    {
        if (capabilityId is CapabilityIds.PowerSustained or CapabilityIds.PowerBoost)
        {
            await _power!.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (capabilityId is CapabilityIds.FanMode or CapabilityIds.FanCurve)
        {
            await _fans!.RefreshAsync(cancellationToken).ConfigureAwait(false);
            await _telemetry!.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (capabilityId is CapabilityIds.LightingBrightness or CapabilityIds.LightingColor)
        {
            await _lighting!.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<CapabilityCommandResult> JournalCommandAsync(
        string resourceId,
        string firmwareIdentity,
        PluginCommandExecution execution,
        Func<CancellationToken, ValueTask<CapabilityValue>> readOriginal,
        PluginCommandHandler apply,
        CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            throw new InvalidOperationException("The recovery journal is unavailable.");
        }

        RequireCommandWriteBudget(execution.Command.Deadline);
        CapabilityValue originalValue = await readOriginal(cancellationToken).ConfigureAwait(false);
        RecoveryJournalEntry entry = await _journal.BeginAsync(
            resourceId,
            execution.Command.CapabilityId,
            firmwareIdentity,
            originalValue,
            execution.Command.RequestedValue,
            cancellationToken).ConfigureAwait(false);
        RequireCommandWriteBudget(execution.Command.Deadline);
        entry = await _journal.MarkApplyingAsync(entry, cancellationToken).ConfigureAwait(false);
        CapabilityCommandResult result;
        try
        {
            result = await apply(execution, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A transport exception does not prove whether the firmware accepted a write. Keep the
            // exact pre-command value outstanding for recovery instead of abandoning Applying.
            _ = await _journal.MarkAppliedAsync(
                entry,
                execution.Command.RequestedValue ?? originalValue,
                verified: false,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _ = await _journal.CompleteApplicationAsync(entry, result, CancellationToken.None)
            .ConfigureAwait(false);
        if (result.Rollback is RollbackResult.RestoreFailed)
        {
            ClawResourceBase? resource = resourceId switch
            {
                ResourceIds.Power => _power,
                ResourceIds.Fans => _fans,
                _ => null,
            };
            if (resource is not null)
            {
                CapabilityReason reason = new(
                    CapabilityReasonCode.TransportFaulted,
                    "A command rollback failed; the resource is quarantined until reconciliation.");
                resource.Quarantine(reason);
                await PublishResourceStateAsync(
                    resource,
                    ResourceState.Faulted,
                    CancellationToken.None,
                    reason).ConfigureAwait(false);
            }
        }

        return result;
    }

    private async ValueTask ReconcileOutstandingAsync(
        IReadOnlyList<RecoveryJournalEntry> entries,
        ClawIdentityState identity,
        ClawA2VmPowerCapability powerCapability,
        ClawA2VmFanCapability fanCapability,
        CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            return;
        }

        foreach (RecoveryJournalEntry entry in entries.OrderByDescending(entry => entry.Sequence))
        {
            if (ResourceFor(entry.ResourceId)?.ReconciliationBlockReason is not null)
            {
                continue;
            }

            if (!string.Equals(entry.PackageId, PackageId, StringComparison.Ordinal)
                || !string.Equals(
                    entry.DeviceId,
                    ClawHardwareFacts.DeviceDefinitionId,
                    StringComparison.Ordinal))
            {
                BlockResource(entry.ResourceId, new CapabilityReason(
                    CapabilityReasonCode.GenerationChanged,
                    "The recovery entry belongs to another package or device definition."));
                continue;
            }

            string? currentFirmware = entry.ResourceId switch
            {
                ResourceIds.Power or ResourceIds.Fans when identity.WmiFirmwareVerified =>
                    ClawFirmwareIdentities.Wmi,
                ResourceIds.Controller when identity.McuFirmwareVerified =>
                    ClawFirmwareIdentities.Mcu,
                _ => null,
            };
            ReconciliationAction action = JournalReconciliation.Decide(entry, currentFirmware);
            if (action is ReconciliationAction.None)
            {
                continue;
            }

            if (action is not ReconciliationAction.Restore)
            {
                BlockResource(entry.ResourceId, new CapabilityReason(
                    action is ReconciliationAction.Quarantine
                        ? CapabilityReasonCode.TransportFaulted
                        : CapabilityReasonCode.FirmwareNotVerified,
                    "An outstanding recovery entry is not safe to restore automatically."));
                continue;
            }

            bool restored;
            bool restoreFailed = false;
            try
            {
                restored = entry.ResourceId switch
                {
                    ResourceIds.Power when ClawRecoveryValues.TryPower(
                        entry.OriginalValue,
                        out PowerPair? power) =>
                        await powerCapability.RestoreAsync(power!, cancellationToken).ConfigureAwait(false),
                    ResourceIds.Fans when ClawRecoveryValues.TryFans(
                        entry.OriginalValue,
                        out FanSnapshot? fans) =>
                        await fanCapability.RestoreAsync(fans!, cancellationToken).ConfigureAwait(false),
                    ResourceIds.Controller =>
                        await RestoreControllerJournalEntryAsync(entry, cancellationToken)
                            .ConfigureAwait(false),
                    _ => false,
                };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                restored = false;
                restoreFailed = true;
                _ = await _journal.CompleteExistingAsync(
                    entry,
                    JournalEntryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (restored)
            {
                _ = await _journal.CompleteExistingAsync(
                    entry,
                    JournalEntryStatus.RestoredVerified,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!restoreFailed)
            {
                _ = await _journal.CompleteExistingAsync(
                    entry,
                    JournalEntryStatus.RestoredUnverified,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!restored)
            {
                BlockResource(entry.ResourceId, new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "An outstanding hardware state could not be restored and verified."));
            }
        }
    }

    private async ValueTask<bool> RestoreControllerJournalEntryAsync(
        RecoveryJournalEntry entry,
        CancellationToken cancellationToken)
    {
        if (!ClawRecoveryValues.TryControllerMode(entry.OriginalValue, out ClawControllerMode mode))
        {
            return false;
        }

        ControllerTopology? current = await _services.Controller.DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null || string.IsNullOrWhiteSpace(current.PhysicalLocation))
        {
            return false;
        }

        if (current.Mode == mode)
        {
            return true;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        ControllerTopology restored = await _services.Mcu.SwitchModeAsync(
            mode,
            current.PhysicalLocation,
            deadline,
            cancellationToken).ConfigureAwait(false);
        return restored.Mode == mode
            && HidEndpointEnumerator.SamePhysicalLocation(
                restored.PhysicalLocation,
                current.PhysicalLocation);
    }

    private void BlockResource(string resourceId, CapabilityReason reason)
    {
        ClawResourceBase? resource = ResourceFor(resourceId);
        if (resource is not null)
        {
            resource.ReconciliationBlockReason = reason;
        }
    }

    private ClawResourceBase? ResourceFor(string resourceId) => resourceId switch
    {
        ResourceIds.Power => _power,
        ResourceIds.Fans => _fans,
        ResourceIds.Controller => _controller,
        _ => null,
    };

    private static void RequireCommandWriteBudget(DateTimeOffset deadline)
    {
        if (deadline - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(2))
        {
            throw new OperationCanceledException(
                "Insufficient command budget for a journalled hardware write.");
        }
    }

    private void Remember(string key, CapabilityCommandResult result)
    {
        lock (_commandGate)
        {
            if (_idempotency.ContainsKey(key))
            {
                return;
            }

            _idempotency.Add(key, result);
            _idempotencyOrder.Enqueue(key);
            while (_idempotencyOrder.Count > 128)
            {
                _idempotency.Remove(_idempotencyOrder.Dequeue());
            }
        }
    }

    private PluginResourceOperationContext OperationContext(DateTimeOffset deadline) =>
        new(_hostGeneration, _deviceGeneration, deadline);

    private static IReadOnlyList<OemControlDescriptor> CreateOemControls() =>
    [
        Oem("oem1", "Claw button", OemControlPlacement.Front, supportsLongPress: false,
            requiresController: false),
        Oem("oem2", "Quick Settings", OemControlPlacement.Front, supportsLongPress: true,
            requiresController: false),
        Oem("oem3", "M1", OemControlPlacement.Rear, supportsLongPress: false,
            requiresController: true),
        Oem("oem4", "M2", OemControlPlacement.Rear, supportsLongPress: false,
            requiresController: true),
    ];

    private static OemControlDescriptor Oem(
        string id,
        string label,
        OemControlPlacement placement,
        bool supportsLongPress,
        bool requiresController) => new()
        {
            ControlId = id,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label },
            Placement = placement,
            SupportsLongPress = supportsLongPress,
            RequiresControllerAcquisition = requiresController,
        };

    private static CapabilityDescriptor IntegerDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        int minimum,
        int maximum,
        CapabilityUnit unit,
        bool writable,
        string? instance = null,
        CapabilityPersistence persistence = CapabilityPersistence.Volatile) => new()
        {
            CapabilityId = id,
            InstanceId = instance,
            Role = role,
            ValueKind = CapabilityValueKind.Integer,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Minimum = minimum,
            Maximum = maximum,
            Step = 1,
            Unit = unit,
            Persistence = persistence,
        };

    private static CapabilityDescriptor ChoiceDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        IReadOnlyList<string> choices,
        bool writable) => new()
        {
            CapabilityId = id,
            Role = role,
            ValueKind = CapabilityValueKind.Choice,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Choices = choices.Select(choice => new CapabilityChoice(
                choice,
                new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = choice })).ToArray(),
            Persistence = CapabilityPersistence.Volatile,
        };

    private static CapabilityDescriptor BooleanDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        bool writable) => new()
        {
            CapabilityId = id,
            Role = role,
            ValueKind = CapabilityValueKind.Boolean,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Persistence = CapabilityPersistence.Volatile,
        };

    private static CapabilityValue Integer(int value) => new()
    {
        Kind = CapabilityValueKind.Integer,
        IntegerValue = value,
    };

    private static CapabilityValue Boolean(bool value) => new()
    {
        Kind = CapabilityValueKind.Boolean,
        BooleanValue = value,
    };

    private static CapabilityValue Color(int value) => new()
    {
        Kind = CapabilityValueKind.Color,
        ColorValue = value,
    };

    private static CapabilityValue Curve(FanTable table) => new()
    {
        Kind = CapabilityValueKind.Curve,
        CurveValue = ClawA2VmFanCapability.DecodeCurve(table),
    };

    private static CapabilityValue Scenario(byte raw) => new()
    {
        Kind = CapabilityValueKind.Choice,
        ChoiceValue = (raw & 0x3F) switch
        {
            0 => "comfort",
            1 => "green",
            2 => "eco",
            3 => "user",
            4 => "sport",
            _ => "unknown",
        },
    };

    private static CapabilityValue FanMode(FanSnapshot snapshot) => new()
    {
        Kind = CapabilityValueKind.Choice,
        ChoiceValue = (snapshot.CustomFlag & 0x80) != 0
            ? "custom"
            : (snapshot.FullSpeedFlag & 0x80) != 0
                ? "full-speed"
                : "automatic",
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

    private static ValueTask<CapabilityCommandResult> ReadOnlyHandler(
        PluginCommandExecution execution,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Rejected(
            execution.Command,
            CapabilityReasonCode.Unsupported,
            "This capability is read-only."));
    }

    private static CapabilityReason? ReasonFor(ResourceState state) => state switch
    {
        ResourceState.Owned => null,
        ResourceState.Passive => new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing),
        ResourceState.Degraded or ResourceState.Faulted or ResourceState.ReleasedUnverified =>
            new CapabilityReason(CapabilityReasonCode.TransportFaulted),
        ResourceState.Releasing => new CapabilityReason(CapabilityReasonCode.Quiescing),
        _ => new CapabilityReason(CapabilityReasonCode.ResourceReleased),
    };

    private static ClawHardwareServices CreateWindowsServices()
    {
        MsiWmiPlatform wmi = new();
        return new ClawHardwareServices(
            new WindowsClawIdentityReader(wmi),
            wmi,
            new MsiOemEventSource(),
            new WindowsClawMcuTransport(),
            new WindowsClawControllerSource(),
            new WindowsClawMotionSource(),
            new FirmwareChordSuppressor());
    }

    private enum FirmwareKind
    {
        None,
        Wmi,
        Mcu,
    }
}
