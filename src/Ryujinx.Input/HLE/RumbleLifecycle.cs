using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.Collections.Concurrent;

namespace Ryujinx.Input.HLE
{
    /// <summary>
    /// Owns one controller's vibration target. Ordinary effects have a short hardware lease,
    /// renewed only while input updates are healthy; stopping also forgets the previous state.
    /// </summary>
    internal sealed class RumbleLifecycle
    {
        internal const uint LeaseMilliseconds = 250;
        internal const long RenewalMilliseconds = 100;

        private readonly object _lock = new();
        private readonly Func<long> _clock;
        private IGamepad _target;
        private ConcurrentQueue<(VibrationValue, VibrationValue)> _queue;
        private float _strong;
        private float _weak;
        private bool _useHd;
        private bool _usedHd;
        private bool _ordinaryActive;
        private float _low;
        private float _high;
        private long _nextRenewal;
        private long _lastUpdate;

        private static VibrationValue Neutral => new()
        {
            AmplitudeLow = 0,
            FrequencyLow = 160,
            AmplitudeHigh = 0,
            FrequencyHigh = 320,
        };

        internal RumbleLifecycle(Func<long> clock = null)
        {
            _clock = clock ?? (() => Environment.TickCount64);
        }

        internal void Configure(IGamepad target, RumbleConfigController settings)
        {
            lock (_lock)
            {
                ConfigureCore(target, settings);
            }
        }

        private void ConfigureCore(IGamepad target, RumbleConfigController settings)
        {
            if (target == null || !target.IsConnected || settings?.EnableRumble != true)
            {
                StopCore();
                return;
            }

            // Settings objects can be recreated (or mutated) every frame. Compare values,
            // not object identity, so an unchanged profile does not interrupt held effects.
            if (!ReferenceEquals(target, _target) || _strong != settings.StrongRumble ||
                _weak != settings.WeakRumble || _useHd != settings.UseHDRumble)
            {
                StopCore();
                _target = target;
                _strong = settings.StrongRumble;
                _weak = settings.WeakRumble;
                _useHd = settings.UseHDRumble;
                _lastUpdate = _clock();
            }
        }

        internal void Update(IGamepad target, RumbleConfigController settings,
            ConcurrentQueue<(VibrationValue, VibrationValue)> queue)
        {
            lock (_lock)
            {
                ConfigureCore(target, settings);
                _queue = queue;

                bool hasLatest = false;
                (VibrationValue, VibrationValue) latest = default;
                while (queue != null && queue.TryDequeue(out (VibrationValue, VibrationValue) value))
                {
                    latest = value;
                    hasLatest = true;
                }

                if (_target == null)
                {
                    return;
                }

                long now = _clock();
                if (now - _lastUpdate >= LeaseMilliseconds)
                {
                    // An expired lease must not be replayed when a blocked/stalled input
                    // loop resumes. A genuinely new command below may start a new effect.
                    StopCore();
                    ConfigureCore(target, settings);
                    _queue = queue;
                    if (_target == null)
                    {
                        return;
                    }
                }

                _lastUpdate = now;

                if (hasLatest)
                {
                    Apply(latest, now);
                }
                else if (_ordinaryActive && now >= _nextRenewal)
                {
                    _ordinaryActive = _target.Rumble(_low, _high, LeaseMilliseconds);
                    _nextRenewal = now + RenewalMilliseconds;
                }
            }
        }

        private void Apply((VibrationValue, VibrationValue) vibration, long now)
        {
            VibrationValue left = vibration.Item1;
            VibrationValue right = vibration.Item2;
            float low = Math.Min(1f, (float)((right.AmplitudeLow * 0.85 + right.AmplitudeHigh * 0.15) * _strong));
            float high = Math.Min(1f, (float)((left.AmplitudeLow * 0.15 + left.AmplitudeHigh * 0.85) * _weak));

            left.AmplitudeLow *= _weak;
            left.AmplitudeHigh *= _strong;
            right.AmplitudeLow *= _weak;
            right.AmplitudeHigh *= _strong;

            if (_useHd && _target.HDRumble(left, right))
            {
                _usedHd = true;
                _ordinaryActive = false;
            }
            else
            {
                _low = low;
                _high = high;
                bool active = low != 0 || high != 0;
                _ordinaryActive = _target.Rumble(low, high, active ? LeaseMilliseconds : 0) && active;
                _nextRenewal = now + RenewalMilliseconds;
            }

            Logger.Debug?.Print(LogClass.Hid, $"Rumble effect: L.low={left.AmplitudeLow}, L.high={left.AmplitudeHigh}, " +
                $"R.low={right.AmplitudeLow}, R.high={right.AmplitudeHigh}");
        }

        internal void Stop(ConcurrentQueue<(VibrationValue, VibrationValue)> queue = null)
        {
            lock (_lock)
            {
                StopCore();
                queue?.Clear();
            }
        }

        private void StopCore()
        {
            IGamepad target = _target;
            bool usedHd = _usedHd;

            // Clear state before calling drivers. Even a failed stop must not be renewed.
            _target = null;
            _usedHd = false;
            _ordinaryActive = false;
            _low = 0;
            _high = 0;
            _nextRenewal = 0;
            _queue?.Clear();
            _queue = null;

            if (target == null)
            {
                return;
            }

            if (usedHd)
            {
                try
                {
                    target.HDRumble(Neutral, Neutral);
                }
                catch (Exception)
                {
                    // Device removal must not prevent ordinary stop or handle disposal.
                }
            }

            try
            {
                target.Rumble(0, 0, 0);
            }
            catch (Exception)
            {
                // Ordinary effects expire on their own after the bounded hardware lease.
            }
        }
    }
}
