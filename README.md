<p align="center">
  <img src="docs/icon.png" width="128" alt="Road Rug icon">
</p>

<h1 align="center">Road Rug</h1>

<p align="center">
  A physics rug for your desktop. Sweep your mess under it.
  <br>
  <b>Download for</b>
  <a href="https://github.com/augustas-rug/road-rug/releases/latest/download/RoadRug-macOS.zip">macOS</a>
  ·
  <a href="https://github.com/augustas-rug/road-rug/releases/latest/download/RoadRug-Windows.zip">Windows</a>
</p>

<p align="center">
  <img src="docs/demo.gif" width="720" alt="Road Rug: dragging, lifting and folding a city-roads play mat on the desktop">
</p>

Road Rug puts a kids' city-roads play mat on your desktop, right above your icons and below every window. It's a real cloth simulation: grab it, drag it around, peel up a corner, fold it over. Lay it over your desktop icons, or drop files onto it, and they're under the rug, leaving little lumps. Bigger files make bigger lumps.

It runs natively on **macOS** (Swift + Metal) and **Windows** (C# / .NET), with the same physics and look on both.

## Features

- **Real cloth physics:** a heavy, thick rug that drapes, folds over itself and settles flat, then sleeps when still.
- **Sweep files under the rug:** lay the rug over your desktop icons, or drop files onto it. Nothing is moved or hidden: they stay ordinary desktop icons, with the rug lying on top of them.
- **Lumps:** every icon the rug covers makes a soft bump, sized by how big the file is.
- **Get things back:** lift a corner or slide the rug away, and your files are right there on the desktop, as selectable as ever.
- **Stays out of the way:** clicks pass straight through to your desktop everywhere the rug isn't.
- **Remembers everything:** the rug's position and what's under it survive restarts.

## Install

### macOS

1. [Download `RoadRug-macOS.zip`](https://github.com/augustas-rug/road-rug/releases/latest/download/RoadRug-macOS.zip) and unzip it.
2. Move **Road Rug.app** into your Applications folder.
3. The app isn't notarized, so macOS blocks the first launch. Either right-click the app and choose **Open**, or run:

   ```bash
   xattr -dr com.apple.quarantine "/Applications/Road Rug.app"
   ```

4. Open it. The rug drops onto your desktop, and a small rug icon appears in the menu bar.

Requires macOS 14 Sonoma or later, on Apple Silicon or Intel.

### Windows

1. [Download `RoadRug-Windows.zip`](https://github.com/augustas-rug/road-rug/releases/latest/download/RoadRug-Windows.zip) and unzip it anywhere, for example into `Documents\Road Rug`.
2. Run **RoadRug.exe**. It's a single file with nothing else to install.
3. The app isn't code-signed yet, so Windows SmartScreen may warn you the first time. Choose **More info**, then **Run anyway**.
4. The rug drops onto your desktop, and a rug icon appears in the system tray. Turn on **Start with Windows** from the tray menu to keep it around.

Requires Windows 10 or 11.

## How to use it

| Do this | To |
| --- | --- |
| Drag the middle of the rug | Slide the whole rug around |
| Drag a corner or edge | Lift it and fold it over |
| Hold <kbd>⌥ Option</kbd> (Mac) or <kbd>Alt</kbd> (Windows) and drag anywhere | Lift from that spot |
| Two-finger rotate (Mac trackpad) or <kbd>Shift</kbd> + scroll (Windows) | Twist the rug |
| Lay the rug over desktop icons, or drop files onto it | Sweep them under the rug |
| Double-click the rug | Lay it flat again |
| Right-click the rug, or use the menu-bar / tray icon | Straighten, recenter, quit |

Road Rug finds your desktop icons through Finder (macOS asks you once to allow this) or Explorer. Files dropped onto the rug from another folder are moved to your Desktop, so everything under the rug is always a normal desktop icon.

## Build from source

### macOS

You only need the Xcode Command Line Tools (`xcode-select --install`), not the full Xcode.

```bash
git clone https://github.com/augustas-rug/road-rug.git
cd road-rug
./build.sh
open "build/Road Rug.app"
```

`./build.sh --zip` also packages `dist/RoadRug.zip`, a universal build for Apple Silicon and Intel.

### Windows

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/augustas-rug/road-rug.git
cd road-rug
dotnet run --project windows -c Release
```

To make the single-file `RoadRug.exe` that the releases ship:

```powershell
dotnet publish windows -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o out
```

### How it works

- **Physics** (`Sources/Cloth.swift`, `windows/Cloth.cs`): a position-based dynamics cloth (49 × 26 particles) with stretch, shear and bending constraints. It adds Coulomb friction against the desktop, contact between folded layers, and a floor height field for the lumps. The two platforms share the same algorithm and numbers.
- **macOS** (`Sources/`): a transparent, click-through Metal view in a borderless window just above the desktop icons. It renders the rug as a thick sheet (printed pile, felt backing, bound edge with rounded corners) with a soft shadow.
- **Windows** (`windows/`): a per-pixel-alpha layered window kept directly above the desktop icons. It holds only the area around the rug and follows it as it moves. A small multi-core software rasterizer draws the same thick sheet, shadow and icons.

Both versions have a headless test scene: `RoadRug --snapshot <folder>` settles the rug over lumps, folds a corner and drags it, writing PNG frames. CI runs it on macOS and Windows for every push. Run `swift scripts/make-icon.swift` to regenerate the app icons from the texture.

### Making a demo

`scripts/demo-mess.sh` (macOS) fills your desktop with about 40 realistic junk files to sweep under the rug on camera. `scripts/demo-clean.sh` moves exactly those files to the Trash afterwards.

## Uninstall

Quit Road Rug from its menu. Your files are already on your desktop.

- **macOS:** delete the app and `~/Library/Application Support/Road Rug`.
- **Windows:** turn off **Start with Windows** if you turned it on, then delete `RoadRug.exe` and `%APPDATA%\Road Rug`.

## Credits

Made by [Augustas Armalis](https://github.com/augustas-rug).
Inspired by Terkel Gjervig's "Desktop Cleaning" idea of a rug for your desktop.

## License

The code is [MIT licensed](LICENSE). The rug artwork in `Resources/rug.png` is a city-roads play mat pattern and isn't covered by the MIT license.
