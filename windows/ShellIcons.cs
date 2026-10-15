using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RoadRug;

/// File icons and thumbnails from the Windows shell, and the desktop-style tiles drawn for hidden items.
static class ShellIcons
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                                                   [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    const int SIIGBF_BIGGERSIZEOK = 0x1;

    /// The shell's icon (or thumbnail) for a file or folder, as a premultiplied bitmap about `px` pixels square.
    public static Bitmap Get(string path, int px)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            if (factory.GetImage(new SIZE { cx = px, cy = px }, SIIGBF_BIGGERSIZEOK, out var hbm) == 0)
                try { return FromHBitmap(hbm); } finally { DeleteObject(hbm); }
        }
        catch { /* fall back below */ }
        try { return Icon.ExtractAssociatedIcon(path)?.ToBitmap(); } catch { return null; }
    }

    static Bitmap FromHBitmap(IntPtr hbm)
    {
        var bm = new BITMAP();
        GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bm);
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bits = new byte[w * h * 4];
        var hdc = GetDC(IntPtr.Zero);
        GetDIBits(hdc, hbm, 0, (uint)h, bits, ref bmi, 0);
        ReleaseDC(IntPtr.Zero, hdc);
        // Photo thumbnails come back without an alpha channel: treat them as opaque.
        bool anyAlpha = false;
        for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(bits, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        bmp.UnlockBits(data);
        return bmp;
    }

    /// Desktop-style tile: icon with its name underneath, 100 x 100 DIPs, at `scale` device pixels per DIP.
    public static Image32 Tile(string path, float scale)
    {
        int size = (int)MathF.Ceiling(100 * scale);
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.ScaleTransform(scale, scale);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var icon = Get(path, (int)MathF.Ceiling(64 * scale)))
                if (icon != null)
                {
                    float iw = 64, ih = 64 * icon.Height / (float)icon.Width;
                    if (ih > 64) { iw = 64 * icon.Width / (float)icon.Height; ih = 64; }
                    g.DrawImage(icon, new RectangleF(50 - iw / 2, 2 + (64 - ih) / 2, iw, ih));
                }
            string name = File.Exists(path) || Directory.Exists(path) ? Path.GetFileName(path) : path;
            using var font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.LineLimit };
            var rect = new RectangleF(2, 68, 96, 30);
            using (var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                g.DrawString(name, font, shadow, new RectangleF(rect.X + 0.8f, rect.Y + 0.8f, rect.Width, rect.Height), fmt);
            g.DrawString(name, font, Brushes.White, rect, fmt);
        }
        var img = new Image32(size, size);
        var data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var row = new int[size];
        for (int y = 0; y < size; y++)
        {
            Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, size);
            Buffer.BlockCopy(row, 0, img.Px, y * size * 4, size * 4);
        }
        bmp.UnlockBits(data);
        return img;
    }
}

/// Where swept items and the rug's state live: %APPDATA%\Road Rug.
static class Store
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Road Rug");
    public static readonly string Stash = Path.Combine(Dir, "Under the Rug");
    public static readonly string Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    static readonly string StateFile = Path.Combine(Dir, "state.json");
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    sealed class SavedItem { public string Name { get; set; } public float X { get; set; } public float Y { get; set; } }
    sealed class SavedState { public List<float[]> Particles { get; set; } = new(); public List<SavedItem> Items { get; set; } }

    public static string UniquePath(string dir, string name)
    {
        string path = Path.Combine(dir, name), stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int k = 2; File.Exists(path) || Directory.Exists(path); k++) path = Path.Combine(dir, $"{stem} {k}{ext}");
        return path;
    }

    public static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
    }

    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            long total = 0; int count = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                total += f.Length;
                if (++count > 20000) break;
            }
            return total;
        }
        catch { return 0; }
    }

    public static void Save(Controller c)
    {
        try
        {
            var st = new SavedState();
            foreach (var p in c.Rug.P) st.Particles.Add(new[] { p.X, p.Y, p.Z });
            Directory.CreateDirectory(Dir);
            File.WriteAllText(StateFile + ".tmp", JsonSerializer.Serialize(st, Json));
            File.Move(StateFile + ".tmp", StateFile, true);
        }
        catch { /* best effort */ }
    }

    /// Restores the rug, and returns where each file swept by version 1.0 was lying (to put it back there).
    public static Dictionary<string, Vector2> Load(Controller c)
    {
        var spots = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StateFile), Json) is { } st)
            {
                if (st.Particles.Count == Cloth.N)
                    for (int i = 0; i < Cloth.N; i++)
                        if (st.Particles[i].Length == 3) c.Rug.P[i] = c.Rug.Q[i] = new Vector3(st.Particles[i][0], st.Particles[i][1], st.Particles[i][2]);
                foreach (var si in st.Items ?? new()) spots[si.Name] = new Vector2(si.X, si.Y);
            }
        }
        catch { /* corrupt state: start fresh */ }
        c.UpdateProjection();
        return spots;
    }
}
