using NUnit.Framework;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.Input;
using Ryujinx.Input.SDL3;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ryubing.Canary.Local.Tests.Input
{
    [TestFixture]
    public class JoyConRumbleTests
    {
        [Test]
        public void RoutesHighToLeftAndLowToRightWithTheOriginalDuration()
        {
            FakeGamepad left = new();
            FakeGamepad right = new();
            SDL3JoyConPair pair = new(left, right);

            Assert.That(pair.Rumble(0.25f, 0.75f, 250), Is.True);
            Assert.That(left.Calls, Is.EqualTo(new[] { new RumbleCall(0.75f, 0.75f, 250) }));
            Assert.That(right.Calls, Is.EqualTo(new[] { new RumbleCall(0.25f, 0.25f, 250) }));
        }

        [Test]
        public void SingleSideZeroUpdatesStopThatSideWhileKeepingTheOtherActive()
        {
            FakeGamepad left = new();
            FakeGamepad right = new();
            SDL3JoyConPair pair = new(left, right);

            pair.Rumble(0.6f, 0.8f, 250);
            pair.Rumble(0.6f, 0, 250);
            pair.Rumble(0, 0.4f, 250);

            Assert.Multiple(() =>
            {
                Assert.That(left.Calls, Is.EqualTo(new[]
                {
                    new RumbleCall(0.8f, 0.8f, 250),
                    new RumbleCall(0, 0, 250),
                    new RumbleCall(0.4f, 0.4f, 250),
                }));
                Assert.That(right.Calls, Is.EqualTo(new[]
                {
                    new RumbleCall(0.6f, 0.6f, 250),
                    new RumbleCall(0.6f, 0.6f, 250),
                    new RumbleCall(0, 0, 250),
                }));
            });
        }

        [Test]
        public void FullStopIsDeliveredToBothSidesEvenWhenRepeated()
        {
            FakeGamepad left = new();
            FakeGamepad right = new();
            SDL3JoyConPair pair = new(left, right);

            pair.Rumble(1, 1, 250);
            Assert.That(pair.Rumble(0, 0, 0), Is.True);
            Assert.That(pair.Rumble(0, 0, 0), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(left.Calls.Count, Is.EqualTo(3));
                Assert.That(right.Calls.Count, Is.EqualTo(3));
                Assert.That(left.Calls[1], Is.EqualTo(new RumbleCall(0, 0, 0)));
                Assert.That(right.Calls[1], Is.EqualTo(new RumbleCall(0, 0, 0)));
                Assert.That(left.Calls[2], Is.EqualTo(new RumbleCall(0, 0, 0)));
                Assert.That(right.Calls[2], Is.EqualTo(new RumbleCall(0, 0, 0)));
            });
        }

        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(false, false)]
        public void AggregatesBothDeviceResultsWithoutShortCircuiting(bool leftResult, bool rightResult)
        {
            FakeGamepad left = new() { RumbleResult = leftResult };
            FakeGamepad right = new() { RumbleResult = rightResult };
            SDL3JoyConPair pair = new(left, right);

            Assert.That(pair.Rumble(0, 0, 0), Is.EqualTo(leftResult && rightResult));
            Assert.Multiple(() =>
            {
                Assert.That(left.Calls.Count, Is.EqualTo(1));
                Assert.That(right.Calls.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void AnExceptionOnTheLeftDoesNotStrandTheRightSide()
        {
            FakeGamepad left = new() { RumbleException = new InvalidOperationException("disconnected") };
            FakeGamepad right = new();
            SDL3JoyConPair pair = new(left, right);

            Assert.Throws<InvalidOperationException>(() => pair.Rumble(0, 0, 0));
            Assert.That(right.Calls, Is.EqualTo(new[] { new RumbleCall(0, 0, 0) }));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AMissingSideReportsFailureButStillUpdatesThePresentSide(bool missingLeft)
        {
            FakeGamepad present = new();
            SDL3JoyConPair pair = missingLeft ? new(null, present) : new(present, null);

            Assert.That(pair.Rumble(0, 0, 0), Is.False);
            Assert.That(present.Calls, Is.EqualTo(new[] { new RumbleCall(0, 0, 0) }));
        }

        [Test]
        public void DisposeReleasesBothSides()
        {
            FakeGamepad left = new();
            FakeGamepad right = new();
            SDL3JoyConPair pair = new(left, right);

            pair.Dispose();
            Assert.Multiple(() =>
            {
                Assert.That(left.DisposeCount, Is.EqualTo(1));
                Assert.That(right.DisposeCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void DisposeStillReleasesTheRightSideIfTheLeftSideThrows()
        {
            FakeGamepad left = new() { DisposeException = new InvalidOperationException("disconnected") };
            FakeGamepad right = new();
            SDL3JoyConPair pair = new(left, right);

            Assert.Throws<InvalidOperationException>(pair.Dispose);
            Assert.That(left.DisposeCount, Is.EqualTo(1));
            Assert.That(right.DisposeCount, Is.EqualTo(1));
        }

        private readonly record struct RumbleCall(float Low, float High, uint DurationMs);

        private sealed class FakeGamepad : IGamepad
        {
            public List<RumbleCall> Calls { get; } = new();
            public bool RumbleResult { get; init; } = true;
            public Exception RumbleException { get; init; }
            public Exception DisposeException { get; init; }
            public int DisposeCount { get; private set; }
            public GamepadFeaturesFlag Features => GamepadFeaturesFlag.Rumble;
            public string Id => "fake-joycon";
            public string Name => "Fake Joy-Con";
            public bool IsConnected => true;

            public bool Rumble(float lowFrequency, float highFrequency, uint durationMs)
            {
                Calls.Add(new RumbleCall(lowFrequency, highFrequency, durationMs));
                if (RumbleException != null)
                {
                    throw RumbleException;
                }

                return RumbleResult;
            }

            public void Dispose()
            {
                DisposeCount++;
                if (DisposeException != null)
                {
                    throw DisposeException;
                }
            }

            public bool HDRumble(VibrationValue left, VibrationValue right) => false;
            public bool IsPressed(GamepadButtonInputId inputId) => false;
            public (float, float) GetStick(StickInputId inputId) => (0, 0);
            public Vector3 GetMotionData(MotionInputId inputId) => Vector3.Zero;
            public void SetTriggerThreshold(float triggerThreshold) { }
            public void SetConfiguration(InputConfig configuration) { }
            public void SetLed(uint packedRgb) { }
            public GamepadStateSnapshot GetMappedStateSnapshot() => default;
            public GamepadStateSnapshot GetStateSnapshot() => default;
        }
    }
}
