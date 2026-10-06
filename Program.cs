namespace TrafficLight;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\WindowsTrafficLight", out bool first);
        if (!first) return; // already running

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
