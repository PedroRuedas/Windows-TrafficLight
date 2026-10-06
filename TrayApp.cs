using Microsoft.Win32;

namespace TrafficLight;

internal sealed class TrayApp : ApplicationContext
{
    private const string SettingsKey = @"Software\WindowsTrafficLight";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "WindowsTrafficLight";

    private readonly WindowTracker tracker = new();
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem enabledItem;
    private readonly ToolStripMenuItem startupItem;

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
        base.ExitThreadCore();
    }
}
