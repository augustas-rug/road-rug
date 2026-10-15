using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace RoadRug;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--snapshot") return Snapshot.Run(args[1]);

        using var single = new Mutex(true, "RoadRug.SingleInstance", out bool first);
        if (!first) return 0;
        ApplicationConfiguration.Initialize();
        Application.Run(new RugWindow());
        return 0;
    }
}

/// `RoadRug.exe --snapshot <folder>`: a scripted, headless scene (settling over lumps, folding a corner,
/// dragging the rug) rendered with the real renderer to PNG frames, plus report.txt with physics checks
/// and render timings. CI runs this on Windows so every build is looked at.
static class Snapshot
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        const int W = 1440, H = 900;
        var c = new Controller(new Vector2(W / 2f, H / 2f));
        c.Rug.Walls = new Vector4(0, 0, W, H);
        c.Rug.Layout(new Vector2(W / 2f, H / 2f), 0.05f, T.SafeDrop);
        using var rug = new Bitmap(typeof(Program).Assembly.GetManifestResourceStream("rug.png"));
        var renderer = new Renderer(rug, 1);
        var canvas = new Canvas();
        canvas.Resize(W, H);
        var report = new List<string>();

        var center = new Vector2(W / 2f, H / 2f);
        foreach (var (path, off, size) in new[] { (@"C:\Windows\notepad.exe", new Vector2(-150, -40), 4_000_000L), (@"C:\Windows\explorer.exe", new Vector2(120, 60), 900_000L) })
            c.Items.Add(new Item(path, center + off) { Icon = ShellIcons.Tile(path, 1), TargetHeight = Item.HeightForBytes(size) });

        var renderTimes = new List<double>();
        void Frames(float seconds, Func<float, Vector2> mouse = null)
        {
            int steps = Math.Max(1, (int)(seconds * 60));
            for (int k = 0; k < steps; k++) c.Advance(1 / 60f, mouse?.Invoke((k + 1f) / steps) ?? Vector2.Zero);
        }
        void Shot(string name)
        {
            var sw = Stopwatch.StartNew();
            renderer.Render(canvas, c, Vector2.Zero, 0xFF67839A);   // stand-in wallpaper
            renderTimes.Add(sw.Elapsed.TotalMilliseconds);
            Save(canvas, Path.Combine(dir, name + ".png"));
        }

        Frames(1.5f);
        Shot("1-settled");

        // Fold the bottom-right corner over towards the middle.
        var p = c.Rug.P[Cloth.N - 1];
        var corner = new Vector2(p.X, p.Y) + Vector2.Normalize(c.Rug.Centroid - new Vector2(p.X, p.Y)) * 8;
        var dropAt = center + new Vector2(80, -40);
        if (c.BeginGrab(corner))
        {
            Frames(1.0f, f => corner + (dropAt - corner) * f);
            Shot("2-folding");
            c.Release();
            Frames(1.0f);
            Shot("3-folded");
        }
        else report.Add("FAIL could not grab the corner");

        c.Straighten();
        Frames(1.0f);
        var mid = c.Rug.Centroid + new Vector2(-100, -20);
        if (c.BeginGrab(mid))
        {
            Frames(0.6f, f => mid + new Vector2(300, -80) * f);
            Shot("4-dragging");
            c.Release();
            Frames(1.5f);
            Shot("5-rest");
        }
        else report.Add("FAIL could not grab the middle");

        // Timing of the window-sized render (what the app does every frame while you drag).
        var bounds = Renderer.Bounds(c);
        var small = new Canvas();
        small.Resize((int)bounds.Width + 1, (int)bounds.Height + 1);
        var sw2 = Stopwatch.StartNew();
        for (int i = 0; i < 30; i++) renderer.Render(small, c, new Vector2(bounds.Left, bounds.Top), 0);
        double perFrame = sw2.Elapsed.TotalMilliseconds / 30;
        var simSw = Stopwatch.StartNew();
        c.Rug.Layout(center, 0, T.SafeDrop);
        for (int i = 0; i < 120; i++) c.Rug.Step(T.FixedDt, Array.Empty<Vector4>());
        double perStep = simSw.Elapsed.TotalMilliseconds / 120;

        bool nan = c.Rug.P.Any(v => float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z));
        if (nan) report.Add("FAIL NaN in the cloth");
        try
        {
            var icons = DesktopIcons.List();
            report.Add($"desktop icons via Explorer: {icons.Count}" + (icons.Count > 0 ? $" (first: {Path.GetFileName(icons[0].Path)} at {icons[0].Center})" : ""));
        }
        catch (Exception ex) { report.Add($"FAIL reading desktop icons: {ex.GetType().Name} {ex.Message}"); }
        report.Add($"max stretch {c.Rug.MaxStretch():F3}");
        report.Add($"full-frame render {renderTimes.Average():F1} ms, rug-sized render {perFrame:F1} ms ({small.W}x{small.H}), physics step {perStep:F2} ms");
        report.Add(report.Any(l => l.StartsWith("FAIL")) ? "RESULT FAIL" : "RESULT OK");
        File.WriteAllLines(Path.Combine(dir, "report.txt"), report);
        return report.Last() == "RESULT OK" ? 0 : 1;
    }

    static void Save(Canvas cv, string path)
    {
        using var bmp = new Bitmap(cv.W, cv.H, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, cv.W, cv.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        var row = new int[cv.W];
        for (int y = 0; y < cv.H; y++)
        {
            Buffer.BlockCopy(cv.Px, y * cv.W * 4, row, 0, cv.W * 4);
            Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, cv.W);
        }
        bmp.UnlockBits(data);
        bmp.Save(path, ImageFormat.Png);
    }
}
