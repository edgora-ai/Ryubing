using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Synchronization;
using System;

namespace Ryubing.Canary.Local.Tests
{
    /// <summary>
    /// Regression tests for GPU syncpoint waiting.
    /// These do not need a GPU or a display: the manager is pure managed state.
    /// </summary>
    [TestFixture]
    public class GraphicsSynchronizationTests
    {
        [Test]
        public void WaitOnSyncpoint_TimesOut_WhenThresholdIsNotReached()
        {
            SynchronizationManager manager = new();

            bool timedOut = manager.WaitOnSyncpoint(5, 1, TimeSpan.FromMilliseconds(10));

            Assert.That(timedOut, Is.True, "Waiting on a threshold that is never reached must report a timeout.");
        }

        [Test]
        public void WaitOnSyncpoint_DoesNotWait_WhenThresholdIsAlreadyReached()
        {
            SynchronizationManager manager = new();

            uint value = manager.IncrementSyncpoint(3);

            Assert.That(value, Is.EqualTo(1));
            Assert.That(manager.WaitOnSyncpoint(3, 1, TimeSpan.FromSeconds(5)), Is.False);
        }

        [Test]
        public void WaitOnSyncpoint_LateCallbackAfterTimeout_DoesNotThrow()
        {
            SynchronizationManager manager = new();

            Assert.DoesNotThrow(() =>
            {
                for (uint i = 0; i < 50; i++)
                {
                    // Force an immediate timeout so the waiter is cancelled while a delayed
                    // callback is allowed to still run afterwards.
                    manager.WaitOnSyncpoint(i % 8, 1000, TimeSpan.Zero);

                    // Incrementing while the timed-out callback may still be in flight must not
                    // surface an ObjectDisposedException on this thread.
                    manager.IncrementSyncpoint(i % 8);
                }
            });
        }

        [Test]
        public void WaitOnSyncpoint_ReturnsWhenSignalledFromAnotherThread()
        {
            SynchronizationManager manager = new();
            const uint id = 7;

            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(20);
                manager.IncrementSyncpoint(id);
            });

            bool timedOut = manager.WaitOnSyncpoint(id, 1, TimeSpan.FromSeconds(5));

            Assert.That(timedOut, Is.False, "A waiter must complete when the syncpoint reaches the threshold.");
        }

        [Test]
        public void SyncpointIncrement_FiresReachedWaitersAndKeepsPendingOnes()
        {
            SynchronizationManager manager = new();
            const uint id = 11;

            int reached = 0;
            int pending = 0;

            manager.RegisterCallbackOnSyncpoint(id, 1, _ => reached++);
            manager.RegisterCallbackOnSyncpoint(id, 2, _ => reached++);
            manager.RegisterCallbackOnSyncpoint(id, 100, _ => pending++);

            manager.IncrementSyncpoint(id);

            Assert.That(reached, Is.EqualTo(1), "Only the waiter whose threshold was reached must fire.");
            Assert.That(pending, Is.EqualTo(0));

            manager.IncrementSyncpoint(id);

            Assert.That(reached, Is.EqualTo(2));
            Assert.That(pending, Is.EqualTo(0));

            for (int i = 0; i < 98; i++)
            {
                manager.IncrementSyncpoint(id);
            }

            Assert.That(pending, Is.EqualTo(1), "A pending waiter must survive compaction and fire once at its threshold.");

            manager.IncrementSyncpoint(id);

            Assert.That(pending, Is.EqualTo(1), "An expired waiter must not fire twice.");
        }
    }
}
