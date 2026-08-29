using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>The exact-device plugin for the MSI Claw 8 AI+ A2VM board <c>MS-1T52</c>.</summary>
public sealed class Claw8A2VmPlugin : IDevicePlugin
{
    private readonly ClawHardwareServices _services;
    private readonly SemaphoreSlim _commandSerializer = new(1, 1);
    private IPluginHostAdapter? _host;
    private CapabilityDescriptorSet? _descriptorSet;
    private IReadOnlyList<ClawServiceStatus> _serviceStatuses = [];
    private OemEventService? _oem;
    private PowerService? _power;
    private FanService? _fans;
    private TelemetryService? _telemetry;
    private LightingService? _lighting;
    private MotionService? _motion;
    private ControllerService? _controller;
    private ChordSuppressorService? _suppressor;
    private ClawRecoveryJournal? _journal;
    private ClawA2VmPowerCapability? _powerCapability;
    private ClawA2VmFanCapability? _fanCapability;
    private ClawA2VmLightingCapability? _lightingCapability;
    private long _cycleGeneration;
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
    public async ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context,
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

        if (context.CycleGeneration != context.Host.CycleGeneration)
        {
            throw new InvalidOperationException("DeviceHost supplied an inconsistent cycle generation.");
        }

        ClawIdentityState identity = await _services.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch)
        {
            throw new InvalidOperationException("The exact MS-1T52 activation gate no longer matches.");
        }

        _host = context.Host;
        _cycleGeneration = context.CycleGeneration;
        _quiescing = false;

        var powerCapability = new ClawA2VmPowerCapability(_services.Wmi);
        var fanCapability = new ClawA2VmFanCapability(_services.Wmi);
        var lightingCapability = new ClawA2VmLightingCapability(_services.Mcu);
        _powerCapability = powerCapability;
        _fanCapability = fanCapability;
        _lightingCapability = lightingCapability;
        _journal = await ClawRecoveryJournal.OpenAsync(context.StateDirectory, cancellationToken)
            .ConfigureAwait(false);
        _oem = new OemEventService(_services.OemEvents, context.Host);
        _power = new PowerService(_services.Identity, powerCapability, _journal);
        _fans = new FanService(_services.Identity, fanCapability, _journal);
        _telemetry = new TelemetryService(_services.Identity, fanCapability);
        _lighting = new LightingService(_services.Identity, _services.Mcu, lightingCapability);
        _motion = new MotionService(_services.Motion);
        _controller = new ControllerService(
            _services.Identity,
            _services.Mcu,
            _services.Controller,
            _motion,
            context.Host,
            _journal)
        {
            Enabled = context.ControllerManagementEnabled,
        };
        _suppressor = new ChordSuppressorService(_services.ChordSuppressor, _oem);

        _serviceStatuses =
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
        BuildCapabilitySurface();

        if (_journal.FailureReason is { } journalFailure)
        {
            BlockService(ServiceIds.Power, journalFailure);
            BlockService(ServiceIds.Fans, journalFailure);
            BlockService(ServiceIds.Controller, journalFailure);
        }
        else
        {
            await ReconcileOutstandingAsync(
                _journal.OutstandingEntries,
                identity,
                powerCapability,
                fanCapability,
                cancellationToken).ConfigureAwait(false);
        }

        await context.Host.PublishDescriptorsAsync(_descriptorSet!, cancellationToken)
            .ConfigureAwait(false);
        await context.Host.PublishOemControlsAsync(CreateOemControls(), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await StartServicesAsync(
                new ClawCycleContext(
                    context.CycleGeneration,
                    DateTimeOffset.UtcNow.AddSeconds(15)),
                cancellationToken).ConfigureAwait(false);
            _active = true;
            await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);
            return CurrentStartResult();
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
        if (!_active || _descriptorSet is null || _quiescing)
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
            if (!_active || _descriptorSet is null || _quiescing)
            {
                return Rejected(
                    command,
                    CapabilityReasonCode.Quiescing,
                    "The Claw device cycle started quiescing before this command could run.");
            }

            CapabilityCommandResult result = await ExecuteBoundCommandAsync(command, cancellationToken)
                .ConfigureAwait(false);

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
        if (_serviceStatuses.Count == 0)
        {
            return;
        }

        _quiescing = true;
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SuspendServicesAsync(
                OperationContext(context.Deadline),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<PluginStartResult> ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_serviceStatuses.Count == 0)
        {
            return new PluginStartResult
            {
                State = PluginOperationalState.Passive,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.ResourceReleased,
                    "The Claw services have not been started."),
            };
        }

        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cycleGeneration = context.CycleGeneration;
            if (_journal is not null
                && await _journal.CheckHealthAsync(cancellationToken).ConfigureAwait(false)
                    is { } journalFailure)
            {
                BlockService(ServiceIds.Power, journalFailure);
                BlockService(ServiceIds.Fans, journalFailure);
                BlockService(ServiceIds.Controller, journalFailure);
            }

            await ResumeServicesAsync(
                OperationContext(context.Deadline),
                cancellationToken).ConfigureAwait(false);
            if (_host is null || _powerCapability is null || _fanCapability is null
                || _lightingCapability is null)
            {
                throw new InvalidOperationException("Resume cannot rebuild the capability surface.");
            }

            BuildCapabilitySurface();
            await _host.PublishDescriptorsAsync(_descriptorSet!, cancellationToken)
                .ConfigureAwait(false);
            _quiescing = false;
            await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);
            return CurrentStartResult();
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
    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cycle"] = _active ? "started" : "stopped",
            ["recovery"] = _journal?.FailureReason is null ? "healthy" : "blocked",
        };
        foreach (ClawServiceStatus service in _serviceStatuses)
        {
            values[service.ServiceId] = service.State.ToString();
        }

        return ValueTask.FromResult(new PluginDiagnostics { Values = values });
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
            _cycleGeneration = context.CycleGeneration;
            if (context.Enabled)
            {
                ClawServiceResult result = await _controller.AcquireAsync(
                    OperationContext(context.Deadline),
                    cancellationToken).ConfigureAwait(false);
                await ApplyServiceLifecycleStateAsync(_controller, result, cancellationToken)
                    .ConfigureAwait(false);
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
        await ApplyServiceLifecycleStateAsync(
            _controller,
            new ClawServiceResult(
                result is ControllerHandoffResult.ReleasedVerified
                    ? ClawServiceState.Idle
                    : ClawServiceState.ReleasedUnverified,
                _controller.Reason),
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
    public async ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _quiescing = true;
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_serviceStatuses.Count > 0)
            {
                await StopServicesAsync(OperationContext(context.Deadline), cancellationToken)
                    .ConfigureAwait(false);
            }

            PluginStopResult result = CurrentStopResult();
            _active = false;
            _descriptorSet = null;
            _serviceStatuses = [];
            if (_journal is not null)
            {
                await _journal.DisposeAsync().ConfigureAwait(false);
                _journal = null;
            }

            return result;
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
            await StopAsync(
                new PluginStopContext(
                    PluginStopReason.WsgmExiting,
                    DateTimeOffset.UtcNow.AddSeconds(12)),
                CancellationToken.None).ConfigureAwait(false);
        }

        await _services.ChordSuppressor.DisposeAsync().ConfigureAwait(false);
        await _services.Motion.DisposeAsync().ConfigureAwait(false);
        await _services.Controller.DisposeAsync().ConfigureAwait(false);
        await _services.Mcu.DisposeAsync().ConfigureAwait(false);
        await _services.OemEvents.DisposeAsync().ConfigureAwait(false);
        await _services.Wmi.DisposeAsync().ConfigureAwait(false);
        if (_journal is not null)
        {
            await _journal.DisposeAsync().ConfigureAwait(false);
            _journal = null;
        }

        _commandSerializer.Dispose();
    }

    private async ValueTask StartServicesAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        RequireServices();
        try
        {
            await StartOneAsync(_oem!, () => _oem!.AcquireAsync(context, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            await StartOneAsync(_power!, () => _power!.AcquireAsync(context, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            await StartOneAsync(_fans!, () => _fans!.AcquireAsync(context, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            await StartOneAsync(
                _telemetry!,
                () => _telemetry!.AcquireAsync(context, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await StartOneAsync(
                _lighting!,
                () => _lighting!.AcquireAsync(context, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await StartOneAsync(_motion!, () => _motion!.AcquireAsync(context, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            await StartOneAsync(
                _controller!,
                () => _controller!.AcquireAsync(context, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await StartOneAsync(
                _suppressor!,
                () => _suppressor!.AcquireAsync(context, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopServicesAsync(context, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask SuspendServicesAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        RequireServices();
        await OperateOneAsync(_oem!, () => _oem!.SuspendAsync(context, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        await OperateOneAsync(
            _motion!,
            () => _motion!.SuspendAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await OperateOneAsync(
            _controller!,
            () => _controller!.SuspendAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await OperateOneAsync(
            _suppressor!,
            () => _suppressor!.SuspendAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ResumeServicesAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        RequireServices();
        await StartOneAsync(_oem!, () => _oem!.ResumeAsync(context, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        await StartOneAsync(_power!, () => _power!.AcquireAsync(context, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        await StartOneAsync(_fans!, () => _fans!.AcquireAsync(context, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        await StartOneAsync(
            _telemetry!,
            () => _telemetry!.AcquireAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await StartOneAsync(
            _lighting!,
            () => _lighting!.AcquireAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await StartOneAsync(_motion!, () => _motion!.ResumeAsync(context, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        await StartOneAsync(
            _controller!,
            () => _controller!.ResumeAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await StartOneAsync(
            _suppressor!,
            () => _suppressor!.ResumeAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask StopServicesAsync(
        ClawCycleContext context,
        CancellationToken cancellationToken)
    {
        RequireServices();
        await StopOneAsync(
            _suppressor!,
            () => _suppressor!.ReleaseAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await StopOneAsync(
            _controller!,
            () => _controller!.ReleaseAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await StopOneAsync(_motion!, () => _motion!.ReleaseAsync(context, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        await StopOneAsync(
            _lighting!,
            () => _lighting!.ReleaseAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await StopOneAsync(
            _telemetry!,
            () => _telemetry!.ReleaseAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await StopOneAsync(_fans!, () => _fans!.ReleaseAsync(context, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        await StopOneAsync(_power!, () => _power!.ReleaseAsync(context, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        await StopOneAsync(_oem!, () => _oem!.ReleaseAsync(context, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask StartOneAsync(
        ClawServiceStatus service,
        Func<ValueTask<ClawServiceResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClawServiceResult result = await InvokeServiceAsync(service, operation, cancellationToken)
            .ConfigureAwait(false);
        service.ApplyResult(NormalizeAcquisitionResult(result));
    }

    private static async ValueTask OperateOneAsync(
        ClawServiceStatus service,
        Func<ValueTask<ClawServiceResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClawServiceResult result = await InvokeServiceAsync(service, operation, cancellationToken)
            .ConfigureAwait(false);
        service.ApplyResult(result);
    }

    private static async ValueTask StopOneAsync(
        ClawServiceStatus service,
        Func<ValueTask<ClawServiceResult>> operation,
        CancellationToken cancellationToken)
    {
        ClawServiceResult result;
        try
        {
            result = await InvokeServiceAsync(service, operation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new ClawServiceResult(
                ClawServiceState.ReleasedUnverified,
                new CapabilityReason(
                    CapabilityReasonCode.Quiescing,
                    $"Release of service '{service.ServiceId}' exceeded its deadline."));
        }

        service.ApplyResult(NormalizeReleaseResult(result));
    }

    private static async ValueTask<ClawServiceResult> InvokeServiceAsync(
        ClawServiceStatus service,
        Func<ValueTask<ClawServiceResult>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ClawServiceResult(
                ClawServiceState.Faulted,
                new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Service '{service.ServiceId}' operation failed: {ex.GetType().Name}."));
        }
    }

    private static ValueTask ApplyServiceLifecycleStateAsync(
        ClawServiceStatus service,
        ClawServiceResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (result.State is not ClawServiceState.Acquiring and not ClawServiceState.Releasing)
        {
            service.ApplyResult(result);
        }

        return ValueTask.CompletedTask;
    }

    private void RequireServices()
    {
        if (_oem is null || _power is null || _fans is null || _telemetry is null
            || _lighting is null || _motion is null || _controller is null || _suppressor is null)
        {
            throw new InvalidOperationException("Claw services have not been created.");
        }
    }

    private static ClawServiceResult NormalizeAcquisitionResult(
        ClawServiceResult result) => result.State switch
        {
            ClawServiceState.Owned or ClawServiceState.Passive or ClawServiceState.Degraded
                or ClawServiceState.Faulted => result,
            _ => new ClawServiceResult(
                ClawServiceState.Faulted,
                new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Acquisition returned invalid state {result.State}.")),
        };

    private static ClawServiceResult NormalizeReleaseResult(
        ClawServiceResult result) => result.State switch
        {
            ClawServiceState.Idle or ClawServiceState.ReleasedUnverified or ClawServiceState.Faulted => result,
            _ => new ClawServiceResult(
                ClawServiceState.ReleasedUnverified,
                new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Release returned invalid state {result.State}.")),
        };

    private void BuildCapabilitySurface()
    {
        if (_power is null || _fans is null || _telemetry is null || _lighting is null
            || _motion is null || _controller is null)
        {
            throw new InvalidOperationException("Services must exist before descriptors are built.");
        }

        IReadOnlyList<CapabilityDescriptor> descriptors =
        [
            IntegerDescriptor(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit,
                DisplayKey.SustainedPowerLimit, 8, 30, CapabilityUnit.Watt, writable: true),
            IntegerDescriptor(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit,
                DisplayKey.BoostPowerLimit, 8, 37, CapabilityUnit.Watt, writable: true),
            ChoiceDescriptor(
                CapabilityIds.Scenario,
                CapabilityRole.ScenarioMode,
                DisplayKey.PerformanceProfile,
                ["comfort", "green", "eco", "user", "sport"],
                writable: false),
            ChoiceDescriptor(
                CapabilityIds.FanMode,
                CapabilityRole.FanMode,
                DisplayKey.FanMode,
                ["automatic", "custom", "full-speed"],
                writable: true),
            FanCurveDescriptor(CapabilityInstances.Left),
            FanCurveDescriptor(CapabilityInstances.Right),
            IntegerDescriptor(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                DisplayKey.FanLeft, 0, 10_000, CapabilityUnit.Rpm, writable: false,
                CapabilityInstances.Left),
            IntegerDescriptor(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                DisplayKey.FanRight, 0, 10_000, CapabilityUnit.Rpm, writable: false,
                CapabilityInstances.Right),
            IntegerDescriptor(CapabilityIds.Temperature, CapabilityRole.Telemetry,
                DisplayKey.CpuTemperature, 0, 110, CapabilityUnit.Celsius, writable: false),
            IntegerDescriptor(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
                DisplayKey.Brightness, 0, 100, CapabilityUnit.Percent, writable: true,
                persistence: CapabilityPersistence.DevicePersistent),
            LightingColorDescriptor(CapabilityInstances.RightRing, "Right ring"),
            LightingColorDescriptor(CapabilityInstances.LeftRing, "Left ring"),
            LightingColorDescriptor(CapabilityInstances.Buttons, "Buttons"),
            BooleanDescriptor(CapabilityIds.Controller, CapabilityRole.ControllerSource,
                DisplayKey.Controller, writable: false),
            BooleanDescriptor(CapabilityIds.Motion, CapabilityRole.MotionSource,
                DisplayKey.Motion, writable: false),
            BooleanDescriptor(CapabilityIds.Rumble, CapabilityRole.HapticSink,
                DisplayKey.Rumble, writable: false),
        ];

        EnsureUniqueCapabilityKeys(descriptors);
        _descriptorSet = new CapabilityDescriptorSet
        {
            Generation = 1,
            CycleGeneration = _cycleGeneration,
            Descriptors = descriptors,
        };
    }

    private async ValueTask<CapabilityCommandResult> ExecuteBoundCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        CapabilityDescriptor? descriptor = _descriptorSet?.Descriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.CapabilityId, command.CapabilityId, StringComparison.Ordinal)
            && string.Equals(candidate.InstanceId, command.InstanceId, StringComparison.Ordinal));
        ClawServiceStatus? service = ServiceForCapability(command.CapabilityId);
        if (descriptor is null || service is null)
        {
            return Rejected(
                command,
                CapabilityReasonCode.Unsupported,
                $"Capability '{CapabilityKey(command.CapabilityId, command.InstanceId)}' is not available.");
        }

        ClawIdentityState identity;
        try
        {
            identity = await _services.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (service.State is ClawServiceState.Owned
                && identity.ExactMachineMatch
                && FirmwareVerified(identity, FirmwareForCapability(command.CapabilityId)))
            {
                await RefreshObservedAsync(command.CapabilityId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Rejected(
                command,
                CapabilityReasonCode.Quiescing,
                "Command was cancelled before hardware application began.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Rejected(
                command,
                CapabilityReasonCode.TransportFaulted,
                $"Current-state revalidation failed: {ex.GetType().Name}.");
        }

        CapabilityReason? refusal = RefusalFor(
            service,
            FirmwareForCapability(command.CapabilityId),
            identity);
        refusal ??= ValidateCommand(command, descriptor, identity.OnAcPower);
        if (refusal is not null)
        {
            return Rejected(command, refusal);
        }

        CapabilityCommandResult result;
        try
        {
            result = await ApplyCapabilityCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Indeterminate(
                command,
                "Command was cancelled after hardware application began.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Indeterminate(
                command,
                $"Capability handler failed after admission: {ex.GetType().Name}.");
        }

        return NormalizeCommandResult(command, result);
    }

    private ValueTask<CapabilityCommandResult> ApplyCapabilityCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        ClawA2VmPowerCapability power = _powerCapability
            ?? throw new InvalidOperationException("The power capability is unavailable.");
        ClawA2VmFanCapability fans = _fanCapability
            ?? throw new InvalidOperationException("The fan capability is unavailable.");
        ClawA2VmLightingCapability lighting = _lightingCapability
            ?? throw new InvalidOperationException("The lighting capability is unavailable.");

        return command.CapabilityId switch
        {
            CapabilityIds.PowerSustained => JournalCommandAsync(
                ServiceIds.Power,
                ClawFirmwareIdentities.Wmi,
                command,
                async token => ClawRecoveryValues.Power(
                    await power.ReadAsync(token).ConfigureAwait(false)),
                (journalCommand, token) => power.ApplySustainedAsync(
                    journalCommand,
                    journalCommand.RequestedValue!.IntegerValue!.Value,
                    token),
                cancellationToken),
            CapabilityIds.PowerBoost => JournalCommandAsync(
                ServiceIds.Power,
                ClawFirmwareIdentities.Wmi,
                command,
                async token => ClawRecoveryValues.Power(
                    await power.ReadAsync(token).ConfigureAwait(false)),
                (journalCommand, token) => power.ApplyBoostAsync(
                    journalCommand,
                    journalCommand.RequestedValue!.IntegerValue!.Value,
                    token),
                cancellationToken),
            CapabilityIds.FanMode => JournalCommandAsync(
                ServiceIds.Fans,
                ClawFirmwareIdentities.Wmi,
                command,
                async token => ClawRecoveryValues.Fans(
                    await fans.ReadSnapshotAsync(token).ConfigureAwait(false)),
                (journalCommand, token) => fans.ApplyModeAsync(
                    journalCommand,
                    journalCommand.RequestedValue!.ChoiceValue!,
                    token),
                cancellationToken),
            CapabilityIds.FanCurve => ApplyFanCurveCommandAsync(command, fans, cancellationToken),
            CapabilityIds.LightingBrightness => lighting.ApplyAsync(
                command,
                state => state with
                {
                    Brightness = command.RequestedValue!.IntegerValue!.Value,
                },
                cancellationToken),
            CapabilityIds.LightingColor => lighting.ApplyAsync(
                command,
                state => command.InstanceId switch
                {
                    CapabilityInstances.RightRing => state with
                    {
                        RightRingColor = command.RequestedValue!.ColorValue!.Value,
                    },
                    CapabilityInstances.LeftRing => state with
                    {
                        LeftRingColor = command.RequestedValue!.ColorValue!.Value,
                    },
                    CapabilityInstances.Buttons => state with
                    {
                        ButtonsColor = command.RequestedValue!.ColorValue!.Value,
                    },
                    _ => state,
                },
                cancellationToken),
            _ => ReadOnlyHandler(command, cancellationToken),
        };
    }

    private ValueTask<CapabilityCommandResult> ApplyFanCurveCommandAsync(
        CapabilityCommand command,
        ClawA2VmFanCapability fans,
        CancellationToken cancellationToken)
    {
        int channel = command.InstanceId == CapabilityInstances.Left ? 1 : 2;
        return JournalCommandAsync(
            ServiceIds.Fans,
            ClawFirmwareIdentities.Wmi,
            command,
            async token => ClawRecoveryValues.Fans(
                await fans.ReadSnapshotAsync(token).ConfigureAwait(false)),
            (journalCommand, token) => fans.ApplyCurveAsync(
                journalCommand,
                channel,
                journalCommand.RequestedValue!.CurveValue,
                token),
            cancellationToken);
    }

    private ClawServiceStatus? ServiceForCapability(string capabilityId) => capabilityId switch
    {
        CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario => _power,
        CapabilityIds.FanMode or CapabilityIds.FanCurve => _fans,
        CapabilityIds.FanRpm or CapabilityIds.Temperature => _telemetry,
        CapabilityIds.LightingBrightness or CapabilityIds.LightingColor => _lighting,
        CapabilityIds.Controller or CapabilityIds.Rumble => _controller,
        CapabilityIds.Motion => _motion,
        _ => null,
    };

    private static FirmwareKind FirmwareForCapability(string capabilityId) => capabilityId switch
    {
        CapabilityIds.LightingBrightness or CapabilityIds.LightingColor
            or CapabilityIds.Controller or CapabilityIds.Rumble => FirmwareKind.Mcu,
        CapabilityIds.Motion => FirmwareKind.None,
        _ => FirmwareKind.Wmi,
    };

    private static CapabilityReason? RefusalFor(
        ClawServiceStatus service,
        FirmwareKind firmwareKind,
        ClawIdentityState identity)
    {
        if (!identity.ExactMachineMatch)
        {
            return new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "Exact device identity no longer matches the Claw implementation.",
                Retryable: true);
        }

        if (!FirmwareVerified(identity, firmwareKind))
        {
            return new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "Current firmware is outside the Claw implementation's verified gate.");
        }

        return service.State switch
        {
            ClawServiceState.Owned => null,
            ClawServiceState.Passive => new CapabilityReason(
                CapabilityReasonCode.ResourceConflict,
                "The device service is passive or held by another owner.", Retryable: true),
            ClawServiceState.Releasing => new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "The device service is being released."),
            ClawServiceState.Degraded => new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The device service is degraded and cannot accept commands."),
            ClawServiceState.Faulted or ClawServiceState.ReleasedUnverified => new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The device service is faulted or its release could not be verified."),
            _ => new CapabilityReason(
                CapabilityReasonCode.ResourceReleased,
                "The plugin does not currently own this device service.", Retryable: true),
        };
    }

    private CapabilityReason? ValidateCommand(
        CapabilityCommand command,
        CapabilityDescriptor descriptor,
        bool onAcPower)
    {
        if (_descriptorSet is null
            || command.ExpectedDescriptorGeneration != _descriptorSet.Generation
            || command.ExpectedCycleGeneration != _cycleGeneration)
        {
            return new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "Command targets a descriptor or device generation that is no longer current.",
                Retryable: true);
        }

        if (command.Deadline <= DateTimeOffset.UtcNow)
        {
            return new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "Command deadline passed before it could be applied.",
                Retryable: true);
        }

        if (onAcPower ? !descriptor.AvailableOnAc : !descriptor.AvailableOnDc)
        {
            return new CapabilityReason(
                CapabilityReasonCode.UnavailableOnPowerSource,
                onAcPower
                    ? "Capability is unavailable on AC power."
                    : "Capability is unavailable on battery power.");
        }

        if (command.RequestedValue is null)
        {
            return descriptor.SupportsAction
                ? null
                : new CapabilityReason(
                    CapabilityReasonCode.Unsupported,
                    "Capability does not support being invoked as an action.");
        }

        if (!descriptor.SupportsWrite)
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported, "Capability is read-only.");
        }

        CapabilityValue value = command.RequestedValue;
        if (value.Kind != descriptor.ValueKind)
        {
            return new CapabilityReason(
                CapabilityReasonCode.Unsupported,
                $"Value kind {value.Kind} does not match descriptor kind {descriptor.ValueKind}.");
        }

        return ValidateCommandValue(value, descriptor);
    }

    private static CapabilityReason? ValidateCommandValue(
        CapabilityValue value,
        CapabilityDescriptor descriptor)
    {
        switch (descriptor.ValueKind)
        {
            case CapabilityValueKind.Integer:
                if (value.IntegerValue is not { } integer)
                {
                    return ValueOutOfRange("No integer value was supplied.");
                }

                if (descriptor.Minimum is { } minimum && integer < minimum)
                {
                    return ValueOutOfRange($"{integer} is below the minimum of {minimum}.");
                }

                if (descriptor.Maximum is { } maximum && integer > maximum)
                {
                    return ValueOutOfRange($"{integer} is above the maximum of {maximum}.");
                }

                if (descriptor.Step is { } step and > 0)
                {
                    int origin = descriptor.Minimum ?? 0;
                    if ((integer - origin) % step != 0)
                    {
                        return ValueOutOfRange(
                            $"{integer} is not on the {step} step boundary from {origin}.");
                    }
                }

                return null;

            case CapabilityValueKind.Choice:
                if (value.ChoiceValue is not { Length: > 0 } choice)
                {
                    return ValueOutOfRange("No choice was supplied.");
                }

                return descriptor.Choices.Any(candidate => string.Equals(
                    candidate.Value,
                    choice,
                    StringComparison.Ordinal))
                    ? null
                    : ValueOutOfRange($"'{choice}' is not one of the declared options.");

            case CapabilityValueKind.Boolean:
                return value.BooleanValue is not null
                    ? null
                    : ValueOutOfRange("No boolean value was supplied.");

            case CapabilityValueKind.Color:
                if (value.ColorValue is not { } color)
                {
                    return ValueOutOfRange("No colour was supplied.");
                }

                return color is >= 0 and <= 0xFFFFFF
                    ? null
                    : ValueOutOfRange("Colour must be 24-bit RGB.");

            case CapabilityValueKind.Curve:
                if (value.CurveValue.Count == 0)
                {
                    return ValueOutOfRange("Curve has no points.");
                }

                for (int index = 1; index < value.CurveValue.Count; index++)
                {
                    if (value.CurveValue[index].Input <= value.CurveValue[index - 1].Input)
                    {
                        return ValueOutOfRange(
                            "Curve points must be strictly increasing in input.");
                    }
                }

                return null;

            default:
                return new CapabilityReason(
                    CapabilityReasonCode.Unsupported,
                    $"Value kind {descriptor.ValueKind} carries no value.");
        }
    }

    private static CapabilityReason ValueOutOfRange(string detail) =>
        new(CapabilityReasonCode.ValueOutOfRange, detail);

    private static bool FirmwareVerified(ClawIdentityState identity, FirmwareKind kind) => kind switch
    {
        FirmwareKind.Wmi => identity.WmiFirmwareVerified,
        FirmwareKind.Mcu => identity.McuFirmwareVerified,
        _ => true,
    };

    private static CapabilityCommandResult NormalizeCommandResult(
        CapabilityCommand command,
        CapabilityCommandResult result)
    {
        if (result.CommandId != command.CommandId)
        {
            return Indeterminate(command, "Capability handler returned a result for another command.");
        }

        if (result.Outcome is CommandOutcome.AppliedVerified && result.ReadbackValue is null)
        {
            return result with
            {
                Outcome = CommandOutcome.AppliedUnverified,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "Handler claimed verified application without readback evidence."),
            };
        }

        return result.Outcome is not CommandOutcome.AppliedVerified && result.ReadbackValue is not null
            ? result with { ReadbackValue = null }
            : result;
    }

    private static void EnsureUniqueCapabilityKeys(IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (CapabilityDescriptor descriptor in descriptors)
        {
            string key = CapabilityKey(
                descriptor.CapabilityId,
                descriptor.InstanceId);
            if (!keys.Add(key))
            {
                throw new InvalidOperationException($"Capability '{key}' is registered more than once.");
            }
        }
    }

    private static string CapabilityKey(string capabilityId, string? instanceId) =>
        instanceId is null ? capabilityId : $"{capabilityId}/{instanceId}";

    private static CapabilityDescriptor FanCurveDescriptor(string instance) => new()
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

    private static CapabilityDescriptor LightingColorDescriptor(string instance, string label) => new()
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

    private async ValueTask PublishCapabilityStatesAsync(CancellationToken cancellationToken)
    {
        if (_host is null || _descriptorSet is null)
        {
            return;
        }

        foreach (CapabilityDescriptor descriptor in _descriptorSet.Descriptors)
        {
            ClawServiceStatus? service = ServiceForCapability(descriptor.CapabilityId);
            if (service is null)
            {
                continue;
            }

            CapabilityValue? value = CurrentState(descriptor);
            await _host.PublishCapabilityStateAsync(
                new CapabilityState
                {
                    CapabilityId = descriptor.CapabilityId,
                    InstanceId = descriptor.InstanceId,
                    Available = service.State is ClawServiceState.Owned,
                    Reason = service.Reason ?? ReasonFor(service.State),
                    ObservedValue = value,
                    Quality = value is null
                        ? HardwareStateQuality.Unknown
                        : HardwareStateQuality.Observed,
                    ObservedAt = value is null ? null : DateTimeOffset.UtcNow,
                    DescriptorGeneration = _descriptorSet.Generation,
                    CycleGeneration = _cycleGeneration,
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private CapabilityValue? CurrentState(CapabilityDescriptor descriptor)
    {
        if (descriptor.CapabilityId == CapabilityIds.PowerSustained)
        {
            return _power!.LastObserved is { } value ? Integer(value.SustainedWatts) : null;
        }

        if (descriptor.CapabilityId == CapabilityIds.PowerBoost)
        {
            return _power!.LastObserved is { } value ? Integer(value.BoostWatts) : null;
        }

        if (descriptor.CapabilityId == CapabilityIds.Scenario)
        {
            return _power!.LastObserved is { } value ? Scenario(value.Scenario) : null;
        }

        if (descriptor.CapabilityId == CapabilityIds.FanMode)
        {
            return _fans!.LastObserved is { } value ? FanMode(value) : null;
        }

        if (descriptor.CapabilityId == CapabilityIds.FanCurve)
        {
            FanSnapshot? value = _fans!.LastObserved;
            return value is null
                ? null
                : Curve(descriptor.InstanceId == CapabilityInstances.Left ? value.Left : value.Right);
        }

        if (descriptor.CapabilityId == CapabilityIds.FanRpm)
        {
            FanTelemetry? value = _telemetry!.LastTelemetry;
            return value is null
                ? null
                : Integer(descriptor.InstanceId == CapabilityInstances.Left ? value.LeftRpm : value.RightRpm);
        }

        if (descriptor.CapabilityId == CapabilityIds.Temperature)
        {
            return _telemetry!.LastTelemetry is { } value
                ? Integer(value.TemperatureCelsius)
                : null;
        }

        if (descriptor.CapabilityId == CapabilityIds.LightingBrightness)
        {
            return _lighting!.LastObserved is { } value ? Integer(value.Brightness) : null;
        }

        if (descriptor.CapabilityId == CapabilityIds.LightingColor)
        {
            LightingState? value = _lighting!.LastObserved;
            return value is null
                ? null
                : Color(descriptor.InstanceId switch
                {
                    CapabilityInstances.RightRing => value.RightRingColor,
                    CapabilityInstances.LeftRing => value.LeftRingColor,
                    CapabilityInstances.Buttons => value.ButtonsColor,
                    _ => 0,
                });
        }

        if (descriptor.CapabilityId == CapabilityIds.Motion)
        {
            return Boolean(_motion!.State is ClawServiceState.Owned);
        }

        return Boolean(_controller!.State is ClawServiceState.Owned);
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
        string serviceId,
        string firmwareIdentity,
        CapabilityCommand command,
        Func<CancellationToken, ValueTask<ClawRecoveryState>> readOriginal,
        ClawCommandHandler apply,
        CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            throw new InvalidOperationException("The recovery journal is unavailable.");
        }

        RequireCommandWriteBudget(command.Deadline);
        ClawRecoveryState originalState = await readOriginal(cancellationToken).ConfigureAwait(false);
        ClawRecoveryOperation operation = await _journal.BeginAsync(
            serviceId,
            command.CapabilityId,
            firmwareIdentity,
            originalState,
            cancellationToken).ConfigureAwait(false);
        RequireCommandWriteBudget(command.Deadline);
        CapabilityCommandResult result;
        try
        {
            result = await apply(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A transport exception does not prove whether the firmware accepted a write. Keep the
            // exact pre-command value outstanding for recovery.
            throw;
        }

        _ = await _journal.CompleteCommandAsync(operation, result, CancellationToken.None)
            .ConfigureAwait(false);
        if (result.Rollback is RollbackResult.RestoreFailed)
        {
            ClawServiceStatus? service = serviceId switch
            {
                ServiceIds.Power => _power,
                ServiceIds.Fans => _fans,
                _ => null,
            };
            if (service is not null)
            {
                CapabilityReason reason = new(
                    CapabilityReasonCode.TransportFaulted,
                    "A command rollback failed; the resource is faulted until reconciliation.");
                service.Fault(reason);
                await ApplyServiceLifecycleStateAsync(
                    service,
                    new ClawServiceResult(ClawServiceState.Faulted, reason),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        return result;
    }

    private async ValueTask ReconcileOutstandingAsync(
        IReadOnlyList<ClawRecoveryEntry> entries,
        ClawIdentityState identity,
        ClawA2VmPowerCapability powerCapability,
        ClawA2VmFanCapability fanCapability,
        CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            return;
        }

        foreach (ClawRecoveryEntry entry in entries)
        {
            if (ServiceFor(entry.ServiceId)?.ReconciliationBlockReason is not null)
            {
                continue;
            }

            string? currentFirmware = entry.ServiceId switch
            {
                ServiceIds.Power or ServiceIds.Fans when identity.WmiFirmwareVerified =>
                    ClawFirmwareIdentities.Wmi,
                ServiceIds.Controller when identity.McuFirmwareVerified =>
                    ClawFirmwareIdentities.Mcu,
                _ => null,
            };
            ClawReconciliationAction action = ClawRecoveryJournal.Decide(entry, currentFirmware);
            if (action is not ClawReconciliationAction.Restore)
            {
                BlockService(entry.ServiceId, new CapabilityReason(
                    action is ClawReconciliationAction.Block
                        ? CapabilityReasonCode.TransportFaulted
                        : CapabilityReasonCode.FirmwareNotVerified,
                    "An outstanding recovery entry is not safe to restore automatically."));
                continue;
            }

            bool restored;
            bool restoreFailed = false;
            try
            {
                restored = entry.ServiceId switch
                {
                    ServiceIds.Power when ClawRecoveryValues.TryPower(
                        entry.OriginalState,
                        out PowerPair? power) =>
                        await powerCapability.RestoreAsync(power!, cancellationToken).ConfigureAwait(false),
                    ServiceIds.Fans when ClawRecoveryValues.TryFans(
                        entry.OriginalState,
                        out FanSnapshot? fans) =>
                        await fanCapability.RestoreAsync(fans!, cancellationToken).ConfigureAwait(false),
                    ServiceIds.Controller =>
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
                    ClawRecoveryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (restored)
            {
                _ = await _journal.CompleteExistingAsync(
                    entry,
                    ClawRecoveryStatus.RestoredVerified,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!restoreFailed)
            {
                _ = await _journal.CompleteExistingAsync(
                    entry,
                    ClawRecoveryStatus.RestoredUnverified,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!restored)
            {
                BlockService(entry.ServiceId, new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "An outstanding hardware state could not be restored and verified."));
            }
        }
    }

    private async ValueTask<bool> RestoreControllerJournalEntryAsync(
        ClawRecoveryEntry entry,
        CancellationToken cancellationToken)
    {
        if (!ClawRecoveryValues.TryControllerMode(entry.OriginalState, out ClawControllerMode mode))
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

    private void BlockService(string serviceId, CapabilityReason reason)
    {
        ClawServiceStatus? service = ServiceFor(serviceId);
        if (service is not null)
        {
            service.ReconciliationBlockReason = reason;
        }
    }

    private ClawServiceStatus? ServiceFor(string serviceId) => serviceId switch
    {
        ServiceIds.Power => _power,
        ServiceIds.Fans => _fans,
        ServiceIds.Controller => _controller,
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

    private ClawCycleContext OperationContext(DateTimeOffset deadline) =>
        new(_cycleGeneration, deadline);

    private PluginStartResult CurrentStartResult()
    {
        int owned = _serviceStatuses.Count(service => service.State is ClawServiceState.Owned);
        bool unhealthy = _serviceStatuses.Any(service => service.State is not ClawServiceState.Owned);
        ClawServiceStatus? firstUnhealthy = _serviceStatuses.FirstOrDefault(
            service => service.State is not ClawServiceState.Owned);
        return new PluginStartResult
        {
            State = owned == 0
                ? PluginOperationalState.Passive
                : unhealthy ? PluginOperationalState.Degraded : PluginOperationalState.Active,
            Reason = firstUnhealthy?.Reason ?? (owned == 0
                ? new CapabilityReason(
                    CapabilityReasonCode.PrerequisiteMissing,
                    "No Claw hardware service could be acquired.")
                : null),
        };
    }

    private PluginStopResult CurrentStopResult()
    {
        ClawServiceStatus? failed = _serviceStatuses.FirstOrDefault(
            service => service.State is ClawServiceState.Faulted);
        ClawServiceStatus? unverified = _serviceStatuses.FirstOrDefault(
            service => service.State is ClawServiceState.ReleasedUnverified);
        if (failed is not null)
        {
            return new PluginStopResult
            {
                Status = PluginStopStatus.Failed,
                Reason = failed.Reason ?? new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Service '{failed.ServiceId}' cleanup failed."),
            };
        }

        if (unverified is not null)
        {
            return new PluginStopResult
            {
                Status = PluginStopStatus.Unverified,
                Reason = unverified.Reason ?? new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Service '{unverified.ServiceId}' cleanup was not verified."),
            };
        }

        return new PluginStopResult { Status = PluginStopStatus.Clean };
    }

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

    private static CapabilityCommandResult Rejected(
        CapabilityCommand command,
        CapabilityReason reason) => new()
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = reason,
            CompletedAt = DateTimeOffset.UtcNow,
        };

    private static CapabilityCommandResult Indeterminate(
        CapabilityCommand command,
        string detail) => new()
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail),
            Rollback = RollbackResult.RestoredUnverified,
            CompletedAt = DateTimeOffset.UtcNow,
        };

    private static ValueTask<CapabilityCommandResult> ReadOnlyHandler(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Rejected(
            command,
            CapabilityReasonCode.Unsupported,
            "This capability is read-only."));
    }

    private static CapabilityReason? ReasonFor(ClawServiceState state) => state switch
    {
        ClawServiceState.Owned => null,
        ClawServiceState.Passive => new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing),
        ClawServiceState.Degraded or ClawServiceState.Faulted or ClawServiceState.ReleasedUnverified =>
            new CapabilityReason(CapabilityReasonCode.TransportFaulted),
        ClawServiceState.Releasing => new CapabilityReason(CapabilityReasonCode.Quiescing),
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

    private delegate ValueTask<CapabilityCommandResult> ClawCommandHandler(
        CapabilityCommand command,
        CancellationToken cancellationToken);

    private enum FirmwareKind
    {
        None,
        Wmi,
        Mcu,
    }
}
