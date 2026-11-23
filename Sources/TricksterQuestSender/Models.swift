import Foundation
import CoreGraphics

enum StepKind: String, Codable, CaseIterable, Identifiable {
    /// Find the NPC by the name label under it (OCR) and click on the NPC.
    case clickNPC
    /// Keep pressing the option key whenever a new dialog page appears, until the dialog closes.
    case dialogUntilEnd
    /// Click wherever the mouse was when F8 was pressed.
    case clickStartMouse
    /// Click a fixed screen position captured earlier.
    case clickPoint
    /// Press a keyboard key (e.g. "1" to choose dialog option [1]).
    case key
    /// Read the screen until the text appears; stop the whole run if it never does.
    case waitText
    /// Read the screen, find the text and click on it; stop the run if not found.
    case clickText

    var id: String { rawValue }

    var label: String {
        switch self {
        case .clickNPC: return "หา NPC แล้วคลิก"
        case .dialogUntilEnd: return "กดตัวเลือกจนบทสนทนาจบ"
        case .clickStartMouse: return "คลิกตรงเมาส์ตอนเริ่ม"
        case .clickPoint: return "คลิกตำแหน่งที่จับไว้"
        case .key: return "กดปุ่มคีย์บอร์ด"
        case .waitText: return "เช็กข้อความบนจอ"
        case .clickText: return "คลิกที่ข้อความ"
        }
    }

    var usesScreenReading: Bool {
        self == .waitText || self == .clickText || self == .clickNPC || self == .dialogUntilEnd
    }
}

struct QuestStep: Identifiable, Codable, Equatable {
    static let defaultNPCNames = "Rookie G, Part-Timer G"

    var id = UUID()
    var kind: StepKind
    var name: String
    var enabled: Bool = true
    var x: Double? = nil
    var y: Double? = nil
    var key: String = "1"
    /// Keyword for waitText/clickText, comma-separated NPC names for clickNPC,
    /// (optional) text that must appear at some point during the dialog for dialogUntilEnd,
    /// or (optional) for key: the dialog line to wait for before pressing.
    var text: String = ""
    var timeoutMs: Int = 3000
    var delayAfterMs: Int = 600
    /// clickNPC: how far from the name label to click (negative = above, where the sprite is).
    var npcOffsetY: Double = -35
    /// dialogUntilEnd: stop the whole run if this text shows up (optional).
    var stopText: String = ""
    /// dialogUntilEnd: press again if the page hasn't changed after this long (lost key press).
    var retryMs: Int = 2500
    /// dialogUntilEnd: safety limit of key presses per dialog.
    var maxPresses: Int = 15
    /// dialogUntilEnd: option line of the last page. Pressing it ends the round.
    var endText: String = ""
    /// dialogUntilEnd: option lines seen while recording (one per line). A page that matches none
    /// of them means the wording changed (e.g. not enough items) → stop.
    var pages: String = ""
    /// dialogUntilEnd: base64 image fingerprint of each recorded page, one per line, same order as `pages`.
    var pageHashes: String = ""

    /// Recorded page images, aligned with `knownPages` (nil where a page had no image).
    var knownPageHashes: [Data?] {
        let lines = pageHashes.split(separator: "\n", omittingEmptySubsequences: false)
        let hashes = lines.map { $0.isEmpty ? nil : Data(base64Encoded: String($0)) }
        let count = knownPages.count
        return Array(hashes.prefix(count)) + Array(repeating: nil, count: max(0, count - hashes.count))
    }

    var knownPages: [String] {
        pages.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    var point: CGPoint? {
        guard let x, let y else { return nil }
        return CGPoint(x: x, y: y)
    }

    var npcNames: [String] {
        text.split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
    }

    enum CodingKeys: String, CodingKey {
        case id, kind, name, enabled, x, y, key, text, timeoutMs, delayAfterMs, npcOffsetY
        case stopText, retryMs, maxPresses, endText, pages, pageHashes
    }

    static func npcStep(name: String = "หาแล้วคลิก NPC", delayAfterMs: Int = 800) -> QuestStep {
        QuestStep(kind: .clickNPC, name: name, text: defaultNPCNames, timeoutMs: 3000, delayAfterMs: delayAfterMs)
    }
}

// Tolerant decoding so settings saved by older versions still load (missing keys use defaults).
extension QuestStep {
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        kind = try c.decode(StepKind.self, forKey: .kind)
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? kind.label
        enabled = try c.decodeIfPresent(Bool.self, forKey: .enabled) ?? true
        x = try c.decodeIfPresent(Double.self, forKey: .x)
        y = try c.decodeIfPresent(Double.self, forKey: .y)
        key = try c.decodeIfPresent(String.self, forKey: .key) ?? "1"
        text = try c.decodeIfPresent(String.self, forKey: .text) ?? ""
        timeoutMs = try c.decodeIfPresent(Int.self, forKey: .timeoutMs) ?? 3000
        delayAfterMs = try c.decodeIfPresent(Int.self, forKey: .delayAfterMs) ?? 600
        npcOffsetY = try c.decodeIfPresent(Double.self, forKey: .npcOffsetY) ?? -35
        stopText = try c.decodeIfPresent(String.self, forKey: .stopText) ?? ""
        retryMs = try c.decodeIfPresent(Int.self, forKey: .retryMs) ?? 2500
        maxPresses = try c.decodeIfPresent(Int.self, forKey: .maxPresses) ?? 15
        endText = try c.decodeIfPresent(String.self, forKey: .endText) ?? ""
        pages = try c.decodeIfPresent(String.self, forKey: .pages) ?? ""
        pageHashes = try c.decodeIfPresent(String.self, forKey: .pageHashes) ?? ""
    }
}

enum KeyMap {
    static let names = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "Enter", "Space"]
    static let codes: [String: CGKeyCode] = [
        "1": 18, "2": 19, "3": 20, "4": 21, "5": 23, "6": 22, "7": 26, "8": 28, "9": 25, "0": 29,
        "Enter": 36, "Space": 49,
    ]

    static func name(for keyCode: UInt16) -> String? {
        codes.first { $0.value == CGKeyCode(keyCode) }?.key
    }
}

struct QuestConfig: Codable, Equatable {
    var steps: [QuestStep]
    var loops: Int
    var unlimited: Bool
    var jitterMs: Int
    var startDelayMs: Int
    var stopIfMouseMoved: Bool

    enum CodingKeys: String, CodingKey {
        case steps, loops, unlimited, jitterMs, startDelayMs, stopIfMouseMoved
    }

    /// Main flow: find the NPC by name → press 1 on every new dialog page until the last page
    /// → click empty space to close the quest window → repeat.
    static let `default` = QuestConfig(
        steps: [
            QuestStep.npcStep(name: "หา NPC ด้วยชื่อแล้วคลิก", delayAfterMs: 300),
            { () -> QuestStep in
                var s = QuestStep(kind: .dialogUntilEnd, name: "กด 1 จนจบบทสนทนา", key: "1",
                                  timeoutMs: 3000, delayAfterMs: 300)
                // "[1] เข้าใจแล้วจะช่วยหามาให้" = accept the quest again = last page of the round.
                s.endText = "เข้าใจแล้ว"
                return s
            }(),
            QuestStep(kind: .clickPoint, name: "คลิกพื้นที่ว่างเพื่อปิดเควส (กด “จับตำแหน่ง” ก่อนใช้ครั้งแรก)",
                      delayAfterMs: 500),
        ],
        loops: 10,
        unlimited: true,
        jitterMs: 150,
        startDelayMs: 300,
        stopIfMouseMoved: true
    )
}

extension QuestConfig {
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = QuestConfig.default
        steps = try c.decodeIfPresent([QuestStep].self, forKey: .steps) ?? d.steps
        loops = try c.decodeIfPresent(Int.self, forKey: .loops) ?? d.loops
        unlimited = try c.decodeIfPresent(Bool.self, forKey: .unlimited) ?? d.unlimited
        jitterMs = try c.decodeIfPresent(Int.self, forKey: .jitterMs) ?? d.jitterMs
        startDelayMs = try c.decodeIfPresent(Int.self, forKey: .startDelayMs) ?? d.startDelayMs
        stopIfMouseMoved = try c.decodeIfPresent(Bool.self, forKey: .stopIfMouseMoved) ?? d.stopIfMouseMoved
    }

    /// Replaces "click where the mouse was" with automatic NPC search, keeping the user's other settings.
    mutating func migrateToNPCSearch() {
        steps = steps.map { step in
            guard step.kind == .clickStartMouse else { return step }
            var s = step
            s.kind = .clickNPC
            s.text = QuestStep.defaultNPCNames
            s.timeoutMs = 3000
            return s
        }
    }
}

// MARK: - Presets

struct Preset: Identifiable, Codable, Equatable {
    var id = UUID()
    var name: String
    var config: QuestConfig
    var isBuiltIn = false
    /// Made by "บันทึกการเล่น": the tab shows a "record again" button that replaces its steps.
    var isRecorded = false

    enum CodingKeys: String, CodingKey {
        case id, name, config, isBuiltIn, isRecorded
    }

    static let dialogModeID = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!
    static let classicID = UUID(uuidString: "00000000-0000-0000-0000-000000000002")!

    /// Shipped with the app; can be loaded but not overwritten or deleted.
    static let builtIns: [Preset] = [
        Preset(id: dialogModeID, name: "กด 1 จนบทสนทนาจบ (แนะนำ)", config: .default, isBuiltIn: true),
        Preset(id: classicID, name: "10 ขั้นตอนเดิมของผม", config: .classic, isBuiltIn: true),
    ]
}

extension Preset {
    // Tolerant decoding: tabs saved before `isRecorded` existed still load.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? "Preset"
        config = try c.decodeIfPresent(QuestConfig.self, forKey: .config) ?? .default
        isBuiltIn = try c.decodeIfPresent(Bool.self, forKey: .isBuiltIn) ?? false
        isRecorded = try c.decodeIfPresent(Bool.self, forKey: .isRecorded) ?? false
    }
}

extension QuestConfig {
    /// The user's original 10-step setup (exported 2026-11-23), with NPC search instead of
    /// "click where the mouse was".
    static let classic = QuestConfig(
        steps: [
            QuestStep.npcStep(name: "Quest G", delayAfterMs: 600),
            QuestStep(kind: .key, name: "[1] ทำ [เควส]", key: "1", delayAfterMs: 100),
            QuestStep(kind: .key, name: "[1] รวบรวม Chaos Integer", key: "1", delayAfterMs: 100),
            QuestStep(kind: .key, name: "[1] ดูเหมือนจะขาดอะไรไป", key: "1", delayAfterMs: 100),
            QuestStep(kind: .key, name: "[1] รับบัคโค้ด [Holy]", key: "1", delayAfterMs: 100),
            QuestStep(kind: .key, name: "[1] รับเควสซ้ำ", key: "1", delayAfterMs: 100),
            QuestStep(kind: .key, name: "เช็คซ้ำ", key: "1", delayAfterMs: 100),
            QuestStep(kind: .key, name: "เช็คซ้ำ", key: "1", delayAfterMs: 100),
            QuestStep(kind: .waitText, name: "เช็คก่อนจบเควส", text: "[Chaos Integer.2Byte]", timeoutMs: 500, delayAfterMs: 200),
            QuestStep.npcStep(name: "จบเควส", delayAfterMs: 100),
        ],
        loops: 10,
        unlimited: true,
        jitterMs: 150,
        startDelayMs: 300,
        stopIfMouseMoved: true
    )
}
