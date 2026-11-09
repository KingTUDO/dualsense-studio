using System.Diagnostics;
using System.Text.Json;

namespace DualSenseStudio;

internal sealed class MainForm : Form
{
    internal static readonly Color Canvas = Color.FromArgb(246, 247, 251);
    internal static readonly Color Surface = Color.White;
    internal static readonly Color TextColor = Color.FromArgb(29, 30, 49);
    internal static readonly Color Muted = Color.FromArgb(91, 95, 119);
    internal static readonly Color Accent = Color.FromArgb(91, 67, 203);

    private readonly ComboBox mode = ListBox(["Nativo · leitura e luz", "Virtual · saída Xbox 360"]);
    private readonly ComboBox controllers = ListBox([]);
    private readonly ComboBox presets = ListBox([]);
    private readonly Button connect = ActionButton("Conectar", prominent: true);
    private readonly Button refresh = ActionButton("Atualizar");
    private readonly CheckBox exclusive = Check("Ocultar controle físico no jogo, Epic e Steam");
    private readonly CheckBox autoProfile = Check("Selecionar pelo jogo em primeiro plano");
    private readonly Label status = LabelText("Pronto.", Muted);
    private readonly Label driverStatus = LabelText("", Muted);
    private readonly Label delayResult = LabelText("Conecte o controle para medir.", TextColor);
    private readonly Label conflict = LabelText("", Muted);
    private readonly Button delayButton = ActionButton("Medir por 10 segundos", prominent: true);
    private readonly Button applyLight = ActionButton("Aplicar luz", prominent: true);
    private readonly Button colorButton = ActionButton("Escolher cor");
    private readonly LightPreview swatch = new();
    private readonly TextBox profileName = TextEntry(190);
    private readonly TextBox processName = TextEntry(205);
    private readonly NumericUpDown brightness = Percentage(100);
    private readonly AxisEditor l2 = new("L2", false);
    private readonly AxisEditor r2 = new("R2", false);
    private readonly AxisEditor ls = new("Analógico esquerdo", true);
    private readonly AxisEditor rs = new("Analógico direito", true);
    private readonly ComboBox[] bindings = new ComboBox[GamepadMapping.PhysicalNames.Length];
    private readonly TriggerView triggerView = new();
    private readonly StickView stickView = new();
    private readonly InputIsolation isolation = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 50 };
    private readonly string profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DualSenseStudio", "profiles.json");
    private readonly List<ControllerProfile> profiles = [];

    private ControllerProfile active = new();
    private CancellationTokenSource? stop;
    private Task? session;
    private DualSenseDevice? device;
    private DelayCapture? capture;
    private DelayCapture? displayedCapture;
    private string? nativeDelayResult;
    private string? virtualDelayResult;
    private int rawTriggers;
    private int mappedTriggers;
    private long rawSticks;
    private long mappedSticks;
    private Color light = Color.FromArgb(94, 191, 255);
    private bool loading;
    private bool closing;
    private bool applyingLight;
    private bool measuringVirtual;
    private int uiTicks;

    public MainForm()
    {
        Text = "DualSense Studio";
        Size = new(1090, 845);
        MinimumSize = new(1000, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new("Segoe UI", 10f);
        BackColor = Canvas;
        ForeColor = TextColor;
        AutoScaleMode = AutoScaleMode.Dpi;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new(SizeType.Absolute, 212));
        shell.ColumnStyles.Add(new(SizeType.Percent, 100));
        Controls.Add(shell);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(25, 18, 23, 7), Margin = Padding.Empty, BackColor = Canvas };
        root.RowStyles.Add(new(SizeType.Absolute, 91));
        root.RowStyles.Add(new(SizeType.Absolute, 104));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 36));
        shell.Controls.Add(root, 1, 0);

        var heading = new StudioHeading { Dock = DockStyle.Fill, Margin = new(0, 0, 0, 10) };
        root.Controls.Add(heading, 0, 0);

        var connectionCard = new StudioCard { Dock = DockStyle.Fill, Padding = new(12, 5, 12, 5), Margin = new(0, 0, 0, 9), BackColor = Canvas };
        var connection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, BackColor = Color.Transparent };
        connection.RowStyles.Add(new(SizeType.Absolute, 41));
        connection.RowStyles.Add(new(SizeType.Percent, 100));
        mode.Width = 210;
        mode.AccessibleName = "Modo do controle";
        controllers.Width = 154;
        controllers.AccessibleName = "DualSense por USB";
        connection.Controls.Add(Horizontal(mode, controllers, refresh, connect), 0, 0);
        connection.Controls.Add(VerticalCompact(exclusive, driverStatus), 0, 1);
        connectionCard.Controls.Add(connection);
        root.Controls.Add(connectionCard, 0, 1);

        var viewport = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Canvas };
        (Panel Page, string Title, string Subtitle)[] sections =
        [
            (TriggerTab(), "Gatilhos", "Faixa de resposta e calibração do R2."),
            (StickTab(), "Analógicos", "Curvas e zonas mortas dos dois analógicos."),
            (BindingTab(), "Botões", "Mapeamento para a saída virtual."),
            (LightTab(), "Luz", "Cor e brilho da barra do DualSense."),
            (ProfilesTab(), "Perfis e jogos", "Configurações por jogo e isolamento da entrada."),
            (DelayTab(), "Teste de delay", "Medição da entrada USB e do envio virtual.")
        ];
        var sidebar = new StudioSidebar(sections.Select(section => section.Title).ToArray());
        void SelectSection(int index)
        {
            var section = sections[index];
            foreach (var item in sections) item.Page.Visible = item.Page == section.Page;
            section.Page.BringToFront();
            heading.SetSection(section.Title, section.Subtitle, index + 1);
        }
        foreach (var section in sections) viewport.Controls.Add(section.Page);
        sidebar.Selected += SelectSection;
        shell.Controls.Add(sidebar, 0, 0);
        root.Controls.Add(viewport, 0, 2);
        SelectSection(0);
        status.Dock = DockStyle.Fill;
        status.AutoEllipsis = true;
        status.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(status, 0, 3);

        foreach (AxisEditor editor in new[] { l2, r2, ls, rs }) editor.SettingsChanged += (_, _) => SyncProfile();
        profileName.TextChanged += (_, _) => SyncProfile();
        processName.TextChanged += (_, _) => SyncProfile();
        brightness.ValueChanged += (_, _) => { UpdateSwatch(); SyncProfile(); };
        mode.SelectedIndexChanged += (_, _) => UpdateControls();
        connect.Click += (_, _) => ToggleConnection();
        refresh.Click += (_, _) => RefreshHardware();
        applyLight.Click += async (_, _) => await ApplyLight();
        delayButton.Click += (_, _) => StartDelayTest();
        timer.Tick += (_, _) => UpdateLive();
        FormClosing += CloseSafely;
        Shown += (_, _) =>
        {
            LoadProfiles();
            RefreshHardware();
            try
            {
                string? recovery = isolation.Recover();
                if (recovery != null) status.Text = recovery;
            }
            catch (Exception ex) { ReportError(ex); }
            timer.Start();
        };
        UpdateControls();
    }

    private Panel TriggerTab()
    {
        var (page, content) = Tab("Gatilhos");
        content.Controls.Add(Title("Resposta analógica"));
        AddAxisPanels(content, l2, r2, triggerView);
        var applyNative = ActionButton("Aplicar R2 no Windows", prominent: true);
        var restoreNative = ActionButton("Restaurar R2");
        applyNative.Click += (_, _) => RunNativeCalibration(restore: false);
        restoreNative.Click += (_, _) => RunNativeCalibration(restore: true);
        content.Controls.Add(Horizontal(applyNative, restoreNative));
        content.Controls.Add(Description("Use no modo Nativo para aplicar o limite do R2 pelo DirectInput. Reabra o jogo após aplicar ou restaurar."));
        return page;
    }

    private void RunNativeCalibration(bool restore)
    {
        if (mode.SelectedIndex != 0)
        {
            status.Text = "Desconecte a saída virtual e selecione Nativo para calibrar o R2 no Windows.";
            return;
        }
        try
        {
            status.Text = restore ? NativeR2Calibration.Restore(Handle) : NativeR2Calibration.Apply(r2.Settings.End, Handle);
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private Panel StickTab()
    {
        var (page, content) = Tab("Analógicos");
        content.Controls.Add(Description("Escolha como a zona morta reage ao movimento. Radial usa a distância ao centro, Axial trata cada eixo e Quadrada segue o maior eixo."));
        AddAxisPanels(content, ls, rs, stickView);
        return page;
    }

    private static void AddAxisPanels(FlowLayoutPanel content, AxisEditor left, AxisEditor right, Control preview)
    {
        content.Controls.Add(Horizontal(left, right));
        content.Controls.Add(preview);
        content.ClientSizeChanged += (_, _) =>
        {
            int width = Math.Max(440, content.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 7);
            left.Width = right.Width = (width - left.Margin.Horizontal - right.Margin.Horizontal) / 2;
            preview.Width = width - left.Margin.Left - right.Margin.Right;
        };
    }

    private Panel BindingTab()
    {
        var (page, content) = Tab("Botões");
        content.Controls.Add(Description("Escolha o comando Xbox 360 para cada botão físico. Selecione Desativado para ignorar um botão na saída virtual."));
        var left = VerticalCompact();
        var right = VerticalCompact();
        for (int index = 0; index < bindings.Length; index++)
        {
            var choice = ListBox(GamepadMapping.OutputNames);
            choice.Width = 170;
            choice.SelectedIndex = GamepadMapping.DefaultButtons()[index];
            choice.SelectedIndexChanged += (_, _) => SyncProfile();
            bindings[index] = choice;
            var label = LabelText(GamepadMapping.PhysicalNames[index], TextColor);
            label.AutoSize = false;
            label.Size = new(124, 34);
            label.TextAlign = ContentAlignment.MiddleLeft;
            (index < 9 ? left : right).Controls.Add(Horizontal(label, choice));
        }
        content.Controls.Add(Horizontal(left, right));
        var reset = ActionButton("Restaurar botões padrão");
        reset.Click += (_, _) => SetButtons(GamepadMapping.DefaultButtons());
        content.Controls.Add(reset);
        return page;
    }

    private Panel LightTab()
    {
        var (page, content) = Tab("Luz");
        content.Controls.Add(Description("Controle a cor e o brilho da barra de luz do DualSense. O ajuste é enviado diretamente ao controle por USB."));
        content.Controls.Add(swatch);
        content.Controls.Add(Horizontal(colorButton));
        var colors = Horizontal();
        foreach ((string name, Color color) in new[] { ("Ciano", Color.DeepSkyBlue), ("Roxo", Color.MediumPurple),
                     ("Vermelho", Color.Tomato), ("Verde", Color.SpringGreen), ("Branco", Color.White), ("Apagar", Color.Black) })
        {
            var button = ActionButton(name);
            button.Click += (_, _) => SetLight(color);
            colors.Controls.Add(button);
        }
        content.Controls.Add(colors);
        content.Controls.Add(Horizontal(LabelText("Brilho %", TextColor), brightness, applyLight));
        colorButton.Click += (_, _) =>
        {
            using var picker = new ColorDialog { Color = light, FullOpen = true };
            if (picker.ShowDialog(this) == DialogResult.OK) SetLight(picker.Color);
        };
        return page;
    }

    private Panel ProfilesTab()
    {
        var (page, content) = Tab("Perfis e jogos");
        content.Controls.Add(Description("Salve ajustes para cada jogo. O perfil em primeiro plano é identificado pelo nome do executável, por exemplo RocketLeague.exe."));
        presets.Width = 218;
        presets.SelectedIndexChanged += (_, _) =>
        {
            if (!loading && presets.SelectedIndex >= 0) LoadProfile(profiles[presets.SelectedIndex]);
        };
        content.Controls.Add(Horizontal(LabelText("Perfil salvo", TextColor), presets));
        content.Controls.Add(Horizontal(LabelText("Nome", TextColor), profileName, LabelText("Executável", TextColor), processName));
        content.Controls.Add(autoProfile);
        var save = ActionButton("Salvar perfil", prominent: true);
        save.Click += (_, _) => SaveProfile();
        var defaultProfile = ActionButton("Carregar Rocket League padrão");
        defaultProfile.Click += (_, _) => LoadProfile(new());
        content.Controls.Add(Horizontal(save, defaultProfile));
        content.Controls.Add(Title("Entrada do jogo e dos launchers"));
        content.Controls.Add(Description("No modo virtual, marque a opção de ocultar o DualSense físico antes de conectar. Ela usa HidHide. Abra o jogo depois para ele detectar a nova saída."));
        content.Controls.Add(Description("Para jogos Steam, abra Propriedades → Controle → Desativar Steam Input. Abra a edição Epic pelo Epic Games Launcher, fora dos atalhos da Steam. O isolamento trata o controle físico. A saída virtual ainda pode aparecer para os launchers."));
        content.Controls.Add(Description("Alguns leitores de Raw Input ainda veem o físico. Confirme que cada comando aciona só uma vez no jogo."));
        content.Controls.Add(conflict);
        var guide = ActionButton("Guia de isolamento");
        guide.Click += (_, _) => OpenUrl("https://docs.nefarius.at/projects/HidHide/Simple-Setup-Guide/");
        content.Controls.Add(guide);
        return page;
    }

    private Panel DelayTab()
    {
        var (page, content) = Tab("Teste de delay");
        content.Controls.Add(Title("Medição no seu PC"));
        content.Controls.Add(Description("Durante 10 segundos, o app mede o intervalo entre relatórios USB e o tempo de processamento após a leitura. No modo virtual, os relatórios com mudança também aparecem em uma linha separada."));
        content.Controls.Add(delayButton);
        delayResult.MaximumSize = new(770, 0);
        delayResult.Margin = new(3, 20, 3, 12);
        content.Controls.Add(delayResult);
        content.Controls.Add(Description("p95 é o tempo abaixo do qual ficaram 95% das amostras. A medição termina no app ou no envio ao driver. O tempo até o jogo reagir na tela exige uma medição separada."));
        return page;
    }

    private void RefreshHardware()
    {
        try
        {
            string? selected = (controllers.SelectedItem as ControllerChoice)?.Path;
            string[] paths = DualSenseDevice.EnumerateDevicePaths();
            controllers.Items.Clear();
            for (int index = 0; index < paths.Length; index++) controllers.Items.Add(new ControllerChoice(index + 1, paths[index]));
            int previous = Array.IndexOf(paths, selected);
            if (paths.Length > 0) controllers.SelectedIndex = previous >= 0 ? previous : 0;
            status.Text = paths.Length > 0 ? $"{paths.Length} DualSense USB encontrado(s)." : "Conecte um DualSense padrão por cabo USB de dados e clique em Atualizar.";
        }
        catch (Exception ex) { ReportError(ex); }
        UpdateControls();
    }

    private void ToggleConnection()
    {
        if (session != null)
        {
            stop?.Cancel();
            connect.Enabled = false;
            status.Text = "Encerrando a conexão…";
            return;
        }
        if (controllers.SelectedItem is not ControllerChoice choice) return;
        bool virtualMode = mode.SelectedIndex == 1;
        if (virtualMode && exclusive.Checked && !isolation.IsAvailable)
        {
            status.Text = "O modo exclusivo exige HidHide. Consulte Perfis e jogos ou o arquivo LEIA-ME.";
            return;
        }
        stop = new();
        session = RunSession(choice.Path, virtualMode, virtualMode && exclusive.Checked, stop.Token);
        _ = FinishSession(session);
        UpdateControls();
    }

    private async Task RunSession(string path, bool virtualMode, bool hide, CancellationToken cancellation)
    {
        VirtualOutput? output = null;
        try
        {
            status.Text = "Abrindo conexão USB…";
            device = await Task.Run(() => DualSenseDevice.Open(path), cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (virtualMode) output = new();
            string? isolationNotice = hide ? isolation.Start(path, GameProcesses.LauncherPaths()) : null;
            status.Text = isolationNotice ?? (virtualMode
                ? "Saída Xbox 360 ativa. Verifique entradas duplicadas antes de jogar."
                : "DualSense nativo conectado. Os ajustes analógicos aparecem na prévia.");
            UpdateControls();
            while (true)
            {
                DualSenseDevice connected = device;
                try
                {
                    await Task.Run(() => ReadReports(connected, output, cancellation), cancellation);
                }
                catch (IOException) when (hide && !cancellation.IsCancellationRequested)
                {
                    output?.Submit(default);
                    device = null;
                    connected.Dispose();
                    status.Text = "Controle desconectado. Aguardando até 20 segundos pela mesma porta USB…";
                    UpdateControls();
                    device = await WaitForController(path, cancellation);
                    status.Text = "Controle reconectado. A saída virtual e o isolamento continuam ativos.";
                    UpdateControls();
                }
            }
        }
        finally
        {
            output?.Dispose();
            device?.Dispose();
            device = null;
            if (isolation.IsActive) isolation.Stop();
        }
    }

    private static async Task<DualSenseDevice> WaitForController(string path, CancellationToken cancellation)
    {
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 20)
        {
            await Task.Delay(350, cancellation);
            var connected = DualSenseDevice.EnumerateDevicePaths()
                .FirstOrDefault(candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));
            if (connected == null) continue;
            try { return DualSenseDevice.Open(connected); }
            catch (IOException) { }
        }
        throw new IOException("O controle não voltou à mesma porta USB em 20 segundos. A saída virtual foi encerrada.");
    }

    private async Task FinishSession(Task running)
    {
        try { await running; status.Text = "Conexão encerrada."; }
        catch (OperationCanceledException) { status.Text = "Conexão encerrada."; }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            stop?.Dispose();
            stop = null;
            session = null;
            if (displayedCapture is not null) CompleteDelayTest();
            Volatile.Write(ref rawTriggers, 0);
            Volatile.Write(ref mappedTriggers, 0);
            Volatile.Write(ref rawSticks, 0);
            Volatile.Write(ref mappedSticks, 0);
            UpdateControls();
        }
    }

    private async Task ReadReports(DualSenseDevice pad, VirtualOutput? output, CancellationToken cancellation)
    {
        byte[] report = new byte[pad.InputReportLength];
        int length;
        while ((length = await pad.ReadReportAsync(report, cancellation).ConfigureAwait(false)) > 0)
        {
            long received = Stopwatch.GetTimestamp();
            if (!GamepadMapping.TryParse(report.AsSpan(0, length), out PhysicalReport raw)) continue;
            Xbox360State mapped = GamepadMapping.Map(raw, Volatile.Read(ref active));
            bool submitted = output?.Submit(mapped) == true;
            Volatile.Read(ref capture)?.Record(received, Stopwatch.GetTimestamp(), submitted);
            Volatile.Write(ref rawTriggers, raw.L2 | (raw.R2 << 8));
            Volatile.Write(ref mappedTriggers, mapped.LeftTrigger | (mapped.RightTrigger << 8));
            Volatile.Write(ref rawSticks, Pack(RawCoordinate(raw.Left.X), RawCoordinate(raw.Left.Y), RawCoordinate(raw.Right.X), RawCoordinate(raw.Right.Y)));
            Volatile.Write(ref mappedSticks, Pack(mapped.LeftX, mapped.LeftY, mapped.RightX, mapped.RightY));
        }
        throw new IOException("O DualSense parou de enviar dados. Verifique o cabo USB e reconecte.");
    }

    private async Task ApplyLight()
    {
        if (device is not { } connected || stop is not { } token || applyingLight) return;
        applyingLight = true;
        UpdateControls();
        try
        {
            Color rgb = BrightColor();
            await connected.SetLightbarAsync(rgb.R, rgb.G, rgb.B, token.Token);
            status.Text = $"Relatório da luz enviado ao controle ({rgb.R}, {rgb.G}, {rgb.B}).";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
        finally { applyingLight = false; UpdateControls(); }
    }

    private void StartDelayTest()
    {
        if (device is null || displayedCapture != null) return;
        measuringVirtual = mode.SelectedIndex == 1;
        displayedCapture = new();
        Volatile.Write(ref capture, displayedCapture);
        delayResult.Text = "Medindo… 10 segundos restantes.";
        UpdateControls();
    }

    private void CompleteDelayTest()
    {
        if (displayedCapture is not { } complete) return;
        Volatile.Write(ref capture, null);
        displayedCapture = null;
        if (measuringVirtual) virtualDelayResult = "MODO VIRTUAL\n" + complete.Result(virtualMode: true);
        else nativeDelayResult = "MODO NATIVO\n" + complete.Result();
        delayResult.Text = string.Join("\n\n", new[] { nativeDelayResult, virtualDelayResult }.OfType<string>());
        UpdateControls();
    }

    private void UpdateLive()
    {
        triggerView.UpdateValues(Volatile.Read(ref rawTriggers), Volatile.Read(ref mappedTriggers));
        stickView.UpdateValues(Volatile.Read(ref rawSticks), Volatile.Read(ref mappedSticks));
        if (displayedCapture is { } measure)
        {
            if (measure.Finished) CompleteDelayTest();
            else delayResult.Text = $"Medindo… {Math.Ceiling(10 - Stopwatch.GetElapsedTime(measure.Start).TotalSeconds):F0} segundos restantes.";
        }
        if (++uiTicks % 20 != 0) return;
        conflict.Text = GameProcesses.Summary();
        if (autoProfile.Checked)
        {
            string? foreground = GameProcesses.ForegroundExe();
            ControllerProfile? match = profiles.FirstOrDefault(item => item.Game.Length > 0 &&
                string.Equals(item.Game, foreground, StringComparison.OrdinalIgnoreCase));
            if (match != null && !string.Equals(match.Name, active.Name, StringComparison.OrdinalIgnoreCase))
            {
                LoadProfile(match);
                status.Text = $"Perfil de {match.Name} selecionado.";
            }
        }
    }

    private void UpdateControls()
    {
        bool busy = session != null;
        bool virtualMode = mode.SelectedIndex == 1;
        mode.Enabled = controllers.Enabled = refresh.Enabled = !busy;
        exclusive.Visible = virtualMode;
        exclusive.Enabled = !busy && virtualMode;
        connect.Enabled = busy ? stop?.IsCancellationRequested == false : controllers.Items.Count > 0;
        connect.Text = busy ? "Desconectar" : "Conectar";
        delayButton.Enabled = device != null && displayedCapture == null;
        applyLight.Enabled = device != null && !applyingLight;
        driverStatus.Text = virtualMode
            ? "Saída Xbox 360 · ViGEmBus necessário. Ocultação opcional via HidHide."
            : "O jogo recebe o DualSense diretamente. Curvas e zonas mortas podem ser visualizadas no app.";
    }

    private async void CloseSafely(object? sender, FormClosingEventArgs args)
    {
        if (closing) return;
        if (session is { } current)
        {
            args.Cancel = true;
            stop?.Cancel();
            try { await current; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Encerramento", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            closing = true;
            DisposeIsolation();
            Close();
            return;
        }
        closing = true;
        DisposeIsolation();
    }

    private void DisposeIsolation()
    {
        timer.Stop();
        try { isolation.Dispose(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Isolamento", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void SyncProfile()
    {
        if (loading || bindings.Any(box => box == null)) return;
        var settings = new ControllerProfile
        {
            Name = profileName.Text.Trim(), Game = processName.Text.Trim(),
            L2 = l2.Settings, R2 = r2.Settings, LeftStick = ls.Settings, RightStick = rs.Settings,
            LeftShape = ls.Shape, RightShape = rs.Shape,
            Buttons = bindings.Select(box => Math.Max(box.SelectedIndex, 0)).ToArray(),
            LightArgb = light.ToArgb(), LightBrightness = (int)brightness.Value
        };
        Volatile.Write(ref active, settings);
    }

    private void LoadProfiles()
    {
        try
        {
            if (File.Exists(profilePath))
                profiles.AddRange((JsonSerializer.Deserialize<List<ControllerProfile>>(File.ReadAllText(profilePath)) ?? [])
                    .Where(profile => profile is { Name.Length: > 0, Game: not null, L2: not null, R2: not null, LeftStick: not null, RightStick: not null }));
        }
        catch (Exception ex) { ReportError(ex); }
        if (profiles.Count == 0) profiles.Add(new());
        LoadProfile(profiles[0]);
    }

    private void LoadProfile(ControllerProfile value)
    {
        loading = true;
        try
        {
            presets.Items.Clear();
            presets.Items.AddRange(profiles.Select(profile => (object)profile.Name).ToArray());
            presets.SelectedIndex = profiles.FindIndex(profile => profile.Name == value.Name);
            profileName.Text = value.Name;
            processName.Text = value.Game;
            l2.Set(value.L2);
            r2.Set(value.R2);
            ls.Set(value.LeftStick, value.LeftShape);
            rs.Set(value.RightStick, value.RightShape);
            SetButtons(value.Buttons?.Length == bindings.Length ? value.Buttons : GamepadMapping.DefaultButtons());
            light = Color.FromArgb(value.LightArgb);
            brightness.Value = Math.Clamp(value.LightBrightness, 0, 100);
            UpdateSwatch();
        }
        finally { loading = false; }
        SyncProfile();
    }

    private void SetButtons(int[] values)
    {
        for (int index = 0; index < bindings.Length; index++)
            bindings[index].SelectedIndex = Math.Clamp(values[index], 0, GamepadMapping.OutputNames.Length - 1);
    }

    private void SaveProfile()
    {
        SyncProfile();
        ControllerProfile value = active;
        if (value.Name.Length == 0) { status.Text = "Dê um nome ao perfil."; return; }
        if (value.Game.Length > 0 && (!value.Game.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(value.Game) != value.Game))
        {
            status.Text = "Informe somente o nome do executável, por exemplo RocketLeague.exe.";
            return;
        }
        try
        {
            List<ControllerProfile> updated = [.. profiles];
            int index = updated.FindIndex(profile => string.Equals(profile.Name, value.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) updated[index] = value;
            else updated.Add(value);
            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            string temporary = profilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, profilePath, overwrite: true);
            profiles.Clear();
            profiles.AddRange(updated);
            LoadProfile(value);
            status.Text = $"Perfil {value.Name} salvo.";
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private void SetLight(Color value) { light = value; UpdateSwatch(); SyncProfile(); }
    private void UpdateSwatch() => swatch.SetColor(BrightColor());
    private Color BrightColor() => Color.FromArgb(light.R * (int)brightness.Value / 100,
        light.G * (int)brightness.Value / 100, light.B * (int)brightness.Value / 100);

    private void ReportError(Exception ex) => status.Text = ex.Message;
    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { ReportError(ex); }
    }

    private static short RawCoordinate(double value) => (short)(value * 32767);
    private static long Pack(short lx, short ly, short rx, short ry) => (ushort)lx | ((long)(ushort)ly << 16) |
        ((long)(ushort)rx << 32) | ((long)(ushort)ry << 48);

    internal static ComboBox ListBox(string[] items)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = TextColor,
            Margin = new(3, 6, 7, 4) };
        box.Items.AddRange(items.Cast<object>().ToArray());
        if (items.Length > 0) box.SelectedIndex = 0;
        return box;
    }

    internal static Label LabelText(string content, Color color) => new() { Text = content, AutoSize = true, ForeColor = color, Margin = new(3, 6, 3, 3) };
    private static Label Description(string content) => new() { Text = content, AutoSize = true, MaximumSize = new(670, 0), ForeColor = Muted, Margin = new(3, 5, 3, 12) };
    private static Label Title(string content) => new() { Text = content, AutoSize = true, Font = new("Segoe UI Semibold", 13f), ForeColor = TextColor, Margin = new(3, 6, 3, 11) };
    private static TextBox TextEntry(int width) => new() { Width = width, BackColor = Surface, ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle, Margin = new(3, 6, 10, 6) };
    private static CheckBox Check(string content) => new() { Text = content, AutoSize = true, ForeColor = TextColor, Margin = new(3, 3, 3, 3) };
    internal static NumericUpDown Percentage(int value) => new() { Minimum = 0, Maximum = 100, Value = value, Width = 76, BackColor = Surface, ForeColor = TextColor, Margin = new(3, 6, 3, 5) };

    private static Button ActionButton(string content, bool prominent = false)
    {
        return new StudioButton(content, prominent);
    }

    private static FlowLayoutPanel Horizontal(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, FlowDirection = FlowDirection.LeftToRight };
        flow.Controls.AddRange(controls);
        return flow;
    }

    private static FlowLayoutPanel VerticalCompact(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, FlowDirection = FlowDirection.TopDown };
        flow.Controls.AddRange(controls);
        return flow;
    }

    private static (Panel Page, FlowLayoutPanel Content) Tab(string title)
    {
        var page = new Panel { Dock = DockStyle.Fill, AccessibleName = title, BackColor = Canvas, ForeColor = TextColor, Margin = Padding.Empty };
        var content = VerticalCompact();
        content.AutoSize = false;
        content.Dock = DockStyle.Fill;
        content.Padding = new(0, 2, 0, 10);
        content.AutoScroll = true;
        page.Controls.Add(content);
        return (page, content);
    }

    private sealed record ControllerChoice(int Index, string Path)
    {
        public override string ToString() => $"DualSense USB {Index}";
    }
}
