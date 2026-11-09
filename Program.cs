using System.Diagnostics;
using System.Text.Json;

namespace DualSenseStudio;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--verify", var result]) return Verify(result);
        if (args is ["--calibrate-r2", var requested, var calibrationResult])
        {
            try
            {
                using var window = new Form();
                string outcome = requested == "restore"
                    ? NativeR2Calibration.Restore(window.Handle)
                    : NativeR2Calibration.Apply(int.Parse(requested), window.Handle);
                File.WriteAllText(calibrationResult, outcome);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(calibrationResult, ex.ToString());
                return 1;
            }
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static int Verify(string result)
    {
        try
        {
            ControllerProfile profile = new();
            byte[] report = new byte[64];
            report[0] = 1;
            report[1] = report[2] = report[3] = report[4] = 128;
            report[5] = 128;
            report[6] = 229;
            report[8] = 0x21;
            report[9] = 0x21;
            if (!GamepadMapping.TryParse(report, out PhysicalReport raw)) throw new InvalidDataException("Relatório USB rejeitado.");
            Xbox360State mapped = GamepadMapping.Map(raw, profile);
            if (mapped.RightTrigger != 255 || mapped.LeftTrigger != 128 || mapped.Buttons != 0x1119 || mapped.LeftX != 0)
                throw new InvalidDataException($"Mapeamento inesperado: {mapped}.");

            for (int i = 1; i <= 255; i++)
            {
                if (profile.R2.MapTrigger((byte)i) < profile.R2.MapTrigger((byte)(i - 1)))
                    throw new InvalidDataException("O gatilho deixou de crescer de forma monotônica.");
            }

            var radial = new AxisSettings(Inner: 10, End: 90);
            if (radial.MapStick(new(0.04, -0.04), DeadzoneShape.Radial) != default ||
                Math.Abs(radial.MapStick(new(0.8, 0), DeadzoneShape.Radial).X - 0.875) > 1e-8)
                throw new InvalidDataException("Zona morta radial inesperada.");

            int[] map = GamepadMapping.DefaultButtons();
            map[0] = 15;
            if ((GamepadMapping.Map(raw, profile with { Buttons = map }).Buttons & 0x1000) != 0)
                throw new InvalidDataException("O botão desativado ainda aparece na saída.");

            var saved = JsonSerializer.Deserialize<ControllerProfile>(JsonSerializer.Serialize(profile));
            if (saved?.R2.End != 90 || saved.Buttons.Length != GamepadMapping.PhysicalNames.Length)
                throw new InvalidDataException("O perfil não foi preservado.");

            string hardware = "DualSense USB indisponível.";
            string timing = "";
            var paths = DualSenseDevice.EnumerateDevicePaths();
            if (paths.Length > 0)
            {
                using var device = DualSenseDevice.Open(paths[0]);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(13));
                int length = device.ReadReportAsync(report, timeout.Token).GetAwaiter().GetResult();
                if (!GamepadMapping.TryParse(report.AsSpan(0, length), out _))
                    throw new InvalidDataException("O relatório do DualSense conectado não pôde ser decodificado.");
                hardware = $"DualSense USB real: {length} bytes lidos e decodificados.";
                var sample = new DelayCapture();
                while (!sample.Finished)
                {
                    length = device.ReadReportAsync(report, timeout.Token).GetAwaiter().GetResult();
                    long received = Stopwatch.GetTimestamp();
                    if (length == 0) throw new IOException("O controle foi desconectado durante o teste.");
                    if (!GamepadMapping.TryParse(report.AsSpan(0, length), out var input)) continue;
                    GamepadMapping.Map(input, profile);
                    sample.Record(received, Stopwatch.GetTimestamp());
                }
                timing = $"\n\nTeste nativo de 10 segundos:\n{sample.Result()}";
            }

            string virtualStatus;
            try
            {
                using var output = new VirtualOutput();
                output.Submit(default);
                virtualStatus = "Saída Xbox 360 virtual iniciada e encerrada com relatório neutro.";
            }
            catch (InvalidOperationException ex) when (ex.InnerException is Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException)
            {
                virtualStatus = "Saída virtual: cliente carregou, ViGEmBus ausente.";
            }

            using var isolation = new InputIsolation();
            string hidHide = isolation.IsAvailable ? "HidHide acessível." : "HidHide ausente ou inacessível.";
            File.WriteAllText(result, $"Mapeamento, perfis e zonas mortas verificados. {hardware}{timing}\n\n{virtualStatus}\n{hidHide}");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(result, ex.ToString());
            return 1;
        }
    }
}

internal sealed class DelayCapture
{
    private readonly double[] processing = new double[131_072];
    private readonly double[] interval = new double[131_072];
    private readonly bool[] submitted = new bool[131_072];
    private int count;
    private long previous;

    public long Start { get; } = Stopwatch.GetTimestamp();
    public bool Finished => Stopwatch.GetElapsedTime(Start).TotalSeconds >= 10;

    public void Record(long received, long sent, bool sentToVirtual = false)
    {
        int index = Volatile.Read(ref count);
        if (received < Start || received - Start > 10 * Stopwatch.Frequency || index >= processing.Length) return;
        processing[index] = Stopwatch.GetElapsedTime(received, sent).TotalMilliseconds;
        submitted[index] = sentToVirtual;
        interval[index] = previous == 0 ? 0 : Stopwatch.GetElapsedTime(previous, received).TotalMilliseconds;
        previous = received;
        Volatile.Write(ref count, index + 1);
    }

    public string Result(bool virtualMode = false)
    {
        int length = Volatile.Read(ref count);
        if (length < 2) return "Não houve relatórios suficientes para medir.";

        double[] proc = processing[..length];
        double[] virtualTimes = virtualMode
            ? Enumerable.Range(0, length).Where(index => submitted[index]).Select(index => proc[index]).ToArray()
            : [];
        double[] periods = interval[1..length];
        double hz = 1000 / periods.Average();
        Array.Sort(proc);
        Array.Sort(periods);
        string limit = length == processing.Length ? " · limite de amostras atingido" : "";
        string virtualSummary = "";
        if (virtualMode)
        {
            Array.Sort(virtualTimes);
            virtualSummary = virtualTimes.Length == 0
                ? "\nSaída virtual  |  Nenhuma mudança registrada. Mova um analógico ou aperte os gatilhos durante o teste."
                : $"\nSaída virtual  |  {virtualTimes.Length:N0} mudanças enviadas  ·  p95 {Percentile(virtualTimes, 0.95):F4} ms até o envio";
        }
        return $"{length:N0} relatórios  •  {hz:F1} Hz observados{limit}\n\n" +
            $"Tempo no app  |  p50 {Percentile(proc, 0.50):F4} ms  ·  p95 {Percentile(proc, 0.95):F4} ms  ·  p99 {Percentile(proc, 0.99):F4} ms\n" +
            $"Intervalo USB  |  p50 {Percentile(periods, 0.50):F3} ms  ·  p95 {Percentile(periods, 0.95):F3} ms  ·  p99 {Percentile(periods, 0.99):F3} ms" + virtualSummary;
    }

    private static double Percentile(double[] values, double p) => values[(int)Math.Ceiling(values.Length * p) - 1];
}
