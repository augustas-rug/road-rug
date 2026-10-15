using System.Numerics;

namespace RoadRug;

/// Tunables, shared with the macOS version (Sources/Cloth.swift).
static class T
{
    public const int NX = 49, NY = 26;            // particle grid, matching the play mat's ~1.9:1 shape
    public const float Spacing = 15f;             // DIPs between particles -> 720 x 375 rug
    public const float RugW = (NX - 1) * Spacing, RugH = (NY - 1) * Spacing;
    public const float FixedDt = 1f / 120f;
    public const int Substeps = 3, Iterations = 12;
    public const float Gravity = 2800f;           // DIP/s^2
    public const float Friction = 0.8f;           // Coulomb coefficient against the desktop
    public const float AirDamping = 0.99f;
    public const float AirDragXY = 0.97f;         // airborne parts fall rather than keep swinging over
    public const float MaxStep = 8f;              // DIP per substep; stops a teleport turning into an explosion
    public const float SafeDrop = 20f;            // drop height when (re)laying the rug
    public const float IconLump = 3.5f;           // how much one desktop icon under the rug lifts it
    public const float LumpRadius = 36f;          // how far that lump spreads
    public const float LumpMax = 11f;             // a whole pile of icons levels off around this height
    public const float BendStiffness = 0.45f;     // smooth curves without flopping like paper
    public const float Thickness = 4f;            // pile height above the backing
    public const float LayerGap = Thickness + 1.5f;
    public const float CornerRadius = 12f;
    public const float Binding = 3f;
    public const float LiftHeight = 80f;
    public const float SettleTime = 4f;
    public const float HandFollow = 18f;          // how quickly the grip catches up with the cursor (1/s)
    public const float Perspective = 1f / 2000f;
}

/// Position-based-dynamics cloth lying on a floor (the desktop). x/y are DIPs with y pointing down,
/// z is height above the desktop towards the viewer.
sealed class Cloth
{
    public const int N = T.NX * T.NY;
    public readonly Vector3[] P = new Vector3[N];
    public readonly Vector3[] Q = new Vector3[N];        // previous positions (velocity = P - Q)
    public readonly bool[] Pinned = new bool[N];
    public readonly Vector3[] PinFrom = new Vector3[N];
    public readonly Vector3[] PinTo = new Vector3[N];
    public readonly float[] FloorH = new float[N];
    public readonly bool[] Flipped = new bool[N];        // upside-down (the top layer of a fold)
    readonly bool[] onLayer = new bool[N];               // resting on another layer (gets desk-like friction)
    readonly int[] head = new int[4096];
    readonly int[] next = new int[N];
    readonly int[] ca, cb;
    readonly float[] crest, ck;
    public Vector4 Walls = new(0, 0, 1000, 1000);        // minX, minY, maxX, maxY
    public float Motion;

    public Cloth(Vector2 center)
    {
        var a = new List<int>(); var b = new List<int>(); var r = new List<float>(); var k = new List<float>();
        (int di, int dj, float k)[] links = { (1, 0, 1f), (0, 1, 1f), (1, 1, 0.8f), (-1, 1, 0.8f), (2, 0, T.BendStiffness), (0, 2, T.BendStiffness) };
        for (int j = 0; j < T.NY; j++)
            for (int i = 0; i < T.NX; i++)
                foreach (var (di, dj, kk) in links)
                {
                    int i2 = i + di, j2 = j + dj;
                    if (i2 < 0 || i2 >= T.NX || j2 >= T.NY) continue;
                    a.Add(j * T.NX + i); b.Add(j2 * T.NX + i2);
                    r.Add(T.Spacing * MathF.Sqrt(di * di + dj * dj)); k.Add(kk);
                }
        ca = a.ToArray(); cb = b.ToArray(); crest = r.ToArray(); ck = k.ToArray();
        Layout(center, 0, T.SafeDrop);
    }

    /// Lays the rug out flat, dropped from `drop` so it settles over anything underneath.
    public void Layout(Vector2 center, float angle, float drop)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        for (int j = 0; j < T.NY; j++)
            for (int i = 0; i < T.NX; i++)
            {
                float lx = i * T.Spacing - T.RugW / 2, ly = j * T.Spacing - T.RugH / 2;
                var v = new Vector3(center.X + lx * c - ly * s, center.Y + lx * s + ly * c, drop);
                int k = j * T.NX + i;
                P[k] = v; Q[k] = v; Pinned[k] = false;
            }
    }

    public Vector2 Centroid
    {
        get { var s = Vector2.Zero; foreach (var v in P) s += new Vector2(v.X, v.Y); return s / N; }
    }

    public float Angle
    {
        get
        {
            var d = Vector2.Zero;
            for (int j = 0; j < T.NY; j++) { var a = P[j * T.NX]; var b = P[j * T.NX + T.NX - 1]; d += new Vector2(b.X - a.X, b.Y - a.Y); }
            return MathF.Atan2(d.Y, d.X);
        }
    }

    public void Rotate(float angle, Vector2 c)
    {
        float co = MathF.Cos(angle), si = MathF.Sin(angle);
        Vector3 R(Vector3 v) { float x = v.X - c.X, y = v.Y - c.Y; return new Vector3(c.X + x * co - y * si, c.Y + x * si + y * co, v.Z); }
        for (int i = 0; i < N; i++) { P[i] = R(P[i]); Q[i] = R(Q[i]); }
    }

    public void Translate(Vector2 d)
    {
        var d3 = new Vector3(d, 0);
        for (int i = 0; i < N; i++) { P[i] += d3; Q[i] += d3; }
    }

    /// The desk's height under the rug: each icon underneath adds a low, soft lump, so a pile of icons in one
    /// spot makes a bigger mound. The total eases off towards LumpMax instead of growing without limit.
    public static float FloorHeight(float x, float y, Vector4[] bumps)
    {
        float sum = 0;
        foreach (var b in bumps)
        {
            float dx = x - b.X, dy = y - b.Y, d2 = dx * dx + dy * dy, r2 = b.W * b.W;
            if (d2 < r2) { float t = 1 - d2 / r2; sum += b.Z * t * t; }
        }
        return sum <= 0 ? 0 : T.LumpMax * (1 - MathF.Exp(-sum / T.LumpMax));
    }

    /// Advances one fixed frame. `bumps` are (x, y, height, radius) of things hidden under the rug.
    public void Step(float dt, Vector4[] bumps)
    {
        float h = dt / T.Substeps, g = T.Gravity * h * h, fr = T.Friction * g, maxMove = 0;
        for (int s = 0; s < T.Substeps; s++)
        {
            float t = (s + 1f) / T.Substeps;
            for (int i = 0; i < N; i++) FloorH[i] = FloorHeight(P[i].X, P[i].Y, bumps);

            // Integrate (Verlet) with Coulomb friction for particles touching the desktop.
            for (int i = 0; i < N; i++)
            {
                if (Pinned[i]) { Q[i] = P[i]; P[i] = PinFrom[i] + (PinTo[i] - PinFrom[i]) * t; continue; }
                var v = (P[i] - Q[i]) * T.AirDamping;
                float speed = v.Length();
                if (speed > T.MaxStep) v *= T.MaxStep / speed;
                if (P[i].Z <= FloorH[i] + 0.5f || onLayer[i])
                {
                    float sp = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
                    if (sp <= fr) { v.X = 0; v.Y = 0; } else { float k = (sp - fr) / sp; v.X *= k; v.Y *= k; }
                }
                else { v.X *= T.AirDragXY; v.Y *= T.AirDragXY; }
                Q[i] = P[i];
                P[i] += v;
                P[i].Z -= g;
            }

            // Gauss-Seidel, sweeping alternately forwards and backwards so no direction is favoured.
            int count = ca.Length;
            for (int it = 0; it < T.Iterations; it++)
            {
                bool forward = it % 2 == 0;
                for (int idx = 0; idx < count; idx++)
                {
                    int c = forward ? idx : count - 1 - idx;
                    int a = ca[c], b = cb[c];
                    float wa = Pinned[a] ? 0 : 1, wb = Pinned[b] ? 0 : 1, w = wa + wb;
                    if (w == 0) continue;
                    var d = P[b] - P[a];
                    float len = d.Length();
                    if (len < 1e-6f) continue;
                    var corr = d * ((len - crest[c]) / (len * w) * ck[c]);
                    P[a] += corr * wa;
                    P[b] -= corr * wb;
                }
                for (int i = 0; i < N; i++) if (P[i].Z < FloorH[i]) P[i].Z = FloorH[i];
            }

            if (IsRaised()) SeparateLayers();
            if (s == T.Substeps - 1)
                for (int i = 0; i < N; i++) if (!Pinned[i]) maxMove = MathF.Max(maxMove, (P[i] - Q[i]).Length());
        }
        Motion = maxMove;
        RescueIfLost();
    }

    /// Never nudged while any of it is on screen; only slid back if it was thrown completely off screen.
    void RescueIfLost()
    {
        foreach (var x in Pinned) if (x) return;
        foreach (var v in P) if (v.X >= Walls.X && v.X <= Walls.Z && v.Y >= Walls.Y && v.Y <= Walls.W) return;
        var c = Centroid;
        var target = new Vector2(Math.Clamp(c.X, Walls.X, Walls.Z), Math.Clamp(c.Y, Walls.Y, Walls.W));
        Translate((target - c) * 0.1f);
    }

    bool IsRaised()
    {
        for (int i = 0; i < N; i++) if (P[i].Z > FloorH[i] + 1) return true;
        Array.Clear(Flipped);
        return false;
    }

    static int Hash(int x, int y) => (x * 73856093 ^ y * 19349663) & 4095;

    /// Keeps the layers of a fold LayerGap apart so the rug can roll over itself without clipping.
    void SeparateLayers()
    {
        for (int j = 0; j < T.NY; j++)
            for (int i = 0; i < T.NX; i++)
            {
                var dx = P[j * T.NX + Math.Min(i + 1, T.NX - 1)] - P[j * T.NX + Math.Max(i - 1, 0)];
                var dy = P[Math.Min(j + 1, T.NY - 1) * T.NX + i] - P[Math.Max(j - 1, 0) * T.NX + i];
                Flipped[j * T.NX + i] = dx.X * dy.Y - dx.Y * dy.X < 0;
            }
        // While held, layers slide freely over each other; the grip that keeps a fold in place applies once let go.
        bool held = Array.IndexOf(Pinned, true) >= 0;
        const float cell = T.Spacing, r = T.Spacing * 1.3f, r2 = r * r;
        Array.Fill(head, -1);
        Array.Clear(onLayer);
        for (int i = 0; i < N; i++)
        {
            int hsh = Hash((int)MathF.Floor(P[i].X / cell), (int)MathF.Floor(P[i].Y / cell));
            next[i] = head[hsh]; head[hsh] = i;
        }
        for (int i = 0; i < N; i++)
        {
            int cx = (int)MathF.Floor(P[i].X / cell), cy = (int)MathF.Floor(P[i].Y / cell);
            int ii = i % T.NX, ij = i / T.NX;
            for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                    for (int j = head[Hash(cx + ox, cy + oy)]; j >= 0; j = next[j])
                    {
                        if (j <= i) continue;
                        if (Math.Abs(ii - j % T.NX) <= 3 && Math.Abs(ij - j / T.NX) <= 3) continue;
                        float dx = P[i].X - P[j].X, dy = P[i].Y - P[j].Y;
                        if (dx * dx + dy * dy > r2) continue;
                        float dz = P[i].Z - P[j].Z;
                        if (MathF.Abs(dz) >= T.LayerGap) continue;
                        // Whichever layer was on top a moment ago stays on top (so unfolding is smooth).
                        float qdz = Q[i].Z - Q[j].Z;
                        bool iAbove = MathF.Abs(qdz) > 0.5f ? qdz > 0 : Flipped[i];
                        int top = iAbove ? i : j, bot = iAbove ? j : i;
                        float w = 1 - MathF.Sqrt(dx * dx + dy * dy) / r;
                        float need = (T.LayerGap - (P[top].Z - P[bot].Z)) * w;
                        if (w > 0.3f && !held) onLayer[top] = true;
                        if (need <= 0) continue;
                        bool botFixed = Pinned[bot] || P[bot].Z <= FloorH[bot] + 0.01f, topFixed = Pinned[top];
                        if (topFixed && botFixed) continue;
                        if (botFixed) P[top].Z += need;
                        else if (topFixed) P[bot].Z -= need;
                        else { P[top].Z += need * 0.5f; P[bot].Z -= need * 0.5f; }
                        if (!topFixed && !held)
                        {
                            var rel = (P[top] - Q[top]) - (P[bot] - Q[bot]);
                            P[top].X -= rel.X * 0.5f; P[top].Y -= rel.Y * 0.5f;
                        }
                    }
        }
    }

    /// Worst stretch of any structural link (1.0 = rest length).
    public float MaxStretch()
    {
        float worst = 0;
        for (int c = 0; c < ca.Length; c++)
            if (ck[c] == 1f) worst = MathF.Max(worst, (P[cb[c]] - P[ca[c]]).Length() / crest[c]);
        return worst;
    }
}
