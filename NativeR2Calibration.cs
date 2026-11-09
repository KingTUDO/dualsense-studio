using System.Runtime.InteropServices;
using System.Text.Json;
using Vortice.DirectInput;

namespace DualSenseStudio;

internal static class NativeR2Calibration
{
    private const int AxisUsage = 0x00010034;
    private const int PhysicalMaximum = 255;
    private static readonly string BackupPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DualSenseStudio", "native-r2-calibration.json");

    public static string Apply(int percentage, nint window)
    {
        using var directInput = DInput.DirectInput8Create();
        Guid device = FindDevice(directInput);
        using var pad = directInput.CreateDevice(device);
        AxisCalibration previous = Read(pad);
        AxisBackup backup = ReadBackup(device) ?? new AxisBackup(device, previous);
        int end = PhysicalMaximum * Math.Clamp(percentage, 1, 100) / 100;
        int range = backup.Calibration.Maximum - backup.Calibration.Minimum;
        if (range <= 0 || end <= backup.Calibration.Minimum + 1)
            throw new InvalidOperationException("O limite selecionado é baixo demais para calibrar o R2 no Windows.");

        int center = backup.Calibration.Minimum + (int)Math.Round(
            (backup.Calibration.Center - backup.Calibration.Minimum) * (end - backup.Calibration.Minimum) / (double)range);
        var updated = new AxisCalibration(backup.Calibration.Minimum, center, end);
        if (!File.Exists(BackupPath)) SaveBackup(backup);
        ApplyAndCheck(directInput, pad, device, previous, updated, window);
        return $"R2 nativo calibrado em {percentage}%. Feche e reabra o Rocket League para testar.";
    }

    public static string Restore(nint window)
    {
        using var directInput = DInput.DirectInput8Create();
        Guid device = FindDevice(directInput);
        AxisBackup? backup = ReadBackup(device);
        if (backup is null) return "Nenhum ajuste nativo do R2 salvo pelo app.";
        using var pad = directInput.CreateDevice(device);
        ApplyAndCheck(directInput, pad, device, Read(pad), backup.Calibration, window);
        File.Delete(BackupPath);
        return "Calibração original do R2 restaurada. Reabra o jogo.";
    }

    private static Guid FindDevice(IDirectInput8 input)
    {
        var devices = input.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
            .Where(device => device.ProductGuid.ToString("N").StartsWith("0ce6054c", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (devices.Length != 1)
            throw new InvalidOperationException("Conecte um único DualSense padrão visível no Windows para calibrar o R2.");
        return devices[0].InstanceGuid;
    }

    private static void ApplyAndCheck(IDirectInput8 input, IDirectInputDevice8 pad, Guid device, AxisCalibration previous,
        AxisCalibration updated, nint window)
    {
        Write(pad, updated);
        try
        {
            Notify(input.NativePointer, window);
            using var freshInput = DInput.DirectInput8Create();
            using var freshPad = freshInput.CreateDevice(device);
            if (Read(freshPad) != updated)
                throw new InvalidOperationException("O Windows não confirmou a calibração em uma nova sessão DirectInput.");
        }
        catch
        {
            Write(pad, previous);
            Notify(input.NativePointer, window);
            throw;
        }
    }

    private static AxisBackup? ReadBackup(Guid device)
    {
        if (!File.Exists(BackupPath)) return null;
        var backup = JsonSerializer.Deserialize<AxisBackup>(File.ReadAllText(BackupPath));
        if (backup is null || backup.Device != device)
            throw new InvalidOperationException("O ajuste original salvo pertence a outra conexão. Reconecte o DualSense à porta USB usada para aplicar a calibração.");
        return backup;
    }

    private static void SaveBackup(AxisBackup backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        using var stream = new FileStream(BackupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, backup);
        stream.Flush(flushToDisk: true);
    }

    private static AxisCalibration Read(IDirectInputDevice8 pad)
    {
        var property = new CalibrationProperty(default);
        Check(Property(pad.NativePointer, 5, ref property));
        return property.Values;
    }

    private static void Write(IDirectInputDevice8 pad, AxisCalibration values)
    {
        var property = new CalibrationProperty(values);
        Check(Property(pad.NativePointer, 6, ref property));
    }

    private static void Check(int result)
    {
        if (result != 0) throw new InvalidOperationException($"O DirectInput recusou a calibração do R2 (0x{result:X8}).");
    }

    private static unsafe int Property(nint device, int method, ref CalibrationProperty property)
    {
        // IDirectInputDevice8 GetProperty/SetProperty. DIPROP_CALIBRATION is predefined property 11.
        var invoke = (delegate* unmanaged[Stdcall]<nint, nint, CalibrationProperty*, int>)(*(nint**)device)[method];
        fixed (CalibrationProperty* data = &property) return invoke(device, 11, data);
    }

    private static unsafe void Notify(nint input, nint window)
    {
        Guid joyConfig = new("eb0d7dfa-1990-4f27-b4d6-edf2eec4a44c");
        if (Marshal.QueryInterface(input, in joyConfig, out nint config) != 0) return;
        try
        {
            nint* methods = *(nint**)config;
            var setCooperativeLevel = (delegate* unmanaged[Stdcall]<nint, nint, uint, int>)methods[5];
            var acquire = (delegate* unmanaged[Stdcall]<nint, int>)methods[3];
            if (setCooperativeLevel(config, window, 9) != 0 || acquire(config) != 0) return;
            try
            {
                var sendNotify = (delegate* unmanaged[Stdcall]<nint, int>)methods[6];
                sendNotify(config);
            }
            finally
            {
                var unacquire = (delegate* unmanaged[Stdcall]<nint, int>)methods[4];
                unacquire(config);
            }
        }
        finally { Marshal.Release(config); }
    }

    private sealed record AxisBackup(Guid Device, AxisCalibration Calibration);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AxisCalibration(int Minimum, int Center, int Maximum);

    [StructLayout(LayoutKind.Sequential)]
    private struct CalibrationProperty(AxisCalibration values)
    {
        public int Size = 28;
        public int HeaderSize = 16;
        public int Usage = AxisUsage;
        public int How = 3;
        public AxisCalibration Values = values;
    }
}
