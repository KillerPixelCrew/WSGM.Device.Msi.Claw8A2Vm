using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>The exact-device plugin for the MSI Claw 8 AI+ A2VM board <c>MS-1T52</c>.</summary>
public sealed class Claw8A2VmPlugin : IDevicePlugin
{
    private const int MaxDiagnosticValueLength = 64;
    private readonly ClawHardwareServices _services;
    private readonly SemaphoreSlim _commandSerializer = new(1, 1);
    private IPluginHostAdapter? _host;
    private CapabilityDescriptorSet? _descriptorSet;
    private IReadOnlyList<ClawServiceStatus> _serviceStatuses = [];
    private OemEventService? _oem;
    private PowerService? _power;
    private ChargeLimitService? _chargeLimit;
    private FanService? _fans;
    private TelemetryService? _telemetry;
    private LightingService? _lighting;
    private MotionService? _motion;
    private ControllerService? _controller;
    private ChordSuppressorService? _suppressor;
    private DisplayService? _arcSync;
    private ClawRecoveryJournal? _journal;
    private ClawA2VmPowerCapability? _powerCapability;
    private ClawA2VmChargeLimitCapability? _chargeLimitCapability;
    private ClawA2VmFanCapability? _fanCapability;
    private ClawA2VmLightingCapability? _lightingCapability;
    private long _cycleGeneration;
    private bool _active;
    private bool _quiescing;
    private bool _disposed;
    private CancellationTokenSource? _observationLoop;
    private Task? _observationTask;

    /// <summary>How often the plugin re-reads and republishes what it observes.</summary>
    /// <remarks>
    /// Comfortably inside WSGM's 30-second freshness policy, so an observation is replaced twice
    /// before it can expire. Without this loop the plugin published state at start, at resume, and
    /// after a command, and never again — so every readable capability went stale thirty seconds
    /// into the cycle and stayed that way until the user happened to change something. The visible
    /// form was the QAM's TDP row disappearing, taking AutoTDP ("No primary power limit is
    /// available to control") with it.
    /// </remarks>
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(10);

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
            throw new InvalidOperationException("WSGM supplied a different device definition.");
        }

        if (context.CycleGeneration != context.Host.CycleGeneration)
        {
            throw new InvalidOperationException("WSGM supplied an inconsistent cycle generation.");
        }

        // Installed before the first hardware read, because startup is when the failures worth
        // tracing happen and an ambient sink installed late traces nothing that mattered.
        PluginTrace.Install(context.Host);
        PluginTrace.Info(
            "lifecycle",
            $"start: definition={context.DeviceDefinitionId}, cycle={context.CycleGeneration}.");

        ClawIdentityState identity = await _services.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.ExactMachineMatch)
        {
            PluginTrace.Error("lifecycle", "the exact MS-1T52 activation gate no longer matches.");
            throw new InvalidOperationException("The exact MS-1T52 activation gate no longer matches.");
        }

        _host = context.Host;
        _cycleGeneration = context.CycleGeneration;
        _quiescing = false;

        try
        {
            var powerCapability = new ClawA2VmPowerCapability(_services.Wmi);
            var chargeLimitCapability = new ClawA2VmChargeLimitCapability(_services.Wmi);
            var fanCapability = new ClawA2VmFanCapability(_services.Wmi);
            var lightingCapability = new ClawA2VmLightingCapability(_services.Mcu);
            _powerCapability = powerCapability;
            _chargeLimitCapability = chargeLimitCapability;
            _fanCapability = fanCapability;
            _lightingCapability = lightingCapability;
            _journal = await ClawRecoveryJournal.OpenAsync(context.StateDirectory, cancellationToken)
                .ConfigureAwait(false);
            _oem = new OemEventService(_services.OemEvents, context.Host, _services.OemButtons);
            _power = new PowerService(_services.Identity, powerCapability, _journal);
            _chargeLimit = new ChargeLimitService(_services.Identity, chargeLimitCapability);
            _fans = new FanService(_services.Identity, fanCapability, _journal);
            _telemetry = new TelemetryService(_services.Identity, fanCapability);
            _lighting = new LightingService(_services.Identity, _services.Mcu, lightingCapability);
            _motion = new MotionService(_services.Motion);

            // Opened before descriptors are built, because whether the variable-refresh row exists at
            // all depends on whether a capable panel answered.
            _arcSync = new DisplayService();
            _ = _arcSync.TryAcquire();
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
            _suppressor = new ChordSuppressorService(
                _services.ChordSuppressor,
                _oem,
                context.Host);

            _serviceStatuses =
            [
                _oem,
                _power,
                _chargeLimit,
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

            await StartServicesAsync(
                new ClawCycleContext(
                    context.CycleGeneration,
                    DateTimeOffset.UtcNow.AddSeconds(15)),
                cancellationToken).ConfigureAwait(false);
            _active = true;
            await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);
            StartObservationLoop();
            return CurrentStartResult();
        }
        catch
        {
            _quiescing = true;
            await RollBackFailedStartAsync().ConfigureAwait(false);
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
                await PublishPostCommandObservationAsync(command.CapabilityId).ConfigureAwait(false);
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
        StopObservationLoop();
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
            if (_host is null || _powerCapability is null || _chargeLimitCapability is null
                || _fanCapability is null
                || _lightingCapability is null)
            {
                throw new InvalidOperationException("Resume cannot rebuild the capability surface.");
            }

            BuildCapabilitySurface();
            await _host.PublishDescriptorsAsync(_descriptorSet!, cancellationToken)
                .ConfigureAwait(false);
            _quiescing = false;
            await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);

            // Resume rebuilds the capability surface, so the observation loop has to come back with
            // it — a resumed cycle that never refreshed would go stale exactly as the original did.
            StartObservationLoop();
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
            ["cycle"] = DiagnosticCycleState(),
            ["recovery"] = DiagnosticRecoveryState(),
        };
        foreach (ClawServiceStatus service in _serviceStatuses)
        {
            values[service.ServiceId] = BoundDiagnosticValue(service.State.ToString());
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

            long previousCycleGeneration = _cycleGeneration;
            _controller.Enabled = context.Enabled;
            _cycleGeneration = context.CycleGeneration;
            // WSGM advances the adapter to a fresh cycle generation when controller
            // management is switched on, and that resets the descriptor generation it will accept.
            // Rebuilding and republishing the surface first is what makes the states below valid —
            // without it the very first state after a successful hardware acquisition was rejected
            // as stale and the whole enable faulted with the controller already taken. The disable
            // request carries the unchanged generation, and republishing there would be refused as
            // non-monotonic, so the rebuild is tied to the advance rather than to the request.
            if (_cycleGeneration != previousCycleGeneration
                && _host is not null
                && _descriptorSet is not null)
            {
                BuildCapabilitySurface();
                await _host.PublishDescriptorsAsync(_descriptorSet!, cancellationToken)
                    .ConfigureAwait(false);
            }

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

        // Before taking the gate: the loop takes the same one, and a refresh must not be able to
        // read hardware the stop is releasing.
        StopObservationLoop();
        await _commandSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_serviceStatuses.Count > 0)
            {
                await StopServicesAsync(OperationContext(context.Deadline), cancellationToken)
                    .ConfigureAwait(false);
            }

            // Restored before the result is taken, and outside the service loop, because the
            // display is held by the graphics driver rather than by anything StopServicesAsync
            // releases. Leaving variable refresh off after WSGM exits would be a change the user
            // never made and has no obvious way to undo.
            bool displayRestored = true;
            if (_arcSync is not null)
            {
                displayRestored = _arcSync.Restore();
                _arcSync.Dispose();
                _arcSync = null;
            }

            PluginStopResult result = CurrentStopResult();
            if (!displayRestored && result.Status is not PluginStopStatus.Failed)
            {
                result = new PluginStopResult
                {
                    Status = PluginStopStatus.Failed,
                    Reason = new CapabilityReason(
                        CapabilityReasonCode.TransportFaulted,
                        "The panel's captured variable-refresh profile could not be restored."),
                };
            }
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

    private async ValueTask RollBackFailedStartAsync()
    {
        StopObservationLoop();
        if (_serviceStatuses.Count > 0)
        {
            try
            {
                await StopServicesAsync(
                    new ClawCycleContext(
                        _cycleGeneration,
                        DateTimeOffset.UtcNow.AddSeconds(12)),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PluginTrace.Error(
                    "lifecycle",
                    $"startup rollback could not release every service: "
                        + $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (_arcSync is not null)
        {
            try
            {
                if (!_arcSync.Restore())
                {
                    PluginTrace.Error(
                        "display",
                        "startup rollback could not verify the captured variable-refresh profile.");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PluginTrace.Error(
                    "display",
                    $"startup rollback failed while restoring variable refresh: "
                        + $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _arcSync.Dispose();
                _arcSync = null;
            }
        }

        if (_journal is not null)
        {
            try
            {
                await _journal.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PluginTrace.Error(
                    "recovery",
                    $"startup rollback could not close the recovery journal: "
                        + $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _journal = null;
            }
        }

        await RetractFailedStartPublicationsAsync().ConfigureAwait(false);

        _active = false;
        _descriptorSet = null;
        _serviceStatuses = [];
    }

    private async ValueTask RetractFailedStartPublicationsAsync()
    {
        if (_host is null)
        {
            return;
        }

        await TryRetractPublicationAsync(
            "physical devices",
            () => _host.PublishPhysicalDevicesAsync([], null, CancellationToken.None))
            .ConfigureAwait(false);
        await TryRetractPublicationAsync(
            "OEM controls",
            () => _host.PublishOemControlsAsync([], CancellationToken.None))
            .ConfigureAwait(false);

        if (_descriptorSet is not null)
        {
            CapabilityDescriptorSet publishedDescriptors = _descriptorSet;
            await TryRetractPublicationAsync(
                "capability descriptors",
                () => _host.PublishDescriptorsAsync(
                    new CapabilityDescriptorSet
                    {
                        Generation = checked(publishedDescriptors.Generation + 1),
                        CycleGeneration = _cycleGeneration,
                        Descriptors = [],
                    },
                    CancellationToken.None))
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask TryRetractPublicationAsync(
        string publication,
        Func<ValueTask> retract)
    {
        try
        {
            await retract().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginTrace.Error(
                "lifecycle",
                $"startup rollback could not retract {publication}: "
                    + $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private string DiagnosticCycleState()
    {
        if (_disposed)
        {
            return "disposed";
        }

        if (_quiescing)
        {
            return _active ? "quiescing" : "stopped";
        }

        if (_active)
        {
            return "started";
        }

        return _host is not null && _descriptorSet is not null
            ? "starting"
            : "stopped";
    }

    private string DiagnosticRecoveryState()
    {
        if (_journal is null)
        {
            return "unavailable";
        }

        if (_journal.FailureReason is not null)
        {
            return "blocked";
        }

        return _journal.OutstandingEntries.Count == 0 ? "healthy" : "pending";
    }

    private static string BoundDiagnosticValue(string value) =>
        value.Length <= MaxDiagnosticValueLength ? value : value[..MaxDiagnosticValueLength];

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopObservationLoop();
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
            await StartOneAsync(
                _chargeLimit!,
                () => _chargeLimit!.AcquireAsync(context, cancellationToken),
                cancellationToken).ConfigureAwait(false);
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
        await StartOneAsync(
            _chargeLimit!,
            () => _chargeLimit!.AcquireAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
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
        await StopOneAsync(
            _chargeLimit!,
            () => _chargeLimit!.ReleaseAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
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
        if (_oem is null || _power is null || _chargeLimit is null || _fans is null || _telemetry is null
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
        if (_power is null || _chargeLimit is null || _fans is null || _telemetry is null
            || _lighting is null
            || _motion is null || _controller is null)
        {
            throw new InvalidOperationException("Services must exist before descriptors are built.");
        }

        IReadOnlyList<CapabilityDescriptor> descriptors =
        [
            IntegerDescriptor(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit,
                // 37 W, matching PL2 and the device's actual ceiling, not the 30 W it ships at.
                DisplayKey.SustainedPowerLimit, 8, 37, CapabilityUnit.Watt, writable: true,
                section: SectionIds.Power, category: CategoryIds.Limits, order: 0),
            IntegerDescriptor(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit,
                DisplayKey.BoostPowerLimit, 8, 37, CapabilityUnit.Watt, writable: true,
                section: SectionIds.Power, category: CategoryIds.Limits, order: 1),
            IntegerDescriptor(CapabilityIds.ChargeLimit, CapabilityRole.ChargeLimit,
                DisplayKey.ChargeLimit,
                ClawA2VmChargeLimitCapability.MinimumPercent,
                ClawA2VmChargeLimitCapability.MaximumPercent,
                CapabilityUnit.Percent,
                writable: true,
                persistence: CapabilityPersistence.DevicePersistent,
                section: SectionIds.Power,
                category: CategoryIds.Charging),
            ChoiceDescriptor(
                CapabilityIds.Scenario,
                CapabilityRole.ScenarioMode,
                DisplayKey.PerformanceProfile,
                ["comfort", "green", "eco", "user", "sport"],
                writable: false,
                section: SectionIds.Power),
            ChoiceDescriptor(
                CapabilityIds.FanMode,
                CapabilityRole.FanMode,
                DisplayKey.FanMode,
                ["automatic", "custom", "full-speed"],
                writable: true,
                section: SectionIds.Power,
                category: CategoryIds.Control),
            FanCurveDescriptor(CapabilityInstances.Left, order: 1),
            FanCurveDescriptor(CapabilityInstances.Right, order: 2),
            IntegerDescriptor(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                DisplayKey.FanLeft, 0, 10_000, CapabilityUnit.Rpm, writable: false,
                CapabilityInstances.Left,
                section: SectionIds.Power, category: CategoryIds.Readings, order: 0),
            IntegerDescriptor(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                DisplayKey.FanRight, 0, 10_000, CapabilityUnit.Rpm, writable: false,
                CapabilityInstances.Right,
                section: SectionIds.Power, category: CategoryIds.Readings, order: 1),
            IntegerDescriptor(CapabilityIds.Temperature, CapabilityRole.Telemetry,
                DisplayKey.CpuTemperature, 0, 110, CapabilityUnit.Celsius, writable: false,
                section: SectionIds.Power, category: CategoryIds.Readings, order: 2),
            IntegerDescriptor(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
                DisplayKey.Brightness, 0, 100, CapabilityUnit.Percent, writable: true,
                persistence: CapabilityPersistence.DevicePersistent,
                section: SectionIds.Lighting),
            LightingColorDescriptor(CapabilityInstances.LeftRing, "Left ring", order: 0),
            LightingColorDescriptor(CapabilityInstances.RightRing, "Right ring", order: 1),
            LightingColorDescriptor(CapabilityInstances.Buttons, "Buttons", order: 2),
            // These three carry the value kinds their roles require. ControllerSource and
            // MotionSource are choices because "who owns this source" has more than two answers —
            // the plugin can hold it, the device can still have it, or acquisition can have failed —
            // and a boolean flattened all three into "not owned". HapticSink carries no value at
            // all: it is a target rumble is written to, not something with a readable state.
            //
            // Declaring them as booleans made the descriptor set fail the SDK's role/value-kind
            // check, and a rejected SET means every capability is rejected — so the whole device
            // published nothing at all because of these three lines.
            ChoiceDescriptor(
                CapabilityIds.Controller,
                CapabilityRole.ControllerSource,
                DisplayKey.Controller,
                SourceOwnershipChoices,
                writable: false,
                section: SectionIds.Input,
                order: 0),
            ChoiceDescriptor(
                CapabilityIds.Motion,
                CapabilityRole.MotionSource,
                DisplayKey.Motion,
                SourceOwnershipChoices,
                writable: false,
                section: SectionIds.Input,
                order: 1),
            ActionDescriptor(
                CapabilityIds.Rumble,
                CapabilityRole.HapticSink,
                DisplayKey.Rumble,
                section: SectionIds.Input,
                order: 2),
            .. _arcSync?.IsAvailable == true
                ?
                [
                    // Published only when a variable-refresh capable panel actually answered. A
                    // descriptor for a panel that cannot do it would draw a row that always
                    // refuses, which is worse than no row: the device-persistent marking is
                    // deliberate, because the driver keeps the profile across a WSGM restart.
                    BooleanDescriptor(
                        CapabilityIds.VariableRefreshRate,
                        CapabilityRole.VariableRefreshRate,
                        DisplayKey.VariableRefreshRate,
                        writable: true,
                        section: SectionIds.Display) with
                    {
                        Persistence = CapabilityPersistence.DevicePersistent,
                    },
                ]
                : (IReadOnlyList<CapabilityDescriptor>)[],
        ];

        EnsureUniqueCapabilityKeys(descriptors);
        _descriptorSet = new CapabilityDescriptorSet
        {
            Generation = 1,
            CycleGeneration = _cycleGeneration,
            Sections = OverlaySections,
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
        ClawA2VmChargeLimitCapability chargeLimit = _chargeLimitCapability
            ?? throw new InvalidOperationException("The charge-limit capability is unavailable.");
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
            CapabilityIds.ChargeLimit => chargeLimit.ApplyAsync(
                command,
                command.RequestedValue!.IntegerValue!.Value,
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
            CapabilityIds.VariableRefreshRate => ApplyVariableRefreshCommand(command),
            _ => ReadOnlyHandler(command, cancellationToken),
        };
    }

    /// <remarks>
    /// Not journalled, unlike the WMI and MCU writes. The journal exists so a value written into
    /// firmware can be put back after an abnormal exit; this one is held by the graphics driver,
    /// restored from the profile captured at cycle start, and reported as verified only because the
    /// read-back agrees rather than because the call returned success.
    /// </remarks>
    private ValueTask<CapabilityCommandResult> ApplyVariableRefreshCommand(CapabilityCommand command)
    {
        if (_arcSync is not { IsAvailable: true } display)
        {
            return ValueTask.FromResult(Rejected(
                command,
                CapabilityReasonCode.Unsupported,
                "No variable-refresh capable panel is present."));
        }

        bool requested = command.RequestedValue!.BooleanValue!.Value;
        if (!display.TryWrite(requested))
        {
            return ValueTask.FromResult(Rejected(
                command,
                CapabilityReasonCode.TransportFaulted,
                $"The display driver did not apply variable refresh {(requested ? "on" : "off")}."));
        }

        // Verified rather than unverified: TryWrite only reports success once the profile has been
        // read back and agrees, so the readback carried here is an observation and not a hope.
        return ValueTask.FromResult(new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.AppliedVerified,
            ReadbackValue = Boolean(requested),
            CompletedAt = DateTimeOffset.UtcNow,
        });
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
        CapabilityIds.ChargeLimit => _chargeLimit,
        CapabilityIds.FanMode or CapabilityIds.FanCurve => _fans,
        CapabilityIds.FanRpm or CapabilityIds.Temperature => _telemetry,
        CapabilityIds.LightingBrightness or CapabilityIds.LightingColor => _lighting,
        CapabilityIds.Controller or CapabilityIds.Rumble => _controller,
        CapabilityIds.Motion => _motion,
        CapabilityIds.VariableRefreshRate => _arcSync,
        _ => null,
    };

    private static FirmwareKind FirmwareForCapability(string capabilityId) => capabilityId switch
    {
        CapabilityIds.LightingBrightness or CapabilityIds.LightingColor
            or CapabilityIds.Controller or CapabilityIds.Rumble => FirmwareKind.Mcu,
        // Driven by the GPU driver, not by MSI firmware, so there is no firmware revision to gate
        // it on and gating it on the WMI one would refuse it whenever that path is degraded.
        CapabilityIds.Motion or CapabilityIds.VariableRefreshRate => FirmwareKind.None,
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

    /// <summary>The Claw's declared Device overlay layout.</summary>
    /// <remarks>
    /// Titles and icons are WSGM-owned vocabulary; only the grouping is this plugin's. A section a
    /// firmware variant leaves empty (Display without an ARC Sync panel) is dropped by WSGM rather
    /// than declared conditionally, so the layout stays one static fact.
    /// </remarks>
    private static readonly IReadOnlyList<CapabilitySection> OverlaySections =
    [
        new CapabilitySection
        {
            SectionId = SectionIds.Power,
            Key = SettingSectionKey.Power,
            Icon = SectionIcon.Power,
            CustomDescription = "Power limits, charging, fans, and thermals",
            SortOrder = 0,
            Categories =
            [
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Limits,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Limits",
                    SortOrder = 0,
                },
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Charging,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Charging",
                    SortOrder = 1,
                },
                // Fans and thermals were their own Cooling section; folded in here so Power is one
                // page instead of two that both read as "power" to the user (maintainer-directed).
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Control,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Fans",
                    SortOrder = 2,
                },
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Readings,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Thermals",
                    SortOrder = 3,
                },
            ],
        },
        new CapabilitySection
        {
            SectionId = SectionIds.Lighting,
            Key = SettingSectionKey.Lighting,
            Icon = SectionIcon.Lighting,
            CustomDescription = "Ring and button lighting",
            SortOrder = 2,
            Categories =
            [
                new CapabilityCategory
                {
                    CategoryId = CategoryIds.Zones,
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Zones",
                    SortOrder = 0,
                },
            ],
        },
        new CapabilitySection
        {
            SectionId = SectionIds.Input,
            Key = SettingSectionKey.Controller,
            Icon = SectionIcon.Controller,
            CustomDescription = "Built-in controller, motion, and rumble",
            SortOrder = 3,
        },
        new CapabilitySection
        {
            SectionId = SectionIds.Display,
            Key = SettingSectionKey.Display,
            Icon = SectionIcon.Display,
            CustomDescription = "Panel synchronization",
            SortOrder = 4,
        },
    ];

    private static CapabilityDescriptor FanCurveDescriptor(string instance, int order) => new()
    {
        CapabilityId = CapabilityIds.FanCurve,
        InstanceId = instance,
        Role = CapabilityRole.FanCurve,
        SectionId = SectionIds.Power,
        CategoryId = CategoryIds.Control,
        SortOrder = order,
        ValueKind = CapabilityValueKind.Curve,
        Display = new CapabilityDisplay
        {
            Key = instance == CapabilityInstances.Left ? DisplayKey.FanLeft : DisplayKey.FanRight,
        },
        SupportsRead = true,
        SupportsWrite = true,
        Persistence = CapabilityPersistence.Volatile,
    };

    private static CapabilityDescriptor LightingColorDescriptor(
        string instance,
        string label,
        int order) => new()
        {
            CapabilityId = CapabilityIds.LightingColor,
            InstanceId = instance,
            Role = CapabilityRole.LightingZoneColor,
            SectionId = SectionIds.Lighting,
            CategoryId = CategoryIds.Zones,
            SortOrder = order,
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

            // A descriptor that cannot be read has no observed value to publish. The haptic sink is
            // the only one: rumble is written to it and never read back, so a state carrying a value
            // for it is rejected against its own descriptor shape. Its availability still matters,
            // so the state is published — with no value, which is what "not readable" means.
            CapabilityValue? value = descriptor.SupportsRead ? CurrentState(descriptor) : null;
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

        if (descriptor.CapabilityId == CapabilityIds.ChargeLimit)
        {
            return _chargeLimit!.LastObserved is { } value ? Integer(value.Percent) : null;
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

        if (descriptor.CapabilityId == CapabilityIds.Rumble)
        {
            // A sink has no value to report. Its descriptor says so, and its state has to agree or
            // the state is rejected for a kind mismatch the way the descriptor set was.
            return new CapabilityValue { Kind = CapabilityValueKind.None };
        }

        if (descriptor.CapabilityId == CapabilityIds.VariableRefreshRate)
        {
            // This branch was missing, and its absence was invisible until it wasn't: VRR fell
            // through to the controller-ownership Choice below, publishing a Choice value against a
            // Boolean descriptor. The router rejected every VRR state for the kind mismatch, the
            // capability never became available, and Valve's own VRR row — which hides itself
            // through exactly that availability — never rendered. One log line every ten seconds
            // said all of this; it took a missing row to make anyone read it.
            ArcSyncState? state = _arcSync?.Read();
            return state is { Supported: true } observed ? Boolean(observed.Enabled) : null;
        }

        return Choice(OwnershipOf(
            descriptor.CapabilityId == CapabilityIds.Motion ? _motion!.State : _controller!.State));
    }

    /// <summary>Projects a service's state onto the ownership vocabulary the descriptor offers.</summary>
    /// <param name="state">The service's current state.</param>
    /// <returns>One of the descriptor's declared choices.</returns>
    /// <remarks>
    /// Acquiring and Releasing report the ownership they are moving away from rather than inventing
    /// a transient value: the row is read continuously, and a state that flickers through a fourth
    /// value on every transition reads as a fault rather than as progress.
    /// </remarks>
    private static string OwnershipOf(ClawServiceState state) => state switch
    {
        ClawServiceState.Owned or ClawServiceState.Releasing => "plugin",
        ClawServiceState.Idle or ClawServiceState.Passive or ClawServiceState.Acquiring => "device",
        _ => "unavailable",
    };

    /// <summary>Starts the periodic observation refresh for this cycle.</summary>
    private void StartObservationLoop()
    {
        StopObservationLoop();
        CancellationTokenSource loop = new();
        _observationLoop = loop;
        _observationTask = Task.Run(() => ObservationLoopAsync(loop.Token));
    }

    /// <summary>Stops and forgets the observation loop, if one is running.</summary>
    private void StopObservationLoop()
    {
        CancellationTokenSource? loop = _observationLoop;
        _observationLoop = null;
        _observationTask = null;
        if (loop is null)
        {
            return;
        }

        try
        {
            loop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by a concurrent stop; nothing left to cancel.
        }

        loop.Dispose();
    }

    /// <summary>Re-reads the hardware and republishes state until the cycle ends.</summary>
    /// <param name="cancellationToken">Ends the loop when the cycle does.</param>
    /// <remarks>
    /// Serialized behind the same gate as commands, so a refresh can never interleave with a
    /// hardware write. Failures are traced and the loop continues: a device that cannot be read for
    /// one interval is a stale reading, which WSGM already models, not a reason to stop observing.
    /// </remarks>
    private async Task ObservationLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(ObservationInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_active || _quiescing || _disposed)
            {
                continue;
            }

            if (!await _commandSerializer.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false))
            {
                // A command is in flight and will republish on its own; skipping is correct.
                continue;
            }

            try
            {
                await RefreshAllObservedAsync(cancellationToken).ConfigureAwait(false);
                await PublishCapabilityStatesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // A pass abandoned by an inner linked or timeout token, not by this loop. It is not
                // a failure — the next pass retries — but the filter above only recognises this
                // loop's own token, so every one of these reached the failure branch below: 3,492
                // warnings in one archived log, all of them a device quiescing normally. Keep
                // polling, and keep the evidence at a level that does not bury the log.
                PluginTrace.Debug("observe", "periodic observation refresh was cancelled; retrying.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PluginTrace.Failure("observe", "periodic observation refresh failed", ex);
            }
            finally
            {
                _commandSerializer.Release();
            }
        }
    }

    /// <summary>Re-reads every observable service that is currently owned.</summary>
    private async ValueTask RefreshAllObservedAsync(CancellationToken cancellationToken)
    {
        if (_power is { State: ClawServiceState.Owned })
        {
            await _power.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_chargeLimit is { State: ClawServiceState.Owned })
        {
            await _chargeLimit.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_fans is { State: ClawServiceState.Owned })
        {
            await _fans.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_telemetry is { State: ClawServiceState.Owned })
        {
            await _telemetry.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_lighting is { State: ClawServiceState.Owned })
        {
            await _lighting.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RefreshObservedAsync(
        string capabilityId,
        CancellationToken cancellationToken)
    {
        if (capabilityId is CapabilityIds.PowerSustained or CapabilityIds.PowerBoost)
        {
            await _power!.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (capabilityId == CapabilityIds.ChargeLimit)
        {
            await _chargeLimit!.RefreshAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Publishes best-effort fresh state after a command already verified its own write.</summary>
    /// <remarks>
    /// Capability handlers own command verification. This secondary refresh updates adjacent rows
    /// such as paired power and fan telemetry; losing it must not rewrite a verified command result
    /// or terminate the plugin cycle.
    /// </remarks>
    private async ValueTask PublishPostCommandObservationAsync(string capabilityId)
    {
        try
        {
            await RefreshObservedAsync(capabilityId, CancellationToken.None).ConfigureAwait(false);
            await PublishCapabilityStatesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginTrace.Failure(
                "observation",
                $"Post-command refresh for '{capabilityId}' failed; the verified command result is retained",
                ex);
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

        ClawWriteBudget.Require(command.Deadline, "journalled command preparation");
        ClawRecoveryState originalState = await readOriginal(cancellationToken).ConfigureAwait(false);
        ClawRecoveryOperation operation = await _journal.BeginAsync(
            serviceId,
            command.CapabilityId,
            firmwareIdentity,
            originalState,
            cancellationToken).ConfigureAwait(false);
        ClawWriteBudget.Require(command.Deadline, "journalled hardware application");
        // A transport exception does not prove whether the firmware accepted a write. Let it
        // propagate while the exact pre-command journal entry remains outstanding for recovery.
        CapabilityCommandResult result = await apply(command, cancellationToken).ConfigureAwait(false);

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

    private ClawCycleContext OperationContext(DateTimeOffset deadline) =>
        new(_cycleGeneration, deadline);

    private PluginStartResult CurrentStartResult()
    {
        IEnumerable<ClawServiceStatus> requiredServices = _serviceStatuses.Where(
            service => service != _controller || _controller.Enabled);
        int owned = requiredServices.Count(service => service.State is ClawServiceState.Owned);
        bool unhealthy = requiredServices.Any(service => service.State is not ClawServiceState.Owned);
        ClawServiceStatus? firstUnhealthy = requiredServices.FirstOrDefault(
            service => service.State is not ClawServiceState.Owned);
        PluginStartResult result = new()
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

        // "Degraded" names the aggregate and carries only the FIRST unhealthy service's reason,
        // which is what made "why is the device only partially available?" unanswerable from a
        // pasted log: the state that reached the user described one service out of eight and never
        // said which of the others were fine. This lists all of them, every time.
        TraceServiceStates(result.State);
        return result;
    }

    /// <summary>Records the state of every service behind one aggregate operational state.</summary>
    private void TraceServiceStates(PluginOperationalState aggregate)
    {
        if (_host is null)
        {
            return;
        }

        StringBuilder detail = new();
        foreach (ClawServiceStatus service in _serviceStatuses)
        {
            if (detail.Length > 0)
            {
                detail.Append(", ");
            }

            detail.Append(service.ServiceId).Append('=').Append(service.State);
            if (service.State is not ClawServiceState.Owned && service.Reason is { } reason)
            {
                detail.Append('(').Append(reason.Code);
                if (!string.IsNullOrWhiteSpace(reason.Detail))
                {
                    detail.Append(": ").Append(reason.Detail);
                }

                detail.Append(')');
            }
        }

        _host.Trace(
            aggregate is PluginOperationalState.Active
                ? DeviceTraceLevel.Info
                : DeviceTraceLevel.Warn,
            "lifecycle",
            $"start state {aggregate}: {detail}");
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
        CapabilityPersistence persistence = CapabilityPersistence.Volatile,
        string? section = null,
        string? category = null,
        int order = 0) => new()
        {
            CapabilityId = id,
            InstanceId = instance,
            Role = role,
            SectionId = section,
            CategoryId = category,
            SortOrder = order,
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
        bool writable,
        string? section = null,
        string? category = null,
        int order = 0) => new()
        {
            CapabilityId = id,
            Role = role,
            SectionId = section,
            CategoryId = category,
            SortOrder = order,
            ValueKind = CapabilityValueKind.Choice,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Choices = choices.Select(choice => new CapabilityChoice(
                choice,
                new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = choice })).ToArray(),
            Persistence = CapabilityPersistence.Volatile,
        };

    /// <summary>Who currently owns a physical input source.</summary>
    /// <remarks>
    /// Ordered so the first value is the resting state. <c>device</c> means the Claw's own firmware
    /// still has it, <c>plugin</c> means this plugin acquired it, and <c>unavailable</c> covers both
    /// a failed acquisition and a source this unit does not expose — a user reading the row needs
    /// those to be distinguishable, which is exactly what the previous boolean threw away.
    /// </remarks>
    private static readonly string[] SourceOwnershipChoices = ["device", "plugin", "unavailable"];

    /// <summary>A capability that is invoked rather than read or written.</summary>
    /// <param name="id">Capability id.</param>
    /// <param name="role">Semantic role, which must be one the SDK maps to <c>None</c>.</param>
    /// <param name="display">Display key.</param>
    /// <param name="section">Overlay section id the row is grouped under, or null for the default.</param>
    /// <param name="order">Sort order within the section; lower sorts first.</param>
    /// <returns>The descriptor.</returns>
    private static CapabilityDescriptor ActionDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        string? section = null,
        int order = 0) => new()
        {
            CapabilityId = id,
            Role = role,
            SectionId = section,
            SortOrder = order,
            ValueKind = CapabilityValueKind.None,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = false,
            SupportsWrite = false,
            // A descriptor has to offer at least one operation, and for a sink the operation is the
            // invoke: rumble is written to it, never read back from it.
            SupportsAction = true,
            Persistence = CapabilityPersistence.Volatile,
        };

    private static CapabilityDescriptor BooleanDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        bool writable,
        string? section = null,
        int order = 0) => new()
        {
            CapabilityId = id,
            Role = role,
            SectionId = section,
            SortOrder = order,
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

    private static CapabilityValue Choice(string value) => new()
    {
        Kind = CapabilityValueKind.Choice,
        ChoiceValue = value,
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
            // Admission succeeded and the handler failed without confirming a rollback. Journalled
            // resources remain outstanding, so claiming any restoration here would be fabricated.
            Rollback = RollbackResult.RestoreFailed,
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

        // One latch, shared by the two services that need it: the OEM event source latches a press
        // and the controller reader merges it into the next samples. The buttons are physical
        // controller buttons that the firmware happens to deliver out of band.
        ClawOemButtonLatch oemButtons = new();
        return new ClawHardwareServices(
            new WindowsClawIdentityReader(wmi),
            wmi,
            new MsiOemEventSource(),
            new WindowsClawMcuTransport(),
            new WindowsClawControllerSource(oemButtons),
            new WindowsClawMotionSource(),
            new FirmwareChordSuppressor(),
            oemButtons);
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
