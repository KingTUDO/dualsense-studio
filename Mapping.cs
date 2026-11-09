namespace DualSenseStudio;

internal enum ResponseCurve { Linear, Suave, Agressiva, Larga, ExtraLarga }
internal enum DeadzoneShape { Radial, Axial, Quadrada }

internal sealed record AxisSettings(int Inner = 0, int End = 100, int Anti = 0,
    ResponseCurve Curve = ResponseCurve.Linear)
{
    public double Transform(double magnitude)
    {
        double start = Math.Clamp(Inner, 0, 99) / 100d;
        if (magnitude <= start) return 0;

        double end = Math.Clamp(End, Math.Clamp(Inner, 0, 99) + 1, 100) / 100d;
        double scaled = Math.Min((magnitude - start) / (end - start), 1);
        double curved = Curve switch
        {
            ResponseCurve.Suave => scaled * scaled,
            ResponseCurve.Agressiva => Math.Sqrt(scaled),
            ResponseCurve.Larga => scaled * scaled * scaled,
            ResponseCurve.ExtraLarga => scaled * scaled * scaled * scaled,
            _ => scaled
        };
        double anti = Math.Clamp(Anti, 0, 99) / 100d;
        return anti + (1 - anti) * curved;
    }

    public byte MapTrigger(byte input)
    {
        // Match the lower report value that the preview can round to the requested percentage.
        int endByte = Math.Clamp(End, 1, 100) * 255 / 100;
        if (input >= endByte && input / 255d > Inner / 100d) return byte.MaxValue;
        return (byte)Math.Round(Transform(input / 255d) * 255);
    }

    public Stick MapStick(Stick stick, DeadzoneShape shape)
    {
        if (shape == DeadzoneShape.Axial)
            return new(Math.CopySign(Transform(Math.Abs(stick.X)), stick.X),
                Math.CopySign(Transform(Math.Abs(stick.Y)), stick.Y));

        double magnitude = shape == DeadzoneShape.Quadrada
            ? Math.Max(Math.Abs(stick.X), Math.Abs(stick.Y))
            : Math.Sqrt(stick.X * stick.X + stick.Y * stick.Y);
        if (magnitude == 0) return stick;

        double scale = Transform(magnitude) / magnitude;
        return new(stick.X * scale, stick.Y * scale);
    }
}

internal readonly record struct Stick(double X, double Y);

internal sealed record ControllerProfile
{
    public string Name { get; init; } = "Rocket League";
    public string Game { get; init; } = "RocketLeague.exe";
    public AxisSettings L2 { get; init; } = new();
    public AxisSettings R2 { get; init; } = new(End: 90);
    public AxisSettings LeftStick { get; init; } = new();
    public AxisSettings RightStick { get; init; } = new();
    public DeadzoneShape LeftShape { get; init; } = DeadzoneShape.Axial;
    public DeadzoneShape RightShape { get; init; } = DeadzoneShape.Axial;
    public int[] Buttons { get; init; } = GamepadMapping.DefaultButtons();
    public int LightArgb { get; init; } = Color.FromArgb(94, 191, 255).ToArgb();
    public int LightBrightness { get; init; } = 100;
}

internal readonly record struct PhysicalReport(byte L2, byte R2, Stick Left, Stick Right, uint Buttons);

internal static class GamepadMapping
{
    public static readonly string[] PhysicalNames =
    [
        "Cruz", "Círculo", "Quadrado", "Triângulo", "L1", "R1", "Criar", "Opções", "L3", "R3",
        "Direcional cima", "Direcional baixo", "Direcional esquerda", "Direcional direita", "PS", "Touchpad", "Microfone"
    ];

    public static readonly string[] OutputNames =
    [
        "A", "B", "X", "Y", "LB", "RB", "Voltar", "Menu", "LS", "RS",
        "Direcional cima", "Direcional baixo", "Direcional esquerda", "Direcional direita", "Guia Xbox", "Desativado"
    ];

    private static readonly ushort[] XboxMasks =
    [0x1000, 0x2000, 0x4000, 0x8000, 0x0100, 0x0200, 0x0020, 0x0010, 0x0040, 0x0080,
        0x0001, 0x0002, 0x0004, 0x0008, 0x0400, 0];

    private static readonly uint[] DpadMasks =
    [1u << 10, (1u << 10) | (1u << 13), 1u << 13, (1u << 11) | (1u << 13),
        1u << 11, (1u << 11) | (1u << 12), 1u << 12, (1u << 10) | (1u << 12), 0];

    public static int[] DefaultButtons() => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 15];

    public static bool TryParse(ReadOnlySpan<byte> report, out PhysicalReport physical)
    {
        physical = default;
        if (report.Length < 11 || report[0] != 0x01) return false;

        int dpad = report[8] & 0x0f;
        uint buttons = dpad < DpadMasks.Length ? DpadMasks[dpad] : 0;
        buttons |= (uint)(((report[8] >> 5) & 3) | ((report[8] >> 2) & 4) | ((report[8] >> 4) & 8));
        buttons |= (uint)((report[9] & 3) << 4);
        buttons |= (uint)((report[9] & 0xf0) << 2);
        buttons |= (uint)((report[10] & 7) << 14);

        physical = new(report[5], report[6], new(Normalize(report[1]), -Normalize(report[2])),
            new(Normalize(report[3]), -Normalize(report[4])), buttons);
        return true;
    }

    public static Xbox360State Map(PhysicalReport raw, ControllerProfile profile)
    {
        Stick left = profile.LeftStick.MapStick(raw.Left, profile.LeftShape);
        Stick right = profile.RightStick.MapStick(raw.Right, profile.RightShape);
        ushort buttons = 0;
        for (int index = 0; index < PhysicalNames.Length; index++)
        {
            if ((raw.Buttons & (1u << index)) == 0) continue;
            buttons |= XboxMasks[profile.Buttons[index]];
        }

        return new(buttons, profile.L2.MapTrigger(raw.L2), profile.R2.MapTrigger(raw.R2),
            Encode(left.X), Encode(left.Y), Encode(right.X), Encode(right.Y));
    }

    private static double Normalize(byte value) => value < 128 ? (value - 128) / 128d : (value - 128) / 127d;

    private static short Encode(double value) => (short)Math.Round(Math.Clamp(value, -1, 1) * (value < 0 ? 32768 : 32767));
}
