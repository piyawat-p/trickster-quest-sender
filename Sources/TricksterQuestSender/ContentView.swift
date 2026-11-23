import SwiftUI

// The window is split into small views. With @Observable, each view only redraws when
// the values it actually reads change (e.g. status updates while running only redraw StatusBar).

struct ContentView: View {
    @State private var alwaysOnTop = true
    @State private var hostWindow: NSWindow?

    var body: some View {
        VStack(spacing: 0) {
            BrowserTabBar()
            SettingsPane(alwaysOnTop: $alwaysOnTop)
                .padding()
                .background(Color(nsColor: .windowBackgroundColor))
        }
        .frame(minWidth: 900)
        .background(WindowAccessor { window in
            hostWindow = window
            applyWindowLevel()
        })
        .onChange(of: alwaysOnTop) { _, _ in applyWindowLevel() }
    }

    /// Only our own window floats. (Setting every NSApp window could float hidden helper windows.)
    private func applyWindowLevel() {
        guard let window = hostWindow else { return }
        let noFloat = ProcessInfo.processInfo.environment["TQS_NO_FLOAT"] != nil
        window.level = (alwaysOnTop && !noFloat) ? .floating : .normal
        debugLog("window level -> \(window.level.rawValue)")
    }
}

/// Gives SwiftUI access to the NSWindow that hosts the view.
struct WindowAccessor: NSViewRepresentable {
    let onWindow: (NSWindow) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async {
            if let window = view.window { onWindow(window) }
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}
}

struct SettingsPane: View {
    @Environment(QuestRunner.self) private var runner
    @Binding var alwaysOnTop: Bool

    var body: some View {
        @Bindable var runner = runner
        let busy = runner.isRunning || runner.capturingStepID != nil || runner.isRecording || runner.isAnalyzing

        VStack(alignment: .leading, spacing: 10) {
            PermissionBanner()

            RecordingBar()

            Text("ขั้นตอนใน 1 รอบ").font(.headline)

            if runner.config.steps.isEmpty {
                if runner.isRecording || runner.isAnalyzing {
                    Text("กำลังบันทึก… เล่นในเกม 1 รอบ: คลิก NPC → กด 1 จนจบ → คลิกพื้นที่ว่างปิดเควส → กด F8")
                        .foregroundStyle(.secondary)
                } else {
                    EmptyTabChoices()
                }
            }

            ScrollView {
                VStack(spacing: 6) {
                    ForEach($runner.config.steps) { $step in
                        StepRow(step: $step, number: runner.number(of: step.id), busy: busy)
                    }
                }
            }
            .frame(minHeight: 320)

            HStack {
                Button("+ เพิ่มขั้นตอน") { debugLog("tap add step"); runner.addStep() }.disabled(busy)
                Spacer()
                TestButtons(busy: busy)
            }

            OCRResultView()

            Divider()

            HStack(spacing: 16) {
                Toggle("วนไม่จำกัด (หยุดเมื่อเช็กข้อความไม่ผ่าน)", isOn: $runner.config.unlimited)
                Stepper("จำนวนรอบ: \(runner.config.loops)", value: $runner.config.loops, in: 1...9999)
                    .disabled(runner.config.unlimited)
            }
            HStack(spacing: 16) {
                Stepper("สุ่มดีเลย์เพิ่ม 0–\(runner.config.jitterMs) ms", value: $runner.config.jitterMs, in: 0...2000, step: 50)
                Stepper("รอก่อนเริ่ม \(runner.config.startDelayMs) ms", value: $runner.config.startDelayMs, in: 0...5000, step: 100)
            }
            HStack(spacing: 16) {
                Toggle("หยุดถ้าผมขยับเมาส์", isOn: $runner.config.stopIfMouseMoved)
                Toggle("หน้าต่างลอยบนสุด", isOn: $alwaysOnTop)
                Toggle("บันทึกเสร็จแล้วรันต่อเลย", isOn: $runner.autoRunAfterRecording)
            }
            .disabled(runner.isRunning)

            Divider()

            StatusBar()

            Text("วิธีใช้: เดินไปยืนใกล้ Rookie G / Part-Timer G ให้เห็นป้ายชื่อบนจอ → กด F8 (หรือ fn+F8) หรือปุ่มเริ่ม → กด Esc เมื่ออยากหยุด")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

struct StatusBar: View {
    @Environment(QuestRunner.self) private var runner

    var body: some View {
        HStack {
            Button(runner.isRunning ? "หยุด (Esc / F8)" : "เริ่ม") {
                runner.isRunning ? runner.stop() : runner.start(fromHotkey: false)
            }
            .disabled(runner.capturingStepID != nil || runner.isRecording || runner.isAnalyzing)

            Text(runner.status)
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer()
            if runner.isRunning || runner.completedCount > 0 {
                Text("ส่งสำเร็จ \(runner.completedCount) รอบ").monospacedDigit()
            }
        }
    }
}

/// First thing an empty tab shows: build the steps yourself, or record one round.
struct EmptyTabChoices: View {
    @Environment(QuestRunner.self) private var runner

    var body: some View {
        HStack(spacing: 16) {
            choice(icon: "plus.square", title: "เพิ่มขั้นตอนใหม่",
                   detail: "ตั้งขั้นตอนเองทีละขั้น") { runner.addStep() }
            choice(icon: "record.circle", title: "บันทึกการเล่น",
                   detail: "เล่นในเกมเอง 1 รอบ แอปจำและสร้างขั้นตอนให้") { runner.startRecording() }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 24)
        .disabled(runner.isRunning)
    }

    private func choice(icon: String, title: String, detail: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 8) {
                Image(systemName: icon).font(.system(size: 28))
                Text(title).font(.headline)
                Text(detail).font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
            }
            .frame(width: 220, height: 120)
            .contentShape(Rectangle())
        }
        .buttonStyle(.bordered)
    }
}

/// Recording controls for the active tab.
struct RecordingBar: View {
    @Environment(QuestRunner.self) private var runner

    var body: some View {
        if runner.isRecording {
            HStack {
                Image(systemName: "record.circle.fill").foregroundStyle(.red)
                Text("กำลังบันทึกการเล่น \(runner.recordedCount) การกระทำ")
                Spacer()
                Button("■ หยุดบันทึก (F8)") { runner.stopRecording() }
                    .tint(.red)
                Button("ยกเลิก") { runner.cancelRecording() }
            }
            .padding(8)
            .background(RoundedRectangle(cornerRadius: 8).fill(Color.red.opacity(0.08)))
        } else if runner.isAnalyzing {
            HStack {
                ProgressView().controlSize(.small)
                Text("กำลังวิเคราะห์การเล่น…")
                Spacer()
            }
            .padding(8)
        } else if !runner.config.steps.isEmpty {
            HStack {
                Image(systemName: "record.circle").foregroundStyle(.red.opacity(0.8))
                Text(runner.activeTabIsRecorded ? "แท็บนี้มาจากการบันทึกการเล่น" : "ขั้นตอนในแท็บนี้ตั้งเอง")
                    .foregroundStyle(.secondary)
                Spacer()
                Button("● เริ่มบันทึกการเล่นใหม่") { runner.startRecording() }
                    .disabled(runner.isRunning)
                    .help("เล่นเองอีก 1 รอบ แล้วแทนที่ขั้นตอนเดิมของแท็บนี้ (⌘⇧R) — ของเดิมถูกแทนที่เมื่อบันทึกใหม่สำเร็จเท่านั้น")
            }
            .padding(8)
            .background(RoundedRectangle(cornerRadius: 8).fill(Color.secondary.opacity(0.08)))
        }
    }
}

struct TestButtons: View {
    @Environment(QuestRunner.self) private var runner
    let busy: Bool

    var body: some View {
        Button(runner.npcTesting ? "กำลังหา..." : "ทดสอบหา NPC") { runner.testFindNPC() }
            .disabled(busy || runner.npcTesting)
        Button(runner.ocrTesting ? "กำลังอ่าน..." : "ทดสอบอ่านข้อความบนจอ") { runner.testOCR() }
            .disabled(busy || runner.ocrTesting)
    }
}

struct OCRResultView: View {
    @Environment(QuestRunner.self) private var runner

    var body: some View {
        if !runner.ocrResult.isEmpty {
            GroupBox("ข้อความที่อ่านได้จากจอ (เอาไปตั้งเป็นคำค้นได้)") {
                ScrollView {
                    Text(runner.ocrResult)
                        .font(.caption.monospaced())
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(height: 110)
            }
        }
    }
}

struct PermissionBanner: View {
    @Environment(QuestRunner.self) private var runner

    var body: some View {
        if !runner.trusted || !runner.screenAccess || !runner.thaiOCR {
            GroupBox {
                VStack(alignment: .leading, spacing: 6) {
                    if !runner.trusted {
                        HStack {
                            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                            Text("ยังไม่ได้อนุญาต Accessibility (ใช้คลิก กดปุ่ม และปุ่มลัด F8/Esc)")
                            Spacer()
                            Button("ขอสิทธิ์") { runner.requestTrust() }
                        }
                    }
                    if !runner.screenAccess {
                        HStack {
                            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                            Text("ยังไม่ได้อนุญาต Screen Recording (ใช้อ่านข้อความบนจอ)")
                            Spacer()
                            Button("ขอสิทธิ์") { runner.requestScreenAccess() }
                        }
                    }
                    if !runner.thaiOCR {
                        Text("macOS เครื่องนี้อ่านภาษาไทยจากจอไม่ได้ ให้ใช้คำภาษาอังกฤษในขั้นตอนเช็กข้อความ หรือปิดขั้นตอนนั้นแล้วใช้จำนวนรอบแทน")
                            .font(.caption)
                    }
                    HStack {
                        Spacer()
                        Button("ตรวจสิทธิ์ใหม่") { runner.refreshPermissions() }
                    }
                }
            }
        }
    }
}

struct StepRow: View {
    @Environment(QuestRunner.self) private var runner
    @Binding var step: QuestStep
    let number: Int
    let busy: Bool

    var body: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Toggle("", isOn: $step.enabled).labelsHidden()
                    Text("\(number).").monospacedDigit().frame(width: 24, alignment: .trailing)
                    Picker("", selection: $step.kind) {
                        ForEach(StepKind.allCases) { kind in
                            Text(kind.label).tag(kind)
                        }
                    }
                    .labelsHidden()
                    .frame(width: 190)
                    TextField("ชื่อขั้นตอน", text: $step.name)
                    Button { runner.move(step.id, by: -1) } label: { Image(systemName: "arrow.up") }
                    Button { runner.move(step.id, by: 1) } label: { Image(systemName: "arrow.down") }
                    Button(role: .destructive) { runner.removeStep(step.id) } label: { Image(systemName: "trash") }
                }
                HStack {
                    details
                    Spacer()
                    Stepper("รอหลังทำ \(step.delayAfterMs) ms", value: $step.delayAfterMs, in: 0...10_000, step: 100)
                        .fixedSize()
                }
            }
            .disabled(busy)
        }
        .opacity(step.enabled ? 1 : 0.5)
    }

    @ViewBuilder
    private var details: some View {
        switch step.kind {
        case .clickNPC:
            TextField("ชื่อ NPC คั่นด้วย ,", text: $step.text)
                .frame(width: 180)
            Stepper("จุดคลิกห่างจากชื่อ \(Int(step.npcOffsetY)) pt", value: $step.npcOffsetY, in: -150...50, step: 5)
                .fixedSize()
                .help("ค่าติดลบ = คลิกเหนือป้ายชื่อ (ตรงตัว NPC)")
            Stepper("รอสูงสุด \(step.timeoutMs) ms", value: $step.timeoutMs, in: 500...15_000, step: 500)
                .fixedSize()
        case .dialogUntilEnd:
            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text("ปุ่ม")
                    Picker("", selection: $step.key) {
                        ForEach(KeyMap.names, id: \.self) { name in
                            Text(name).tag(name)
                        }
                    }
                    .labelsHidden()
                    .frame(width: 80)
                    TextField("ต้องเจอข้อความนี้ระหว่างคุย (เว้นว่างได้)", text: $step.text)
                        .frame(width: 230)
                        .help("ถ้าบทสนทนาจบโดยไม่เคยเห็นข้อความนี้ = ส่งเควสไม่ได้ → หยุด")
                    TextField("หยุดทันทีถ้าเจอ (เว้นว่างได้)", text: $step.stopText)
                        .frame(width: 180)
                }
                HStack {
                    TextField("หน้าสุดท้าย: กดหน้านี้แล้วถือว่าจบรอบ (เว้นว่าง = จบเมื่อบทสนทนาปิด)", text: $step.endText)
                        .frame(width: 420)
                    if !step.knownPages.isEmpty {
                        let withImages = step.knownPageHashes.compactMap { $0 }.count
                        Text("จำไว้ \(step.knownPages.count) หน้า" + (withImages > 0 ? " · มีภาพ \(withImages) หน้า" : ""))
                            .foregroundStyle(.secondary)
                            .help(step.knownPages.joined(separator: "\n")
                                  + "\n\nตัดสินจากภาพก่อน ข้อความเป็นตัวสำรอง · เจอหน้าที่ไม่อยู่ในนี้ = ข้อความเปลี่ยน → หยุด")
                        Button("ล้าง") {
                            step.pages = ""
                            step.pageHashes = ""
                        }
                        .help("ลบหน้าที่บันทึกไว้ (ไม่เช็กหน้าที่ไม่รู้จัก)")
                    }
                }
                HStack {
                    Stepper("รอบทสนทนาขึ้น \(step.timeoutMs) ms", value: $step.timeoutMs, in: 500...15_000, step: 500)
                        .fixedSize()
                    Stepper("หน้าค้างนานกว่า \(step.retryMs) ms กดซ้ำ", value: $step.retryMs, in: 500...10_000, step: 250)
                        .fixedSize()
                    Stepper("กดไม่เกิน \(step.maxPresses) ครั้ง", value: $step.maxPresses, in: 1...50)
                        .fixedSize()
                }
            }
        case .clickStartMouse:
            Text("คลิกตรงที่เมาส์ชี้อยู่ตอนกด F8 (ให้ชี้ที่ NPC)")
                .foregroundStyle(.secondary)
        case .clickPoint:
            Text(step.point.map { "(\(Int($0.x)), \(Int($0.y)))" } ?? "ยังไม่ตั้ง")
                .monospacedDigit()
                .foregroundStyle(step.point == nil ? .red : .primary)
                .frame(width: 100, alignment: .leading)
            Button(runner.capturingStepID == step.id ? "กำลังจับ..." : "จับตำแหน่ง") {
                runner.capture(stepID: step.id)
            }
        case .key:
            Text("ปุ่ม")
            Picker("", selection: $step.key) {
                ForEach(KeyMap.names, id: \.self) { name in
                    Text(name).tag(name)
                }
            }
            .labelsHidden()
            .frame(width: 90)
            TextField("รอให้เจอข้อความนี้ก่อนกด (เว้นว่าง = กดเลย)", text: $step.text)
                .frame(width: 260)
                .help("ถ้าใส่ไว้: รอจนเห็นข้อความนี้ → กด → รอจนข้อความหายไป (เปลี่ยนหน้า) ถ้าไม่เปลี่ยนจะกดซ้ำ")
            if !step.text.isEmpty {
                Stepper("รอสูงสุด \(step.timeoutMs) ms", value: $step.timeoutMs, in: 500...15_000, step: 500)
                    .fixedSize()
            }
        case .waitText, .clickText:
            TextField("ข้อความที่ต้องเจอบนจอ", text: $step.text)
                .frame(width: 200)
            Stepper("รอสูงสุด \(step.timeoutMs) ms", value: $step.timeoutMs, in: 500...15_000, step: 500)
                .fixedSize()
        }
    }
}
