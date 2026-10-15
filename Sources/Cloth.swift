import Foundation
import simd

typealias F2 = SIMD2<Float>
typealias F3 = SIMD3<Float>

// MARK: - Tunables

let NX = 49, NY = 26                 // particle grid, matching the play mat's ~1.9:1 shape
let SPACING: Float = 15              // points between particles -> 720 x 375 pt rug
let FIXED_DT: Float = 1.0 / 120.0
let SUBSTEPS = 3
let ITERATIONS = 12
let GRAVITY: Float = 2800            // pt/s^2
let FRICTION: Float = 0.8            // Coulomb coefficient against the desktop
let AIR_DAMPING: Float = 0.99
let AIR_DRAG_XY: Float = 0.97        // airborne parts fall rather than keep swinging over
let MAX_STEP: Float = 8              // pt per substep (~2900 pt/s); stops a teleport turning into an explosion
let SAFE_DROP: Float = 20            // drop height when (re)laying the rug (above the tallest lump)
let ICON_LUMP: Float = 3.5           // how much one desktop icon under the rug lifts it
let LUMP_RADIUS: Float = 36          // how far that lump spreads
let LUMP_MAX: Float = 11             // a whole pile of icons levels off around this height
let BEND_STIFFNESS: Float = 0.45     // bends in smooth curves without flopping like paper; folds stay put
let RUG_THICKNESS: Float = 4         // pile height: the printed side sits this far above the backing
let LAYER_GAP: Float = RUG_THICKNESS + 1.5   // folded layers never get closer than this
let CORNER_RADIUS: Float = 12        // rounded corners, like a real bound rug
let BINDING_WIDTH: Float = 3         // the stitched edge around the print
let LIFT_HEIGHT: Float = 80
let SETTLE_TIME: Float = 4           // after this long untouched and nearly still, the simulation sleeps
let HAND_FOLLOW: Float = 18          // how quickly the grip catches up with the cursor (1/s): a touch of smoothing
let PERSPECTIVE: Float = 1.0 / 2000.0

struct Constraint {
    var a: Int32
    var b: Int32
    var rest: Float
    var k: Float
}

/// Position-based-dynamics cloth lying on a floor (the desktop). x/y are screen points,
/// z is height above the desktop towards the viewer.
final class Cloth {
    let n = NX * NY
    var p: [F3]
    var q: [F3]                      // previous positions (velocity = p - q)
    var pinned: [Bool]
    var pinFrom: [F3]
    var pinTo: [F3]
    var floorH: [Float]
    var flipped: [Bool] = Array(repeating: false, count: NX * NY)   // upside-down (the top layer of a fold)
    var onLayer: [Bool] = Array(repeating: false, count: NX * NY)   // resting on another layer of the rug (gets desk-like friction)
    private var head = [Int32](repeating: -1, count: 4096)
    private var next = [Int32](repeating: -1, count: NX * NY)
    var cons: [Constraint] = []
    var walls = SIMD4<Float>(0, 0, 1000, 1000)   // minX, minY, maxX, maxY
    var motion: Float = 0

    init(center: F2) {
        p = Array(repeating: .zero, count: NX * NY)
        q = p
        pinFrom = p
        pinTo = p
        pinned = Array(repeating: false, count: NX * NY)
        floorH = Array(repeating: 0, count: NX * NY)

        for j in 0..<NY {
            for i in 0..<NX {
                for (di, dj, k) in [(1, 0, Float(1)), (0, 1, 1), (1, 1, 0.8), (-1, 1, 0.8),
                                    (2, 0, BEND_STIFFNESS), (0, 2, BEND_STIFFNESS)] {
                    let i2 = i + di, j2 = j + dj
                    guard i2 >= 0, i2 < NX, j2 < NY else { continue }
                    let rest = SPACING * Float(di * di + dj * dj).squareRoot()
                    cons.append(Constraint(a: Int32(j * NX + i), b: Int32(j2 * NX + i2), rest: rest, k: k))
                }
            }
        }
        layout(center: center, angle: 0, drop: SAFE_DROP)
    }

    /// Lays the rug out flat, dropped from `drop` points so it settles over anything underneath.
    func layout(center: F2, angle: Float, drop: Float) {
        let w = Float(NX - 1) * SPACING, h = Float(NY - 1) * SPACING
        let ca = cos(angle), sa = sin(angle)
        for j in 0..<NY {
            for i in 0..<NX {
                let lx = Float(i) * SPACING - w / 2, ly = Float(j) * SPACING - h / 2
                let v = F3(center.x + lx * ca - ly * sa, center.y + lx * sa + ly * ca, drop)
                p[j * NX + i] = v
                q[j * NX + i] = v
                pinned[j * NX + i] = false
            }
        }
    }

    var centroid: F2 {
        var s = F2.zero
        for v in p { s += F2(v.x, v.y) }
        return s / Float(n)
    }

    var angle: Float {
        var d = F2.zero
        for j in 0..<NY {
            let a = p[j * NX], b = p[j * NX + NX - 1]
            d += F2(b.x - a.x, b.y - a.y)
        }
        return atan2(d.y, d.x)
    }

    func rotate(by angle: Float, around c: F2) {
        let ca = cos(angle), sa = sin(angle)
        func r(_ v: F3) -> F3 {
            let x = v.x - c.x, y = v.y - c.y
            return F3(c.x + x * ca - y * sa, c.y + x * sa + y * ca, v.z)
        }
        for i in 0..<n {
            p[i] = r(p[i])
            q[i] = r(q[i])
        }
    }

    func translate(by d: F2) {
        let d3 = F3(d.x, d.y, 0)
        for i in 0..<n {
            p[i] += d3
            q[i] += d3
        }
    }

    /// The desk's height under the rug: each icon underneath adds a low, soft lump, so a pile of icons in one
    /// spot makes a bigger mound. The total eases off towards LUMP_MAX instead of growing without limit.
    @inline(__always)
    static func floorHeight(_ x: Float, _ y: Float, _ bumps: [SIMD4<Float>]) -> Float {
        var sum: Float = 0
        for b in bumps {
            let dx = x - b.x, dy = y - b.y
            let d2 = dx * dx + dy * dy, r2 = b.w * b.w
            if d2 < r2 {
                let t = 1 - d2 / r2
                sum += b.z * t * t
            }
        }
        return sum <= 0 ? 0 : LUMP_MAX * (1 - exp(-sum / LUMP_MAX))
    }

    /// Advances one fixed frame. `bumps` are (x, y, height, radius) of things hidden under the rug.
    func step(dt: Float, bumps: [SIMD4<Float>]) {
        let h = dt / Float(SUBSTEPS)
        let g = GRAVITY * h * h
        let fr = FRICTION * g
        var maxMove: Float = 0

        for s in 0..<SUBSTEPS {
            let t = Float(s + 1) / Float(SUBSTEPS)
            p.withUnsafeMutableBufferPointer { P in
            q.withUnsafeMutableBufferPointer { Q in
            pinned.withUnsafeBufferPointer { Pin in
            pinFrom.withUnsafeBufferPointer { PF in
            pinTo.withUnsafeBufferPointer { PT in
            floorH.withUnsafeMutableBufferPointer { FH in
            cons.withUnsafeBufferPointer { C in
                for i in 0..<n { FH[i] = Cloth.floorHeight(P[i].x, P[i].y, bumps) }

                // Integrate (Verlet) with Coulomb friction for particles touching the desktop.
                for i in 0..<n {
                    if Pin[i] {
                        Q[i] = P[i]
                        P[i] = PF[i] + (PT[i] - PF[i]) * t
                        continue
                    }
                    var v = (P[i] - Q[i]) * AIR_DAMPING
                    let speed = simd_length(v)
                    if speed > MAX_STEP { v *= MAX_STEP / speed }
                    if P[i].z <= FH[i] + 0.5 || onLayer[i] {
                        let sp = (v.x * v.x + v.y * v.y).squareRoot()
                        if sp <= fr { v.x = 0; v.y = 0 } else { let k = (sp - fr) / sp; v.x *= k; v.y *= k }
                    } else {
                        v.x *= AIR_DRAG_XY
                        v.y *= AIR_DRAG_XY
                    }
                    Q[i] = P[i]
                    P[i] += v
                    P[i].z -= g
                }

                // Gauss-Seidel, sweeping alternately forwards and backwards so no direction is favoured
                // (a one-way sweep makes the rug creep and twist a little after every drag).
                let count = C.count
                for it in 0..<ITERATIONS {
                    let forward = it % 2 == 0
                    for idx in 0..<count {
                        let c = C[forward ? idx : count - 1 - idx]
                        let a = Int(c.a), b = Int(c.b)
                        let wa: Float = Pin[a] ? 0 : 1, wb: Float = Pin[b] ? 0 : 1
                        let w = wa + wb
                        if w == 0 { continue }
                        let d = P[b] - P[a]
                        let len = simd_length(d)
                        if len < 1e-6 { continue }
                        let corr = d * ((len - c.rest) / (len * w) * c.k)
                        P[a] += corr * wa
                        P[b] -= corr * wb
                    }
                    for i in 0..<n where P[i].z < FH[i] { P[i].z = FH[i] }
                }
            }}}}}}}

            if isRaised() { separateLayers() }
            if s == SUBSTEPS - 1 {
                for i in 0..<n where !pinned[i] { maxMove = max(maxMove, simd_length(p[i] - q[i])) }
            }
        }
        motion = maxMove
        rescueIfLost()
    }

    /// The rug is never nudged while any of it is on screen. Only if it has been thrown completely off
    /// screen is it slid back until an edge shows again, so it can't be lost.
    private func rescueIfLost() {
        if pinned.contains(true) { return }
        for v in p where v.x >= walls.x && v.x <= walls.z && v.y >= walls.y && v.y <= walls.w { return }
        let c = centroid
        let target = F2(min(max(c.x, walls.x), walls.z), min(max(c.y, walls.y), walls.w))
        translate(by: (target - c) * 0.1)
    }

    /// Self-contact only matters once part of the rug is off the desk.
    private func isRaised() -> Bool {
        for i in 0..<n where p[i].z > floorH[i] + 1 { return true }
        for i in 0..<n where flipped[i] { flipped[i] = false }
        return false
    }

    @inline(__always)
    private static func hash(_ x: Int, _ y: Int) -> Int { ((x &* 73856093) ^ (y &* 19349663)) & 4095 }

    /// Keeps the layers of a fold LAYER_GAP apart so the rug can roll over itself without clipping.
    /// Particles within 3 grid steps of each other (the crease itself) never collide, which is what
    /// used to make folds jitter; the upside-down layer is always the top one, and touching layers
    /// grip each other so a fold stays where it's put.
    private func separateLayers() {
        for j in 0..<NY {
            for i in 0..<NX {
                let dx = p[j * NX + min(i + 1, NX - 1)] - p[j * NX + max(i - 1, 0)]
                let dy = p[min(j + 1, NY - 1) * NX + i] - p[max(j - 1, 0) * NX + i]
                flipped[j * NX + i] = dx.x * dy.y - dx.y * dy.x < 0
            }
        }
        // While the rug is being held, layers slide freely over each other (pulling a fold back must not
        // snag on the layer underneath); the grip that keeps a fold in place only applies once let go.
        let held = pinned.contains(true)
        let cell = SPACING
        let r = SPACING * 1.3, r2 = r * r       // soft contact radius: support fades out instead of switching off
        let n = self.n
        flipped.withUnsafeBufferPointer { FL in
        head.withUnsafeMutableBufferPointer { H in
        next.withUnsafeMutableBufferPointer { N in
        p.withUnsafeMutableBufferPointer { P in
        q.withUnsafeBufferPointer { Q in
        floorH.withUnsafeBufferPointer { FH in
        pinned.withUnsafeBufferPointer { Pin in
            for i in 0..<H.count { H[i] = -1 }
            for i in 0..<n { self.onLayer[i] = false }
            for i in 0..<n {
                let hsh = Cloth.hash(Int((P[i].x / cell).rounded(.down)), Int((P[i].y / cell).rounded(.down)))
                N[i] = H[hsh]
                H[hsh] = Int32(i)
            }
            for i in 0..<n {
                let cx = Int((P[i].x / cell).rounded(.down)), cy = Int((P[i].y / cell).rounded(.down))
                let ii = i % NX, ij = i / NX
                for oy in -1...1 {
                    for ox in -1...1 {
                        var cur = H[Cloth.hash(cx + ox, cy + oy)]
                        while cur >= 0 {
                            let j = Int(cur)
                            cur = N[j]
                            if j <= i { continue }
                            if abs(ii - j % NX) <= 3 && abs(ij - j / NX) <= 3 { continue }
                            let dx = P[i].x - P[j].x, dy = P[i].y - P[j].y
                            if dx * dx + dy * dy > r2 { continue }
                            let dz = P[i].z - P[j].z
                            if abs(dz) >= LAYER_GAP { continue }
                            // Whichever layer was on top a moment ago stays on top (so unfolding is smooth);
                            // only when they were level does the upside-down one count as the top.
                            let qdz = Q[i].z - Q[j].z
                            let iAbove = abs(qdz) > 0.5 ? qdz > 0 : FL[i]
                            let top = iAbove ? i : j, bot = iAbove ? j : i
                            let w = 1 - (dx * dx + dy * dy).squareRoot() / r   // 1 directly above, 0 at the rim
                            let need = (LAYER_GAP - (P[top].z - P[bot].z)) * w
                            if w > 0.3 && !held { self.onLayer[top] = true }
                            if need <= 0 { continue }
                            let botFixed = Pin[bot] || P[bot].z <= FH[bot] + 0.01
                            let topFixed = Pin[top]
                            if topFixed && botFixed { continue }
                            if botFixed {
                                P[top].z += need
                            } else if topFixed {
                                P[bot].z -= need
                            } else {
                                P[top].z += need * 0.5
                                P[bot].z -= need * 0.5
                            }
                            // Layer grip: the top layer moves with the one it rests on.
                            if !topFixed && !held {
                                let rel = (P[top] - Q[top]) - (P[bot] - Q[bot])
                                P[top].x -= rel.x * 0.5
                                P[top].y -= rel.y * 0.5
                            }
                        }
                    }
                }
            }
        }}}}}}}
    }

    /// Worst stretch of any structural link (1.0 = rest length). Used by the stress test.
    var maxStretch: Float {
        var worst: Float = 0
        for c in cons where c.k == 1 {
            worst = max(worst, simd_length(p[Int(c.b)] - p[Int(c.a)]) / c.rest)
        }
        return worst
    }
}

struct SplitMix {
    var s: UInt64
    mutating func next() -> Double {
        s &+= 0x9E37_79B9_7F4A_7C15
        var z = s
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        z ^= z >> 31
        return Double(z >> 11) / Double(UInt64(1) << 53)
    }
    mutating func nextF() -> Float { Float(next()) }
    mutating func nextCG() -> CGFloat { CGFloat(next()) }
}
