using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static TrafficLight.Native;

namespace TrafficLight;

/// <summary>
/// Watches every top-level window and keeps one <see cref="Overlay"/> on top of the
/// caption buttons of each eligible window. Runs entirely on the UI thread.
/// </summary>
internal sealed class WindowTracker : IDisposable
{
    private sealed record Geometry(RECT Cover, float Scale, bool Maximized, bool CanMinimize, bool CanMaximize,
        bool CloseOnly, int CornerRadius, bool Topmost);

    /// <summary>Caption button position learned through WM_NCHITTEST, relative to the frame's top-right corner.</summary>
    private sealed record Probe(bool Maximized, uint Dpi, bool Found, int LeftFromRight, int Height,
        bool HasMinimize, bool HasMaximize, long Time);

    private static readonly OverlayState Hidden = new(false, default, Color.Empty, false, false, false, false, false, 0, 1, false);

    private readonly Dictionary<IntPtr, Overlay> overlays = new();
    private readonly Dictionary<IntPtr, Probe> probes = new();
    private readonly Dictionary<uint, bool> elevatedPids = new();
    private readonly HashSet<IntPtr> moving = new();
    private readonly HashSet<IntPtr> resampleSoon = new();
    private readonly List<IntPtr> hooks = new();
    private readonly WinEventProc eventProc;
    private readonly System.Windows.Forms.Timer heartbeat = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer resampleTimer = new() { Interval = 250 };
    private readonly uint ownPid = (uint)Environment.ProcessId;
    private readonly bool selfElevated;
    private readonly bool isWindows11 = Environment.OSVersion.Version.Build >= 22000;
    private IntPtr foreground;
    private int ticks;
    private bool running;

    public WindowTracker()
    {
        eventProc = OnWinEvent; // keep the delegate alive while hooks exist
        selfElevated = IsProcessElevated(ownPid);
        heartbeat.Tick += (_, _) => Heartbeat();
        resampleTimer.Tick += (_, _) => FlushResamples();
    }

    public void Start()
    {
        if (running) return;
        running = true;

        (uint, uint)[] ranges =
        {
            (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND),
            (EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND),
            (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
            (EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE),
            (EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE),
            (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED),
        };
        foreach (var (min, max) in ranges)
        {
            IntPtr hook = SetWinEventHook(min, max, IntPtr.Zero, eventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (hook != IntPtr.Zero) hooks.Add(hook);
        }

        foreground = GetForegroundWindow();
        heartbeat.Start();
        SyncAll(resampleAll: true);
    }

    public void Stop()
    {
        if (!running) return;
        running = false;
        heartbeat.Stop();
        resampleTimer.Stop();
        foreach (var hook in hooks) UnhookWinEvent(hook);
        hooks.Clear();
        foreach (var overlay in overlays.Values) overlay.Dispose();
        overlays.Clear();
        probes.Clear();
        moving.Clear();
        resampleSoon.Clear();
    }

    public void Dispose()
    {
        Stop();
        heartbeat.Dispose();
        resampleTimer.Dispose();
    }

    // ---------------------------------------------------------------- events

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!running || hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != 0) return;

        switch (evt)
        {
            case EVENT_SYSTEM_FOREGROUND:
            {
                IntPtr old = foreground;
                foreground = hwnd;
                if (old != hwnd) SyncWindow(old, resample: false);
                SyncWindow(hwnd, resample: false);
                // Title bars repaint with their active/inactive colours a moment later.
                QueueResample(old);
                QueueResample(hwnd);
                return;
            }
            case EVENT_OBJECT_DESTROY:
                Forget(hwnd);
                return;
            case EVENT_SYSTEM_MOVESIZESTART:
                moving.Add(hwnd);
                return;
            case EVENT_SYSTEM_MOVESIZEEND:
                moving.Remove(hwnd);
                SyncWindow(hwnd, resample: true);
                return;
        }

        if (!overlays.ContainsKey(hwnd) && GetAncestor(hwnd, GA_ROOT) != hwnd) return;

        SyncWindow(hwnd, resample: false);
        if (evt is EVENT_OBJECT_SHOW or EVENT_SYSTEM_MINIMIZEEND or EVENT_OBJECT_UNCLOAKED)
            QueueResample(hwnd);
    }

    private void Heartbeat()
    {
        ticks++;
        foreground = GetForegroundWindow();
        SyncAll(resampleAll: ticks % 4 == 0);
    }

    private void QueueResample(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        resampleSoon.Add(hwnd);
        resampleTimer.Stop();
        resampleTimer.Start();
    }

    private void FlushResamples()
    {
        resampleTimer.Stop();
        var list = resampleSoon.ToArray();
        resampleSoon.Clear();
        foreach (var hwnd in list) SyncWindow(hwnd, resample: true);
    }

    private void SyncAll(bool resampleAll)
    {
        var seen = new HashSet<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            seen.Add(hwnd);
            if (overlays.ContainsKey(hwnd) || IsWindowVisible(hwnd))
                SyncWindow(hwnd, resampleAll || hwnd == foreground);
            return true;
        }, IntPtr.Zero);

        foreach (var hwnd in overlays.Keys.Where(h => !seen.Contains(h) || !IsWindow(h)).ToList())
            Forget(hwnd);
        foreach (var hwnd in probes.Keys.Where(h => !seen.Contains(h)).ToList())
            probes.Remove(hwnd);
    }

    private void Forget(IntPtr hwnd)
    {
        if (overlays.Remove(hwnd, out var overlay)) overlay.Dispose();
        probes.Remove(hwnd);
        moving.Remove(hwnd);
        resampleSoon.Remove(hwnd);
    }

    // ---------------------------------------------------------------- sync

    private void SyncWindow(IntPtr hwnd, bool resample)
    {
        if (hwnd == IntPtr.Zero) return;

        var geo = Measure(hwnd);
        overlays.TryGetValue(hwnd, out var overlay);
        if (geo == null)
        {
            overlay?.Update(Hidden);
            return;
        }

        if (overlay == null)
        {
            overlay = new Overlay(hwnd, geo.Topmost);
            overlays[hwnd] = overlay;
            resample = true;
        }

        if ((resample || overlay.Background == null) && !moving.Contains(hwnd))
        {
            var color = SampleTitleBar(hwnd, geo);
            if (color.HasValue) overlay.Background = color;
        }

        overlay.Update(new OverlayState(
            Visible: true,
            Bounds: geo.Cover,
            Background: overlay.Background ?? DefaultTitleBarColor(),
            Active: hwnd == foreground,
            Maximized: geo.Maximized,
            CanMinimize: geo.CanMinimize,
            CanMaximize: geo.CanMaximize,
            CloseOnly: geo.CloseOnly,
            CornerRadius: geo.CornerRadius,
            Scale: geo.Scale,
            Topmost: geo.Topmost));
    }

    private Geometry? Measure(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return null;

        long style = Style(hwnd), ex = ExStyle(hwnd);
        if ((style & WS_CHILD) != 0 || (style & WS_CAPTION) != WS_CAPTION) return null;
        if ((ex & WS_EX_TOOLWINDOW) != 0) return null;
        if (IsCloaked(hwnd)) return null;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == ownPid) return null;
        if (!selfElevated && IsElevated(pid)) return null; // UIPI would block our clicks anyway

        RECT frame = FrameBounds(hwnd);
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        float scale = dpi / 96f;
        if (frame.Width < 120 * scale || frame.Height < 40 * scale) return null;

        bool maximized = IsZoomed(hwnd);
        if (!maximized && IsFullscreen(hwnd, frame)) return null;

        RECT cover;
        bool hasMin, hasMax;
        var probe = GetProbe(hwnd, frame, dpi, scale, maximized);
        if (probe is { Found: true })
        {
            cover = new RECT(frame.Right - probe.LeftFromRight, frame.Top, frame.Right, frame.Top + probe.Height);
            hasMin = probe.HasMinimize;
            hasMax = probe.HasMaximize;
        }
        else
        {
            // Windows whose caption buttons are drawn by DWM (e.g. File Explorer) don't answer
            // WM_NCHITTEST with button codes, but DWM knows where it drew them.
            if ((style & WS_SYSMENU) == 0) return null;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, out RECT b, Marshal.SizeOf<RECT>()) != 0) return null;
            if (b.Width < 20 * scale || b.Height < 10 * scale) return null;
            GetWindowRect(hwnd, out RECT wr);
            cover = new RECT(wr.Left + b.Left, wr.Top + b.Top, wr.Left + b.Right, wr.Top + b.Bottom);
            hasMin = hasMax = (style & (WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) != 0;
        }

        // Stay inside the visible frame and leave the 1px window border alone.
        int border = maximized ? 0 : 1;
        cover.Top = Math.Max(cover.Top, frame.Top + border);
        cover.Right = Math.Min(cover.Right, frame.Right - border);
        cover.Left = Math.Max(cover.Left, frame.Left);
        if (cover.Width < 20 * scale || cover.Height < 10 * scale || cover.Height > 90 * scale) return null;

        bool hasSysMenu = (style & WS_SYSMENU) != 0;
        bool canMin = hasMin && (!hasSysMenu || (style & WS_MINIMIZEBOX) != 0);
        bool canMax = hasMax && (!hasSysMenu || (style & WS_MAXIMIZEBOX) != 0);

        return new Geometry(cover, scale, maximized, canMin, canMax,
            CloseOnly: !hasMin && !hasMax,
            CornerRadius: CornerRadius(hwnd, maximized, scale) ,
            Topmost: (ex & WS_EX_TOPMOST) != 0);
    }

    private int CornerRadius(IntPtr hwnd, bool maximized, float scale)
    {
        if (!isWindows11 || maximized) return 0;
        DwmGetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, out int pref, sizeof(int));
        return pref switch
        {
            1 => 0,                              // DWMWCP_DONOTROUND
            3 => (int)Math.Round(4 * scale) - 1, // DWMWCP_ROUNDSMALL
            _ => (int)Math.Round(8 * scale) - 1,
        };
    }

    private static bool IsFullscreen(IntPtr hwnd, RECT frame)
    {
        var mon = Screen.FromHandle(hwnd).Bounds;
        return frame.Left <= mon.Left && frame.Top <= mon.Top && frame.Right >= mon.Right && frame.Bottom >= mon.Bottom;
    }

    private bool IsElevated(uint pid)
    {
        if (!elevatedPids.TryGetValue(pid, out bool elevated))
        {
            elevated = IsProcessElevated(pid);
            elevatedPids[pid] = elevated;
        }
        return elevated;
    }

    // ---------------------------------------------------------------- caption button discovery

    private Probe? GetProbe(IntPtr hwnd, RECT frame, uint dpi, float scale, bool maximized)
    {
        probes.TryGetValue(hwnd, out var cached);
        if (cached != null && cached.Maximized == maximized && cached.Dpi == dpi)
        {
            // Positive results stay valid; negative ones are retried now and then
            // because apps often finish building their title bar after first showing.
            if (cached.Found || Environment.TickCount64 - cached.Time < 5000) return cached;
        }
        if (moving.Contains(hwnd)) return cached is { Found: true } ? cached : null;

        var probe = RunProbe(hwnd, frame, dpi, scale, maximized);
        if (probe != null) probes[hwnd] = probe;
        return probe;
    }

    /// <summary>
    /// Asks the window itself where its caption buttons are, by hit-testing along the top of
    /// the frame. Custom title bars (Chromium, VS Code, UWP, WinUI) report HTMINBUTTON /
    /// HTMAXBUTTON / HTCLOSE for Snap Layouts support, so this finds their real size.
    /// </summary>
    private static Probe? RunProbe(IntPtr hwnd, RECT frame, uint dpi, float scale, bool maximized)
    {
        long now = Environment.TickCount64;
        var notFound = new Probe(maximized, dpi, false, 0, 0, false, false, now);

        int y = frame.Top + (int)(10 * scale);
        int scanLimit = Math.Max(frame.Left, frame.Right - (int)(320 * scale));
        int slack = Math.Max(3, (int)(4 * scale));

        int left = int.MinValue, closeX = int.MinValue, miss = 0;
        bool hasMin = false, hasMax = false;
        for (int x = frame.Right - 1; x > scanLimit; x--)
        {
            int ht = HitTest(hwnd, x, y);
            if (ht == int.MinValue) return null; // hung or timed out: try again later
            if (ht is HTCLOSE or HTMAXBUTTON or HTMINBUTTON)
            {
                left = x;
                miss = 0;
                if (ht == HTCLOSE && closeX == int.MinValue) closeX = x;
                hasMin |= ht == HTMINBUTTON;
                hasMax |= ht == HTMAXBUTTON;
            }
            else if (left != int.MinValue && ++miss > slack)
            {
                break;
            }
        }
        if (closeX == int.MinValue) return notFound;

        int probeX = closeX - (int)(12 * scale);
        int bottom = int.MinValue;
        for (int yy = frame.Top; yy < frame.Top + (int)(90 * scale); yy++)
        {
            int ht = HitTest(hwnd, probeX, yy);
            if (ht == int.MinValue) return null;
            if (ht == HTCLOSE) bottom = yy + 1;
            else if (bottom != int.MinValue) break;
        }
        if (bottom == int.MinValue) return notFound;

        return new Probe(maximized, dpi, true, frame.Right - left, bottom - frame.Top, hasMin, hasMax, now);
    }

    private static int HitTest(IntPtr hwnd, int x, int y)
    {
        IntPtr ok = SendMessageTimeout(hwnd, WM_NCHITTEST, IntPtr.Zero, MakeLParam(x, y), SMTO_ABORTIFHUNG, 50, out IntPtr result);
        return ok == IntPtr.Zero ? int.MinValue : (int)result.ToInt64();
    }

    // ---------------------------------------------------------------- colour

    /// <summary>Reads the title bar colour just left of the caption buttons, if that spot is visible.</summary>
    private static Color? SampleTitleBar(IntPtr hwnd, Geometry geo)
    {
        int x = geo.Cover.Left - Math.Max(2, (int)(3 * geo.Scale)) - 1;
        int y = geo.Cover.Top + geo.Cover.Height / 3;

        IntPtr at = WindowFromPoint(new POINT(x, y));
        if (at == IntPtr.Zero || GetAncestor(at, GA_ROOT) != hwnd) return null;

        IntPtr dc = GetDC(IntPtr.Zero);
        uint c = GetPixel(dc, x, y);
        ReleaseDC(IntPtr.Zero, dc);
        if (c == CLR_INVALID) return null;
        return Color.FromArgb((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));
    }

    private static Color DefaultTitleBarColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light && light == 0)
                return Color.FromArgb(0x20, 0x20, 0x20);
        }
        catch { }
        return Color.FromArgb(0xF3, 0xF3, 0xF3);
    }
}
