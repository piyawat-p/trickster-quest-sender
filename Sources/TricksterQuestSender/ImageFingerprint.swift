import CoreGraphics
import Foundation

/// A small "fingerprint" of a picture (difference hash), used to recognise dialog pages by how they
/// look instead of by OCR'd text. Tolerant to small changes (compression, a few moving pixels),
/// different for different text.
enum ImageFingerprint {
    /// 32×16 comparisons = 512 bits. Wide, because dialog text runs in horizontal lines.
    static let columns = 32
    static let rows = 16

    /// Distance at or below this = same page.
    static let matchThreshold = 0.12
    /// The best page must beat the runner-up by this much, otherwise it's ambiguous.
    static let requiredMargin = 0.05

    /// - Parameter crop: area of `image` to fingerprint, in the image's pixels (top-left origin).
    static func make(_ image: CGImage, crop: CGRect) -> Data? {
        let bounds = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        let r = crop.integral.intersection(bounds)
        guard !r.isNull, r.width >= 8, r.height >= 8, let cropped = image.cropping(to: r) else { return nil }

        let w = columns + 1, h = rows
        var pixels = [UInt8](repeating: 0, count: w * h)
        let drawn = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let ctx = CGContext(data: buffer.baseAddress, width: w, height: h, bitsPerComponent: 8,
                                      bytesPerRow: w, space: CGColorSpaceCreateDeviceGray(),
                                      bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return false }
            ctx.interpolationQuality = .medium
            ctx.draw(cropped, in: CGRect(x: 0, y: 0, width: w, height: h))
            return true
        }
        guard drawn else { return nil }

        var bytes = [UInt8](repeating: 0, count: columns * rows / 8)
        for y in 0..<h {
            for x in 0..<columns where pixels[y * w + x] > pixels[y * w + x + 1] {
                let bit = y * columns + x
                bytes[bit / 8] |= UInt8(1 << (bit % 8))
            }
        }
        return Data(bytes)
    }

    /// 0 = identical, 1 = completely different.
    static func distance(_ a: Data, _ b: Data) -> Double {
        guard a.count == b.count, !a.isEmpty else { return 1 }
        var diff = 0
        for (x, y) in zip(a, b) { diff += (x ^ y).nonzeroBitCount }
        return Double(diff) / Double(a.count * 8)
    }
}
