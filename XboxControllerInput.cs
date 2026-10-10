using System.Numerics;
using System.Runtime.InteropServices;

namespace ForzavistaFreeRoam;

internal static class XboxControllerInput
{
    internal const uint DPadUp = 0x0001;
    internal const uint DPadDown = 0x0002;
    internal const uint DPadLeft = 0x0004;
    internal const uint DPadRight = 0x0008;
    internal const uint Menu = 0x0010;
    internal const uint View = 0x0020;
    internal const uint LeftStick = 0x0040;
    internal const uint RightStick = 0x0080;
    internal const uint LeftBumper = 0x0100;
    internal const uint RightBumper = 0x0200;
    internal const uint A = 0x1000;
    internal const uint B = 0x2000;
    internal const uint X = 0x4000;
    internal const uint Y = 0x8000;
    internal const uint LeftTrigger = 0x00010000;
    internal const uint RightTrigger = 0x00020000;

    private const byte TriggerThreshold = 96;

    private static readonly (uint Mask, string Name)[] Names =
    [
        (LeftTrigger, "LT"), (RightTrigger, "RT"),
        (LeftBumper, "LB"), (RightBumper, "RB"),
        (DPadUp, "D-pad Up"), (DPadDown, "D-pad Down"),
        (DPadLeft, "D-pad Left"), (DPadRight, "D-pad Right"),
        (LeftStick, "LS"), (RightStick, "RS"),
        (View, "View"), (Menu, "Menu"),
        (A, "A"), (B, "B"), (X, "X"), (Y, "Y")
    ];

    internal static bool TryGetFirstConnected(out int controllerIndex, out uint buttons)
    {
        for (var index = 0; index < 4; index++)
        {
            if (XInputGetState((uint)index, out var state) != 0) continue;
            controllerIndex = index;
            buttons = state.Gamepad.Buttons;
            if (state.Gamepad.LeftTrigger >= TriggerThreshold) buttons |= LeftTrigger;
            if (state.Gamepad.RightTrigger >= TriggerThreshold) buttons |= RightTrigger;
            return true;
        }

        controllerIndex = -1;
        buttons = 0;
        return false;
    }

    internal static int ButtonCount(uint mask) => BitOperations.PopCount(mask);

    internal static string Display(uint mask)
    {
        if (mask == 0) return "None";
        return string.Join("+", Names.Where(item => (mask & item.Mask) != 0).Select(item => item.Name));
    }

    internal static bool TryParse(string text, out uint mask)
    {
        mask = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts)
        {
            var match = Names.FirstOrDefault(item => item.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (match.Mask == 0) return false;
            mask |= match.Mask;
        }
        return mask != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        internal uint PacketNumber;
        internal XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        internal ushort Buttons;
        internal byte LeftTrigger;
        internal byte RightTrigger;
        internal short ThumbLX;
        internal short ThumbLY;
        internal short ThumbRX;
        internal short ThumbRY;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
}
