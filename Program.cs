namespace TrafficLight;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\WindowsTrafficLight", out bool first);
        if (!first)
        {
            // Already running: ask that instance to switch itself on and say so.
            IntPtr control = Native.FindWindow(null, TrayApp.ControlWindowName);
            if (control != IntPtr.Zero) Native.PostMessage(control, TrayApp.WM_APP_ACTIVATE, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
