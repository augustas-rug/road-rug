import Cocoa
import Metal
import MetalKit
import QuartzCore
import simd

struct Vertex {
    var pos: SIMD4<Float>
    var nrm: SIMD4<Float>
    var uv: SIMD4<Float>        // u, v, surface (0 printed top, 1 felt backing, 2 bound edge), unused
}

struct Uniforms {
    var viewSize: F2
    var persp: Float
    var time: Float
    var shadowDir: F2
    var pad: F2
}

struct IconUniforms {
    var center: F2
    var size: F2
    var alpha: Float
    var pad0: Float = 0
    var pad1: F2 = .zero
}

/// Something swept under the rug. The file itself lives in the stash folder; this is where it sits on the desk.
final class Item {
    var url: URL
    var pos: F2
    var height: Float = 0
    var targetHeight: Float = ICON_LUMP
    var texture: MTLTexture?
    init(url: URL, pos: F2) {
        self.url = url
        self.pos = pos
    }
    var bump: SIMD4<Float> { SIMD4(pos.x, pos.y, height, LUMP_RADIUS) }   // a low, soft lump about the size of the icon
}

struct SavedItem: Codable { var name: String; var x: Float; var y: Float }
struct SavedState: Codable { var particles: [[Float]]; var items: [SavedItem]? }

enum GrabMode { case none, lift, slide }

enum Store {
    static let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Road Rug", isDirectory: true)
    static let stash = support.appendingPathComponent("Under the Rug", isDirectory: true)
    static let desktop = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask)[0]
    static let state = support.appendingPathComponent("state.json")

    static func uniqueURL(in dir: URL, name: String) -> URL {
        var url = dir.appendingPathComponent(name)
        let base = (name as NSString).deletingPathExtension, ext = (name as NSString).pathExtension
        var k = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = dir.appendingPathComponent(ext.isEmpty ? "\(base) \(k)" : "\(base) \(k).\(ext)")
            k += 1
        }
        return url
    }
}


private let shaderSource = """
#include <metal_stdlib>
using namespace metal;

struct V { float4 pos; float4 nrm; float4 uv; };
struct U { float2 viewSize; float persp; float time; float2 shadowDir; float2 pad; };
struct IU { float2 center; float2 size; float alpha; float pad0; float2 pad1; };

struct RO { float4 position [[position]]; float3 nrm; float2 uv; float surface; };
struct SO { float4 position [[position]]; float a; };
struct IO { float4 position [[position]]; float2 uv; float alpha; };

static float2 project(float3 p, constant U& u) {
    float2 c = u.viewSize * 0.5;
    return c + (p.xy - c) * (1.0 + p.z * u.persp);
}
static float4 toClip(float2 s, float depth, constant U& u) {
    return float4(s / u.viewSize * 2.0 - 1.0, depth, 1.0);
}
static float hash21(float2 p) {
    p = fract(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

vertex RO rugVert(uint vid [[vertex_id]], device const V* v [[buffer(0)]], constant U& u [[buffer(1)]]) {
    V x = v[vid];
    RO o;
    o.position = toClip(project(x.pos.xyz, u), clamp(0.5 - x.pos.z / 4000.0, 0.0, 1.0), u);
    o.nrm = x.nrm.xyz;
    o.uv = x.uv.xy;
    o.surface = x.uv.z;
    return o;
}

fragment float4 rugFrag(RO in [[stage_in]], texture2d<float> tex [[texture(0)]], sampler smp [[sampler(0)]]) {
    float3 n = normalize(in.nrm);
    if (n.z < 0.0) n = -n;
    float3 L = normalize(float3(-0.55, 0.65, 0.75));
    float flat = dot(float3(0, 0, 1), L);
    float diff = max(dot(n, L), 0.0) / flat;                 // 1.0 when lying flat
    float2 size = float2(\(Float(NX - 1) * SPACING), \(Float(NY - 1) * SPACING));
    float2 g = in.uv * size * 1.6;                             // ~1.6 tufts per point
    float knot = hash21(floor(g));

    // Rounded corners: distance (in points) from the nearest edge, measured round the corner arc.
    float2 pt = in.uv * size;
    float2 d = min(pt, size - pt);
    float R = \(CORNER_RADIUS);
    float edgeDist = min(d.x, d.y);
    if (d.x < R && d.y < R) {
        float arc = length(float2(R) - d);
        if (arc > R) discard_fragment();
        edgeDist = R - arc;
    }

    float3 base;
    if (in.surface < 0.5) {                                    // printed pile
        base = tex.sample(smp, in.uv).rgb;
        float row = 0.5 + 0.5 * sin(g.y * 6.2831);
        base *= 0.95 + 0.08 * knot - 0.03 * row;
        if (edgeDist < \(BINDING_WIDTH)) {                     // stitched binding around the print
            float stitch = 0.5 + 0.5 * sin((pt.x + pt.y) * 2.4);
            base = float3(0.30, 0.31, 0.34) * (0.88 + 0.12 * stitch + 0.08 * knot);
        }
    } else if (in.surface < 1.5) {                             // non-slip felt backing
        base = float3(0.29, 0.29, 0.31) * (0.92 + 0.12 * knot);
    } else {                                                   // bound edge
        base = float3(0.30, 0.31, 0.34) * (0.9 + 0.15 * knot);
    }
    float sheen = pow(1.0 - n.z, 3.0) * 0.08;
    // Flat rug = full brightness; slopes facing the light brighten and the far side darkens, strongly
    // enough that lumps read through the busy print.
    float shade = clamp(1.0 + 2.4 * (dot(n, L) - flat), 0.35, 1.5);
    float3 col = base * shade + sheen;
    return float4(col, 1.0);
}

vertex SO shadowVert(uint vid [[vertex_id]], uint iid [[instance_id]],
                     device const V* v [[buffer(0)]], constant U& u [[buffer(1)]]) {
    V x = v[vid];
    float z = max(x.pos.z, 0.0);
    float ang = float(iid) * 2.39996;
    float r = (1.2 + z * 0.16) * sqrt((float(iid) + 0.5) / 8.0);
    float2 s = x.pos.xy + u.shadowDir * (z + 2.5) + float2(cos(ang), sin(ang)) * r;
    SO o;
    o.position = toClip(s, 0.9, u);
    o.a = 0.07 * (1.0 - smoothstep(0.0, 400.0, z) * 0.6);
    return o;
}

fragment float4 shadowFrag(SO in [[stage_in]]) {
    return float4(0.0, 0.0, 0.0, in.a);
}

vertex IO iconVert(uint vid [[vertex_id]], constant IU& iu [[buffer(0)]], constant U& u [[buffer(1)]]) {
    const float2 corners[6] = { float2(0,0), float2(1,0), float2(0,1), float2(1,0), float2(1,1), float2(0,1) };
    float2 c = corners[vid];
    IO o;
    o.position = toClip(iu.center + (c - 0.5) * iu.size, 0.95, u);
    o.uv = float2(c.x, 1.0 - c.y);
    o.alpha = iu.alpha;
    return o;
}

fragment float4 iconFrag(IO in [[stage_in]], texture2d<float> tex [[texture(0)]], sampler smp [[sampler(0)]]) {
    return tex.sample(smp, in.uv) * in.alpha;
}
"""

final class RugView: MTKView {
    let rug: Cloth
    var items: [Item] = []

    private let queue: MTLCommandQueue
    private var rugPipe: MTLRenderPipelineState!
    private var shadowPipe: MTLRenderPipelineState!
    private var iconPipe: MTLRenderPipelineState!
    private var depthOn: MTLDepthStencilState!
    private var depthOff: MTLDepthStencilState!
    private var sampler: MTLSamplerState!
    private var rugTexture: MTLTexture?
    private var indexBuffer: MTLBuffer!
    private var indexCount = 0
    private var topIndexCount = 0                  // just the printed surface (used for the shadow)
    private var vertices: [Vertex]
    private var vertexBuffers: [MTLBuffer] = []    // reused round-robin instead of allocating every frame
    private var frameIndex = 0
    private let inFlight = DispatchSemaphore(value: 3)

    /// Border particles in order around the rug (for the bound edge walls).
    private static let perimeter: [Int] = {
        var r: [Int] = []
        for i in 0..<NX { r.append(i) }
        for j in 1..<NY { r.append(j * NX + NX - 1) }
        for i in stride(from: NX - 2, through: 0, by: -1) { r.append((NY - 1) * NX + i) }
        for j in stride(from: NY - 2, through: 1, by: -1) { r.append(j * NX) }
        return r
    }()
    private var proj: [F2]
    private var bbox = SIMD4<Float>(0, 0, 0, 0)

    private var link: CADisplayLink?
    private var lastTime: CFTimeInterval = 0
    private var awake = true
    var isAwake: Bool { awake }
    private var quiet: Float = 0
    private var cursorOver = false

    private var grab: GrabMode = .none
    private var grabbed: [(Int, F2)] = []
    private var grabZ: Float = 0
    private var slideK = 0
    private var acc: Float = 0
    private var idleTime: Float = 0      // seconds since the rug was last handled
    private var grabOffset = F2.zero     // from the cursor to that vertex, so nothing jumps on pick-up
    private var hand = F2.zero
    var drawsItemIcons = false           // Finder draws the real icons; only the snapshot test draws its own
    private var iconPoll: Timer?
    private var lastMouse = F2(-1, -1)
    private var lastGrab: GrabMode = .none
    private var windowsAbove: [CGRect] = []
    private var windowsAboveTime: CFTimeInterval = 0
    private var lastIconCount = -1

    init(frame: NSRect, device: MTLDevice) {
        queue = device.makeCommandQueue()!
        rug = Cloth(center: F2(Float(frame.midX), Float(frame.midY)))
        vertices = Array(repeating: Vertex(pos: .zero, nrm: .zero, uv: .zero), count: 2 * NX * NY + 2 * RugView.perimeter.count)
        proj = Array(repeating: .zero, count: NX * NY)
        super.init(frame: frame, device: device)

        colorPixelFormat = .bgra8Unorm
        depthStencilPixelFormat = .depth32Float
        clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 0)
        layer?.isOpaque = false
        isPaused = true
        enableSetNeedsDisplay = false
        registerForDraggedTypes([.fileURL])

        buildPipelines(device)
        rugTexture = makeTexture(device: device, from: paintRug(), mipmapped: true, queue: queue)

        // Printed top, felt backing (same grid, offset by N), then the edge walls joining them.
        let n = UInt32(NX * NY)
        var idx: [UInt32] = []
        for j in 0..<(NY - 1) {
            for i in 0..<(NX - 1) {
                let a = UInt32(j * NX + i), b = a + 1, c = a + UInt32(NX) + 1, d = a + UInt32(NX)
                idx += [a, b, c, a, c, d]
            }
        }
        topIndexCount = idx.count
        idx += idx.map { $0 + n }
        let ring = RugView.perimeter.count
        func inCorner(_ k: Int) -> Bool {   // inside a rounded-off corner (no edge wall there)
            let i = k % NX, j = k / NX
            return Float(min(i, NX - 1 - i)) * SPACING < CORNER_RADIUS && Float(min(j, NY - 1 - j)) * SPACING < CORNER_RADIUS
        }
        for e in 0..<ring {
            // Leave the edge wall out at the four corners, which the shader rounds off.
            if inCorner(RugView.perimeter[e]) || inCorner(RugView.perimeter[(e + 1) % ring]) { continue }
            let ta = 2 * n + UInt32(2 * e), tb = 2 * n + UInt32(2 * ((e + 1) % ring))
            idx += [ta, tb, tb + 1, ta, tb + 1, ta + 1]
        }
        indexCount = idx.count
        indexBuffer = device.makeBuffer(bytes: idx, length: idx.count * 4)
        for _ in 0..<3 {
            vertexBuffers.append(device.makeBuffer(length: vertices.count * MemoryLayout<Vertex>.stride, options: .storageModeShared)!)
        }

        load()
    }

    required init(coder: NSCoder) { fatalError() }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard window != nil, link == nil else { return }
        let l = displayLink(target: self, selector: #selector(tick(_:)))
        l.add(to: .main, forMode: .common)
        link = l
        updateWalls()
        // Keep up with the desktop icons (cheap: one Finder query every couple of seconds, off the main thread).
        refreshDesktopIcons()
        iconPoll = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in self?.refreshDesktopIcons() }
    }

    func updateWalls() {
        guard let screen = window?.screen ?? NSScreen.main else { return }
        let top = Float(screen.visibleFrame.maxY - screen.frame.minY)
        rug.walls = SIMD4(0, 0, Float(bounds.width), top)
        wake()
    }

    private func buildPipelines(_ device: MTLDevice) {
        let lib = try! device.makeLibrary(source: shaderSource, options: nil)
        func pipe(_ v: String, _ f: String) -> MTLRenderPipelineState {
            let d = MTLRenderPipelineDescriptor()
            d.vertexFunction = lib.makeFunction(name: v)
            d.fragmentFunction = lib.makeFunction(name: f)
            let ca = d.colorAttachments[0]!
            ca.pixelFormat = .bgra8Unorm
            ca.isBlendingEnabled = true
            ca.sourceRGBBlendFactor = .one
            ca.sourceAlphaBlendFactor = .one
            ca.destinationRGBBlendFactor = .oneMinusSourceAlpha
            ca.destinationAlphaBlendFactor = .oneMinusSourceAlpha
            d.depthAttachmentPixelFormat = .depth32Float
            return try! device.makeRenderPipelineState(descriptor: d)
        }
        rugPipe = pipe("rugVert", "rugFrag")
        shadowPipe = pipe("shadowVert", "shadowFrag")
        iconPipe = pipe("iconVert", "iconFrag")

        let on = MTLDepthStencilDescriptor()
        on.depthCompareFunction = .less
        on.isDepthWriteEnabled = true
        depthOn = device.makeDepthStencilState(descriptor: on)
        let off = MTLDepthStencilDescriptor()
        off.depthCompareFunction = .always
        off.isDepthWriteEnabled = false
        depthOff = device.makeDepthStencilState(descriptor: off)

        let s = MTLSamplerDescriptor()
        s.minFilter = .linear
        s.magFilter = .linear
        s.mipFilter = .linear
        s.maxAnisotropy = 8
        s.sAddressMode = .clampToEdge
        s.tAddressMode = .clampToEdge
        sampler = device.makeSamplerState(descriptor: s)
    }

    // MARK: - Geometry helpers

    private var center: F2 { F2(Float(bounds.width) / 2, Float(bounds.height) / 2) }

    private func project(_ p: F3) -> F2 { center + (F2(p.x, p.y) - center) * (1 + p.z * PERSPECTIVE) }
    private func unproject(_ s: F2, z: Float) -> F2 { center + (s - center) / (1 + z * PERSPECTIVE) }

    private func updateProjection() {
        var mn = F2(repeating: .greatestFiniteMagnitude), mx = F2(repeating: -.greatestFiniteMagnitude)
        for i in 0..<rug.n {
            let s = project(rug.p[i])
            proj[i] = s
            mn = simd_min(mn, s)
            mx = simd_max(mx, s)
        }
        bbox = SIMD4(mn.x, mn.y, mx.x, mx.y)
    }

    /// Topmost rug particle under a screen point, with its height.
    private func hitRug(_ s: F2) -> (k: Int, z: Float)? {
        guard s.x >= bbox.x, s.y >= bbox.y, s.x <= bbox.z, s.y <= bbox.w else { return nil }
        var best: (Int, Float)?
        func tri(_ a: Int, _ b: Int, _ c: Int) {
            let pa = proj[a], v0 = proj[b] - pa, v1 = proj[c] - pa, v2 = s - pa
            let d = v0.x * v1.y - v1.x * v0.y
            if abs(d) < 1e-6 { return }
            let u = (v2.x * v1.y - v1.x * v2.y) / d, v = (v0.x * v2.y - v2.x * v0.y) / d
            guard u >= 0, v >= 0, u + v <= 1 else { return }
            let w = SIMD3<Float>(1 - u - v, u, v)
            let z = w.x * rug.p[a].z + w.y * rug.p[b].z + w.z * rug.p[c].z
            if best == nil || z > best!.1 {
                let k = w.x >= w.y && w.x >= w.z ? a : (w.y >= w.z ? b : c)
                best = (k, z)
            }
        }
        for j in 0..<(NY - 1) {
            for i in 0..<(NX - 1) {
                let a = j * NX + i, b = a + 1, c = a + NX + 1, d = a + NX
                let lo = simd_min(simd_min(proj[a], proj[b]), simd_min(proj[c], proj[d]))
                let hi = simd_max(simd_max(proj[a], proj[b]), simd_max(proj[c], proj[d]))
                if s.x < lo.x || s.y < lo.y || s.x > hi.x || s.y > hi.y { continue }
                tri(a, b, c)
                tri(a, c, d)
            }
        }
        return best.map { (k: $0.0, z: $0.1) }
    }

    private func point(_ p: NSPoint) -> F2 {
        let v = convert(p, from: nil)
        return F2(Float(v.x), Float(v.y))
    }

    // MARK: - Frame loop

    func wake() {
        awake = true
        quiet = 0
        idleTime = 0
    }

    @objc private func tick(_ link: CADisplayLink) {
        guard let window else { return }
        let now = CACurrentMediaTime()
        let dt = lastTime == 0 ? 1.0 / 60.0 : min(Float(now - lastTime), 1.0 / 20.0)
        lastTime = now

        let mouse = point(window.mouseLocationOutsideOfEventStream)
        // Only re-test what's under the cursor when it moved or the rug did; an idle desktop costs nothing.
        if grab != .none || awake || mouse != lastMouse {
            lastMouse = mouse
            let over = grab != .none || (hitRug(mouse) != nil && !coveredByWindow(at: NSEvent.mouseLocation))
            if window.ignoresMouseEvents == over { window.ignoresMouseEvents = !over }
            if over != cursorOver || grab != lastGrab {
                if over { (grab == .none ? NSCursor.openHand : NSCursor.closedHand).set() } else { NSCursor.arrow.set() }
            }
            cursorOver = over
            lastGrab = grab
        }

        if advance(dt: dt, mouse: mouse) { draw() }
    }

    /// True when another app's window sits on top of the rug at this screen point, so the rug must not react.
    /// Asks the window server for the visible windows stacked above ours (cached briefly; it's cheap either way).
    private func coveredByWindow(at p: NSPoint) -> Bool {
        guard let window, let screen = window.screen ?? NSScreen.screens.first else { return false }
        let now = CACurrentMediaTime()
        if now - windowsAboveTime > 0.25 {
            windowsAboveTime = now
            let mainH = NSScreen.screens.first?.frame.height ?? screen.frame.height
            let list = CGWindowListCopyWindowInfo([.optionOnScreenAboveWindow, .excludeDesktopElements],
                                                  CGWindowID(window.windowNumber)) as? [[String: Any]] ?? []
            windowsAbove = list.compactMap { w in
                // Ignore invisible windows and the very-high overlay layers some utilities draw everywhere;
                // normal windows, the Dock (20) and the menu bar (24) all count.
                guard (w[kCGWindowAlpha as String] as? Double ?? 1) > 0.01, (w[kCGWindowLayer as String] as? Int ?? 0) < 25,
                      let b = w[kCGWindowBounds as String] as? [String: CGFloat],
                      let x = b["X"], let y = b["Y"], let width = b["Width"], let height = b["Height"] else { return nil }
                // Window-server bounds are top-left based; flip into Cocoa screen coordinates.
                return CGRect(x: x, y: mainH - y - height, width: width, height: height)
            }
        }
        return windowsAbove.contains { $0.contains(p) }
    }

    /// Runs physics for one display frame. Returns false when the rug is asleep and nothing changed.
    @discardableResult
    func advance(dt: Float, mouse: F2) -> Bool {
        var itemsMoving = false
        for it in items where it.height != it.targetHeight {
            let d = it.targetHeight - it.height
            it.height = abs(d) < 0.05 ? it.targetHeight : it.height + d * min(1, dt * 5)
            itemsMoving = true
        }
        if itemsMoving || grab != .none { wake() }
        guard awake else { return false }

        // The hand drags a heavy rug: the grip trails the cursor (smoothly) instead of snapping to it.
        if grab != .none {
            hand += (mouse - hand) * min(1, dt * HAND_FOLLOW)
        }
        if grab == .lift {
            grabZ += (LIFT_HEIGHT - grabZ) * min(1, dt * 6)
            let target = unproject(hand, z: grabZ) + grabOffset
            for (k, off) in grabbed {
                let droop = simd_length(off) * 0.35
                rug.pinTo[k] = F3(target.x + off.x, target.y + off.y, max(grabZ - droop, 0))
            }
        } else if grab == .slide {
            // Towed like a real rug: the grip point follows the hand and the body swings round to trail behind it.
            let g = F2(rug.p[slideK].x, rug.p[slideK].y)
            let d = unproject(hand, z: rug.p[slideK].z) + grabOffset - g
            let len = simd_length(d)
            if len > 0.01 {
                let r = rug.centroid - g
                let lr = simd_length(r)
                if lr > 8 {
                    let back = -d / len
                    let sinT = (r.x * back.y - r.y * back.x) / lr
                    rug.rotate(by: sinT * min(len / lr, 0.2) * 0.35, around: g)   // heavy: swings round slowly
                }
                rug.translate(by: d)
            }
        }

        let bumps = items.map(\.bump)
        acc += dt
        var steps = 0
        while acc >= FIXED_DT && steps < 4 {
            for (k, _) in grabbed { rug.pinFrom[k] = rug.p[k] }
            rug.step(dt: FIXED_DT, bumps: bumps)
            acc -= FIXED_DT
            steps += 1
        }
        if steps == 4 { acc = 0 }
        updateProjection()

        idleTime = grab == .none && !itemsMoving ? idleTime + dt : 0
        // Sleep once still; after SETTLE_TIME untouched, also accept a tiny residual creep (never mid-fall).
        let still = rug.motion < 0.03 || (idleTime > SETTLE_TIME && rug.motion < 0.3)
        if still && grab == .none && !itemsMoving {
            quiet += dt
            if quiet > 0.6 {
                awake = false
                save()
            }
        } else {
            quiet = 0
        }
        return true
    }

    // MARK: - Rendering

    /// Particles sit on the backing; the printed pile is RUG_THICKNESS above it along the surface normal,
    /// so folds show a rounded thick edge instead of a paper-thin crease.
    private func buildVertices() {
        let P = rug.p
        let n = NX * NY
        var normals = [F3](repeating: .zero, count: n)
        for j in 0..<NY {
            for i in 0..<NX {
                let k = j * NX + i
                let dx = P[j * NX + min(i + 1, NX - 1)] - P[j * NX + max(i - 1, 0)]
                let dy = P[min(j + 1, NY - 1) * NX + i] - P[max(j - 1, 0) * NX + i]
                let nrm = simd_normalize(simd_cross(dx, dy))
                normals[k] = nrm
                let uv = F2(Float(i) / Float(NX - 1), 1 - Float(j) / Float(NY - 1))
                vertices[k] = Vertex(pos: SIMD4(P[k] + nrm * RUG_THICKNESS, 1), nrm: SIMD4(nrm, 0), uv: SIMD4(uv.x, uv.y, 0, 0))
                vertices[n + k] = Vertex(pos: SIMD4(P[k], 1), nrm: SIMD4(-nrm, 0), uv: SIMD4(uv.x, uv.y, 1, 0))
            }
        }
        for (e, k) in RugView.perimeter.enumerated() {
            let i = k % NX, j = k / NX
            let ii = i == 0 ? 1 : (i == NX - 1 ? NX - 2 : i), jj = j == 0 ? 1 : (j == NY - 1 ? NY - 2 : j)
            var out = P[k] - P[jj * NX + ii]
            out = simd_length(out) > 1e-4 ? simd_normalize(out) : normals[k]
            let uv = SIMD4<Float>(vertices[k].uv.x, vertices[k].uv.y, 2, 0)
            vertices[2 * n + 2 * e] = Vertex(pos: SIMD4(P[k] + normals[k] * RUG_THICKNESS, 1), nrm: SIMD4(out, 0), uv: uv)
            vertices[2 * n + 2 * e + 1] = Vertex(pos: SIMD4(P[k], 1), nrm: SIMD4(out, 0), uv: uv)
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let rpd = currentRenderPassDescriptor, let drawable = currentDrawable,
              let cb = queue.makeCommandBuffer() else { return }
        encodeScene(cb, rpd)
        cb.present(drawable)
        cb.commit()
    }

    private func encodeScene(_ cb: MTLCommandBuffer, _ rpd: MTLRenderPassDescriptor) {
        inFlight.wait()
        cb.addCompletedHandler { [inFlight] _ in inFlight.signal() }
        buildVertices()
        let vb = vertexBuffers[frameIndex]
        frameIndex = (frameIndex + 1) % vertexBuffers.count
        vertices.withUnsafeBytes { vb.contents().copyMemory(from: $0.baseAddress!, byteCount: $0.count) }
        var u = Uniforms(viewSize: F2(Float(bounds.width), Float(bounds.height)), persp: PERSPECTIVE,
                         time: 0, shadowDir: F2(0.32, -0.48), pad: .zero)
        guard let enc = cb.makeRenderCommandEncoder(descriptor: rpd) else { return }

        // Things on the desk (visible wherever the rug isn't covering them).
        enc.setRenderPipelineState(iconPipe)
        enc.setDepthStencilState(depthOff)
        enc.setFragmentSamplerState(sampler, index: 0)
        for it in items where drawsItemIcons {
            guard let tex = it.texture else { continue }
            var iu = IconUniforms(center: it.pos - F2(0, 16), size: F2(100, 100), alpha: 1)
            enc.setVertexBytes(&iu, length: MemoryLayout<IconUniforms>.stride, index: 0)
            enc.setVertexBytes(&u, length: MemoryLayout<Uniforms>.stride, index: 1)
            enc.setFragmentTexture(tex, index: 0)
            enc.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 6)
        }

        // Soft shadow on the desk.
        enc.setRenderPipelineState(shadowPipe)
        enc.setVertexBuffer(vb, offset: 0, index: 0)
        enc.setVertexBytes(&u, length: MemoryLayout<Uniforms>.stride, index: 1)
        enc.drawIndexedPrimitives(type: .triangle, indexCount: topIndexCount, indexType: .uint32,
                                  indexBuffer: indexBuffer, indexBufferOffset: 0, instanceCount: 8)

        // The rug itself.
        enc.setRenderPipelineState(rugPipe)
        enc.setDepthStencilState(depthOn)
        enc.setCullMode(.none)
        enc.setFragmentTexture(rugTexture, index: 0)
        enc.drawIndexedPrimitives(type: .triangle, indexCount: indexCount, indexType: .uint32,
                                  indexBuffer: indexBuffer, indexBufferOffset: 0)
        enc.endEncoding()
    }

    // MARK: - Mouse

    override func mouseDown(with e: NSEvent) {
        let s = point(e.locationInWindow)
        if e.clickCount == 2, hitRug(s) != nil {
            // Double-click: lay the rug back down flat where it is.
            release()
            straighten()
            return
        }
        if beginGrab(at: s, forceLift: e.modifierFlags.contains(.option)) {
            NSCursor.closedHand.set()
        }
    }

    /// Grabs the rug under `s`: edges and corners lift, the middle slides.
    func beginGrab(at s: F2, forceLift: Bool = false) -> Bool {
        updateProjection()
        guard let hit = hitRug(s) else { return false }
        let i = hit.k % NX, j = hit.k / NX
        let edge = min(i, NX - 1 - i, j, NY - 1 - j)
        grab = (edge <= 3 || forceLift) ? .lift : .slide
        let pk = rug.p[hit.k]
        slideK = hit.k
        grabbed = []
        for jj in max(0, j - 2)...min(NY - 1, j + 2) where grab == .lift {
            for ii in max(0, i - 2)...min(NX - 1, i + 2) {
                let dg = F2(Float(ii - i), Float(jj - j))
                guard simd_length(dg) <= 1.6 else { continue }
                let k = jj * NX + ii
                grabbed.append((k, F2(rug.p[k].x - pk.x, rug.p[k].y - pk.y)))
                rug.pinned[k] = true
                rug.pinTo[k] = rug.p[k]
            }
        }
        grabZ = pk.z
        grabOffset = F2(pk.x, pk.y) - unproject(s, z: pk.z)
        hand = s
        wake()
        return true
    }

    override func mouseUp(with e: NSEvent) {
        release()
    }

    func release() {
        guard grab != .none else { return }
        for (k, _) in grabbed {
            rug.pinned[k] = false
            rug.q[k] = rug.p[k]   // drops where you let go; no throw, so a flap never keeps rolling over
        }
        grabbed = []
        grab = .none
        wake()
        refreshDesktopIcons()
    }

    override func rotate(with e: NSEvent) {
        rug.rotate(by: e.rotation * .pi / 180, around: rug.centroid)
        wake()
    }

    override func rightMouseDown(with e: NSEvent) {
        NSMenu.popUpContextMenu(makeMenu(), with: e, for: self)
    }

    func makeMenu() -> NSMenu {
        let m = NSMenu()
        m.addItem(withTitle: "Straighten Rug", action: #selector(straighten), keyEquivalent: "").target = self
        m.addItem(withTitle: "Move Rug to Center", action: #selector(recenter), keyEquivalent: "").target = self
        m.addItem(.separator())
        m.addItem(withTitle: "Quit Road Rug", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        return m
    }

    @objc func straighten() {
        rug.layout(center: rug.centroid, angle: rug.angle, drop: SAFE_DROP)
        wake()
    }

    @objc func recenter() {
        rug.layout(center: center, angle: 0, drop: SAFE_DROP)
        wake()
    }

    // MARK: - Desktop icons under the rug

    /// Finder is the source of truth: every file stays a real desktop icon, and the rug simply lies on top.
    /// Whatever the rug covers makes a lump. This keeps `items` in step with Finder's icon positions.
    func refreshDesktopIcons() {
        guard persistenceEnabled else { return }
        let screenH = Float(bounds.height)
        Finder.desktopIcons { [weak self] icons in
            guard let self else { return }
            var byPath = Dictionary(uniqueKeysWithValues: self.items.map { ($0.url.standardizedFileURL.path, $0) })
            var next: [Item] = []
            var changed = false
            for icon in icons {
                let pos = F2(Float(icon.position.x), screenH - Float(icon.position.y))
                let key = icon.url.standardizedFileURL.path
                if let it = byPath.removeValue(forKey: key) {
                    if simd_length(it.pos - pos) > 0.5 { it.pos = pos; changed = true }
                    next.append(it)
                } else {
                    next.append(self.makeItem(url: icon.url, pos: pos))
                    changed = true
                }
            }
            if !byPath.isEmpty { changed = true }
            self.items = next
            if changed {
                if next.count != self.lastIconCount { NSLog("Road Rug: tracking \(next.count) desktop icons") }
                self.lastIconCount = next.count
                self.wake()
            }
        }
    }

    private func makeItem(url: URL, pos: F2) -> Item {
        let it = Item(url: url, pos: pos)
        if drawsItemIcons, let device { it.texture = makeIconTexture(device: device, url: url) }
        return it
    }

    // MARK: - Sweeping files under the rug (drop target)

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { dropOperation(sender) }
    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation { dropOperation(sender) }

    private func dropOperation(_ sender: NSDraggingInfo) -> NSDragOperation {
        hitRug(point(sender.draggingLocation)) != nil ? .move : []
    }

    /// Dropping files onto the rug tucks them under it: they stay ordinary desktop icons (anything from
    /// another folder is moved onto the Desktop first), and Finder places them where they were dropped.
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let s = point(sender.draggingLocation)
        guard hitRug(s) != nil,
              let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self],
                                                               options: [.urlReadingFileURLsOnly: true]) as? [URL]
        else { return false }
        let base = unproject(s, z: 0)
        var placed: [(URL, CGPoint)] = []
        for (n, url) in urls.enumerated() {
            let a = Float(n) * 2.39996, r = 30 * Float(n).squareRoot()
            let pos = base + F2(cos(a), sin(a)) * r
            var onDesk = url
            if url.deletingLastPathComponent().standardizedFileURL != Store.desktop.standardizedFileURL {
                onDesk = Store.uniqueURL(in: Store.desktop, name: url.lastPathComponent)
                do { try FileManager.default.moveItem(at: url, to: onDesk) } catch {
                    NSLog("Road Rug: could not move \(url.path) to the Desktop: \(error)")
                    NSSound.beep()
                    continue
                }
            }
            placed.append((onDesk, CGPoint(x: CGFloat(pos.x), y: bounds.height - CGFloat(pos.y))))
            if let it = items.first(where: { $0.url.standardizedFileURL == onDesk.standardizedFileURL }) {
                it.pos = pos
            } else {
                items.append(makeItem(url: onDesk, pos: pos))   // the lump appears straight away
            }
        }
        Finder.place(placed) { [weak self] in self?.refreshDesktopIcons() }
        wake()
        return true
    }

    // MARK: - Persistence

    private func load() {
        let fm = FileManager.default
        // Early builds kept their data in ".../Application Support/Rug"; carry it over once.
        let legacy = Store.support.deletingLastPathComponent().appendingPathComponent("Rug", isDirectory: true)
        if !fm.fileExists(atPath: Store.support.path), fm.fileExists(atPath: legacy.path) {
            try? fm.moveItem(at: legacy, to: Store.support)
        }
        try? fm.createDirectory(at: Store.support, withIntermediateDirectories: true)
        var savedSpots: [String: F2] = [:]
        if let data = try? Data(contentsOf: Store.state),
           let st = try? JSONDecoder().decode(SavedState.self, from: data) {
            if st.particles.count == rug.n {
                for (i, v) in st.particles.enumerated() where v.count == 3 {
                    rug.p[i] = F3(v[0], v[1], v[2])
                    rug.q[i] = rug.p[i]
                }
            }
            for si in st.items ?? [] { savedSpots[si.name] = F2(si.x, si.y) }
        }
        rescueStash(Store.stash, savedSpots: savedSpots)
        // The pre-rename build ("Rug") kept its own folder: rescue that too, using its saved spots.
        // (`legacy` is the pre-rename folder found above.)
        var legacySpots: [String: F2] = [:]
        if let data = try? Data(contentsOf: legacy.appendingPathComponent("state.json")),
           let st = try? JSONDecoder().decode(SavedState.self, from: data) {
            for si in st.items ?? [] { legacySpots[si.name] = F2(si.x, si.y) }
        }
        rescueStash(legacy.appendingPathComponent("Under the Rug", isDirectory: true), savedSpots: legacySpots)
        updateProjection()
    }

    /// Version 1.0 hid swept files in a folder of their own. Put each one back on the Desktop, in the spot
    /// it had under the rug, so it's a normal desktop icon again.
    private func rescueStash(_ stash: URL, savedSpots: [String: F2]) {
        let fm = FileManager.default
        let names = ((try? fm.contentsOfDirectory(atPath: stash.path)) ?? []).filter { !$0.hasPrefix(".") }
        var placed: [(URL, CGPoint)] = []
        let c = rug.centroid
        for (n, name) in names.enumerated() {
            let dest = Store.uniqueURL(in: Store.desktop, name: name)
            guard (try? fm.moveItem(at: stash.appendingPathComponent(name), to: dest)) != nil else { continue }
            let a = Float(n) * 2.39996, r = 40 * Float(n).squareRoot()
            let pos = savedSpots[name] ?? c + F2(cos(a), sin(a)) * r
            placed.append((dest, CGPoint(x: CGFloat(pos.x), y: bounds.height - CGFloat(pos.y))))
        }
        if placed.isEmpty {
            try? fm.removeItem(at: stash.appendingPathComponent(".DS_Store"))
            try? fm.removeItem(at: stash)   // only succeeds if it's now empty
        } else {
            Finder.place(placed) { [weak self] in self?.refreshDesktopIcons() }
        }
    }

    // MARK: - Headless snapshots (used by `RoadRug --snapshot <dir>` to check rendering and physics)

    var persistenceEnabled = true

    func addDebugItem(url: URL, at pos: F2) {
        items.append(makeItem(url: url, pos: pos))
    }

    func writeSnapshot(to path: String) {
        guard let device, let cb = queue.makeCommandBuffer() else { return }
        let w = Int(bounds.width), h = Int(bounds.height)
        let cd = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: w, height: h, mipmapped: false)
        cd.usage = .renderTarget
        cd.storageMode = .shared
        let dd = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .depth32Float, width: w, height: h, mipmapped: false)
        dd.usage = .renderTarget
        dd.storageMode = .private
        guard let color = device.makeTexture(descriptor: cd), let depth = device.makeTexture(descriptor: dd) else { return }
        let rpd = MTLRenderPassDescriptor()
        rpd.colorAttachments[0].texture = color
        rpd.colorAttachments[0].loadAction = .clear
        rpd.colorAttachments[0].storeAction = .store
        rpd.colorAttachments[0].clearColor = MTLClearColor(red: 0.42, green: 0.52, blue: 0.60, alpha: 1)   // stand-in wallpaper
        rpd.depthAttachment.texture = depth
        rpd.depthAttachment.loadAction = .clear
        rpd.depthAttachment.clearDepth = 1
        rpd.depthAttachment.storeAction = .dontCare
        updateProjection()
        encodeScene(cb, rpd)
        cb.commit()
        cb.waitUntilCompleted()

        var bytes = [UInt8](repeating: 0, count: w * h * 4)
        color.getBytes(&bytes, bytesPerRow: w * 4, from: MTLRegionMake2D(0, 0, w, h), mipmapLevel: 0)
        let info = CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        guard let provider = CGDataProvider(data: Data(bytes) as CFData),
              let img = CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: w * 4,
                                space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: info, provider: provider,
                                decode: nil, shouldInterpolate: false, intent: .defaultIntent)
        else { return }
        let rep = NSBitmapImageRep(cgImage: img)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
    }

    func save() {
        guard persistenceEnabled else { return }
        let st = SavedState(particles: rug.p.map { [$0.x, $0.y, $0.z] }, items: nil)
        if let data = try? JSONEncoder().encode(st) { try? data.write(to: Store.state, options: .atomic) }
    }
}

/// Talks to Finder (AppleScript, off the main thread): where the desktop icons are, and moving them.
/// Positions are Finder's: points from the top-left corner of the main screen.
enum Finder {
    private static let queue = DispatchQueue(label: "Road Rug Finder", qos: .utility)
    private static let listScript = NSAppleScript(source: """
        tell application "Finder"
            set out to ""
            repeat with i in (get every item of desktop)
                try
                    set p to desktop position of i
                    set out to out & (POSIX path of (i as alias)) & tab & (item 1 of p) & tab & (item 2 of p) & linefeed
                end try
            end repeat
            return out
        end tell
        """)

    static func desktopIcons(_ done: @escaping ([(url: URL, position: CGPoint)]) -> Void) {
        queue.async {
            var error: NSDictionary?
            guard let text = listScript?.executeAndReturnError(&error).stringValue else { return }
            let icons: [(url: URL, position: CGPoint)] = text.split(separator: "\n").compactMap { line in
                let f = line.split(separator: "\t", omittingEmptySubsequences: false)
                guard f.count == 3, let x = Double(f[1]), let y = Double(f[2]) else { return nil }
                return (URL(fileURLWithPath: String(f[0])), CGPoint(x: x, y: y))
            }
            DispatchQueue.main.async { done(icons) }
        }
    }

    static func place(_ icons: [(URL, CGPoint)], then done: @escaping () -> Void) {
        guard !icons.isEmpty else { return done() }
        queue.async {
            for (url, p) in icons {
                let path = url.path.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
                let src = "tell application \"Finder\" to set desktop position of (POSIX file \"\(path)\" as alias) to {\(Int(p.x)), \(Int(p.y))}"
                var error: NSDictionary?
                NSAppleScript(source: src)?.executeAndReturnError(&error)
                if let error { NSLog("Road Rug: Finder couldn't place \(url.lastPathComponent): \(error)") }
            }
            DispatchQueue.main.async(execute: done)
        }
    }
}
