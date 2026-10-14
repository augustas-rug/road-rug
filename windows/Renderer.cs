using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RoadRug;

/// Render target: premultiplied BGRA pixels plus a depth buffer and a half-resolution shadow mask.
sealed class Canvas
{
    public int W, H, SW, SH;
    public uint[] Px = Array.Empty<uint>();
    public float[] Z = Array.Empty<float>();
    public float[] Sh = Array.Empty<float>(), ShTmp = Array.Empty<float>();

    public void Resize(int w, int h)
    {
        if (w == W && h == H) return;
        W = w; H = h; SW = w / 2 + 1; SH = h / 2 + 1;
        Px = new uint[w * h]; Z = new float[w * h];
        Sh = new float[SW * SH]; ShTmp = new float[SW * SH];
    }
}

/// A small software rasterizer for the rug, matching the macOS Metal renderer: printed pile on top,
/// felt backing underneath, a bound edge with rounded corners, a soft shadow, and the icons on the desk.
sealed class Renderer
{
    public readonly float Scale;                     // device pixels per DIP
    readonly Image32 top, back;                      // baked rug surfaces at Scale
    static readonly int[] Perimeter = BuildPerimeter();
    static readonly Vector3 L = Vector3.Normalize(new Vector3(-0.55f, -0.65f, 0.75f));   // light from the top left
    static readonly Vector2 ShadowDir = new(0.32f, 0.48f);
    const uint EdgeColor = 0xFF4D4F57;

    struct V { public float X, Y, Z, U, Vv, Shade; }
    readonly V[] verts = new V[2 * Cloth.N + 2 * Perimeter.Length];
    readonly int[] indices;
    readonly int topTris, backTris;

    public Renderer(Bitmap rug, float scale)
    {
        Scale = scale;
        int tw = (int)MathF.Ceiling(T.RugW * scale), th = (int)MathF.Ceiling(T.RugH * scale);
        top = Bake(rug, tw, th, scale);
        back = Bake(null, Math.Max(8, tw / 2), Math.Max(8, th / 2), scale * 0.5f);

        var idx = new List<int>();
        for (int j = 0; j < T.NY - 1; j++)
            for (int i = 0; i < T.NX - 1; i++)
            {
                int a = j * T.NX + i, b = a + 1, c = a + T.NX + 1, d = a + T.NX;
                idx.AddRange(new[] { a, b, c, a, c, d });
            }
        topTris = idx.Count / 3;
        idx.AddRange(idx.Select(k => k + Cloth.N).ToArray());
        backTris = topTris;
        int n = 2 * Cloth.N, ring = Perimeter.Length;
        for (int e = 0; e < ring; e++)
        {
            if (InCorner(Perimeter[e]) || InCorner(Perimeter[(e + 1) % ring])) continue;   // rounded off by the texture
            int ta = n + 2 * e, tb = n + 2 * ((e + 1) % ring);
            idx.AddRange(new[] { ta, tb, tb + 1, ta, tb + 1, ta + 1 });
        }
        indices = idx.ToArray();
    }

    static int[] BuildPerimeter()
    {
        var r = new List<int>();
        for (int i = 0; i < T.NX; i++) r.Add(i);
        for (int j = 1; j < T.NY; j++) r.Add(j * T.NX + T.NX - 1);
        for (int i = T.NX - 2; i >= 0; i--) r.Add((T.NY - 1) * T.NX + i);
        for (int j = T.NY - 2; j >= 1; j--) r.Add(j * T.NX);
        return r.ToArray();
    }

    static bool InCorner(int k)
    {
        int i = k % T.NX, j = k / T.NX;
        return Math.Min(i, T.NX - 1 - i) * T.Spacing < T.CornerRadius && Math.Min(j, T.NY - 1 - j) * T.Spacing < T.CornerRadius;
    }

    static float Hash21(float x, float y)
    {
        x = Fract(x * 123.34f); y = Fract(y * 456.21f);
        float d = x * (x + 45.32f) + y * (y + 45.32f);
        x += d; y += d;
        return Fract(x * y);
    }
    static float Fract(float v) => v - MathF.Floor(v);
    static uint Pack(float a, float r, float g, float b) =>
        ((uint)Math.Clamp(a * 255 + 0.5f, 0, 255) << 24) | ((uint)Math.Clamp(r * 255 + 0.5f, 0, 255) << 16) |
        ((uint)Math.Clamp(g * 255 + 0.5f, 0, 255) << 8) | (uint)Math.Clamp(b * 255 + 0.5f, 0, 255);

    /// Bakes a rug surface: the print (or felt), pile noise, the stitched binding and anti-aliased rounded corners.
    static Image32 Bake(Bitmap src, int tw, int th, float scale)
    {
        var img = new Image32(tw, th);
        if (src != null)
        {
            using var bmp = new Bitmap(tw, th, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var ia = new ImageAttributes())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(src, new Rectangle(0, 0, tw, th), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            var data = bmp.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var row = new int[tw];
            for (int y = 0; y < th; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, tw);
                Buffer.BlockCopy(row, 0, img.Px, y * tw * 4, tw * 4);
            }
            bmp.UnlockBits(data);
        }

        const float R = T.CornerRadius;
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                float px = (x + 0.5f) / tw * T.RugW, py = (y + 0.5f) / th * T.RugH;
                float dx = MathF.Min(px, T.RugW - px), dy = MathF.Min(py, T.RugH - py);
                float edgeDist = MathF.Min(dx, dy), cover = 1;
                if (dx < R && dy < R)
                {
                    float arc = MathF.Sqrt((R - dx) * (R - dx) + (R - dy) * (R - dy));
                    edgeDist = R - arc;
                    cover = Math.Clamp(edgeDist * scale + 0.5f, 0, 1);
                }
                float knot = Hash21(MathF.Floor(px * 1.6f), MathF.Floor(py * 1.6f));
                float r, g, b;
                if (src == null)
                {
                    float f = 0.92f + 0.12f * knot;                                 // non-slip felt backing
                    r = 0.29f * f; g = 0.29f * f; b = 0.31f * f;
                }
                else if (edgeDist < T.Binding)
                {
                    float stitch = 0.5f + 0.5f * MathF.Sin((px + py) * 2.4f);      // stitched binding
                    float f = 0.88f + 0.12f * stitch + 0.08f * knot;
                    r = 0.30f * f; g = 0.31f * f; b = 0.34f * f;
                }
                else
                {
                    uint c = img.Px[y * tw + x];
                    float rowShade = 0.5f + 0.5f * MathF.Sin(py * 1.6f * 6.2831f);
                    float f = 0.95f + 0.08f * knot - 0.03f * rowShade;              // low-pile tufts
                    r = ((c >> 16) & 255) / 255f * f; g = ((c >> 8) & 255) / 255f * f; b = (c & 255) / 255f * f;
                }
                img.Px[y * tw + x] = Pack(cover, r * cover, g * cover, b * cover);
            }
        return img;
    }

    /// Draws the scene into `cv`, whose top-left corner sits at `origin` (DIPs). `background` is 0 for a
    /// transparent window, or an opaque colour for snapshots.
    public void Render(Canvas cv, Controller c, Vector2 origin, uint background)
    {
        Array.Fill(cv.Px, background);
        Array.Fill(cv.Z, float.NegativeInfinity);
        BuildVertices(c, origin);

        // Things on the desk, wherever the rug isn't covering them.
        foreach (var it in c.Items)
            if (!it.Hidden && it.Icon != null)
                Blit(cv, it.Icon, (int)MathF.Round((it.Pos.X - 50 - origin.X) * Scale), (int)MathF.Round((it.Pos.Y + 16 - 50 - origin.Y) * Scale));

        DrawShadow(cv, c, origin);

        // Each band of rows is rasterized on its own core; bands never touch the same pixels.
        int tris = indices.Length / 3;
        ForBands(cv.H, (y0, y1) =>
        {
            for (int t = 0; t < tris; t++)
            {
                int surf = t < topTris ? 0 : t < topTris + backTris ? 1 : 2;
                Raster(cv, verts[indices[3 * t]], verts[indices[3 * t + 1]], verts[indices[3 * t + 2]], surf, y0, y1);
            }
        });
    }

    static void ForBands(int rows, Action<int, int> body)
    {
        int bands = Math.Max(1, Math.Min(Environment.ProcessorCount, rows / 32));
        Parallel.For(0, bands, b => body(rows * b / bands, rows * (b + 1) / bands));
    }

    void BuildVertices(Controller c, Vector2 origin)
    {
        var P = c.Rug.P;
        int n = Cloth.N;
        Span<Vector3> normals = new Vector3[n];
        for (int j = 0; j < T.NY; j++)
            for (int i = 0; i < T.NX; i++)
            {
                int k = j * T.NX + i;
                var dx = P[j * T.NX + Math.Min(i + 1, T.NX - 1)] - P[j * T.NX + Math.Max(i - 1, 0)];
                var dy = P[Math.Min(j + 1, T.NY - 1) * T.NX + i] - P[Math.Max(j - 1, 0) * T.NX + i];
                var nrm = Vector3.Normalize(Vector3.Cross(dx, dy));
                normals[k] = nrm;
                float u = (float)i / (T.NX - 1), v = (float)j / (T.NY - 1);
                verts[k] = MakeVertex(c, origin, P[k] + nrm * T.Thickness, nrm, u, v);
                verts[n + k] = MakeVertex(c, origin, P[k], nrm, u, v);
            }
        for (int e = 0; e < Perimeter.Length; e++)
        {
            int k = Perimeter[e], i = k % T.NX, j = k / T.NX;
            int ii = i == 0 ? 1 : (i == T.NX - 1 ? T.NX - 2 : i), jj = j == 0 ? 1 : (j == T.NY - 1 ? T.NY - 2 : j);
            var outward = P[k] - P[jj * T.NX + ii];
            outward = outward.LengthSquared() > 1e-8f ? Vector3.Normalize(outward) : normals[k];
            verts[2 * n + 2 * e] = MakeVertex(c, origin, P[k] + normals[k] * T.Thickness, outward, 0, 0);
            verts[2 * n + 2 * e + 1] = MakeVertex(c, origin, P[k], outward, 0, 0);
        }
    }

    V MakeVertex(Controller c, Vector2 origin, Vector3 p, Vector3 nrm, float u, float v)
    {
        var s = (c.Project(p) - origin) * Scale;
        if (nrm.Z < 0) nrm = -nrm;
        // Flat rug = full brightness; slopes facing the light brighten and the far side darkens,
        // strongly enough that lumps read through the busy print.
        float shade = Math.Clamp(1 + 2.4f * (Vector3.Dot(nrm, L) - L.Z), 0.35f, 1.5f);
        return new V { X = s.X, Y = s.Y, Z = p.Z, U = u, Vv = v, Shade = shade };
    }

    void DrawShadow(Canvas cv, Controller c, Vector2 origin)
    {
        Array.Clear(cv.Sh);
        var P = c.Rug.P;
        float hs = Scale * 0.5f;
        Vector3 S(int k)
        {
            float z = MathF.Max(P[k].Z, 0);
            var f = (new Vector2(P[k].X, P[k].Y) + ShadowDir * (z + 2.5f) - origin) * hs;
            float t = Math.Clamp(z / 400f, 0, 1);
            return new Vector3(f, 0.42f * (1 - 0.6f * t * t * (3 - 2 * t)));
        }
        var shadowVerts = new Vector3[Cloth.N];
        for (int k = 0; k < Cloth.N; k++) shadowVerts[k] = S(k);
        ForBands(cv.SH, (y0, y1) =>
        {
            for (int t = 0; t < topTris; t++)
                ShadowTri(cv, shadowVerts[indices[3 * t]], shadowVerts[indices[3 * t + 1]], shadowVerts[indices[3 * t + 2]], y0, y1);
        });

        int radius = Math.Max(2, (int)MathF.Round(3 * Scale));
        for (int pass = 0; pass < 2; pass++) { BoxBlur(cv.Sh, cv.ShTmp, cv.SW, cv.SH, radius, true); BoxBlur(cv.ShTmp, cv.Sh, cv.SW, cv.SH, radius, false); }

        Parallel.For(0, cv.H, y =>
        {
            int sy = (y >> 1) * cv.SW, row = y * cv.W;
            for (int x = 0; x < cv.W; x++)
            {
                float sa = cv.Sh[sy + (x >> 1)];
                if (sa < 0.004f) continue;
                uint d = cv.Px[row + x];
                float k = 1 - sa;
                float a = ((d >> 24) & 255) * k + sa * 255;
                cv.Px[row + x] = ((uint)a << 24) | ((uint)(((d >> 16) & 255) * k) << 16) | ((uint)(((d >> 8) & 255) * k) << 8) | (uint)((d & 255) * k);
            }
        });
    }

    static void ShadowTri(Canvas cv, Vector3 a, Vector3 b, Vector3 c, int y0, int y1)
    {
        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 1e-6f) return;
        float inv = 1 / area;
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        int maxX = Math.Min(cv.SW - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int minY = Math.Max(y0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        int maxY = Math.Min(y1 - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) * inv;
                float w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) * inv;
                float w2 = 1 - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                float v = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                int i = y * cv.SW + x;
                if (v > cv.Sh[i]) cv.Sh[i] = v;
            }
        }
    }

    static void BoxBlur(float[] src, float[] dst, int w, int h, int r, bool horizontal)
    {
        float inv = 1f / (2 * r + 1);
        if (horizontal)
            for (int y = 0; y < h; y++)
            {
                int row = y * w; float sum = 0;
                for (int x = -r; x <= r; x++) sum += src[row + Math.Clamp(x, 0, w - 1)];
                for (int x = 0; x < w; x++)
                {
                    dst[row + x] = sum * inv;
                    sum += src[row + Math.Min(x + r + 1, w - 1)] - src[row + Math.Max(x - r, 0)];
                }
            }
        else
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int y = -r; y <= r; y++) sum += src[Math.Clamp(y, 0, h - 1) * w + x];
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = sum * inv;
                    sum += src[Math.Min(y + r + 1, h - 1) * w + x] - src[Math.Max(y - r, 0) * w + x];
                }
            }
    }

    void Raster(Canvas cv, in V a, in V b, in V c, int surf, int y0, int y1)
    {
        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 1e-6f) return;
        float inv = 1 / area;
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        int maxX = Math.Min(cv.W - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int minY = Math.Max(y0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        int maxY = Math.Min(y1 - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (minX > maxX || minY > maxY) return;
        var tex = surf == 0 ? top : back;
        float edgeR = ((EdgeColor >> 16) & 255) / 255f, edgeG = ((EdgeColor >> 8) & 255) / 255f, edgeB = (EdgeColor & 255) / 255f;

        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            int row = y * cv.W;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) * inv;
                float w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) * inv;
                float w2 = 1 - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                int i = row + x;
                if (z <= cv.Z[i]) continue;
                float shade = w0 * a.Shade + w1 * b.Shade + w2 * c.Shade;

                float ta, tr, tg, tb;
                if (surf == 2) { ta = 1; tr = edgeR; tg = edgeG; tb = edgeB; }
                else
                {
                    float u = w0 * a.U + w1 * b.U + w2 * c.U, v = w0 * a.Vv + w1 * b.Vv + w2 * c.Vv;
                    Sample(tex, u, v, out ta, out tr, out tg, out tb);
                    if (ta < 0.004f) continue;                       // outside a rounded corner
                }
                tr = MathF.Min(tr * shade, ta); tg = MathF.Min(tg * shade, ta); tb = MathF.Min(tb * shade, ta);
                if (ta >= 0.996f)
                {
                    cv.Px[i] = Pack(1, tr, tg, tb);
                    cv.Z[i] = z;
                }
                else
                {
                    uint d = cv.Px[i];
                    float k = 1 - ta;
                    cv.Px[i] = Pack(ta + ((d >> 24) & 255) / 255f * k, tr + ((d >> 16) & 255) / 255f * k,
                                     tg + ((d >> 8) & 255) / 255f * k, tb + (d & 255) / 255f * k);
                }
            }
        }
    }

    /// Bilinear sample of a premultiplied texture.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Sample(Image32 t, float u, float v, out float a, out float r, out float g, out float b)
    {
        float fx = Math.Clamp(u * t.W - 0.5f, 0, t.W - 1.001f), fy = Math.Clamp(v * t.H - 0.5f, 0, t.H - 1.001f);
        int x0 = (int)fx, y0 = (int)fy;
        float ax = fx - x0, ay = fy - y0;
        int i = y0 * t.W + x0;
        uint c00 = t.Px[i], c10 = t.Px[i + 1], c01 = t.Px[i + t.W], c11 = t.Px[i + t.W + 1];
        float w00 = (1 - ax) * (1 - ay), w10 = ax * (1 - ay), w01 = (1 - ax) * ay, w11 = ax * ay;
        const float k = 1f / 255;
        a = (((c00 >> 24) & 255) * w00 + ((c10 >> 24) & 255) * w10 + ((c01 >> 24) & 255) * w01 + ((c11 >> 24) & 255) * w11) * k;
        r = (((c00 >> 16) & 255) * w00 + ((c10 >> 16) & 255) * w10 + ((c01 >> 16) & 255) * w01 + ((c11 >> 16) & 255) * w11) * k;
        g = (((c00 >> 8) & 255) * w00 + ((c10 >> 8) & 255) * w10 + ((c01 >> 8) & 255) * w01 + ((c11 >> 8) & 255) * w11) * k;
        b = ((c00 & 255) * w00 + (c10 & 255) * w10 + (c01 & 255) * w01 + (c11 & 255) * w11) * k;
    }

    static void Blit(Canvas cv, Image32 img, int ox, int oy)
    {
        for (int y = 0; y < img.H; y++)
        {
            int cy = oy + y;
            if (cy < 0 || cy >= cv.H) continue;
            for (int x = 0; x < img.W; x++)
            {
                int cx = ox + x;
                if (cx < 0 || cx >= cv.W) continue;
                uint s = img.Px[y * img.W + x];
                uint sa = s >> 24;
                if (sa == 0) continue;
                int i = cy * cv.W + cx;
                if (sa == 255) { cv.Px[i] = s; continue; }
                uint d = cv.Px[i], k = 255 - sa;
                uint a = sa + ((d >> 24) & 255) * k / 255;
                uint r = ((s >> 16) & 255) + ((d >> 16) & 255) * k / 255;
                uint g = ((s >> 8) & 255) + ((d >> 8) & 255) * k / 255;
                uint b = (s & 255) + (d & 255) * k / 255;
                cv.Px[i] = (Math.Min(a, 255u) << 24) | (Math.Min(r, 255u) << 16) | (Math.Min(g, 255u) << 8) | Math.Min(b, 255u);
            }
        }
    }

    /// The area (DIPs) the scene covers: the rug and its shadow, plus any icons lying on the desk.
    public static RectangleF Bounds(Controller c)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = 0;
        foreach (var s in c.Proj) { minX = MathF.Min(minX, s.X); minY = MathF.Min(minY, s.Y); maxX = MathF.Max(maxX, s.X); maxY = MathF.Max(maxY, s.Y); }
        foreach (var p in c.Rug.P) maxZ = MathF.Max(maxZ, p.Z);
        float m = 24 + T.Thickness + maxZ * 0.6f;           // shadow offset and blur
        var r = RectangleF.FromLTRB(minX - m, minY - m, maxX + m, maxY + m);
        foreach (var it in c.Items)
            if (!it.Hidden) r = RectangleF.Union(r, new RectangleF(it.Pos.X - 50, it.Pos.Y - 34, 100, 100));
        return r;
    }
}
