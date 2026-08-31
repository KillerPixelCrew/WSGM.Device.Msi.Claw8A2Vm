using WSGM.Device.Msi.Claw8A2Vm;

namespace WSGM.Device.Tests;

public sealed class ArcSyncLayoutTests
{
    // Measured from igcl_api.h itself on 2026-08-30 with offsetof and sizeof, against the header at
    // intel/drivers.gpu.control-library. Every IGCL call passes the caller's own sizeof in a Size
    // field and the driver refuses a mismatch -- and that refusal looks exactly like "this machine
    // has no variable refresh", so drift here removes the feature silently rather than loudly.
    [Theory]
    [InlineData("ctl_init_args_t", 36)]
    [InlineData("ctl_intel_arc_sync_monitor_params_t", 24)]
    [InlineData("ctl_intel_arc_sync_profile_params_t", 28)]
    public void ManagedMirrors_MatchTheNativeHeaderExactly(string native, int expected)
    {
        (int init, int monitor, int profile) = ArcSyncTransport.NativeStructureSizes;
        int actual = native switch
        {
            "ctl_init_args_t" => init,
            "ctl_intel_arc_sync_monitor_params_t" => monitor,
            _ => profile,
        };

        Assert.Equal(expected, actual);
    }
}
