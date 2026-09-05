using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Settings;
using WSGM.Device.Sdk.Testing;

namespace WSGM.Device.Tests;

internal sealed class ControllablePluginHostAdapter(long cycleGeneration) : IPluginHostAdapter
{
    private readonly TestPluginHostAdapter _inner = new(cycleGeneration);
    private readonly object _gate = new();
    private readonly List<(string Scope, string Message)> _faults = [];
    private readonly TaskCompletionSource _controllerSampleEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _oemEventEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long CycleGeneration => _inner.CycleGeneration;

    public bool FailNextNonEmptyOemPublication { get; set; }

    public bool BlockControllerSamples { get; set; }

    public bool BlockOemEvents { get; set; }
    public TaskCompletionSource? CapabilityPublicationBlock { get; set; }
    public TaskCompletionSource CapabilityPublicationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<CapabilityDescriptorSet> DescriptorSets => _inner.DescriptorSets;

    public IReadOnlyList<IReadOnlyList<PhysicalDeviceIdentity>> PhysicalDeviceSets =>
        _inner.PhysicalDeviceSets;

    public IReadOnlyList<IReadOnlyList<OemControlDescriptor>> OemControlSets =>
        _inner.OemControlSets;

    public IReadOnlyList<(DeviceTraceLevel Level, string Scope, string Message)> Traces =>
        _inner.Traces;

    public IReadOnlyList<(string Scope, string Message)> Faults
    {
        get
        {
            lock (_gate)
            {
                return [.. _faults];
            }
        }
    }

    public Task ControllerSampleEntered => _controllerSampleEntered.Task;

    public Task OemEventEntered => _oemEventEntered.Task;

    public ValueTask PublishDescriptorsAsync(
        CapabilityDescriptorSet descriptors,
        CancellationToken cancellationToken) =>
        _inner.PublishDescriptorsAsync(descriptors, cancellationToken);

    public ValueTask PublishCapabilityStateAsync(
        CapabilityState state,
        CancellationToken cancellationToken)
    {
        if (CapabilityPublicationBlock is { } blocked)
        {
            CapabilityPublicationEntered.TrySetResult();
            return new ValueTask(blocked.Task);
        }
        return _inner.PublishCapabilityStateAsync(state, cancellationToken);
    }

    public ValueTask PublishPhysicalDevicesAsync(
        IReadOnlyList<PhysicalDeviceIdentity> devices,
        HapticCapabilities? output,
        CancellationToken cancellationToken) =>
        _inner.PublishPhysicalDevicesAsync(devices, output, cancellationToken);

    public async ValueTask PublishControllerSampleAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        await _inner.PublishControllerSampleAsync(sample, cancellationToken).ConfigureAwait(false);
        if (!BlockControllerSamples)
        {
            return;
        }

        _controllerSampleEntered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PublishOemControlsAsync(
        IReadOnlyList<OemControlDescriptor> controls,
        CancellationToken cancellationToken)
    {
        await _inner.PublishOemControlsAsync(controls, cancellationToken).ConfigureAwait(false);
        if (controls.Count > 0 && FailNextNonEmptyOemPublication)
        {
            FailNextNonEmptyOemPublication = false;
            throw new IOException("Synthetic OEM publication failure after acceptance.");
        }
    }

    public async ValueTask PublishOemEventAsync(
        OemControlEvent controlEvent,
        CancellationToken cancellationToken)
    {
        await _inner.PublishOemEventAsync(controlEvent, cancellationToken).ConfigureAwait(false);
        if (!BlockOemEvents)
        {
            return;
        }

        _oemEventEntered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask PublishSettingsManifestAsync(
        PluginSettingsManifest manifest,
        CancellationToken cancellationToken) =>
        _inner.PublishSettingsManifestAsync(manifest, cancellationToken);

    public void Trace(DeviceTraceLevel level, string scope, string message) =>
        _inner.Trace(level, scope, message);

    public void ReportFault(string scope, string message)
    {
        lock (_gate)
        {
            _faults.Add((scope, message));
        }

        _inner.Trace(DeviceTraceLevel.Error, scope, message);
    }
}
