using Microsoft.Win32;

namespace TrafficLight;

internal sealed class TrayApp : ApplicationContext
{
    private const string SettingsKey = @"Software\WindowsTrafficLight";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "WindowsTrafficLight";

    /// <summary>Title of the hidden window that a second instance and the installer talk to.</summary>
    public const string ControlWindowName = "WindowsTrafficLight.Control";
    public const int WM_APP_ACTIVATE = Native.WM_APP + 1;

    private readonly WindowTracker tracker = new();
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem enabledItem;
    private readonly ToolStripMenuItem startupItem;
    private readonly ControlWindow control;

    public TrayApp()
    {
        enabledItem = new ToolStripMenuItem("Ativado");
        enabledItem.Click += (_, _) => SetEnabled(!enabledItem.Checked);
        startupItem = new ToolStripMenuItem("Iniciar com o Windows");
        startupItem.Click += (_, _) => SetStartup(!startupItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Windows TrafficLight") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(enabledItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Sair", null, (_, _) => ExitThread()));

        tray = new NotifyIcon
        {
            Icon = TrafficLightRenderer.CreateTrayIcon(SystemInformation.SmallIconSize.Width * 2),
            Text = "Windows TrafficLight",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) SetEnabled(!enabledItem.Checked);
        };

        EnsureStartup();
        startupItem.Checked = IsStartupEnabled();
        SetEnabled(ReadEnabled());

        control = new ControlWindow(this);
    }

    /// <summary>Another copy of the app was launched while this one runs.</summary>
    private void OnActivateRequest()
    {
        if (!enabledItem.Checked) SetEnabled(true);
        tray.ShowBalloonTip(3000, "Windows TrafficLight",
            "Já está em execução. Clique no ícone das três bolinhas na bandeja para pausar.", ToolTipIcon.Info);
    }

    private static string StartupCommand => $"\"{Environment.ProcessPath}\"";

    /// <summary>
    /// Registers the app to start with Windows unless the user turned that off from the menu,
    /// and keeps the entry pointing at the executable that is running now.
    /// </summary>
    private static void EnsureStartup()
    {
        try
        {
            using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey);
            if (settings.GetValue("StartupOptOut") is int optOut && optOut != 0) return;

            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (run.GetValue(RunValue) as string != StartupCommand)
                run.SetValue(RunValue, StartupCommand);
        }
        catch { }
    }

    private void SetEnabled(bool enabled)
    {
        enabledItem.Checked = enabled;
        tray.Text = enabled ? "Windows TrafficLight — ativado" : "Windows TrafficLight — pausado";
        if (enabled) tracker.Start(); else tracker.Stop();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("Enabled", enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { }
    }

    private static bool ReadEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return key?.GetValue("Enabled") is not int v || v != 0;
        }
        catch { return true; }
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        catch { return false; }
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(RunValue, StartupCommand);
            else key.DeleteValue(RunValue, false);
            using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey);
            settings.SetValue("StartupOptOut", enabled ? 0 : 1, RegistryValueKind.DWord);
            startupItem.Checked = enabled;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível alterar a inicialização: {ex.Message}", "Windows TrafficLight",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void ExitThreadCore()
    {
        tracker.Dispose();
        tray.Visible = false;
        tray.Dispose();
        control.DestroyHandle(); // after the tray icon, so the installer sees it gone once we're done
        base.ExitThreadCore();
    }

    /// <summary>
    /// Hidden top-level window with a known title. A second instance posts
    /// <see cref="WM_APP_ACTIVATE"/> to it, and the installer posts WM_CLOSE so the app
    /// can remove its tray icon before its files are replaced.
    /// </summary>
    private sealed class ControlWindow : NativeWindow
    {
        private const int WM_CLOSE = 0x0010;
        private readonly TrayApp app;

        public ControlWindow(TrayApp app)
        {
            this.app = app;
            CreateHandle(new CreateParams { Caption = ControlWindowName });
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_APP_ACTIVATE:
                    app.OnActivateRequest();
                    return;
                case WM_CLOSE:
                    app.ExitThread();
                    return;
            }
            base.WndProc(ref m);
        }
    }
}
