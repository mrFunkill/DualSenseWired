// Optional input changes applied to the USB input report before games see it: button remapping,
// swapping/disabling sticks, stick dead zones, inverted right stick Y.
// USB input report 0x01: bytes 1-2 left stick X/Y, 3-4 right stick X/Y, 5-6 L2/R2 analog,
// byte 8 = d-pad (hat, low nibble) + Square/Cross/Circle/Triangle,
// byte 9 = L1 R1 L2 R2 Create Options L3 R3, byte 10 = PS, touchpad click, mute.

namespace DualSenseWired.Bridge;

public enum Button
{
    Cross, Circle, Square, Triangle,
    Up, Down, Left, Right,
    L1, R1, L2, R2, L3, R3,
    Create, Options, PS, Touchpad, Mute,
    LeftStick, RightStick,
}

public sealed class ButtonMap
{
    public static readonly Button[] All = Enum.GetValues<Button>();
    public static bool IsStick(Button b) => b is Button.LeftStick or Button.RightStick;

    // (byte, bit) of each button; the d-pad directions come from the hat.
    static readonly Dictionary<Button, (int Byte, int Bit)> Bits = new()
    {
        [Button.Square] = (8, 4), [Button.Cross] = (8, 5), [Button.Circle] = (8, 6), [Button.Triangle] = (8, 7),
        [Button.L1] = (9, 0), [Button.R1] = (9, 1), [Button.L2] = (9, 2), [Button.R2] = (9, 3),
        [Button.Create] = (9, 4), [Button.Options] = (9, 5), [Button.L3] = (9, 6), [Button.R3] = (9, 7),
        [Button.PS] = (10, 0), [Button.Touchpad] = (10, 1), [Button.Mute] = (10, 2),
    };

    // Hat values 0..7 clockwise from up; 8 = released.
    static readonly (bool Up, bool Right, bool Down, bool Left)[] Hat =
    {
        (true, false, false, false), (true, true, false, false), (false, true, false, false), (false, true, true, false),
        (false, false, true, false), (false, false, true, true), (false, false, false, true), (true, false, false, true),
    };

    const int LeftX = 1, RightX = 3, L2Axis = 5, R2Axis = 6;

    /// <summary>Physical input -> what games see (null = disabled). Missing entries stay as they are.</summary>
    readonly Dictionary<Button, Button?> _map;
    readonly double _deadzoneLeft, _deadzoneRight;     // 0..1 of the stick's travel
    readonly bool _invertRightY;

    public ButtonMap(IReadOnlyDictionary<Button, Button?> map, int deadzoneLeftPercent = 0, int deadzoneRightPercent = 0,
                     bool invertRightY = false)
    {
        _map = map.Where(kv => kv.Value != kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
        _deadzoneLeft = Math.Clamp(deadzoneLeftPercent, 0, 50) / 100.0;
        _deadzoneRight = Math.Clamp(deadzoneRightPercent, 0, 50) / 100.0;
        _invertRightY = invertRightY;
    }

    public bool IsIdentity => _map.Count == 0 && _deadzoneLeft == 0 && _deadzoneRight == 0 && !_invertRightY;

    Button? Target(Button b) => _map.TryGetValue(b, out var t) ? t : b;

    public void Apply(byte[] report)
    {
        if (report.Length < 11 || IsIdentity)
            return;

        // Sticks: dead zones and inversion belong to the physical stick, then the stick is routed.
        byte lx = report[LeftX], ly = report[LeftX + 1], rx = report[RightX], ry = report[RightX + 1];
        Deadzone(ref lx, ref ly, _deadzoneLeft);
        Deadzone(ref rx, ref ry, _deadzoneRight);
        if (_invertRightY)
            ry = (byte)(255 - ry);
        (byte, byte) left = (0x80, 0x80), right = (0x80, 0x80);
        void Route(Button stick, (byte, byte) value)
        {
            if (Target(stick) == Button.LeftStick)
                left = value;
            else if (Target(stick) == Button.RightStick)
                right = value;
        }
        Route(Button.LeftStick, (lx, ly));
        Route(Button.RightStick, (rx, ry));
        (report[LeftX], report[LeftX + 1]) = left;
        (report[RightX], report[RightX + 1]) = right;

        if (_map.Keys.All(IsStick))
            return;

        var pressed = new HashSet<Button>();
        foreach (var button in All)
            if (!IsStick(button) && IsPressed(report, button))
                pressed.Add(button);
        var result = new HashSet<Button>();
        foreach (var button in pressed)
            if (Target(button) is { } target && !IsStick(target))
                result.Add(target);

        // The analog trigger values follow their buttons: a trigger mapped away reads released,
        // a button mapped onto a trigger pulls it fully.
        byte l2 = Target(Button.L2) == Button.L2 ? report[L2Axis] : (byte)0;
        byte r2 = Target(Button.R2) == Button.R2 ? report[R2Axis] : (byte)0;
        if (result.Contains(Button.L2) && !(pressed.Contains(Button.L2) && Target(Button.L2) == Button.L2))
            l2 = 0xFF;
        if (result.Contains(Button.R2) && !(pressed.Contains(Button.R2) && Target(Button.R2) == Button.R2))
            r2 = 0xFF;
        report[L2Axis] = l2;
        report[R2Axis] = r2;

        report[8] = 0;
        report[9] = 0;
        report[10] &= unchecked((byte)~0x07);
        foreach (var button in result)
            if (Bits.TryGetValue(button, out var bit))
                report[bit.Byte] |= (byte)(1 << bit.Bit);
        report[8] |= EncodeHat(result.Contains(Button.Up), result.Contains(Button.Right),
                               result.Contains(Button.Down), result.Contains(Button.Left));
    }

    /// <summary>Radial dead zone, rescaled so the stick still reaches full travel.</summary>
    static void Deadzone(ref byte x, ref byte y, double zone)
    {
        if (zone <= 0)
            return;
        double dx = (x - 127.5) / 127.5, dy = (y - 127.5) / 127.5;
        double r = Math.Sqrt(dx * dx + dy * dy);
        if (r <= zone)
        {
            x = y = 0x80;
            return;
        }
        double scale = Math.Min(1, (r - zone) / (1 - zone)) / r;
        x = (byte)Math.Clamp(Math.Round(127.5 + dx * scale * 127.5), 0, 255);
        y = (byte)Math.Clamp(Math.Round(127.5 + dy * scale * 127.5), 0, 255);
    }

    static bool IsPressed(byte[] report, Button button)
    {
        if (Bits.TryGetValue(button, out var bit))
            return (report[bit.Byte] & (1 << bit.Bit)) != 0;
        int hat = report[8] & 0x0F;
        if (hat > 7)
            return false;
        var d = Hat[hat];
        return button switch
        {
            Button.Up => d.Up,
            Button.Right => d.Right,
            Button.Down => d.Down,
            Button.Left => d.Left,
            _ => false,
        };
    }

    static byte EncodeHat(bool up, bool right, bool down, bool left)
    {
        if (up && down)
            up = down = false;
        if (left && right)
            left = right = false;
        for (int i = 0; i < Hat.Length; i++)
            if (Hat[i] == (up, right, down, left))
                return (byte)i;
        return 8;
    }
}
