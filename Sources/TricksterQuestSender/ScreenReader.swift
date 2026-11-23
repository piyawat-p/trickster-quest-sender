import Foundation
import CoreGraphics
import ScreenCaptureKit
import Vision

struct TextHit {
    let text: String
    /// Global screen coordinates (top-left origin, points).
    let rect: CGRect
    var center: CGPoint { CGPoint(x: rect.midX, y: rect.midY) }
}

/// OCR result together with the screenshot it came from (for image fingerprints).
struct ScreenText {
    let hits: [TextHit]
    let image: CGImage
    /// Screen area the image covers (global points).
    let area: CGRect

    /// Converts a screen rect (points) to this image's pixels.
    func pixelRect(for r: CGRect) -> CGRect {
        let sx = CGFloat(image.width) / area.width
        let sy = CGFloat(image.height) / area.height
        return CGRect(x: (r.minX - area.minX) * sx, y: (r.minY - area.minY) * sy,
                      width: r.width * sx, height: r.height * sy)
    }
}

struct DisplayText {
    let bounds: CGRect
    let hits: [TextHit]
}

enum ScreenReaderError: LocalizedError {
    case noDisplay
    var errorDescription: String? { "ไม่พบหน้าจอ" }
}

/// Takes screenshots (excluding this app's own window) and reads text with Apple's Vision OCR.
enum ScreenReader {
    static var hasScreenPermission: Bool { CGPreflightScreenCaptureAccess() }

    static func requestScreenPermission() {
        _ = CGRequestScreenCaptureAccess()
    }

    static var thaiSupported: Bool {
        let request = VNRecognizeTextRequest()
        request.revision = VNRecognizeTextRequestRevision3
        request.recognitionLevel = .accurate
        let langs = (try? request.supportedRecognitionLanguages()) ?? []
        return langs.contains { $0.hasPrefix("th") }
    }

    /// Reads the display that contains `point`.
    static func readScreen(near point: CGPoint) async throws -> [TextHit] {
        let content = try await shareableContent()
        guard let display = content.displays.first(where: { CGDisplayBounds($0.displayID).contains(point) })
                ?? content.displays.first else {
            throw ScreenReaderError.noDisplay
        }
        return try await read(display, content: content)
    }

    /// Reads only `rect` (global coordinates). Much faster than the whole screen.
    static func readRegion(_ rect: CGRect) async throws -> [TextHit] {
        let content = try await shareableContent()
        let mid = CGPoint(x: rect.midX, y: rect.midY)
        guard let display = content.displays.first(where: { CGDisplayBounds($0.displayID).contains(mid) })
                ?? content.displays.first else {
            throw ScreenReaderError.noDisplay
        }
        let bounds = CGDisplayBounds(display.displayID)
        let r = rect.intersection(bounds)
        guard !r.isNull, r.width > 10, r.height > 10 else { return [] }
        return try await read(display, content: content, region: r)
    }

    /// Finds the dialog option line for `key`, e.g. "[1] ทำ [เควส]". OCR sometimes reads 1 as l or I.
    static func findDialogOption(key: String, in hits: [TextHit]) -> TextHit? {
        let digit: String
        switch key {
        case "1": digit = "[1lI|]"
        case "Enter", "Space": digit = "[0-9lI|]"
        default: digit = NSRegularExpression.escapedPattern(for: key)
        }
        let pattern = "^\\s*[\\[\\(\\{|]\\s*" + digit + "\\s*[\\]\\)\\}|]"
        return hits.first { $0.text.range(of: pattern, options: .regularExpression) != nil }
    }

    /// Like readScreen, but also returns the screenshot.
    static func readScreenDetailed(near point: CGPoint) async throws -> ScreenText {
        let content = try await shareableContent()
        guard let display = content.displays.first(where: { CGDisplayBounds($0.displayID).contains(point) })
                ?? content.displays.first else {
            throw ScreenReaderError.noDisplay
        }
        let (image, area) = try await capture(display, content: content, region: nil)
        return ScreenText(hits: try await recognize(image, displayBounds: area), image: image, area: area)
    }

    /// Like readRegion, but also returns the screenshot. Nil if the region is off-screen.
    static func readRegionDetailed(_ rect: CGRect) async throws -> ScreenText? {
        let content = try await shareableContent()
        let mid = CGPoint(x: rect.midX, y: rect.midY)
        guard let display = content.displays.first(where: { CGDisplayBounds($0.displayID).contains(mid) })
                ?? content.displays.first else {
            throw ScreenReaderError.noDisplay
        }
        let r = rect.intersection(CGDisplayBounds(display.displayID))
        guard !r.isNull, r.width > 10, r.height > 10 else { return nil }
        let (image, area) = try await capture(display, content: content, region: r)
        return ScreenText(hits: try await recognize(image, displayBounds: area), image: image, area: area)
    }

    /// The part of a dialog that identifies a page: the text above the option line and the options.
    /// Anchored on the "[1] ..." line, so it's the same box when recording and when running.
    static func pageRect(around option: CGRect) -> CGRect {
        CGRect(x: option.minX - 40, y: option.minY - 110, width: 380, height: 220)
    }

    /// Reads every connected display.
    static func readAllDisplays() async throws -> [DisplayText] {
        let content = try await shareableContent()
        guard !content.displays.isEmpty else { throw ScreenReaderError.noDisplay }
        var result: [DisplayText] = []
        for display in content.displays {
            let hits = try await read(display, content: content)
            result.append(DisplayText(bounds: CGDisplayBounds(display.displayID), hits: hits))
        }
        return result
    }

    static func find(_ keyword: String, in hits: [TextHit]) -> TextHit? {
        let needle = normalize(keyword)
        guard !needle.isEmpty else { return nil }
        return hits.first { normalize($0.text).contains(needle) }
    }

    /// Finds an NPC name label. Only short text lines that are (almost) exactly the name count,
    /// so chat lines that mention the name are ignored. With several matches, picks the one
    /// closest to `prefer` (last known NPC position) or else closest to the screen centre (the player).
    /// When `prefer` is given, matches farther than `maxDistance` are ignored (NPCs don't move, so a far
    /// match is probably the name in the dialog title, not the NPC).
    static func findNPC(_ names: [String], in screens: [DisplayText], prefer: CGPoint?,
                        maxDistance: CGFloat = 150) -> TextHit? {
        let targets = names.map(compact).filter { !$0.isEmpty }
        guard !targets.isEmpty else { return nil }

        var best: (hit: TextHit, distance: CGFloat)?
        for screen in screens {
            let centre = CGPoint(x: screen.bounds.midX, y: screen.bounds.midY)
            for hit in screen.hits {
                let t = compact(hit.text)
                let matches = targets.contains { t == $0 || (t.contains($0) && t.count <= $0.count + 3) }
                guard matches else { continue }
                let ref = prefer ?? centre
                let d = hypot(hit.center.x - ref.x, hit.center.y - ref.y)
                if prefer != nil && d > maxDistance { continue }
                if best == nil || d < best!.distance {
                    best = (hit, d)
                }
            }
        }
        return best?.hit
    }

    /// OCR often adds/drops spaces in Thai, so compare without whitespace.
    static func normalize(_ s: String) -> String {
        s.lowercased().filter { !$0.isWhitespace }
    }

    /// Letters and digits only: "Part-Timer G" -> "parttimerg".
    static func compact(_ s: String) -> String {
        s.lowercased().filter { $0.isLetter || $0.isNumber }
    }


    private static func shareableContent() async throws -> SCShareableContent {
        try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
    }

    // MARK: - Capture without OCR (used by the recorder)

    struct CaptureTarget {
        fileprivate let display: SCDisplay
        fileprivate let content: SCShareableContent
    }

    /// Looks up the display once, so repeated captures are cheap.
    static func captureTarget(near point: CGPoint) async throws -> CaptureTarget {
        let content = try await shareableContent()
        guard let display = content.displays.first(where: { CGDisplayBounds($0.displayID).contains(point) })
                ?? content.displays.first else {
            throw ScreenReaderError.noDisplay
        }
        return CaptureTarget(display: display, content: content)
    }

    static func captureFrame(_ target: CaptureTarget) async throws -> (CGImage, CGRect) {
        try await capture(target.display, content: target.content, region: nil)
    }

    static func recognizeText(in image: CGImage, bounds: CGRect) async throws -> [TextHit] {
        try await recognize(image, displayBounds: bounds)
    }

    // MARK: - Fuzzy matching (OCR output varies a little between screenshots)

    /// Exact "contains" first, then the line most similar to `keyword` if similar enough.
    static func findFuzzy(_ keyword: String, in hits: [TextHit], threshold: Double = 0.75) -> TextHit? {
        let needle = normalize(keyword)
        guard !needle.isEmpty else { return nil }
        if let exact = hits.first(where: { normalize($0.text).contains(needle) }) { return exact }
        var best: (TextHit, Double)?
        for hit in hits {
            let score = similarity(needle, normalize(hit.text))
            if score >= threshold, score > (best?.1 ?? 0) { best = (hit, score) }
        }
        return best?.0
    }

    /// 1 − (edit distance / longer length).
    static func similarity(_ a: String, _ b: String) -> Double {
        let x = Array(a), y = Array(b)
        if x.isEmpty || y.isEmpty { return 0 }
        var prev = Array(0...y.count)
        var cur = [Int](repeating: 0, count: y.count + 1)
        for i in 1...x.count {
            cur[0] = i
            for j in 1...y.count {
                cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1))
            }
            swap(&prev, &cur)
        }
        return 1 - Double(prev[y.count]) / Double(max(x.count, y.count))
    }

    // MARK: - Private

    /// `region`: part of the display to read, in global coordinates (nil = whole display).
    private static func read(_ display: SCDisplay, content: SCShareableContent,
                             region: CGRect? = nil) async throws -> [TextHit] {
        let (image, area) = try await capture(display, content: content, region: region)
        return try await recognize(image, displayBounds: area)
    }

    private static func capture(_ display: SCDisplay, content: SCShareableContent,
                                region: CGRect?) async throws -> (CGImage, CGRect) {
        let bounds = CGDisplayBounds(display.displayID)
        let area = region ?? bounds
        // Exclude our own window, otherwise the keywords typed in the app would be "found".
        let pid = ProcessInfo.processInfo.processIdentifier
        let ownApps = content.applications.filter { $0.processID == pid }
        let filter = SCContentFilter(display: display, excludingApplications: ownApps, exceptingWindows: [])

        var scale: CGFloat = 2
        if let mode = CGDisplayCopyDisplayMode(display.displayID), mode.width > 0 {
            scale = CGFloat(mode.pixelWidth) / CGFloat(mode.width)
        }
        let config = SCStreamConfiguration()
        if region != nil {
            config.sourceRect = area.offsetBy(dx: -bounds.minX, dy: -bounds.minY) // display-local points
        }
        config.width = Int(area.width * scale)
        config.height = Int(area.height * scale)
        config.showsCursor = false

        let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
        return (image, area)
    }

    private static func recognize(_ image: CGImage, displayBounds b: CGRect) async throws -> [TextHit] {
        try await Task.detached(priority: .userInitiated) {
            let request = VNRecognizeTextRequest()
            request.revision = VNRecognizeTextRequestRevision3
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = false
            let supported = (try? request.supportedRecognitionLanguages()) ?? []
            let wanted = ["th-TH", "en-US"].filter { supported.contains($0) }
            if !wanted.isEmpty { request.recognitionLanguages = wanted }

            try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])

            return (request.results ?? []).compactMap { obs -> TextHit? in
                guard let top = obs.topCandidates(1).first else { return nil }
                let bb = obs.boundingBox // normalized, bottom-left origin
                let rect = CGRect(
                    x: b.minX + bb.minX * b.width,
                    y: b.minY + (1 - bb.maxY) * b.height,
                    width: bb.width * b.width,
                    height: bb.height * b.height
                )
                return TextHit(text: top.string, rect: rect)
            }
        }.value
    }
}
