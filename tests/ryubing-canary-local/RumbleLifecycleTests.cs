using NUnit.Framework;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using StickInputId = Ryujinx.Input.StickInputId;

namespace Ryubing.Canary.Local.Tests
{
    [TestFixture]
    public class RumbleLifecycleTests
    {
        private static VibrationValue Value(float low = 0, float high = 0) => new()
        {
            AmplitudeLow = low,
            FrequencyLow = 160,
            AmplitudeHigh = high,
            FrequencyHigh = 320,
        };

        private static RumbleConfigController Settings(bool hd = false) => new()
        {
            EnableRumble = true,
            StrongRumble = 1,
            WeakRumble = 1,
            UseHDRumble = hd,
        };

        private static ConcurrentQueue<(VibrationValue, VibrationValue)> VibratingQueue()
        {
            ConcurrentQueue<(VibrationValue, VibrationValue)> queue = new();
            queue.Enqueue((Value(1, 1), Value(1, 1)));
            return queue;
        }

        [Test]
        public void OrdinaryLeaseExpiresWithoutHealthyInputUpdates()
        {
            long now = 0;
            FakeGamepad pad = new(() => now);
            RumbleLifecycle lifecycle = new(() => now);
            lifecycle.Update(pad, Settings(), VibratingQueue());

            Assert.That(pad.Ordinary.Single(), Is.EqualTo((1f, 1f, 250u)));
            Assert.That(pad.IsVibrating(249), Is.True);
            Assert.That(pad.IsVibrating(250), Is.False);
        }

        [Test]
        public void HealthyUpdatesKeepHeldOrdinaryEffectAliveWithoutNewGamePackets()
        {
            long now = 0;
            FakeGamepad pad = new(() => now);
            RumbleLifecycle lifecycle = new(() => now);
            RumbleConfigController settings = Settings();
            var queue = VibratingQueue();
            lifecycle.Update(pad, settings, queue);

            for (now = 16; now <= 2000; now += 16)
            {
                lifecycle.Update(pad, settings, queue);
                Assert.That(pad.IsVibrating(now), Is.True);
            }

            Assert.That(pad.Ordinary.Count, Is.InRange(18, 21));
            Assert.That(pad.Ordinary.All(call => call.Duration == 250), Is.True);
        }

        [Test]
        public void ExpiredCachedEffectDoesNotRestartAfterInputStall()
        {
            long now = 0;
            FakeGamepad pad = new(() => now);
            RumbleLifecycle lifecycle = new(() => now);
            var queue = VibratingQueue();
            lifecycle.Update(pad, Settings(), queue);
            now = 500;
            lifecycle.Update(pad, Settings(), queue);
            now = 600;
            lifecycle.Update(pad, Settings(), queue);

            Assert.That(pad.Ordinary, Is.EqualTo(new[] { (1f, 1f, 250u), (0f, 0f, 0u) }));
            Assert.That(pad.IsVibrating(now), Is.False);
        }

        [Test]
        public void FreshCommandAfterStallCanStartNewEffect()
        {
            long now = 0;
            FakeGamepad pad = new(() => now);
            RumbleLifecycle lifecycle = new(() => now);
            var queue = VibratingQueue();
            lifecycle.Update(pad, Settings(), queue);
            now = 500;
            queue.Enqueue((Value(0.5f, 0.5f), Value(0.5f, 0.5f)));
            lifecycle.Update(pad, Settings(), queue);

            Assert.That(pad.Ordinary.Last(), Is.EqualTo((0.5f, 0.5f, 250u)));
            Assert.That(pad.IsVibrating(now), Is.True);
        }

        [Test]
        public void QueueIsDrainedToLatestStopRatherThanPlayingBacklog()
        {
            FakeGamepad pad = new();
            RumbleLifecycle lifecycle = new(() => 0);
            var queue = VibratingQueue();
            for (int i = 0; i < 1000; i++)
            {
                queue.Enqueue((Value(1, 1), Value(1, 1)));
            }
            queue.Enqueue((Value(), Value()));
            lifecycle.Update(pad, Settings(), queue);

            Assert.That(queue.IsEmpty, Is.True);
            Assert.That(pad.Ordinary, Is.EqualTo(new[] { (0f, 0f, 0u) }));
        }

        [Test]
        public void ZeroPacketStopsActiveLeaseImmediatelyAndIsNotRenewed()
        {
            long now = 0;
            FakeGamepad pad = new(() => now);
            RumbleLifecycle lifecycle = new(() => now);
            var queue = VibratingQueue();
            lifecycle.Update(pad, Settings(), queue);
            now = 20;
            queue.Enqueue((Value(), Value()));
            lifecycle.Update(pad, Settings(), queue);
            now = 120;
            lifecycle.Update(pad, Settings(), queue);
            Assert.That(pad.Ordinary, Is.EqualTo(new[] { (1f, 1f, 250u), (0f, 0f, 0u) }));
            Assert.That(pad.IsVibrating(now), Is.False);
        }

        [Test]
        public void DisconnectedTargetStopsAndForgetsExistingEffect()
        {
            FakeGamepad pad = new();
            RumbleLifecycle lifecycle = new(() => 0);
            var queue = VibratingQueue();
            lifecycle.Update(pad, Settings(), queue);
            pad.Disposed = true;
            lifecycle.Update(pad, Settings(), queue);
            Assert.That(pad.Ordinary.Last(), Is.EqualTo((0f, 0f, 0u)));
        }

        [Test]
        public void DisableWithoutNewPacketStopsAndReenableDoesNotReplay()
        {
            long now = 0;
            FakeGamepad pad = new(() => now);
            RumbleLifecycle lifecycle = new(() => now);
            RumbleConfigController settings = Settings();
            var queue = VibratingQueue();
            lifecycle.Update(pad, settings, queue);
            settings.EnableRumble = false;
            lifecycle.Update(pad, settings, queue);
            int stoppedCount = pad.Ordinary.Count;
            settings.EnableRumble = true;
            now = 100;
            lifecycle.Update(pad, settings, queue);
            now = 200;
            lifecycle.Update(pad, settings, queue);

            Assert.That(pad.Ordinary.Last(), Is.EqualTo((0f, 0f, 0u)));
            Assert.That(pad.Ordinary.Count, Is.EqualTo(stoppedCount));
        }

        [Test]
        public void StopClearsBlockedPacketsAndDoesNotReplayAfterResume()
        {
            long now = 0;
            FakeGamepad pad = new(() => now);
            RumbleLifecycle lifecycle = new(() => now);
            var queue = VibratingQueue();
            lifecycle.Update(pad, Settings(), queue);
            queue.Enqueue((Value(1, 1), Value(1, 1)));
            lifecycle.Stop(queue);
            now = 100;
            lifecycle.Update(pad, Settings(), queue);

            Assert.That(queue.IsEmpty, Is.True);
            Assert.That(pad.Ordinary, Is.EqualTo(new[] { (1f, 1f, 250u), (0f, 0f, 0u) }));
        }

        [Test]
        public void TargetSwitchStopsOldPadAndDoesNotTransferCachedEffect()
        {
            FakeGamepad oldPad = new();
            FakeGamepad newPad = new();
            RumbleLifecycle lifecycle = new(() => 0);
            var queue = VibratingQueue();
            lifecycle.Update(oldPad, Settings(), queue);
            queue.Enqueue((Value(1, 1), Value(1, 1)));
            lifecycle.Configure(newPad, Settings());
            lifecycle.Update(newPad, Settings(), queue);

            Assert.That(oldPad.Ordinary.Last(), Is.EqualTo((0f, 0f, 0u)));
            Assert.That(newPad.Ordinary, Is.Empty);
            Assert.That(queue.IsEmpty, Is.True);
            queue.Enqueue((Value(0.5f, 0.5f), Value(0.5f, 0.5f)));
            lifecycle.Update(newPad, Settings(), queue);
            Assert.That(newPad.Ordinary.Single(), Is.EqualTo((0.5f, 0.5f, 250u)));
        }

        [Test]
        public void EquivalentSettingsObjectsDoNotInterruptHeldEffect()
        {
            long now = 0;
            FakeGamepad pad = new(() => now);
            RumbleLifecycle lifecycle = new(() => now);
            var queue = VibratingQueue();
            lifecycle.Update(pad, Settings(), queue);
            now = 100;
            lifecycle.Configure(pad, Settings());
            lifecycle.Update(pad, Settings(), queue);

            Assert.That(pad.Ordinary, Is.EqualTo(new[] { (1f, 1f, 250u), (1f, 1f, 250u) }));
        }

        [Test]
        public void HdUsesOriginalPacketPathAndExplicitNeutralOnStop()
        {
            long now = 0;
            FakeGamepad pad = new(() => now) { SupportsHd = true };
            RumbleLifecycle lifecycle = new(() => now);
            var queue = VibratingQueue();
            lifecycle.Update(pad, Settings(true), queue);
            for (now = 100; now <= 1000; now += 100)
            {
                lifecycle.Update(pad, Settings(true), queue);
            }
            Assert.That(pad.Hd.Count, Is.EqualTo(1));
            Assert.That(pad.Ordinary, Is.Empty);
            lifecycle.Stop();
            var stop = pad.Hd.Last();
            Assert.Multiple(() =>
            {
                Assert.That(stop.Left.AmplitudeLow, Is.Zero);
                Assert.That(stop.Left.AmplitudeHigh, Is.Zero);
                Assert.That(stop.Right.AmplitudeLow, Is.Zero);
                Assert.That(stop.Right.AmplitudeHigh, Is.Zero);
                Assert.That(stop.Left.FrequencyLow, Is.EqualTo(160));
                Assert.That(stop.Right.FrequencyHigh, Is.EqualTo(320));
                Assert.That(pad.Ordinary.Single(), Is.EqualTo((0f, 0f, 0u)));
            });
        }

        [Test]
        public void UnsupportedHdFallsBackToFiniteOrdinaryAndRenewsWithoutRetryingHd()
        {
            long now = 0;
            FakeGamepad pad = new(() => now);
            RumbleLifecycle lifecycle = new(() => now);
            var queue = VibratingQueue();
            lifecycle.Update(pad, Settings(true), queue);
            now = 100;
            lifecycle.Update(pad, Settings(true), queue);

            Assert.That(pad.Hd.Count, Is.EqualTo(1));
            Assert.That(pad.Ordinary, Is.EqualTo(new[] { (1f, 1f, 250u), (1f, 1f, 250u) }));
        }

        [Test]
        public void StopExceptionCannotRetainRenewableStateOrSkipOrdinaryStop()
        {
            long now = 0;
            FakeGamepad pad = new(() => now) { SupportsHd = true };
            RumbleLifecycle lifecycle = new(() => now);
            var queue = VibratingQueue();
            lifecycle.Update(pad, Settings(true), queue);
            pad.ThrowOnHdStop = true;
            pad.ThrowOnOrdinaryStop = true;
            Assert.DoesNotThrow(() => lifecycle.Stop());
            Assert.That(pad.Ordinary.Last(), Is.EqualTo((0f, 0f, 0u)));
            pad.ThrowOnOrdinaryStop = false;
            now = 100;
            lifecycle.Update(pad, Settings(), queue);
            Assert.That(pad.Ordinary.Count, Is.EqualTo(1));
        }

        [Test]
        public void StopAndRenewalAreSerializedWithStopLast()
        {
            long now = 0;
            FakeGamepad pad = new(() => now);
            RumbleLifecycle lifecycle = new(() => now);
            var queue = VibratingQueue();
            lifecycle.Update(pad, Settings(), queue);
            using ManualResetEventSlim entered = new();
            using ManualResetEventSlim release = new();
            pad.OnRumble = (low, high) =>
            {
                if (low != 0 || high != 0)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                }
            };
            now = 100;
            Task renewal = Task.Run(() => lifecycle.Update(pad, Settings(), queue));
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Task stop = Task.Run(() => lifecycle.Stop());
            release.Set();
            Assert.That(Task.WaitAll(new[] { renewal, stop }, TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(pad.Ordinary.Last(), Is.EqualTo((0f, 0f, 0u)));
            now = 200;
            lifecycle.Update(pad, Settings(), queue);
            Assert.That(pad.Ordinary.Count, Is.EqualTo(3));
        }

        [Test]
        public void RealControllerConfigurationDisableStopsWithoutQueueUpdate()
        {
            FakeGamepad pad = new();
            FakeDriver driver = new(pad);
            using NpadController controller = new(null);
            StandardControllerInputConfig config = new() { Id = pad.Id, Rumble = Settings() };
            Assert.That(controller.UpdateDriverConfiguration(driver, driver, config, null), Is.True);
            controller.UpdateRumble(VibratingQueue());
            config.Rumble.EnableRumble = false;
            controller.UpdateUserConfiguration(config);

            Assert.That(pad.Ordinary.Last(), Is.EqualTo((0f, 0f, 0u)));
        }

        [Test]
        public void RealControllerReleaseStopsBeforeDisposingHandleEvenWhenStopThrows()
        {
            FakeGamepad pad = new();
            FakeDriver driver = new(pad);
            NpadController controller = new(null);
            StandardControllerInputConfig config = new() { Id = pad.Id, Rumble = Settings() };
            controller.UpdateDriverConfiguration(driver, driver, config, null);
            controller.UpdateRumble(VibratingQueue());
            pad.ThrowOnOrdinaryStop = true;
            Assert.DoesNotThrow(() => controller.Dispose());
            Assert.That(pad.Disposed, Is.True);
            Assert.That(pad.Events.TakeLast(2), Is.EqualTo(new[] { "stop", "dispose" }));
        }

        private sealed class FakeDriver : IGamepadDriver
        {
            private readonly FakeGamepad _pad;
            private readonly string[] _ids;
            public FakeDriver(FakeGamepad pad) { _pad = pad; _ids = [pad.Id]; }
            public string DriverName => "fake";
            public ReadOnlySpan<string> GamepadsIds => _ids;
            public event Action<string> OnGamepadConnected { add { } remove { } }
            public event Action<string> OnGamepadDisconnected { add { } remove { } }
            public IGamepad GetGamepad(string id) => id == _pad.Id ? _pad : null;
            public IEnumerable<IGamepad> GetGamepads() => [_pad];
            public void Dispose() { }
        }

        private sealed class FakeGamepad : IGamepad
        {
            private readonly Func<long> _clock;
            private long _until;
            private bool _active;
            internal FakeGamepad(Func<long> clock = null) => _clock = clock ?? (() => 0);
            internal readonly List<(float Low, float High, uint Duration)> Ordinary = [];
            internal readonly List<(VibrationValue Left, VibrationValue Right)> Hd = [];
            internal readonly List<string> Events = [];
            internal bool SupportsHd;
            internal bool ThrowOnHdStop;
            internal bool ThrowOnOrdinaryStop;
            internal bool Disposed;
            internal Action<float, float> OnRumble;
            internal bool IsVibrating(long at) => _active && at < _until;
            public GamepadFeaturesFlag Features => GamepadFeaturesFlag.Rumble;
            public string Id => "rumble-lifecycle-fake";
            public string Name => "Fake controller, no hardware";
            public bool IsConnected => !Disposed;
            public bool Rumble(float low, float high, uint duration)
            {
                Ordinary.Add((low, high, duration));
                bool stop = low == 0 && high == 0;
                Events.Add(stop ? "stop" : "start");
                OnRumble?.Invoke(low, high);
                if (stop && ThrowOnOrdinaryStop) throw new InvalidOperationException("fake stop failure");
                _active = !stop;
                _until = _clock() + duration;
                return true;
            }
            public bool HDRumble(VibrationValue left, VibrationValue right)
            {
                Hd.Add((left, right));
                if (ThrowOnHdStop && left.AmplitudeLow == 0 && left.AmplitudeHigh == 0 &&
                    right.AmplitudeLow == 0 && right.AmplitudeHigh == 0)
                    throw new InvalidOperationException("fake HD stop failure");
                return SupportsHd;
            }
            public bool IsPressed(GamepadButtonInputId id) => false;
            public (float, float) GetStick(StickInputId id) => default;
            public Vector3 GetMotionData(MotionInputId id) => default;
            public void SetTriggerThreshold(float value) { }
            public void SetConfiguration(InputConfig config) { }
            public void SetLed(uint rgb) { }
            public GamepadStateSnapshot GetMappedStateSnapshot() => default;
            public GamepadStateSnapshot GetStateSnapshot() => default;
            public void Dispose() { Events.Add("dispose"); Disposed = true; }
        }
    }
}
