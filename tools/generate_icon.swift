import AppKit

let outputDirectory = CommandLine.arguments.dropFirst().first ?? "."
let iconsetURL = URL(fileURLWithPath: outputDirectory, isDirectory: true)
    .appendingPathComponent("AppIcon.iconset", isDirectory: true)

try FileManager.default.createDirectory(at: iconsetURL, withIntermediateDirectories: true)

let iconSpecs: [(base: Int, scale: Int, name: String)] = [
    (16, 1, "icon_16x16.png"),
    (16, 2, "icon_16x16@2x.png"),
    (32, 1, "icon_32x32.png"),
    (32, 2, "icon_32x32@2x.png"),
    (128, 1, "icon_128x128.png"),
    (128, 2, "icon_128x128@2x.png"),
    (256, 1, "icon_256x256.png"),
    (256, 2, "icon_256x256@2x.png"),
    (512, 1, "icon_512x512.png"),
    (512, 2, "icon_512x512@2x.png")
]

func image(size: Int) -> NSImage {
    let dimension = CGFloat(size)
    let image = NSImage(size: NSSize(width: dimension, height: dimension))
    image.lockFocus()

    NSColor.white.setFill()
    NSBezierPath(rect: NSRect(x: 0, y: 0, width: dimension, height: dimension)).fill()

    let text = "LWM"
    var fontSize = dimension * 0.36
    var attributes: [NSAttributedString.Key: Any] = [:]
    var textSize = CGSize.zero

    repeat {
        let font = NSFont.systemFont(ofSize: fontSize, weight: .black)
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .center
        attributes = [
            .font: font,
            .foregroundColor: NSColor.black,
            .paragraphStyle: paragraph,
            .kern: -fontSize * 0.03
        ]
        textSize = text.size(withAttributes: attributes)
        fontSize -= 1
    } while textSize.width > dimension * 0.84 && fontSize > 8

    let rect = NSRect(
        x: (dimension - textSize.width) / 2,
        y: (dimension - textSize.height) / 2 + dimension * 0.01,
        width: textSize.width,
        height: textSize.height
    )
    text.draw(in: rect, withAttributes: attributes)

    image.unlockFocus()
    return image
}

for spec in iconSpecs {
    let pixelSize = spec.base * spec.scale
    let icon = image(size: pixelSize)
    guard let tiffData = icon.tiffRepresentation,
          let bitmap = NSBitmapImageRep(data: tiffData),
          let pngData = bitmap.representation(using: .png, properties: [:]) else {
        fatalError("Failed to render \(spec.name)")
    }

    try pngData.write(to: iconsetURL.appendingPathComponent(spec.name))
}
