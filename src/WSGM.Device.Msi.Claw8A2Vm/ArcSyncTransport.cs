using System;
using System.Runtime.InteropServices;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

/// <summary>What Arc Sync reports for the panel right now.</summary>
/// <param name="Supported">Whether the panel supports variable refresh at all.</param>
/// <param name="Enabled">Whether a profile other than OFF is applied.</param>
/// <param name="MinimumHz">Lowest refresh the panel can hold under variable refresh.</param>
/// <param name="MaximumHz">Highest refresh the panel can hold under variable refresh.</param>
internal readonly record struct ArcSyncState(
    bool Supported,
    bool Enabled,
    float MinimumHz,
    float MaximumHz
);

/// <summary>
/// Variable refresh for the device's own panel, over Intel's Graphics Control Library.
/// </summary>
/// <remarks>
/// The panel belongs to the device, so the transport that drives it belongs to the plugin. WSGM
/// projects the capability and never learns that Intel answered — a device on another GPU vendor
/// implements the same capability through whatever its driver offers.
/// <para>
/// Device-verified on the reference Claw 8 AI+ A2VM, 2026-08-30, unelevated: the panel reports
/// supported across 30-120 Hz, a write to OFF and a restore of the saved parameter struct both
/// succeed, and the read-back confirms each. The range collapsing to 120/120 under OFF is a second
/// confirmation independent of the profile enum, which is why this capability can report a verified
/// read-back rather than an applied-unverified one.
/// </para>
/// <para>
/// <c>ControlLib.dll</c> ships with the Intel driver and is already in <c>System32</c>, so it is
/// loaded by name and its absence simply means unsupported. Nothing here is vendored.
/// </para>
/// </remarks>
internal sealed unsafe class ArcSyncTransport : IDisposable
{
    /// <summary>Success.</summary>
    private const int ResultSuccess = 0;

    /// <summary>Returned by every display output that has nothing attached to it.</summary>
    private const int ResultKmdCall = 0x40000017;

    /// <summary>Selects an appropriate profile for the monitor; the driver's own default.</summary>
    private const int ProfileRecommended = 1;

    /// <summary>Disables variable refresh; all flips occur at the OS-requested rate.</summary>
    private const int ProfileOff = 5;

    /// <summary>IGCL 1.1, the version this transport was verified against.</summary>
    private const uint ImplVersion = (1 << 16) | 1;

    private const int MaxDevices = 8;
    private const int MaxOutputs = 16;

    private nint _library;
    private nint _api;
    private nint _panel;
    private ArcSyncProfileParams _saved;
    private bool _savedValid;
    private bool _disposed;

    private delegate* unmanaged[Cdecl]<CtlInitArgs*, nint*, int> _init;
    private delegate* unmanaged[Cdecl]<nint, int> _close;
    private delegate* unmanaged[Cdecl]<nint, uint*, nint*, int> _enumerateDevices;
    private delegate* unmanaged[Cdecl]<nint, uint*, nint*, int> _enumerateOutputs;
    private delegate* unmanaged[Cdecl]<nint, ArcSyncMonitorParams*, int> _monitorInfo;
    private delegate* unmanaged[Cdecl]<nint, ArcSyncProfileParams*, int> _getProfile;
    private delegate* unmanaged[Cdecl]<nint, ArcSyncProfileParams*, int> _setProfile;

    /// <summary>Whether a variable-refresh capable panel was found and is usable.</summary>
    public bool IsAvailable => _panel != 0;

    /// <summary>
    /// Sizes of the three structures IGCL validates against, for a layout regression test.
    /// </summary>
    /// <remarks>
    /// Every IGCL call carries the caller's own <c>sizeof</c> in a <c>Size</c> field and the driver
    /// refuses a mismatch. That refusal is indistinguishable from "this machine has no variable
    /// refresh", so a layout drift would silently remove the feature instead of failing loudly.
    /// Pinning the numbers is the only cheap way to notice.
    /// </remarks>
    internal static (int Init, int Monitor, int Profile) NativeStructureSizes =>
        (sizeof(CtlInitArgs), sizeof(ArcSyncMonitorParams), sizeof(ArcSyncProfileParams));

    /// <summary>
    /// Loads the library, initialises IGCL, and selects the attached panel.
    /// </summary>
    /// <returns><see langword="true"/> when a variable-refresh capable panel was found.</returns>
    /// <remarks>
    /// Every failure is traced with what was actually observed, because "no variable refresh row
    /// appeared" is otherwise indistinguishable from "the driver refused" and from "this machine has
    /// no Intel GPU".
    /// </remarks>
    public bool TryOpen()
    {
        if (!NativeLibrary.TryLoad("ControlLib.dll", out _library))
        {
            PluginTrace.Info("arcsync", "ControlLib.dll not present; variable refresh unavailable.");
            return false;
        }

        if (!TryBind())
        {
            PluginTrace.Warn("arcsync", "ControlLib.dll is missing an expected entry point.");
            return false;
        }

        CtlInitArgs args = default;
        args.Size = (uint)sizeof(CtlInitArgs);
        args.AppVersion = ImplVersion;
        nint api = 0;
        int result = _init(&args, &api);
        if (result != ResultSuccess)
        {
            PluginTrace.Warn("arcsync", $"ctlInit refused with 0x{result:x}.");
            return false;
        }

        _api = api;
        return TrySelectPanel();
    }

    /// <summary>
    /// Reads the panel's current variable-refresh state.
    /// </summary>
    /// <returns>The state, or null when it cannot be read.</returns>
    public ArcSyncState? Read()
    {
        if (_panel == 0)
        {
            return null;
        }

        ArcSyncMonitorParams monitor = default;
        monitor.Size = (uint)sizeof(ArcSyncMonitorParams);
        int monitorResult = _monitorInfo(_panel, &monitor);
        if (monitorResult != ResultSuccess)
        {
            PluginTrace.Warn("arcsync", $"Monitor info failed with 0x{monitorResult:x}.");
            return null;
        }

        ArcSyncProfileParams profile = default;
        profile.Size = (uint)sizeof(ArcSyncProfileParams);
        int profileResult = _getProfile(_panel, &profile);
        if (profileResult != ResultSuccess)
        {
            PluginTrace.Warn("arcsync", $"Profile read failed with 0x{profileResult:x}.");
            return null;
        }

        return new ArcSyncState(
            monitor.IsSupported != 0,
            profile.Profile != ProfileOff,
            monitor.MinimumHz,
            monitor.MaximumHz);
    }

    /// <summary>
    /// Turns variable refresh on or off.
    /// </summary>
    /// <param name="enabled">Whether variable refresh should be active.</param>
    /// <returns><see langword="true"/> when the panel reports the requested state afterwards.</returns>
    /// <remarks>
    /// Enabling restores the profile captured at cycle start when there is one, so a user who had
    /// chosen EXCELLENT does not silently end up on RECOMMENDED after a toggle. Only when nothing
    /// was captured does it fall back to the driver's own default.
    /// </remarks>
    public bool TryWrite(bool enabled)
    {
        if (_panel == 0)
        {
            PluginTrace.Warn("arcsync", "Write refused: no variable-refresh panel selected.");
            return false;
        }

        ArcSyncProfileParams request = _savedValid ? _saved : default;
        request.Size = (uint)sizeof(ArcSyncProfileParams);
        if (enabled)
        {
            // A saved profile of OFF says nothing about what the user would want when enabling.
            if (!_savedValid || request.Profile == ProfileOff)
            {
                request.Profile = ProfileRecommended;
            }
        }
        else
        {
            request.Profile = ProfileOff;
        }

        int result = _setProfile(_panel, &request);
        if (result != ResultSuccess)
        {
            PluginTrace.Warn(
                "arcsync",
                $"Profile {request.Profile} refused with 0x{result:x} (wanted enabled={enabled}).");
            return false;
        }

        ArcSyncState? readback = Read();
        if (readback is not { } state || state.Enabled != enabled)
        {
            PluginTrace.Warn(
                "arcsync",
                $"Profile {request.Profile} applied but read back as "
                + $"{(readback?.Enabled.ToString() ?? "unreadable")}, wanted {enabled}.");
            return false;
        }

        PluginTrace.Info("arcsync", $"Variable refresh {(enabled ? "on" : "off")}, verified.");
        return true;
    }

    /// <summary>
    /// Writes back the profile captured when the cycle started.
    /// </summary>
    /// <returns><see langword="true"/> when the saved profile was restored or none was captured.</returns>
    /// <remarks>
    /// The saved parameter struct is written back verbatim rather than reconstructed, so a custom
    /// profile with its own refresh bounds and frame-time limits returns exactly as it was rather
    /// than collapsing to whichever named profile looks closest.
    /// </remarks>
    public bool TryRestore()
    {
        if (_panel == 0 || !_savedValid)
        {
            return true;
        }

        ArcSyncProfileParams restore = _saved;
        int result = _setProfile(_panel, &restore);
        if (result != ResultSuccess)
        {
            PluginTrace.Error("arcsync", $"Restore of profile {_saved.Profile} failed with 0x{result:x}.");
            return false;
        }

        PluginTrace.Info("arcsync", $"Variable refresh restored to profile {_saved.Profile}.");
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_api != 0 && _close is not null)
        {
            _ = _close(_api);
            _api = 0;
        }

        _panel = 0;
        if (_library != 0)
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }
    }

    private bool TryBind()
    {
        // Resolved as addresses first and cast at the end, because a function-pointer type cannot
        // be a generic argument and so cannot be threaded through one shared helper.
        if (!TryGet("ctlInit", out nint init)
            || !TryGet("ctlClose", out nint close)
            || !TryGet("ctlEnumerateDevices", out nint enumerateDevices)
            || !TryGet("ctlEnumerateDisplayOutputs", out nint enumerateOutputs)
            || !TryGet("ctlGetIntelArcSyncInfoForMonitor", out nint monitorInfo)
            || !TryGet("ctlGetIntelArcSyncProfile", out nint getProfile)
            || !TryGet("ctlSetIntelArcSyncProfile", out nint setProfile))
        {
            return false;
        }

        _init = (delegate* unmanaged[Cdecl]<CtlInitArgs*, nint*, int>)init;
        _close = (delegate* unmanaged[Cdecl]<nint, int>)close;
        _enumerateDevices = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)enumerateDevices;
        _enumerateOutputs = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)enumerateOutputs;
        _monitorInfo = (delegate* unmanaged[Cdecl]<nint, ArcSyncMonitorParams*, int>)monitorInfo;
        _getProfile = (delegate* unmanaged[Cdecl]<nint, ArcSyncProfileParams*, int>)getProfile;
        _setProfile = (delegate* unmanaged[Cdecl]<nint, ArcSyncProfileParams*, int>)setProfile;
        return true;

        bool TryGet(string name, out nint address)
        {
            if (NativeLibrary.TryGetExport(_library, name, out address))
            {
                return true;
            }

            PluginTrace.Warn("arcsync", $"Entry point {name} is missing.");
            return false;
        }
    }

    /// <remarks>
    /// Both enumerations are two-call: the count is asked for with a null buffer and only then
    /// fetched. Passing a buffer straight away returns nothing, which cost real time to find.
    /// <para>
    /// The panel is chosen by which output answers rather than by index, because every unattached
    /// connector answers <c>CTL_RESULT_ERROR_KMD_CALL</c> — the reference unit enumerates twelve
    /// outputs of which one is real. An external display when docked is a different output, so this
    /// deliberately selects the first output that both answers and reports support.
    /// </para>
    /// </remarks>
    private bool TrySelectPanel()
    {
        uint deviceCount = 0;
        if (_enumerateDevices(_api, &deviceCount, null) != ResultSuccess || deviceCount == 0)
        {
            PluginTrace.Warn("arcsync", "No graphics adapters enumerated.");
            return false;
        }

        deviceCount = Math.Min(deviceCount, MaxDevices);
        nint* devices = stackalloc nint[MaxDevices];
        if (_enumerateDevices(_api, &deviceCount, devices) != ResultSuccess)
        {
            PluginTrace.Warn("arcsync", "Adapter handles could not be fetched.");
            return false;
        }

        // Allocated once outside the loop; a stackalloc per adapter would grow the frame with the
        // number of adapters rather than staying bounded.
        nint* outputs = stackalloc nint[MaxOutputs];
        int unattached = 0;
        for (uint d = 0; d < deviceCount; d++)
        {
            uint outputCount = 0;
            if (_enumerateOutputs(devices[d], &outputCount, null) != ResultSuccess || outputCount == 0)
            {
                continue;
            }

            outputCount = Math.Min(outputCount, MaxOutputs);
            if (_enumerateOutputs(devices[d], &outputCount, outputs) != ResultSuccess)
            {
                continue;
            }

            for (uint o = 0; o < outputCount; o++)
            {
                ArcSyncMonitorParams monitor = default;
                monitor.Size = (uint)sizeof(ArcSyncMonitorParams);
                int result = _monitorInfo(outputs[o], &monitor);
                if (result == ResultKmdCall)
                {
                    unattached++;
                    continue;
                }

                if (result != ResultSuccess || monitor.IsSupported == 0)
                {
                    continue;
                }

                _panel = outputs[o];
                CaptureProfile();
                PluginTrace.Info(
                    "arcsync",
                    $"Variable refresh available at {monitor.MinimumHz:0}-{monitor.MaximumHz:0} Hz "
                    + $"(adapter {d}, output {o}).");
                return true;
            }
        }

        PluginTrace.Info(
            "arcsync",
            $"No variable-refresh capable output; {unattached} unattached connectors.");
        return false;
    }

    private void CaptureProfile()
    {
        ArcSyncProfileParams profile = default;
        profile.Size = (uint)sizeof(ArcSyncProfileParams);
        if (_getProfile(_panel, &profile) != ResultSuccess)
        {
            PluginTrace.Warn("arcsync", "Profile could not be captured; restore will be skipped.");
            return;
        }

        _saved = profile;
        _savedValid = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CtlInitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public fixed byte ApplicationUid[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ArcSyncMonitorParams
    {
        public uint Size;
        public byte Version;

        /// <summary>One byte in C, so a managed <c>bool</c> here would be four and shift the floats.</summary>
        public byte IsSupported;
        public float MinimumHz;
        public float MaximumHz;
        public uint MaxFrameTimeIncreaseUs;
        public uint MaxFrameTimeDecreaseUs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ArcSyncProfileParams
    {
        public uint Size;
        public byte Version;
        public int Profile;
        public float MaximumHz;
        public float MinimumHz;
        public uint MaxFrameTimeIncreaseUs;
        public uint MaxFrameTimeDecreaseUs;
    }
}
