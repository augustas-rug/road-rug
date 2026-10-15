import Cocoa
import Metal
import simd

/// Borderless, click-through window that sits just above the desktop icons and below every app window.
final class RugWindow: NSWindow {
    override var canBecomeKey: Bool { true }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    var window: RugWindow!
    var view: RugView!
    var statusItem: NSStatusItem!

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        guard let screen = NSScreen.main, let device = MTLCreateSystemDefaultDevice() else {
            NSLog("Road Rug: no screen or Metal device")
            NSApp.terminate(nil)
            return
        }

        window = RugWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.setFrame(screen.frame, display: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = false
        window.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) + 1)
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenNone]
        window.ignoresMouseEvents = true
        window.isReleasedWhenClosed = false

        view = RugView(frame: NSRect(origin: .zero, size: screen.frame.size), device: device)
        window.contentView = view
        window.orderFrontRegardless()

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "rectangle.grid.3x2", accessibilityDescription: "Road Rug")
        statusItem.menu = view.makeMenu()

        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { [weak self] _ in
            guard let self, let screen = NSScreen.main else { return }
            self.window.setFrame(screen.frame, display: true)
            self.view.frame = NSRect(origin: .zero, size: screen.frame.size)
            self.view.updateWalls()
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        view?.save()
    }
}

/// `RoadRug --snapshot <dir>`: a scripted, headless run that renders PNG frames of the rug settling over
/// some lumps, a corner being folded over, and the rug being dragged around. Handy for checking changes
/// to the physics or rendering without touching your real desktop.
func runSnapshots(into dir: String) {
    guard let device = MTLCreateSystemDefaultDevice() else { return }
    let size = F2(1440, 900)
    let view = RugView(frame: NSRect(x: 0, y: 0, width: CGFloat(size.x), height: CGFloat(size.y)), device: device)
    view.persistenceEnabled = false
    view.items.removeAll()
    view.rug.walls = SIMD4(0, 0, size.x, size.y)
    let c = size / 2
    view.rug.layout(center: c, angle: 0.05, drop: SAFE_DROP)
    view.drawsItemIcons = true
    view.addDebugItem(url: URL(fileURLWithPath: "/System/Applications/Calculator.app"), at: c + F2(-150, 40))
    // A pile of four in one spot (should make a bigger mound than the single icon above).
    for (k, app) in ["Chess", "TextEdit", "Notes", "Maps"].enumerated() {
        view.addDebugItem(url: URL(fileURLWithPath: "/System/Applications/\(app).app"), at: c + F2(130, -50) + F2(Float(k % 2) * 22, Float(k / 2) * 18))
    }

    func frames(_ seconds: Float, _ mouse: (Float) -> F2 = { _ in .zero }) {
        let steps = max(1, Int(seconds * 60))
        for k in 0..<steps {
            RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.001))   // let file sizes (lump heights) arrive
            view.advance(dt: 1.0 / 60.0, mouse: mouse(Float(k + 1) / Float(steps)))
        }
    }
    func shot(_ name: String) { view.writeSnapshot(to: "\(dir)/\(name).png") }

    frames(1.5)
    shot("1-settled")
    let bumps = view.items.map(\.bump)
    FileHandle.standardError.write("lump height: single icon \(Cloth.floorHeight(c.x - 150, c.y + 40, bumps)) pt, pile of four \(Cloth.floorHeight(c.x + 141, c.y - 41, bumps)) pt\n".data(using: .utf8)!)

    // Fold the top-right corner over towards the middle.
    let p = view.rug.p[NY * NX - 1]
    let corner = F2(p.x, p.y) + simd_normalize(view.rug.centroid - F2(p.x, p.y)) * 8
    let dropAt = c + F2(80, 40)
    if view.beginGrab(at: corner) {
        frames(1.0) { f in corner + (dropAt - corner) * f }
        shot("2-folding")
        view.release()
        frames(1.0)
        shot("3-folded")
    }

    // Straighten, then drag it by the middle.
    view.straighten()
    frames(1.0)
    let mid = view.rug.centroid + F2(-100, 20)
    if view.beginGrab(at: mid) {
        frames(0.6) { f in mid + F2(300, 80) * f }
        shot("4-dragging")
        view.release()
        frames(1.5)
        shot("5-rest")
    }
    FileHandle.standardError.write("Wrote snapshots to \(dir)\n".data(using: .utf8)!)
}

let args = CommandLine.arguments
if let i = args.firstIndex(of: "--snapshot"), i + 1 < args.count {
    _ = NSApplication.shared
    runSnapshots(into: args[i + 1])
    exit(0)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
