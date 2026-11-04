using Ryujinx.Common.Configuration.Hid;
using Ryujinx.HLE.HOS.Services.Hid;
using System.Collections.Generic;
using System.Numerics;
using SDL;
using static SDL.SDL3;

namespace Ryujinx.Input.SDL3
{
    internal class SDL3JoyConPair(IGamepad left, IGamepad right) : IGamepad
    {
        public GamepadFeaturesFlag Features => (left?.Features ?? GamepadFeaturesFlag.None) |
                                               (right?.Features ?? GamepadFeaturesFlag.None);

        public const string Id = "JoyConPair";
        string IGamepad.Id => Id;

        public string Name => "Nintendo Switch Dual Joy-Con (L/R)";
        public bool IsConnected => left is { IsConnected: true } && right is { IsConnected: true };

        public void Dispose()
        {
            try
            {
                left?.Dispose();
            }
            finally
            {
                right?.Dispose();
            }
        }

        public GamepadStateSnapshot GetMappedStateSnapshot()
        {
            return GetStateSnapshot();
        }

        public Vector3 GetMotionData(MotionInputId inputId)
        {
            return inputId switch
            {
                MotionInputId.Accelerometer or
                    MotionInputId.Gyroscope => left.GetMotionData(inputId),
                MotionInputId.SecondAccelerometer => right.GetMotionData(MotionInputId.Accelerometer),
                MotionInputId.SecondGyroscope => right.GetMotionData(MotionInputId.Gyroscope),
                _ => Vector3.Zero
            };
        }

        public GamepadStateSnapshot GetStateSnapshot()
        {
            return IGamepad.GetStateSnapshot(this);
        }

        public (float, float) GetStick(StickInputId inputId)
        {
            return inputId switch
            {
                StickInputId.Left => left.GetStick(StickInputId.Left),
                StickInputId.Right => right.GetStick(StickInputId.Right),
                _ => (0, 0)
            };
        }

        public bool IsPressed(GamepadButtonInputId inputId)
        {
            return left.IsPressed(inputId) || right.IsPressed(inputId);
        }

        public bool HDRumble(VibrationValue left, VibrationValue right)
        {
            // return _hdRumble?.HdRumble(left, right) ?? false;
            // TODO: Track rumble and motion on both controllers
            return false;
        }

        public bool Rumble(float lowFrequency, float highFrequency, uint durationMs)
        {
            // Both devices must receive every update, including a zero on just one side.
            // Otherwise that side retains its previous (potentially indefinite) effect.
            bool leftResult;
            bool rightResult = false;
            try
            {
                leftResult = left?.Rumble(highFrequency, highFrequency, durationMs) ?? false;
            }
            finally
            {
                // Do not short-circuit or strand the other side if one device fails.
                rightResult = right?.Rumble(lowFrequency, lowFrequency, durationMs) ?? false;
            }

            return leftResult && rightResult;
        }

        public void SetConfiguration(InputConfig configuration)
        {
            left.SetConfiguration(configuration);
            right.SetConfiguration(configuration);
        }

        public void SetLed(uint packedRgb)
        {
        }

        public void SetTriggerThreshold(float triggerThreshold)
        {
            left.SetTriggerThreshold(triggerThreshold);
            right.SetTriggerThreshold(triggerThreshold);
        }

        public static bool IsCombinable(SDL_JoystickID joyCon1, Dictionary<SDL_JoystickID, string> joyConIds, out SDL_JoystickID match)
        {
            bool isLeft = SDL3JoyCon.IsLeftJoyCon(joyCon1);
            string matchName = isLeft ? SDL3JoyCon.RightName : SDL3JoyCon.LeftName;
            match = 0;

            foreach (var joyConId in joyConIds.Keys)
            {
                if (SDL_GetGamepadNameForID(joyConId) == matchName)
                {
                    match = joyConId;
                    
                    return true;
                }
            }
            
            return false;
        }
    }
}
