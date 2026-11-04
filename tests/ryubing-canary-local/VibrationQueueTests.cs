using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.Hid.Types;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryubing.Canary.Local.Tests;

[TestFixture]
public class VibrationQueueTests
{
    private const PlayerIndex Player = PlayerIndex.Player1;

    private static NpadDevices CreateQueueOwner()
    {
        // These queue operations need neither an emulated Switch nor native devices.
        // Bypass the kernel-backed constructor and initialize only their public state.
        NpadDevices devices = (NpadDevices)RuntimeHelpers.GetUninitializedObject(typeof(NpadDevices));
        devices.RumbleQueues = new Dictionary<PlayerIndex, ConcurrentQueue<(VibrationValue, VibrationValue)>>
        {
            [Player] = new(),
        };
        devices.LastVibrationValues = new();
        return devices;
    }

    private static VibrationValue Effect(float amplitude) => new()
    {
        AmplitudeLow = amplitude,
        FrequencyLow = 160,
        AmplitudeHigh = amplitude,
        FrequencyHigh = 320,
    };

    [Test]
    public void NeutralHasZeroAmplitudeAndValidFrequencies()
    {
        VibrationValue neutral = CreateQueueOwner().GetLastVibrationValue(Player, 0);
        Assert.Multiple(() =>
        {
            Assert.That(neutral.AmplitudeLow, Is.Zero);
            Assert.That(neutral.AmplitudeHigh, Is.Zero);
            Assert.That(neutral.FrequencyLow, Is.EqualTo(160));
            Assert.That(neutral.FrequencyHigh, Is.EqualTo(320));
        });
    }

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    public void FirstSingleSideCommandLeavesOtherSideStopped(byte side)
    {
        NpadDevices devices = CreateQueueOwner();
        devices.UpdateRumbleQueue(Player, new() { [side] = Effect(0.5f) });
        Assert.That(devices.GetLastVibrationValue(Player, side).AmplitudeLow, Is.EqualTo(0.5f));
        Assert.That(devices.GetLastVibrationValue(Player, (byte)(1 - side)).AmplitudeLow, Is.Zero);
    }

    [Test]
    public void IndependentStopsRetainOtherSideUntilItAlsoStops()
    {
        NpadDevices devices = CreateQueueOwner();
        devices.UpdateRumbleQueue(Player, new() { [0] = Effect(0.6f), [1] = Effect(0.8f) });
        devices.RumbleQueues[Player].Clear();

        devices.UpdateRumbleQueue(Player, new() { [0] = Effect(0) });
        Assert.That(devices.RumbleQueues[Player].TryDequeue(out var leftStop), Is.True);
        Assert.That(leftStop.Item1.AmplitudeLow, Is.Zero);
        Assert.That(leftStop.Item2.AmplitudeLow, Is.EqualTo(0.8f));

        devices.UpdateRumbleQueue(Player, new() { [1] = Effect(0) });
        Assert.That(devices.RumbleQueues[Player].TryDequeue(out var bothStop), Is.True);
        Assert.That(bothStop.Item1.AmplitudeLow, Is.Zero);
        Assert.That(bothStop.Item2.AmplitudeLow, Is.Zero);
    }

    [Test]
    public void RepeatedStopDoesNotInventOrQueueAnotherEffect()
    {
        NpadDevices devices = CreateQueueOwner();
        devices.UpdateRumbleQueue(Player, new() { [0] = Effect(0), [1] = Effect(0) });
        devices.RumbleQueues[Player].Clear();
        devices.UpdateRumbleQueue(Player, new() { [0] = Effect(0) });
        Assert.That(devices.RumbleQueues[Player].IsEmpty, Is.True);
    }
}
