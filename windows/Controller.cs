using System.Numerics;

namespace RoadRug;

/// Premultiplied BGRA image (0xAARRGGBB per pixel) in device pixels.
sealed class Image32
{
    public readonly int W, H;
    public readonly uint[] Px;
    public Image32(int w, int h) { W = w; H = h; Px = new uint[w * h]; }
}

/// Something swept under the rug. The file itself lives in the stash folder; this is where it sits on the desk.
sealed class Item
{
    public string Path;
    public Vector2 Pos;                 // DIPs
    public float Height, TargetHeight = T.IconLump;
    public bool Hidden;
    public Image32 Icon;                // 100 x 100 DIP tile (icon + label), rendered at the current scale
    public Item(string path, Vector2 pos) { Path = path; Pos = pos; }
    public Vector4 Bump => new(Pos.X, Pos.Y, Height, T.LumpRadius);   // a low, soft lump about the size of the icon
}

/// The rug and everything under it: grabbing, lifting, sliding, hit-testing and when to sleep.
/// Platform-neutral, so the window and the headless snapshot test share it.
sealed class Controller
{
    enum Grab { None, Lift, Slide }

    public readonly Cloth Rug;
    public readonly List<Item> Items = new();
    public Vector2 ScreenCenter;                        // perspective centre, DIPs
    public readonly Vector2[] Proj = new Vector2[Cloth.N];
    Vector4 bbox;

    Grab grab = Grab.None;
    readonly List<(int k, Vector2 off)> grabbed = new();
    float grabZ, quiet, idle, acc;
    int slideK;
    Vector2 grabOffset, hand;
    bool awake = true;

    public bool Grabbing => grab != Grab.None;
    public bool Awake => awake;
    public event Action Settled;                        // the rug just went to sleep (a good moment to save)

    public Controller(Vector2 screenCenter)
    {
        ScreenCenter = screenCenter;
        Rug = new Cloth(screenCenter);
        UpdateProjection();
    }

    public Vector2 Project(Vector3 p) => ScreenCenter + (new Vector2(p.X, p.Y) - ScreenCenter) * (1 + p.Z * T.Perspective);
    public Vector2 Unproject(Vector2 s, float z) => ScreenCenter + (s - ScreenCenter) / (1 + z * T.Perspective);

    public void UpdateProjection()
    {
        var mn = new Vector2(float.MaxValue); var mx = new Vector2(float.MinValue);
        for (int i = 0; i < Cloth.N; i++)
        {
            var s = Project(Rug.P[i]);
            Proj[i] = s;
            mn = Vector2.Min(mn, s); mx = Vector2.Max(mx, s);
        }
        bbox = new Vector4(mn.X, mn.Y, mx.X, mx.Y);
    }

    /// Topmost rug particle under a point (DIPs), with its height.
    public (int k, float z)? HitRug(Vector2 s)
    {
        if (s.X < bbox.X || s.Y < bbox.Y || s.X > bbox.Z || s.Y > bbox.W) return null;
        (int k, float z)? best = null;
        void Tri(int a, int b, int c)
        {
            var pa = Proj[a]; var v0 = Proj[b] - pa; var v1 = Proj[c] - pa; var v2 = s - pa;
            float d = v0.X * v1.Y - v1.X * v0.Y;
            if (MathF.Abs(d) < 1e-6f) return;
            float u = (v2.X * v1.Y - v1.X * v2.Y) / d, v = (v0.X * v2.Y - v2.X * v0.Y) / d;
            if (u < 0 || v < 0 || u + v > 1) return;
            float w0 = 1 - u - v;
            float z = w0 * Rug.P[a].Z + u * Rug.P[b].Z + v * Rug.P[c].Z;
            if (best == null || z > best.Value.z)
                best = (w0 >= u && w0 >= v ? a : (u >= v ? b : c), z);
        }
        for (int j = 0; j < T.NY - 1; j++)
            for (int i = 0; i < T.NX - 1; i++)
            {
                int a = j * T.NX + i, b = a + 1, c = a + T.NX + 1, d = a + T.NX;
                var lo = Vector2.Min(Vector2.Min(Proj[a], Proj[b]), Vector2.Min(Proj[c], Proj[d]));
                var hi = Vector2.Max(Vector2.Max(Proj[a], Proj[b]), Vector2.Max(Proj[c], Proj[d]));
                if (s.X < lo.X || s.Y < lo.Y || s.X > hi.X || s.Y > hi.Y) continue;
                Tri(a, b, c); Tri(a, c, d);
            }
        return best;
    }

    /// An uncovered item under a point: its icon sits above the label, centred on Pos.
    public Item HitItem(Vector2 s)
    {
        if (HitRug(s) != null) return null;
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            var it = Items[i];
            if (!it.Hidden && MathF.Abs(s.X - it.Pos.X) <= 40 && s.Y >= it.Pos.Y - 34 && s.Y <= it.Pos.Y + 52) return it;
        }
        return null;
    }

    public void Wake() { awake = true; quiet = 0; idle = 0; }

    /// Grabs the rug under `s`: edges and corners lift, the middle slides.
    public bool BeginGrab(Vector2 s, bool forceLift = false)
    {
        UpdateProjection();
        if (HitRug(s) is not { } hit) return false;
        int i = hit.k % T.NX, j = hit.k / T.NX;
        int edge = Math.Min(Math.Min(i, T.NX - 1 - i), Math.Min(j, T.NY - 1 - j));
        grab = edge <= 3 || forceLift ? Grab.Lift : Grab.Slide;
        var pk = Rug.P[hit.k];
        slideK = hit.k;
        grabbed.Clear();
        if (grab == Grab.Lift)
            for (int jj = Math.Max(0, j - 2); jj <= Math.Min(T.NY - 1, j + 2); jj++)
                for (int ii = Math.Max(0, i - 2); ii <= Math.Min(T.NX - 1, i + 2); ii++)
                {
                    if (new Vector2(ii - i, jj - j).Length() > 1.6f) continue;
                    int k = jj * T.NX + ii;
                    grabbed.Add((k, new Vector2(Rug.P[k].X - pk.X, Rug.P[k].Y - pk.Y)));
                    Rug.Pinned[k] = true;
                    Rug.PinTo[k] = Rug.P[k];
                }
        grabZ = pk.Z;
        grabOffset = new Vector2(pk.X, pk.Y) - Unproject(s, pk.Z);
        hand = s;
        Wake();
        return true;
    }

    public void Release()
    {
        if (grab == Grab.None) return;
        foreach (var (k, _) in grabbed)
        {
            Rug.Pinned[k] = false;
            Rug.Q[k] = Rug.P[k];   // drops where you let go; no throw, so a flap never keeps rolling over
        }
        grabbed.Clear();
        grab = Grab.None;
        Wake();
    }

    public void Straighten() { Release(); Rug.Layout(Rug.Centroid, Rug.Angle, T.SafeDrop); Wake(); }
    public void Recenter() { Release(); Rug.Layout(ScreenCenter, 0, T.SafeDrop); Wake(); }
    public void Twist(float radians) { Rug.Rotate(radians, Rug.Centroid); Wake(); }

    /// Runs physics for one display frame. Returns false when the rug is asleep and nothing changed.
    public bool Advance(float dt, Vector2 mouse)
    {
        bool itemsMoving = false;
        foreach (var it in Items)
        {
            if (it.Height == it.TargetHeight) continue;
            float d = it.TargetHeight - it.Height;
            it.Height = MathF.Abs(d) < 0.05f ? it.TargetHeight : it.Height + d * MathF.Min(1, dt * 5);
            itemsMoving = true;
        }
        if (itemsMoving || grab != Grab.None) Wake();
        if (!awake) return false;

        // The hand drags a heavy rug: the grip trails the cursor (smoothly) instead of snapping to it.
        if (grab != Grab.None) hand += (mouse - hand) * MathF.Min(1, dt * T.HandFollow);
        if (grab == Grab.Lift)
        {
            grabZ += (T.LiftHeight - grabZ) * MathF.Min(1, dt * 6);
            var target = Unproject(hand, grabZ) + grabOffset;
            foreach (var (k, off) in grabbed)
            {
                float droop = off.Length() * 0.35f;
                Rug.PinTo[k] = new Vector3(target + off, MathF.Max(grabZ - droop, 0));
            }
        }
        else if (grab == Grab.Slide)
        {
            // Towed like a real rug: the grip follows the hand and the body swings round to trail behind it.
            var g = new Vector2(Rug.P[slideK].X, Rug.P[slideK].Y);
            var d = Unproject(hand, Rug.P[slideK].Z) + grabOffset - g;
            float len = d.Length();
            if (len > 0.01f)
            {
                var r = Rug.Centroid - g;
                float lr = r.Length();
                if (lr > 8)
                {
                    var back = -d / len;
                    float sinT = (r.X * back.Y - r.Y * back.X) / lr;
                    Rug.Rotate(sinT * MathF.Min(len / lr, 0.2f) * 0.35f, g);   // heavy: swings round slowly
                }
                Rug.Translate(d);
            }
        }

        var bumps = Items.Where(it => !it.Hidden).Select(it => it.Bump).ToArray();
        acc += dt;
        int steps = 0;
        while (acc >= T.FixedDt && steps < 4)
        {
            foreach (var (k, _) in grabbed) Rug.PinFrom[k] = Rug.P[k];
            Rug.Step(T.FixedDt, bumps);
            acc -= T.FixedDt;
            steps++;
        }
        if (steps == 4) acc = 0;
        UpdateProjection();

        idle = grab == Grab.None && !itemsMoving ? idle + dt : 0;
        // Sleep once still; after SettleTime untouched, also accept a tiny residual creep (never mid-fall).
        bool still = Rug.Motion < 0.03f || (idle > T.SettleTime && Rug.Motion < 0.3f);
        if (still && grab == Grab.None && !itemsMoving)
        {
            quiet += dt;
            if (quiet > 0.6f) { awake = false; Settled?.Invoke(); }
        }
        else quiet = 0;
        return true;
    }
}
