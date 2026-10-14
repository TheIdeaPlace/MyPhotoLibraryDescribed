import AppKit
// The disk image's window background (scripts/release-mac.sh): 640 × 400 points, drawn at 1x and 2x.
//
//   swift scripts/make-dmg-background.swift <output folder> "<app name>"
//
// writes background.png and background@2x.png; release-mac.sh joins them into one TIFF that Finder shows sharp on
// either kind of screen. A pale sky, the colours of the app icon's photo, fading to white at the top; the app's name
// as a title; the app on the left and Applications on the right, at the points release-mac.sh puts their icons, with a
// blue arrow between them; and a line saying what to do. Finder draws the same picture whether the Mac is in light or
// dark mode, so the palette is light and the text is dark navy, well over WCAG AA contrast.
let args = CommandLine.arguments
guard args.count == 3 else { print("Usage: make-dmg-background.swift <output folder> <app name>"); exit(1) }
let outDir = URL(fileURLWithPath: args[1])
let name = args[2]
let width = 640.0, height = 400.0
// Icon centres, in Finder's coordinates (from the top left), as release-mac.sh places them.
let appCentre = CGPoint(x: 160, y: 205), appsCentre = CGPoint(x: 480, y: 205)

func rgb(_ r: Double, _ g: Double, _ b: Double, _ a: Double = 1) -> CGColor {
  CGColor(red: r/255, green: g/255, blue: b/255, alpha: a)
}
let navy = NSColor(red: 16/255, green: 35/255, blue: 63/255, alpha: 1)       // #10233F
let slate = NSColor(red: 52/255, green: 72/255, blue: 99/255, alpha: 1)      // #344863
let blue = rgb(10, 103, 200)                                                    // the icon's tile

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
  // A soft warm glow in the top right, the icon's sun, barely there.
  let glow = CGGradient(colorsSpace: nil, colors: [rgb(255, 214, 102, 0.30), rgb(255, 214, 102, 0)] as CFArray,
                        locations: [0, 1])!
  ctx.drawRadialGradient(glow, startCenter: CGPoint(x: 560, y: 40), startRadius: 0,
                         endCenter: CGPoint(x: 560, y: 40), endRadius: 190, options: [])
  // Two gentle hills along the bottom, the icon's greens washed out so they sit behind everything.
  func hill(_ from: CGPoint, _ c1: CGPoint, _ c2: CGPoint, _ to: CGPoint, _ colour: CGColor) {
    ctx.beginPath(); ctx.move(to: CGPoint(x: from.x, y: height)); ctx.addLine(to: from)
    ctx.addCurve(to: to, control1: c1, control2: c2)
    ctx.addLine(to: CGPoint(x: to.x, y: height)); ctx.closePath()
    ctx.setFillColor(colour); ctx.fillPath()
  }
  hill(CGPoint(x: -20, y: 380), CGPoint(x: 140, y: 350), CGPoint(x: 300, y: 352), CGPoint(x: 660, y: 388), rgb(196, 226, 200))
  hill(CGPoint(x: -20, y: 398), CGPoint(x: 260, y: 380), CGPoint(x: 470, y: 356), CGPoint(x: 660, y: 368), rgb(172, 212, 178))

  // A white card behind the two icons and their names, lifting them off the sky.
  let card = CGRect(x: 48, y: 106, width: width - 96, height: 200)
  let cardPath = CGPath(roundedRect: card, cornerWidth: 22, cornerHeight: 22, transform: nil)
  ctx.saveGState()
  ctx.setShadow(offset: CGSize(width: 0, height: 4), blur: 18, color: rgb(16, 35, 63, 0.12))
  ctx.addPath(cardPath); ctx.setFillColor(rgb(255, 255, 255, 0.72)); ctx.fillPath()
  ctx.restoreGState()
  ctx.addPath(cardPath); ctx.setStrokeColor(rgb(255, 255, 255, 0.9)); ctx.setLineWidth(1); ctx.strokePath()

  // The arrow, from the app to Applications: a rounded shaft and a rounded head, in the icon's blue.
  let y = appCentre.y - 8
  let start = appCentre.x + 84, end = appsCentre.x - 84
  ctx.setStrokeColor(blue); ctx.setLineCap(.round); ctx.setLineJoin(.round)
  ctx.setLineWidth(7)
  ctx.beginPath(); ctx.move(to: CGPoint(x: start, y: y)); ctx.addLine(to: CGPoint(x: end - 4, y: y)); ctx.strokePath()
  ctx.beginPath()
  ctx.move(to: CGPoint(x: end - 20, y: y - 18)); ctx.addLine(to: CGPoint(x: end, y: y)); ctx.addLine(to: CGPoint(x: end - 20, y: y + 18))
  ctx.strokePath()

  // Text, centred.
  func text(_ s: String, _ font: NSFont, _ colour: NSColor, top: Double, kern: Double = 0) {
    let para = NSMutableParagraphStyle(); para.alignment = .center
    let a = NSAttributedString(string: s, attributes: [.font: font, .foregroundColor: colour, .paragraphStyle: para, .kern: kern])
    a.draw(in: CGRect(x: 20, y: top, width: width - 40, height: font.pointSize * 1.6))
  }
  let titleFont = NSFont.systemFont(ofSize: 27, weight: .bold)
  text(name, NSFontManager.shared.convert(titleFont, toHaveTrait: []), navy, top: 34, kern: -0.3)
  text("Describe your photos, on your Mac.", NSFont.systemFont(ofSize: 14, weight: .regular), slate, top: 72)
  text("Drag \(name) to Applications to install.", NSFont.systemFont(ofSize: 14, weight: .semibold), navy, top: 322)

  NSGraphicsContext.current = nil
  let rep = NSBitmapImageRep(cgImage: ctx.makeImage()!)
  rep.size = NSSize(width: width, height: height)   // 72 dpi at 1x, 144 at 2x, so Finder knows which is which
  return rep.representation(using: .png, properties: [:])!
}

try! FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
try! draw(scale: 1).write(to: outDir.appendingPathComponent("background.png"))
try! draw(scale: 2).write(to: outDir.appendingPathComponent("background@2x.png"))
