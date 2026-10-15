using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RoadRug;

/// A borderless, per-pixel-alpha layered window just above the desktop icons and below every app window.
/// It only covers the rug, moves with it, and lets clicks through everywhere else. Files are never hidden
/// away: they stay real desktop icons, and the rug simply lies on top of them.
sealed class RugWindow : Form
{
    readonly Controller ctl;
    readonly Bitmap rugImage;
    Renderer renderer;
    readonly Canvas canvas = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 10 };
    readonly System.Windows.Forms.Timer iconPoll = new() { Interval = 2000 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu;
    readonly List<(string path, Point center, int tries)> pendingPlacements = new();
    double lastTick;
    float scale = 1;
    bool clickThrough = true, needsPresent = true, wasGrabbing;
    Point lastCursor = new(int.MinValue, 0);

    public RugWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        AllowDrop = true;
        Text = "Road Rug";

        using (var s = typeof(RugWindow).Assembly.GetManifestResourceStream("rug.png"))
            rugImage = new Bitmap(s);

        var area = Screen.PrimaryScreen.Bounds;
        ctl = new Controller(new Vector2(area.Width / 2f, area.Height / 2f));   // replaced once the DPI is known
        ctl.Settled += () => { Store.Save(ctl); RefreshDesktopIcons(); };

        menu = BuildMenu();
        tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? SystemIcons.Application,
            Text = "Road Rug",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ctl.Recenter();
        timer.Tick += (_, _) => Tick();
        iconPoll.Tick += (_, _) => RefreshDesktopIcons();
    }

    // MARK: Window setup

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        scale = DeviceDpi / 96f;
        renderer = new Renderer(rugImage, scale);
        UpdateScreen();
        ctl.Recenter();
        var savedSpots = Store.Load(ctl);
        RescueStash(savedSpots);
        RefreshDesktopIcons();
        ctl.Wake();
        timer.Start();
        iconPoll.Start();
        SystemEvents.DisplaySettingsChanged += (_, _) => BeginInvoke(UpdateScreen);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        scale = e.DeviceDpiNew / 96f;
        renderer = new Renderer(rugImage, scale);
        UpdateScreen();
    }

    /// World coordinates are DIPs of the primary screen; keep the rug's centre within its work area.
    void UpdateScreen()
    {
        var b = Screen.PrimaryScreen.Bounds;
        var w = Screen.PrimaryScreen.WorkingArea;
        ctl.ScreenCenter = new Vector2((b.Left + b.Width / 2f) / scale, (b.Top + b.Height / 2f) / scale);
        ctl.Rug.Walls = new Vector4(w.Left / scale, w.Top / scale, w.Right / scale, w.Bottom / scale);
        ctl.Wake();
        needsPresent = true;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEACTIVATE:
                m.Result = (IntPtr)Native.MA_NOACTIVATE;
                return;
            case Native.WM_WINDOWPOSCHANGING:
                // Stay directly above the desktop icons, below every other window.
                var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(m.LParam);
                if ((pos.flags & Native.SWP_NOZORDER) == 0)
                {
                    var above = Native.WindowAboveDesktop(Handle);
                    if (above == IntPtr.Zero) pos.flags |= Native.SWP_NOZORDER; else pos.hwndInsertAfter = above;
                    Marshal.StructureToPtr(pos, m.LParam, false);
                }
                break;
            case Native.WM_CAPTURECHANGED:
                if (m.LParam != Handle) ctl.Release();
                break;
        }
        base.WndProc(ref m);
    }

    // MARK: Frame loop

    Vector2 CursorDip() { var p = Cursor.Position; return new Vector2(p.X / scale, p.Y / scale); }

    void Tick()
    {
        double now = clock.Elapsed.TotalSeconds;
        float dt = lastTick == 0 ? 1 / 60f : (float)Math.Min(now - lastTick, 1 / 20.0);
        lastTick = now;

        var cursor = Cursor.Position;
        var mouse = new Vector2(cursor.X / scale, cursor.Y / scale);
        // Only re-test what's under the cursor when it moved or the rug did; an idle desktop costs nothing.
        if (ctl.Grabbing || ctl.Awake || cursor != lastCursor)
        {
            lastCursor = cursor;
            bool over = ctl.Grabbing || (ctl.HitRug(mouse) != null && !Native.CoveredByAnotherWindow(Handle, cursor));
            SetClickThrough(!over);
            if (ctl.Grabbing != wasGrabbing) Cursor = ctl.Grabbing ? Cursors.SizeAll : Cursors.Hand;
            wasGrabbing = ctl.Grabbing;
        }

        if (ctl.Advance(dt, mouse) || needsPresent) Present();
    }

    void SetClickThrough(bool through)
    {
        if (through == clickThrough) return;
        clickThrough = through;
        long ex = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
        ex = through ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, (IntPtr)ex);
    }

    /// Renders just the area the rug covers and hands it to the compositor with per-pixel alpha.
    void Present()
    {
        needsPresent = false;
        var r = Renderer.Bounds(ctl);
        int x0 = (int)MathF.Floor(r.Left * scale), y0 = (int)MathF.Floor(r.Top * scale);
        int w = Math.Max(1, (int)MathF.Ceiling(r.Right * scale) - x0), h = Math.Max(1, (int)MathF.Ceiling(r.Bottom * scale) - y0);
        canvas.Resize(w, h);
        renderer.Render(canvas, ctl, new Vector2(x0 / scale, y0 / scale), 0);
        Native.PresentLayered(Handle, canvas.Px, w, h, x0, y0);
    }

    // MARK: Mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var s = CursorDip();
        if (e.Button == MouseButtons.Right) { menu.Show(Cursor.Position); return; }
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks == 2 && ctl.HitRug(s) != null) { ctl.Straighten(); return; }      // lay it flat again
        if (ctl.BeginGrab(s, ModifierKeys.HasFlag(Keys.Alt))) Capture = true;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        Capture = false;
        ctl.Release();
        RefreshDesktopIcons();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (ModifierKeys.HasFlag(Keys.Shift)) ctl.Twist(e.Delta / 120f * 0.06f);   // Shift + scroll twists the rug
    }

    // MARK: Desktop icons under the rug

    /// Explorer is the source of truth: every file stays a real desktop icon, and whatever the rug covers
    /// makes a lump. This keeps the controller's items in step with Explorer's icon positions.
    void RefreshDesktopIcons()
    {
        RetryPlacements();
        var icons = DesktopIcons.List();
        var byPath = ctl.Items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var next = new List<Item>();
        bool changed = false;
        foreach (var icon in icons)
        {
            var pos = new Vector2(icon.Center.X / scale, icon.Center.Y / scale);
            if (byPath.Remove(icon.Path, out var it))
            {
                if (Vector2.Distance(it.Pos, pos) > 0.5f) { it.Pos = pos; changed = true; }
                next.Add(it);
            }
            else { next.Add(MakeItem(icon.Path, pos)); changed = true; }
        }
        if (byPath.Count > 0) changed = true;
        if (!changed) return;
        ctl.Items.Clear();
        ctl.Items.AddRange(next);
        ctl.Wake();
    }

    static Item MakeItem(string path, Vector2 pos) => new(path, pos);

    /// Explorer only knows about a file a moment after it lands on the desktop, so placements are retried.
    void RetryPlacements()
    {
        for (int i = pendingPlacements.Count - 1; i >= 0; i--)
        {
            var (path, center, tries) = pendingPlacements[i];
            if (DesktopIcons.Place(path, center) || tries >= 10) pendingPlacements.RemoveAt(i);
            else pendingPlacements[i] = (path, center, tries + 1);
        }
        iconPoll.Interval = pendingPlacements.Count > 0 ? 300 : 2000;
    }

    Point ScreenPoint(Vector2 dip) => new((int)MathF.Round(dip.X * scale), (int)MathF.Round(dip.Y * scale));

    /// Version 1.0 hid swept files in a folder of their own. Put each one back on the Desktop, in the spot
    /// it had under the rug, so it's a normal desktop icon again.
    void RescueStash(Dictionary<string, Vector2> savedSpots)
    {
        if (!Directory.Exists(Store.Stash)) return;
        var center = ctl.Rug.Centroid;
        int n = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(Store.Stash).ToArray())
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var dest = Store.UniquePath(Store.Desktop, name);
                Store.Move(path, dest);
                float a = n * 2.39996f, r = 40 * MathF.Sqrt(n++);
                var spot = savedSpots.TryGetValue(name, out var s) ? s : center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
                pendingPlacements.Add((dest, ScreenPoint(spot), 0));
            }
            catch { /* leave it where it is */ }
        }
        try { if (!Directory.EnumerateFileSystemEntries(Store.Stash).Any()) Directory.Delete(Store.Stash); } catch { }
    }

    // MARK: Dropping files onto the rug

    bool OverRug(DragEventArgs e) => ctl.HitRug(new Vector2(e.X / scale, e.Y / scale)) != null;

    protected override void OnDragEnter(DragEventArgs e) => OnDragOver(e);

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) && OverRug(e) ? DragDropEffects.Move : DragDropEffects.None;
    }

    /// Dropping files onto the rug tucks them under it: they stay ordinary desktop icons (anything from
    /// another folder is moved onto the Desktop first), and Explorer places them where they were dropped.
    protected override void OnDragDrop(DragEventArgs e)
    {
        if (!OverRug(e) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var at = ctl.Unproject(new Vector2(e.X / scale, e.Y / scale), 0);
        for (int n = 0; n < paths.Length; n++)
        {
            float a = n * 2.39996f, r = 30 * MathF.Sqrt(n);
            var pos = at + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            var path = paths[n].TrimEnd('\\');
            try
            {
                if (!string.Equals(Path.GetDirectoryName(path), Store.Desktop, StringComparison.OrdinalIgnoreCase))
                {
                    var dest = Store.UniquePath(Store.Desktop, Path.GetFileName(path));
                    Store.Move(path, dest);
                    path = dest;
                }
                pendingPlacements.Add((path, ScreenPoint(pos), 0));
                if (ctl.Items.Find(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) is { } existing) existing.Pos = pos;
                else ctl.Items.Add(MakeItem(path, pos));          // the lump appears straight away
            }
            catch { System.Media.SystemSounds.Beep.Play(); }
        }
        RetryPlacements();
        ctl.Wake();
    }

    // MARK: Menu

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Straighten Rug", null, (_, _) => ctl.Straighten());
        m.Items.Add("Move Rug to Center", null, (_, _) => ctl.Recenter());
        m.Items.Add(new ToolStripSeparator());
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.Enabled, CheckOnClick = true };
        startup.CheckedChanged += (_, _) => Autostart.Enabled = startup.Checked;
        m.Items.Add(startup);
        m.Items.Add("Quit Road Rug", null, (_, _) => Close());
        return m;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        timer.Stop();
        iconPoll.Stop();
        Store.Save(ctl);
        tray.Visible = false;
        base.OnFormClosing(e);
    }
}

/// "Start with Windows" via the per-user Run key.
static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Road Rug";

    public static bool Enabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(Name) != null; }
        set
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, true);
            if (k == null) return;
            if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\""); else k.DeleteValue(Name, false);
        }
    }
}

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    public const int WM_MOUSEACTIVATE = 0x21, WM_WINDOWPOSCHANGING = 0x46, WM_CAPTURECHANGED = 0x215, MA_NOACTIVATE = 3;
    public const uint SWP_NOZORDER = 0x4;
    const uint GW_HWNDPREV = 3, GA_ROOT = 2, ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz, IntPtr src, ref POINT srcPt, int key, ref BLENDFUNCTION bf, uint flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    /// The top-level window that hosts the desktop icons (Progman, or a WorkerW on some setups).
    static IntPtr DesktopHost()
    {
        IntPtr host = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            host = h;
            return false;
        }, IntPtr.Zero);
        return host != IntPtr.Zero ? host : FindWindow("Progman", null);
    }

    /// What to pass as hwndInsertAfter to sit just above the desktop host (zero = leave the z-order alone).
    public static IntPtr WindowAboveDesktop(IntPtr self)
    {
        var host = DesktopHost();
        if (host == IntPtr.Zero) return IntPtr.Zero;
        var prev = GetWindow(host, GW_HWNDPREV);
        return prev == self ? IntPtr.Zero : prev;
    }

    /// True when another app's window is on top at this screen point, so the rug must not react.
    /// (Our window is click-through while idle, so the hit test sees whatever is beneath it.)
    public static bool CoveredByAnotherWindow(IntPtr self, Point p)
    {
        var hit = WindowFromPoint(new POINT { X = p.X, Y = p.Y });
        if (hit == IntPtr.Zero) return false;
        var root = GetAncestor(hit, GA_ROOT);
        return root != self && root != DesktopHost();
    }

    static IntPtr memDC, dib, dibBits;
    static int dibW, dibH;

    /// Pushes premultiplied BGRA pixels to a layered window at screen position (x, y).
    public static unsafe void PresentLayered(IntPtr hwnd, uint[] px, int w, int h, int x, int y)
    {
        var screen = GetDC(IntPtr.Zero);
        if (memDC == IntPtr.Zero) memDC = CreateCompatibleDC(screen);
        if (w != dibW || h != dibH)
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            dib = CreateDIBSection(screen, ref bmi, 0, out dibBits, IntPtr.Zero, 0);
            SelectObject(memDC, dib);
            dibW = w; dibH = h;
        }
        long bytes = (long)w * h * 4;
        fixed (uint* src = px) Buffer.MemoryCopy(src, (void*)dibBits, bytes, bytes);
        var dst = new POINT { X = x, Y = y };
        var size = new SIZE { CX = w, CY = h };
        var origin = new POINT();
        var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
        UpdateLayeredWindow(hwnd, screen, ref dst, ref size, memDC, ref origin, 0, ref blend, ULW_ALPHA);
        ReleaseDC(IntPtr.Zero, screen);
    }
}
