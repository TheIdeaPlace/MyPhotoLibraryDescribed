import AppKit
// The Windows app's icon (windows/packaging/Assets), redrawn at 1024 px in macOS's icon grid: an 824 px rounded
// square with a soft shadow, a white-framed photo of a sky, sun and two green hills.
let size = 1024.0
let image = NSImage(size: NSSize(width: size, height: size))
image.lockFocus()
let ctx = NSGraphicsContext.current!.cgContext
func rgb(_ r: Double, _ g: Double, _ b: Double) -> CGColor { CGColor(red: r/255, green: g/255, blue: b/255, alpha: 1) }
// The tile, with macOS's usual drop shadow.
let tile = CGRect(x: 100, y: 100, width: 824, height: 824)
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 28, color: CGColor(gray: 0, alpha: 0.35))
let tilePath = CGPath(roundedRect: tile, cornerWidth: 185, cornerHeight: 185, transform: nil)
ctx.addPath(tilePath); ctx.setFillColor(rgb(10, 103, 200)); ctx.fillPath()
ctx.restoreGState()
// A slight sheen from the top, as Mac icons have.
ctx.saveGState(); ctx.addPath(tilePath); ctx.clip()
let sheen = CGGradient(colorsSpace: nil, colors: [CGColor(gray: 1, alpha: 0.18), CGColor(gray: 1, alpha: 0)] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(sheen, start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 512), options: [])
ctx.restoreGState()
// The photo's white frame, then the picture inside it.
let frame = CGRect(x: 210, y: 270, width: 604, height: 484)
ctx.addPath(CGPath(roundedRect: frame, cornerWidth: 46, cornerHeight: 46, transform: nil)); ctx.setFillColor(.white); ctx.fillPath()
let pic = frame.insetBy(dx: 34, dy: 34)
ctx.saveGState()
ctx.addPath(CGPath(roundedRect: pic, cornerWidth: 22, cornerHeight: 22, transform: nil)); ctx.clip()
ctx.setFillColor(rgb(205, 231, 252)); ctx.fill(pic)
// Sun.
ctx.setFillColor(rgb(255, 190, 0)); ctx.fillEllipse(in: CGRect(x: 620, y: 560, width: 96, height: 96))
// Back hill, then front hill.
ctx.setFillColor(rgb(16, 124, 16))
ctx.beginPath(); ctx.move(to: CGPoint(x: pic.minX - 10, y: pic.minY)); ctx.addLine(to: CGPoint(x: 400, y: 560)); ctx.addLine(to: CGPoint(x: 590, y: pic.minY)); ctx.closePath(); ctx.fillPath()
ctx.setFillColor(rgb(11, 92, 11))
ctx.beginPath(); ctx.move(to: CGPoint(x: 420, y: pic.minY)); ctx.addLine(to: CGPoint(x: 600, y: 500)); ctx.addLine(to: CGPoint(x: pic.maxX + 10, y: pic.minY)); ctx.closePath(); ctx.fillPath()
ctx.restoreGState()
image.unlockFocus()
let rep = NSBitmapImageRep(data: image.tiffRepresentation!)!
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
