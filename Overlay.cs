using System.Drawing;
using static TrafficLight.Native;

namespace TrafficLight;

/// <summary>
/// One traffic-light overlay attached to one target window.
///
/// The overlay is an owned popup of the target (so it follows the target's z-order,
/// minimize state and virtual desktop). A cross-process owner relationship attaches
/// the input queues of both threads, so every overlay runs on its own thread: a hung
/// application can only freeze its own overlay, never the tracker or other overlays.
/// </summary>
internal sealed class Overlay : IDisposable
{
    private const int WM_APP_UPDATE = WM_APP + 1;
    private const int WM_APP_CLOSE = WM_APP + 2;

    public IntPtr Target { get; }

    /// <summary>Last sampled title bar colour (tracker thread only).</summary>
    public Color? Background { get; set; }

    private readonly object gate = new();
    private OverlayState? pending;
    private OverlayState? lastSent;
    private bool posted;
    private bool disposed;
    private IntPtr hwnd;

    public Overlay(IntPtr target, bool topmost)
    {
        Target = target;
        var thread = new Thread(() => Run(topmost)) { IsBackground = true, Name = $"Overlay {target:X}" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Called from the tracker thread. Coalesces bursts of updates into one repaint.</summary>
    public void Update(OverlayState state)
    {
        if (state.Equals(lastSent)) return;
        lastSent = state;

        IntPtr h;
        lock (gate)
        {
            pending = state;
            if (posted || hwnd == IntPtr.Zero || disposed) return;
            posted = true;
            h = hwnd;
        }
        PostMessage(h, WM_APP_UPDATE, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        IntPtr h;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            h = hwnd;
        }
        if (h != IntPtr.Zero) PostMessage(h, WM_APP_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private OverlayState? TakePending()
    {
        lock (gate)
        {
            posted = false;
            var s = pending;
            pending = null;
            return s;
        }
    }

    private void Run(bool topmost)
    {
        var window = new OverlayWindow(this);
        try
        {
            window.Create(Target, topmost);
        }
        catch
        {
            return; // target vanished or refused ownership
        }

        bool post;
        lock (gate)
        {
            hwnd = window.Handle;
            post = disposed || pending != null;
            posted = post;
        }
        if (post) PostMessage(hwnd, disposed ? WM_APP_CLOSE : WM_APP_UPDATE, IntPtr.Zero, IntPtr.Zero);

        Application.Run();
    }

    private sealed class OverlayWindow : NativeWindow
    {
        private readonly Overlay owner;
        private OverlayState? state;
        private LightButton[] buttons = Array.Empty<LightButton>();
        private bool shown;
        private bool topmost;
        private bool tracking;
        private bool groupHover;
        private int pressed = -1;
        private bool exited;
        private long lastBlankClick;
        private Point lastBlankPoint;

        public OverlayWindow(Overlay owner) => this.owner = owner;

        public void Create(IntPtr target, bool topmost)
        {
            this.topmost = topmost;
            var cp = new CreateParams
            {
                Caption = "TrafficLight",
                Style = unchecked((int)WS_POPUP),
                ExStyle = (int)(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | (topmost ? WS_EX_TOPMOST : 0)),
                Parent = target, // owner, since this is a popup
                Width = 1,
                Height = 1,
            };
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_APP_UPDATE:
                    var next = owner.TakePending();
                    if (next != null) Apply(next);
                    return;

                case WM_APP_CLOSE:
                    DestroyHandle();
                    Exit();
                    return;

                case WM_MOUSEACTIVATE:
                    m.Result = MA_NOACTIVATE;
                    return;

                case WM_MOUSEMOVE:
                    OnMouseMove(PointFrom(m.LParam));
                    return;

                case WM_MOUSELEAVE:
                    tracking = false;
                    if (pressed < 0 && groupHover)
                    {
                        groupHover = false;
                        Redraw();
                    }
                    return;

                case WM_LBUTTONDOWN:
                    OnLeftDown(PointFrom(m.LParam));
                    return;

                case WM_LBUTTONUP:
                    OnLeftUp(PointFrom(m.LParam));
                    return;

                case WM_CAPTURECHANGED:
                    if (pressed >= 0)
                    {
                        pressed = -1;
                        Redraw();
                    }
                    break;

                case WM_RBUTTONUP:
                    if (HitButton(PointFrom(m.LParam)) < 0 && GetCursorPos(out var cur))
                        PostMessage(owner.Target, WM_POPUPSYSTEMMENU, IntPtr.Zero, MakeLParam(cur.X, cur.Y));
                    return;

                case WM_DESTROY:
                    base.WndProc(ref m);
                    Exit(); // the owner was destroyed, which destroys us too
                    return;
            }
            base.WndProc(ref m);
        }

        private void Exit()
        {
            if (exited) return;
            exited = true;
            Application.ExitThread();
        }

        private void Apply(OverlayState s)
        {
            var previous = state;
            state = s;

            if (!s.Visible)
            {
                if (shown) ShowWindow(Handle, SW_HIDE);
                shown = false;
                groupHover = false;
                pressed = -1;
                return;
            }

            buttons = TrafficLightRenderer.Layout(s);

            if (s.Topmost != topmost)
            {
                topmost = s.Topmost;
                SetWindowPos(Handle, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }

            Redraw();

            if (!shown)
            {
                ShowWindow(Handle, SW_SHOWNOACTIVATE);
                shown = true;
                PlaceAboveTarget();
            }
            else if (s.Active && previous is { Active: false })
            {
                PlaceAboveTarget();
            }
        }

        /// <summary>Puts the overlay directly above its target, never above unrelated windows.</summary>
        private void PlaceAboveTarget()
        {
            IntPtr prev = GetWindow(owner.Target, GW_HWNDPREV);
            if (prev == Handle) return;

            IntPtr insertAfter = prev;
            bool prevTopmost = prev != IntPtr.Zero && (ExStyle(prev) & WS_EX_TOPMOST) != 0;
            if (prev == IntPtr.Zero || prevTopmost != topmost)
                insertAfter = topmost ? HWND_TOPMOST : HWND_TOP;

            SetWindowPos(Handle, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        private void Redraw()
        {
            var s = state;
            if (s == null || !s.Visible || Handle == IntPtr.Zero) return;

            using var bmp = TrafficLightRenderer.Render(s, buttons, pressed, groupHover);
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(mem, hbmp);
            try
            {
                var dst = new POINT(s.Bounds.Left, s.Bounds.Top);
                var size = new SIZE(bmp.Width, bmp.Height);
                var src = new POINT(0, 0);
                var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private int HitButton(Point p)
        {
            if (state == null) return -1;
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i].HitTest(p, 3 * state.Scale)) return i;
            return -1;
        }

        private bool InGroup(Point p) =>
            state != null && buttons.Length > 0 &&
            TrafficLightRenderer.GroupBounds(buttons, 5 * state.Scale).Contains(p);

        private void OnMouseMove(Point p)
        {
            if (!tracking)
            {
                var tme = new TRACKMOUSEEVENT
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(),
                    dwFlags = TME_LEAVE,
                    hwndTrack = Handle,
                };
                tracking = TrackMouseEvent(ref tme);
            }

            bool hover = InGroup(p) || pressed >= 0;
            if (hover != groupHover)
            {
                groupHover = hover;
                Redraw();
            }
        }

        private void OnLeftDown(Point p)
        {
            int hit = HitButton(p);
            if (hit >= 0)
            {
                if (!buttons[hit].Enabled) return;
                pressed = hit;
                SetCapture(Handle);
                Redraw();
                return;
            }

            // Empty part of the patch: behave like the title bar it covers.
            long now = Environment.TickCount64;
            var size = SystemInformation.DoubleClickSize;
            bool isDouble = now - lastBlankClick <= GetDoubleClickTime()
                && Math.Abs(p.X - lastBlankPoint.X) <= size.Width
                && Math.Abs(p.Y - lastBlankPoint.Y) <= size.Height;
            lastBlankClick = isDouble ? 0 : now;
            lastBlankPoint = p;

            SetForegroundWindow(owner.Target);
            if (isDouble)
            {
                if (state?.CanMaximize == true) ToggleZoom();
                return;
            }
            ReleaseCapture();
            PostMessage(owner.Target, WM_SYSCOMMAND, (IntPtr)SC_DRAGMOVE, IntPtr.Zero);
        }

        private void OnLeftUp(Point p)
        {
            if (pressed < 0) return;
            int was = pressed;
            pressed = -1;
            ReleaseCapture();

            if (HitButton(p) == was)
            {
                switch (buttons[was].Kind)
                {
                    case ButtonKind.Close:
                        PostMessage(owner.Target, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
                        break;
                    case ButtonKind.Minimize:
                        PostMessage(owner.Target, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero);
                        break;
                    case ButtonKind.Zoom:
                        ToggleZoom();
                        break;
                }
            }

            groupHover = InGroup(p);
            Redraw();
        }

        private void ToggleZoom()
        {
            int cmd = IsZoomed(owner.Target) ? SC_RESTORE : SC_MAXIMIZE;
            PostMessage(owner.Target, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero);
        }

        private static Point PointFrom(IntPtr lParam)
        {
            long v = lParam.ToInt64();
            return new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
        }
    }
}
