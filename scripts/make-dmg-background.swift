import AppKit
// The disk image's window background, drawn at 1x and 2x for scripts/make-dmg.sh, which joins them into one TIFF.
//
//   swift scripts/make-dmg-background.swift <output folder> "<app name>" <app x> <Applications x> <icons' y>
//
// writes background.png and background@2x.png. The icons' centres, in points from the window's top left, come from
// make-dmg.sh, which puts the icons there, so the arrow and card can't drift from them.
//
// A pale sky in the colours of the app icon's photo, fading to white at the top, with rolling green hills along the
// bottom; the app's name as a title; a white card holding the two icons with a blue arrow from the app to
// Applications; and a line saying what to do.
//
// Finder draws the same picture in light and dark mode, but draws the icons' names itself, and nothing says what
// colour it picks over a picture (reports say black, as if light, but macOS can't be relied on for that). So the
// names sit on a steel-blue band whose contrast with black text and with white text are both over WCAG AA's 4.5:1.
// The rest of the text is dark navy on near-white, about 14:1.
let args = CommandLine.arguments
guard args.count == 6, let appX = Double(args[3]), let appsX = Double(args[4]), let iconY = Double(args[5]) else {
  print("Usage: make-dmg-background.swift <output folder> <app name> <app x> <Applications x> <icons' y>"); exit(1)
}
let outDir = URL(fileURLWithPath: args[1])
let name = args[2]
// 640 wide; drawn 410 tall although the window shows 400, so a macOS with a shorter title bar (macOS 15's is 28
// points, 26's is 32) still has picture to the bottom edge. Where the window's shorter, Finder crops the hills.
let width = 640.0, height = 410.0

func rgb(_ r: Double, _ g: Double, _ b: Double, _ a: Double = 1) -> CGColor {
  CGColor(srgbRed: r/255, green: g/255, blue: b/255, alpha: a)
}
let navy = NSColor(srgbRed: 16/255, green: 35/255, blue: 63/255, alpha: 1)        // #10233F
let slate = NSColor(srgbRed: 52/255, green: 72/255, blue: 99/255, alpha: 1)       // #344863
let blue = rgb(10, 103, 200)                                                     // the icon's tile
// Relative luminance 0.177: 4.6:1 against white text, 4.5:1 against black.
let band = rgb(60, 120, 180)                                                     // #3C78B4

func draw(scale: Double) -> Data {
  let ctx = CGContext(data: nil, width: Int(width * scale), height: Int(height * scale), bitsPerComponent: 8,
                      bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
  ctx.scaleBy(x: scale, y: scale)
  // Draw from the top left, as Finder counts.
  ctx.translateBy(x: 0, y: height); ctx.scaleBy(x: 1, y: -1)
  NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)

  // Sky: near-white at the top to the icon's pale blue at the bottom.
  let sky = CGGradient(colorsSpace: nil, colors: [rgb(251, 253, 255), rgb(236, 244, 253), rgb(214, 232, 251)] as CFArray,
                       locations: [0, 0.45, 1])!
  ctx.drawLinearGradient(sky, start: .zero, end: CGPoint(x: 0, y: height), options: [])
  // The icon's sun, as the faintest warm light in the top right corner.
  let glow = CGGradient(colorsSpace: nil, colors: [rgb(255, 221, 120, 0.16), rgb(255, 221, 120, 0)] as CFArray,
                        locations: [0, 1])!
  ctx.drawRadialGradient(glow, startCenter: CGPoint(x: 590, y: 20), startRadius: 0,
                         endCenter: CGPoint(x: 590, y: 20), endRadius: 150, options: [])
  // Rolling hills along the bottom, in the icon's greens washed out so they sit behind everything: each a gentle
  // wave of two swells, the nearer one lower and a little deeper in colour.
  func hills(base: Double, swell: Double, period: Double, phase: Double, _ colour: CGColor) {
    ctx.beginPath(); ctx.move(to: CGPoint(x: 0, y: height))
    for x in stride(from: 0.0, through: width, by: 2) {
      let y = base - swell * (0.5 + 0.5 * cos(2 * .pi * (x - phase) / period))
      ctx.addLine(to: CGPoint(x: x, y: y))
    }
    ctx.addLine(to: CGPoint(x: width, y: height)); ctx.closePath()
    ctx.setFillColor(colour); ctx.fillPath()
  }
  hills(base: 384, swell: 22, period: 520, phase: 150, rgb(200, 228, 203))
  hills(base: 400, swell: 20, period: 460, phase: 470, rgb(174, 213, 180))

  // A card behind the two icons, lifting them off the sky, with the names on its steel-blue lower band. The icons are
  // 128 points, so they end 64 below iconY; Finder puts their names just under that.
  let card = CGRect(x: 48, y: iconY - 90, width: width - 96, height: 200)
  let bandTop = iconY + 66
  let cardPath = CGPath(roundedRect: card, cornerWidth: 22, cornerHeight: 22, transform: nil)
  ctx.saveGState()
  ctx.setShadow(offset: CGSize(width: 0, height: 4), blur: 18, color: rgb(16, 35, 63, 0.14))
  ctx.addPath(cardPath); ctx.setFillColor(rgb(255, 255, 255, 0.82)); ctx.fillPath()
  ctx.restoreGState()
  ctx.saveGState(); ctx.addPath(cardPath); ctx.clip()
  ctx.setFillColor(band); ctx.fill(CGRect(x: card.minX, y: bandTop, width: card.width, height: card.maxY - bandTop))
  ctx.restoreGState()
  ctx.addPath(cardPath); ctx.setStrokeColor(rgb(255, 255, 255, 0.9)); ctx.setLineWidth(1); ctx.strokePath()

  // The arrow, from the app to Applications on the icons' centre line: a rounded shaft and head, in the icon's blue.
  let start = appX + 84, end = appsX - 84
  ctx.setStrokeColor(blue); ctx.setLineCap(.round); ctx.setLineJoin(.round)
  ctx.setLineWidth(7)
  ctx.beginPath(); ctx.move(to: CGPoint(x: start, y: iconY)); ctx.addLine(to: CGPoint(x: end - 4, y: iconY)); ctx.strokePath()
  ctx.beginPath()
  ctx.move(to: CGPoint(x: end - 20, y: iconY - 18)); ctx.addLine(to: CGPoint(x: end, y: iconY))
  ctx.addLine(to: CGPoint(x: end - 20, y: iconY + 18))
  ctx.strokePath()

  // Text, centred.
  func text(_ s: String, _ font: NSFont, _ colour: NSColor, top: Double, kern: Double = 0) {
    let para = NSMutableParagraphStyle(); para.alignment = .center
    let a = NSAttributedString(string: s, attributes: [.font: font, .foregroundColor: colour, .paragraphStyle: para, .kern: kern])
    a.draw(in: CGRect(x: 20, y: top, width: width - 40, height: font.pointSize * 1.6))
  }
  text(name, NSFont.systemFont(ofSize: 27, weight: .bold), navy, top: card.minY - 82, kern: -0.3)
  text("Describe your photos on your Mac.", NSFont.systemFont(ofSize: 14, weight: .regular), slate, top: card.minY - 44)
  text("Move \(name) to Applications to install.", NSFont.systemFont(ofSize: 14, weight: .semibold), navy,
       top: card.maxY + 12)

  NSGraphicsContext.current = nil
  let rep = NSBitmapImageRep(cgImage: ctx.makeImage()!)
  rep.size = NSSize(width: width, height: height)   // 72 dpi at 1x, 144 at 2x, so Finder knows which is which
  return rep.representation(using: .png, properties: [:])!
}

try! FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
try! draw(scale: 1).write(to: outDir.appendingPathComponent("background.png"))
try! draw(scale: 2).write(to: outDir.appendingPathComponent("background@2x.png"))
