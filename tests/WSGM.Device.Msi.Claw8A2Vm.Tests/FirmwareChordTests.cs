using System.Runtime.InteropServices;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

public sealed class FirmwareChordTests
{
    [Fact]
    public void SyntheticInput_MatchesTheWindowsX64Layout()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(24, Marshal.SizeOf<NativeKeyboard.KeyboardInput>());
        Assert.Equal(32, Marshal.SizeOf<NativeKeyboard.InputUnion>());
        Assert.Equal(40, Marshal.SizeOf<NativeKeyboard.Input>());
        Assert.Equal((nint)8, Marshal.OffsetOf<NativeKeyboard.Input>(nameof(NativeKeyboard.Input.Data)));
        Assert.Equal((nint)16, Marshal.OffsetOf<NativeKeyboard.KeyboardInput>(nameof(NativeKeyboard.KeyboardInput.ExtraInfo)));

        const uint marker = 0x5753474D;
        NativeKeyboard.Input release = NativeKeyboard.KeyInput(NativeKeyboard.VK_LWIN, keyUp: true, marker);
        Assert.Equal(NativeKeyboard.INPUT_KEYBOARD, release.Type);
        Assert.Equal(NativeKeyboard.VK_LWIN, (uint)release.Data.Keyboard.VirtualKey);
        Assert.Equal(NativeKeyboard.KEYEVENTF_KEYUP | NativeKeyboard.KEYEVENTF_EXTENDEDKEY, release.Data.Keyboard.Flags);
        Assert.Equal((nuint)marker, release.Data.Keyboard.ExtraInfo);
    }

    [Theory]
    [InlineData(NativeKeyboard.VK_LWIN, true, 3u)]
    [InlineData(NativeKeyboard.VK_RWIN, true, 3u)]
    [InlineData(NativeKeyboard.VK_LWIN, false, 1u)]
    [InlineData(NativeKeyboard.VK_DUMMY, false, 0u)]
    [InlineData(NativeKeyboard.VK_DUMMY, true, 2u)]
    public void SyntheticInput_MarksWindowsKeysAsExtended(uint key, bool keyUp, uint flags)
    {
        Assert.Equal(flags, NativeKeyboard.KeyInput(key, keyUp, 0).Data.Keyboard.Flags);
    }

    [Theory]
    [InlineData(NativeKeyboard.VK_G)]
    [InlineData(NativeKeyboard.VK_TAB)]
    public void FirmwareBurst_SuppressesTheOrphanAndReleasesWindowsOnce(uint target)
    {
        FirmwareChordStateMachine state = new();
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false));
        Assert.Equal(new ChordDecision(true, true, false), state.Observe(target, keyDown: false, injected: false));

        state.CommitSyntheticReleases(leftAccepted: true, rightAccepted: false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, keyDown: false, injected: true));
        Assert.Equal(new ChordDecision(true, false, false), state.Observe(target, keyDown: false, injected: false));
        Assert.True(state.Observe(NativeKeyboard.VK_LWIN, keyDown: false, injected: false).Suppress);
        Assert.Equal(default, state.Observe(target, keyDown: false, injected: false));

        // A later physical Windows press and release must pass through normally.
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, keyDown: false, injected: false));
    }

    [Theory]
    [InlineData(NativeKeyboard.VK_TAB)]
    public void PhysicalChord_IncludingRepeatedKeyDowns_PassesThrough(uint target)
    {
        FirmwareChordStateMachine state = new();
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false));
        Assert.Equal(default, state.Observe(target, keyDown: true, injected: false));
        Assert.Equal(default, state.Observe(target, keyDown: true, injected: false));
        Assert.Equal(default, state.Observe(target, keyDown: false, injected: false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, keyDown: false, injected: false));
    }

    [Theory]
    [InlineData(NativeKeyboard.VK_LWIN, true)]
    [InlineData(NativeKeyboard.VK_LWIN, false)]
    [InlineData(NativeKeyboard.VK_RWIN, true)]
    [InlineData(NativeKeyboard.VK_RWIN, false)]
    public void WinG_BlocksDownRepeatsAndUpInEitherReleaseOrder(uint windowsKey, bool windowsUpFirst)
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(windowsKey, keyDown: true, injected: false);
        ChordDecision down = state.Observe(NativeKeyboard.VK_G, keyDown: true, injected: false);
        Assert.Equal(new ChordDecision(true, windowsKey == NativeKeyboard.VK_LWIN, windowsKey == NativeKeyboard.VK_RWIN), down);
        state.CommitSyntheticReleases(down.ReleaseLeftWindows, down.ReleaseRightWindows);
        Assert.Equal(new ChordDecision(true, false, false), state.Observe(NativeKeyboard.VK_G, keyDown: true, injected: false));
        if (windowsUpFirst)
        {
            Assert.True(state.Observe(windowsKey, keyDown: false, injected: false).Suppress);
        }

        Assert.True(state.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false).Suppress);
        if (!windowsUpFirst)
        {
            Assert.True(state.Observe(windowsKey, keyDown: false, injected: false).Suppress);
        }

        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, keyDown: true, injected: false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false));
    }

    [Fact]
    public void WinG_FailedReleaseDoesNotRetryOnRepeatOrSwallowPhysicalUps()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false);
        Assert.True(state.Observe(NativeKeyboard.VK_G, keyDown: true, injected: false).ReleaseLeftWindows);
        state.CommitSyntheticReleases(false, false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, keyDown: true, injected: false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, keyDown: false, injected: false));
    }

    [Fact]
    public void WinG_WithModifiersMatchesHcKeyDownInterception()
    {
        FirmwareChordStateMachine state = new();
        state.SynchronizeModifiers(true, true, true);
        _ = state.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false);
        Assert.True(state.Observe(NativeKeyboard.VK_G, keyDown: true, injected: false).ReleaseLeftWindows);
    }

    [Fact]
    public void WinG_AfterOrphanReleaseStillConsumesGUp()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false);
        _ = state.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false);
        state.CommitSyntheticReleases(true, false);
        Assert.Equal(new ChordDecision(true, false, false), state.Observe(NativeKeyboard.VK_G, keyDown: true, injected: false));
        Assert.True(state.Observe(NativeKeyboard.VK_LWIN, keyDown: false, injected: false).Suppress);
        Assert.True(state.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false).Suppress);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void WinG_BothWindowsKeysSuppressOnlyAcceptedSyntheticReleases(bool leftAccepted, bool rightAccepted)
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        _ = state.Observe(NativeKeyboard.VK_RWIN, true, false);
        Assert.Equal(new ChordDecision(true, true, true), state.Observe(NativeKeyboard.VK_G, true, false));
        state.CommitSyntheticReleases(leftAccepted, rightAccepted);
        Assert.Equal(leftAccepted, state.Observe(NativeKeyboard.VK_LWIN, false, false).Suppress);
        Assert.Equal(rightAccepted, state.Observe(NativeKeyboard.VK_RWIN, false, false).Suppress);
        Assert.True(state.Observe(NativeKeyboard.VK_G, false, false).Suppress);
    }

    [Fact]
    public void WinG_ResetClearsAnActiveSuppression()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        _ = state.Observe(NativeKeyboard.VK_G, true, false);
        state.CommitSyntheticReleases(true, false);
        state.Reset();
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, false, false));
    }

    [Fact]
    public void WinG_InjectedGDoesNotStartOrEndPhysicalSuppression()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, true, false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, true, true));
        Assert.True(state.Observe(NativeKeyboard.VK_G, true, false).ReleaseLeftWindows);
        state.CommitSyntheticReleases(true, false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, false, true));
        Assert.True(state.Observe(NativeKeyboard.VK_G, false, false).Suppress);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ModifiedOrphans_PassThrough(bool control, bool alt, bool shift)
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false);
        state.SynchronizeModifiers(control, alt, shift);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_TAB, keyDown: false, injected: false));
    }

    [Theory]
    [InlineData(0xADu)] // Volume mute
    [InlineData(0xAEu)] // Volume down
    [InlineData(0xAFu)] // Volume up
    [InlineData(0x48u)] // Unknown future firmware target
    public void OtherKeys_PassThroughEvenWithWindowsHeld(uint target)
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false);
        Assert.Equal(default, state.Observe(target, keyDown: false, injected: false));
        Assert.Equal(default, state.Observe(target, keyDown: true, injected: false));
        Assert.Equal(default, state.Observe(target, keyDown: false, injected: false));
    }

    [Fact]
    public void InjectedChord_DoesNotChangePhysicalKeyState()
    {
        FirmwareChordStateMachine state = new();
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: true));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, keyDown: false, injected: true));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, keyDown: false, injected: true));
    }

    [Fact]
    public void FailedSyntheticRelease_LeavesThePhysicalWindowsReleaseAlone()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false);
        Assert.True(state.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false).ReleaseLeftWindows);
        state.CommitSyntheticReleases(leftAccepted: false, rightAccepted: false);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, keyDown: false, injected: false));
    }

    [Fact]
    public void HookStartup_PreservesKeysAlreadyHeldOnTheDesktop()
    {
        FirmwareChordStateMachine state = new();
        state.InitializePreexisting(true, false, false, false, false, true, true);
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_TAB, keyDown: false, injected: false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, keyDown: false, injected: false));
    }

    [Fact]
    public void Reset_DropsPendingSyntheticReleaseState()
    {
        FirmwareChordStateMachine state = new();
        _ = state.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false);
        _ = state.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false);
        state.CommitSyntheticReleases(leftAccepted: true, rightAccepted: false);
        state.Reset();
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_LWIN, keyDown: false, injected: false));
        Assert.Equal(default, state.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false));
    }
}
