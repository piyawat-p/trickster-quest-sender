import AppKit
import Observation

@MainActor
@Observable
final class QuestRunner {
    private static let storageKey = "questConfig.v2"
    private static let npcMigrationKey = "migratedToNPCSearch.v1"
    private static let oldPresetsKey = "presets.v1"
    private static let tabsKey = "tabs.v1"
    private static let activeTabKey = "activeTabID.v1"

    var config: QuestConfig {
        // Saving is debounced so typing/clicking steppers stays smooth.
        didSet { schedulePersist() }
    }
    private(set) var isRunning = false
    private(set) var status = "พร้อม — ยืนใกล้ NPC แล้วกด F8 หรือปุ่มเริ่ม"
    private(set) var currentLoop = 0
    private(set) var completedCount = 0
    private(set) var capturingStepID: UUID?
    private(set) var trusted = Clicker.isTrusted
    private(set) var screenAccess = ScreenReader.hasScreenPermission
    private(set) var thaiOCR = ScreenReader.thaiSupported
    private(set) var ocrTesting = false
    var ocrResult = ""
    private(set) var npcTesting = false
    private(set) var isRecording = false
    private(set) var isAnalyzing = false
    private(set) var recordedCount = 0
    /// After a recording is analyzed, start running the new tab automatically.
    var autoRunAfterRecording = true
    @ObservationIgnored private var recorder: ActionRecorder?
    @ObservationIgnored private var autoStartTask: Task<Void, Never>?
    /// Each tab holds one preset. The selected tab's settings are the ones being edited and run.
    private(set) var tabs: [Preset] = []
    private(set) var activeTabID: UUID?

    @ObservationIgnored private var runTask: Task<Void, Never>?
    /// Where the dialog was last seen; reading just this area is much faster than the whole screen.
    @ObservationIgnored private var dialogRegion: CGRect?
    @ObservationIgnored private var hotkeys: HotkeyMonitor?
    @ObservationIgnored private var persistTask: Task<Void, Never>?
    @ObservationIgnored private var terminateObserver: NSObjectProtocol?
    /// Recently closed tabs for "reopen closed tab" (⌘⇧T).
    private var closedTabs: [(tab: Preset, index: Int)] = []

    init() {
        if let data = UserDefaults.standard.data(forKey: Self.storageKey),
           let saved = try? JSONDecoder().decode(QuestConfig.self, from: data) {
            config = saved
        } else {
            config = .default
        }
        // One-time: turn the old "click where the mouse was" step into automatic NPC search,
        // keeping everything else the user set.
        if !UserDefaults.standard.bool(forKey: Self.npcMigrationKey) {
            config.migrateToNPCSearch()
            UserDefaults.standard.set(true, forKey: Self.npcMigrationKey)
            save()
        }
        loadTabs()
        // NOTE: nothing that touches AppKit events here. This object is created while the
        // App struct is being built, *before* NSApplication has finished launching; installing
        // event monitors that early broke all mouse/keyboard input to the window.
        // See startSystemHooks(), called from applicationDidFinishLaunching.
    }

    // MARK: - System hooks (call once, after the app has launched)

    @ObservationIgnored private var hooksStarted = false

    func startSystemHooks() {
        guard !hooksStarted else { return }
        hooksStarted = true
        terminateObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.persistNow() }
        }
        if ProcessInfo.processInfo.environment["TQS_NO_HOTKEYS"] != nil {
            debugLog("hotkeys DISABLED (TQS_NO_HOTKEYS)")
        } else {
            hotkeys = HotkeyMonitor { [weak self] keyCode in
                Task { @MainActor in self?.handleKey(keyCode) }
            }
        }
        debugLog("system hooks started")
    }

    // MARK: - Controls

    /// - Parameter fromHotkey: true when started with F8 (mouse is pointing at the NPC).
    func start(fromHotkey: Bool) {
        debugLog("start fromHotkey=\(fromHotkey)")
        guard !isRunning, capturingStepID == nil, !isRecording, !isAnalyzing else { return }
        refreshPermissions()

        let steps = config.steps.filter(\.enabled)
        guard trusted else {
            status = "ต้องอนุญาต Accessibility ก่อน"
            Clicker.requestTrust()
            return
        }
        guard !steps.isEmpty else {
            status = "ยังไม่มีขั้นตอนที่เปิดใช้"
            return
        }
        if !fromHotkey, steps.contains(where: { $0.kind == .clickStartMouse }) {
            status = "ชี้เมาส์ที่ NPC แล้วกด F8 เพื่อเริ่ม"
            return
        }
        if let s = steps.first(where: { $0.kind == .clickPoint && $0.point == nil }) {
            status = "ยังไม่ได้จับตำแหน่ง: \(s.name)"
            return
        }
        if let s = steps.first(where: { $0.kind.usesScreenReading && $0.kind != .dialogUntilEnd
                                        && $0.text.trimmingCharacters(in: .whitespaces).isEmpty }) {
            status = "ยังไม่ได้ใส่ข้อความ: \(s.name)"
            return
        }
        if steps.contains(where: { $0.kind.usesScreenReading || ($0.kind == .key && !$0.text.isEmpty) })
            && !screenAccess {
            ScreenReader.requestScreenPermission()
            status = "ต้องอนุญาต Screen Recording แล้วเปิดแอปใหม่"
            return
        }

        isRunning = true
        currentLoop = 0
        completedCount = 0
        dialogRegion = nil
        let snapshot = config
        let startMouse = Clicker.currentLocation
        runTask = Task { [weak self] in
            await self?.run(snapshot, startMouse: startMouse)
        }
    }

    func stop(reason: String = "หยุดแล้ว") {
        runTask?.cancel()
        runTask = nil
        isRunning = false
        status = reason
    }

    func refreshPermissions() {
        trusted = Clicker.isTrusted
        screenAccess = ScreenReader.hasScreenPermission
    }

    func requestTrust() { Clicker.requestTrust() }
    func requestScreenAccess() { ScreenReader.requestScreenPermission() }

    /// Reads the screen once and shows what OCR sees, to help pick keywords.
    func testOCR() {
        guard !ocrTesting else { return }
        refreshPermissions()
        guard screenAccess else {
            ScreenReader.requestScreenPermission()
            status = "ต้องอนุญาต Screen Recording แล้วเปิดแอปใหม่"
            return
        }
        ocrTesting = true
        status = "กำลังอ่านข้อความบนจอ..."
        Task { [weak self] in
            do {
                let hits = try await ScreenReader.readScreen(near: Clicker.currentLocation)
                self?.ocrResult = hits.map(\.text).joined(separator: "\n")
                self?.status = "อ่านได้ \(hits.count) บรรทัด"
            } catch {
                self?.status = "อ่านจอไม่สำเร็จ: \(error.localizedDescription)"
            }
            self?.ocrTesting = false
        }
    }

    /// Looks for the NPC once and moves the mouse (without clicking) to where it would click.
    func testFindNPC() {
        guard !npcTesting else { return }
        refreshPermissions()
        guard screenAccess else {
            ScreenReader.requestScreenPermission()
            status = "ต้องอนุญาต Screen Recording แล้วเปิดแอปใหม่"
            return
        }
        let step = config.steps.first { $0.kind == .clickNPC } ?? QuestStep.npcStep()
        let names = step.npcNames
        npcTesting = true
        status = "กำลังหา \(names.joined(separator: " / "))..."
        Task { [weak self] in
            do {
                let screens = try await ScreenReader.readAllDisplays()
                if let hit = ScreenReader.findNPC(names, in: screens, prefer: nil) {
                    let p = CGPoint(x: hit.center.x, y: hit.center.y + step.npcOffsetY)
                    CGWarpMouseCursorPosition(p)
                    self?.status = "เจอ “\(hit.text)” — เลื่อนเมาส์ไปจุดที่จะคลิกแล้ว (ยังไม่คลิก)"
                } else {
                    self?.ocrResult = screens.flatMap(\.hits).map(\.text).joined(separator: "\n")
                    self?.status = "ไม่เจอ NPC บนจอ (ดูข้อความที่อ่านได้ด้านล่าง)"
                }
            } catch {
                self?.status = "อ่านจอไม่สำเร็จ: \(error.localizedDescription)"
            }
            self?.npcTesting = false
        }
    }

    // MARK: - Recording ("teach by playing one round") — always belongs to one tab

    /// Tab the current recording will fill.
    private(set) var recordingTabID: UUID?
    @ObservationIgnored private var recordingMadeNewTab = false

    /// "+" → "บันทึกการเล่น": a new tab that is filled by playing one round.
    func addRecordedTab() {
        guard !isRunning, !isRecording, !isAnalyzing else { return }
        commitActiveConfig()
        var cfg = QuestConfig.default
        cfg.steps = []
        let tab = Preset(name: "บันทึก \(tabs.count + 1)", config: cfg, isRecorded: true)
        tabs.append(tab)
        selectTab(tab.id)
        startRecording(madeNewTab: true)
    }

    /// Records into the active tab. Its steps are replaced only when the new recording succeeds.
    func startRecording(madeNewTab: Bool = false) {
        guard !isRunning, !isRecording, !isAnalyzing, let tabID = activeTabID else { return }
        refreshPermissions()
        guard trusted else {
            Clicker.requestTrust()
            status = "ต้องอนุญาต Accessibility ก่อนบันทึก"
            return
        }
        guard screenAccess else {
            ScreenReader.requestScreenPermission()
            status = "ต้องอนุญาต Screen Recording ก่อนบันทึก"
            return
        }
        autoStartTask?.cancel()
        autoStartTask = nil
        let rec = ActionRecorder()
        rec.onChange = { [weak self] count in
            self?.recordedCount = count
            self?.status = "กำลังบันทึก: \(count) การกระทำ — เล่นจนจบ 1 รอบแล้วกด F8 หรือ “หยุดบันทึก”"
        }
        recorder = rec
        recordedCount = 0
        recordingTabID = tabID
        recordingMadeNewTab = madeNewTab
        isRecording = true
        rec.start(near: Clicker.currentLocation)
        status = "กำลังบันทึก — ในเกม: คลิก NPC → กด 1 จนจบ → คลิกพื้นที่ว่างปิดเควส แล้วกด F8 หรือ “หยุดบันทึก”"
    }

    func cancelRecording() {
        _ = recorder?.stop()
        recorder = nil
        isRecording = false
        // A brand-new tab that never got a recording isn't useful: close it again.
        if recordingMadeNewTab, let id = recordingTabID,
           let tab = tabs.first(where: { $0.id == id }), tab.config.steps.isEmpty, config.steps.isEmpty {
            closeTab(id)
        }
        recordingTabID = nil
        recordingMadeNewTab = false
        status = "ยกเลิกการบันทึกแล้ว"
    }

    func stopRecording() {
        guard isRecording, let rec = recorder, let tabID = recordingTabID else { return }
        let events = rec.stop()
        recorder = nil
        isRecording = false
        recordingTabID = nil
        guard !events.isEmpty else {
            status = "ไม่ได้บันทึกอะไรเลย (คลิก/กดในหน้าต่างเกมระหว่างบันทึก) — กด “บันทึกการเล่นใหม่” เพื่อลองอีกครั้ง"
            return
        }
        isAnalyzing = true
        status = "กำลังวิเคราะห์ \(events.count) การกระทำ..."
        Task { [weak self] in
            let steps = await RecordingAnalyzer.makeSteps(from: events)
            self?.finishAnalysis(steps, tabID: tabID)
        }
    }

    private func finishAnalysis(_ steps: [QuestStep], tabID: UUID) {
        isAnalyzing = false
        guard !steps.isEmpty else {
            status = "วิเคราะห์ไม่ได้ขั้นตอน — กด “บันทึกการเล่นใหม่” แล้วลองอีกครั้ง"
            return
        }
        commitActiveConfig()
        guard let i = tabs.firstIndex(where: { $0.id == tabID }) else {
            status = "แท็บที่บันทึกถูกปิดไปแล้ว"
            return
        }
        // Replace this tab's steps with the new recording (its other settings stay).
        tabs[i].config.steps = steps
        tabs[i].isRecorded = true
        if activeTabID == tabID {
            config = tabs[i].config
        } else {
            activeTabID = tabID
            config = tabs[i].config
        }
        persistNow()
        let name = tabs[i].name

        guard autoRunAfterRecording else {
            status = "บันทึกลงแท็บ “\(name)” แล้ว (\(steps.count) ขั้นตอน) — ตรวจดูแล้วกด F8 เพื่อเริ่ม"
            return
        }
        status = "บันทึกลงแท็บ “\(name)” แล้ว — เริ่มรันใน 2 วินาที (Esc เพื่อยกเลิก)"
        autoStartTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            guard !Task.isCancelled, let self else { return }
            self.autoStartTask = nil
            self.start(fromHotkey: true)
        }
    }

    var activeTabIsRecorded: Bool { tabs.first { $0.id == activeTabID }?.isRecorded ?? false }

    // MARK: - Tabs (browser-style: each tab is a preset; the selected tab is what runs)
    //
    // While a tab is selected, its live settings are `config`; `tabs[i].config` is only
    // refreshed when switching/closing/saving, so editing doesn't redraw the tab bar.

    var canReopenTab: Bool { !closedTabs.isEmpty }

    private var activeIndex: Int? { tabs.firstIndex { $0.id == activeTabID } }

    func selectTab(_ id: UUID) {
        debugLog("selectTab \(id) (running=\(isRunning))")
        guard !isRunning, !isRecording, !isAnalyzing, id != activeTabID,
              let tab = tabs.first(where: { $0.id == id }) else { return }
        commitActiveConfig()
        activeTabID = id
        config = tab.config
        persistNow()
    }

    func selectTab(at index: Int) {
        guard tabs.indices.contains(index) else { return }
        selectTab(tabs[index].id)
    }

    func selectAdjacentTab(_ offset: Int) {
        guard let i = activeIndex, !tabs.isEmpty else { return }
        selectTab(tabs[(i + offset + tabs.count) % tabs.count].id)
    }

    /// New tab with the recommended settings (⌘T).
    /// New empty tab "untitled-N". It then offers: add steps yourself, or record a round.
    func addTab() {
        guard !isRunning, !isRecording, !isAnalyzing else { return }
        commitActiveConfig()
        var empty = QuestConfig.default
        empty.steps = []
        let tab = Preset(name: nextUntitledName(), config: empty)
        tabs.append(tab)
        selectTab(tab.id)
    }

    /// untitled-1, untitled-2, … (one more than the highest number in use).
    private func nextUntitledName() -> String {
        let used = tabs.compactMap { tab -> Int? in
            guard tab.name.hasPrefix("untitled-") else { return nil }
            return Int(tab.name.dropFirst("untitled-".count))
        }
        return "untitled-\((used.max() ?? 0) + 1)"
    }

    func duplicateTab(_ id: UUID) {
        guard !isRunning, let i = tabs.firstIndex(where: { $0.id == id }) else { return }
        commitActiveConfig()
        let copy = Preset(name: tabs[i].name + " (สำเนา)", config: tabs[i].config, isRecorded: tabs[i].isRecorded)
        tabs.insert(copy, at: i + 1)
        selectTab(copy.id)
    }

    func renameTab(_ id: UUID, to name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, let i = tabs.firstIndex(where: { $0.id == id }), tabs[i].name != trimmed else { return }
        tabs[i].name = trimmed
        persistNow()
    }

    func closeActiveTab() {
        if let id = activeTabID { closeTab(id) }
    }

    /// Closes a tab like a browser does; ⌘⇧T brings it back.
    func closeTab(_ id: UUID) {
        guard !isRunning, tabs.count > 1, let i = tabs.firstIndex(where: { $0.id == id }) else { return }
        commitActiveConfig()
        let closed = tabs.remove(at: i)
        closedTabs.append((closed, i))
        if closedTabs.count > 10 { closedTabs.removeFirst() }
        if activeTabID == id {
            let next = tabs[min(i, tabs.count - 1)]
            activeTabID = next.id
            config = next.config
        }
        persistNow()
        status = "ปิดแท็บ “\(closed.name)” แล้ว (⌘⇧T เพื่อเปิดคืน)"
    }

    func reopenClosedTab() {
        guard !isRunning, let last = closedTabs.popLast() else { return }
        commitActiveConfig()
        tabs.insert(last.tab, at: min(last.index, tabs.count))
        activeTabID = last.tab.id
        config = last.tab.config
        persistNow()
        status = "เปิดแท็บ “\(last.tab.name)” คืนแล้ว"
    }

    private func commitActiveConfig() {
        guard let i = activeIndex, tabs[i].config != config else { return }
        tabs[i].config = config
    }

    private func schedulePersist() {
        persistTask?.cancel()
        persistTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 400_000_000)
            guard !Task.isCancelled else { return }
            self?.persistNow()
        }
    }

    /// Writes the current settings and all tabs to disk.
    func persistNow() {
        persistTask?.cancel()
        persistTask = nil
        save()
        var snapshot = tabs
        if let i = activeIndex { snapshot[i].config = config }
        if let data = try? JSONEncoder().encode(snapshot) {
            UserDefaults.standard.set(data, forKey: Self.tabsKey)
        }
        UserDefaults.standard.set(activeTabID?.uuidString, forKey: Self.activeTabKey)
    }

    /// First launch with tabs: recommended + the user's 10-step setup + any old presets,
    /// plus the settings currently in use if they match none of those.
    private func loadTabs() {
        if let data = UserDefaults.standard.data(forKey: Self.tabsKey),
           let saved = try? JSONDecoder().decode([Preset].self, from: data), !saved.isEmpty {
            tabs = saved
            let savedID = UserDefaults.standard.string(forKey: Self.activeTabKey).flatMap(UUID.init(uuidString:))
            let active = saved.first { $0.id == savedID } ?? saved[0]
            activeTabID = active.id
            config = active.config
            return
        }

        var seeded = [
            Preset(name: "กดจนบทสนทนาจบ", config: .default),
            Preset(name: "10 ขั้นตอนเดิม", config: .classic),
        ]
        if let data = UserDefaults.standard.data(forKey: Self.oldPresetsKey),
           let old = try? JSONDecoder().decode([Preset].self, from: data) {
            seeded += old.map { var p = $0; p.isBuiltIn = false; return p }
        }
        if let match = seeded.first(where: { $0.config == config }) {
            activeTabID = match.id
        } else {
            let current = Preset(name: "ค่าที่ใช้อยู่", config: config)
            seeded.insert(current, at: 0)
            activeTabID = current.id
        }
        tabs = seeded
        persistNow()
    }

    // MARK: - Steps

    func number(of id: UUID) -> Int {
        (config.steps.firstIndex { $0.id == id } ?? 0) + 1
    }

    func addStep() {
        // A round starts at the NPC, so an empty tab's first step is "find NPC by name".
        if config.steps.isEmpty {
            config.steps.append(QuestStep.npcStep(name: "หา NPC ด้วยชื่อแล้วคลิก", delayAfterMs: 300))
        } else {
            config.steps.append(QuestStep(kind: .key, name: "ขั้นตอน \(config.steps.count + 1)"))
        }
    }

    func removeStep(_ id: UUID) {
        config.steps.removeAll { $0.id == id }
    }

    func move(_ id: UUID, by offset: Int) {
        guard let i = config.steps.firstIndex(where: { $0.id == id }) else { return }
        let j = i + offset
        guard config.steps.indices.contains(j) else { return }
        config.steps.swapAt(i, j)
    }

    func resetToDefault() {
        config = .default
    }

    /// Gives the user 3 seconds to hover over the target, then records the mouse position.
    func capture(stepID: UUID) {
        guard !isRunning, capturingStepID == nil else { return }
        capturingStepID = stepID
        Task { [weak self] in
            for remaining in stride(from: 3, through: 1, by: -1) {
                self?.status = "วางเมาส์ไว้บนจุดที่จะคลิก... \(remaining)"
                try? await Task.sleep(nanoseconds: 1_000_000_000)
            }
            guard let self else { return }
            let p = Clicker.currentLocation
            if let i = self.config.steps.firstIndex(where: { $0.id == stepID }) {
                self.config.steps[i].x = p.x.rounded()
                self.config.steps[i].y = p.y.rounded()
                self.status = "บันทึก \(self.config.steps[i].name) ที่ (\(Int(p.x)), \(Int(p.y)))"
            }
            self.capturingStepID = nil
        }
    }

    // MARK: - Private

    private func handleKey(_ keyCode: UInt16) {
        switch keyCode {
        case HotkeyMonitor.f8:
            if isRecording {
                stopRecording()
            } else if isRunning {
                stop()
            } else {
                start(fromHotkey: true)
            }
        case HotkeyMonitor.escape:
            if isRecording {
                cancelRecording()
            } else if autoStartTask != nil {
                autoStartTask?.cancel()
                autoStartTask = nil
                status = "ยกเลิกการเริ่มอัตโนมัติ"
            } else if isRunning {
                stop(reason: "หยุดด้วย Esc")
            }
        default:
            break
        }
    }

    private func run(_ cfg: QuestConfig, startMouse: CGPoint) async {
        status = "กำลังจะเริ่ม..."
        guard await wait(ms: cfg.startDelayMs) else { return }

        let steps = cfg.steps.filter(\.enabled)
        var lastClick: CGPoint?
        var lastNPC: CGPoint?
        var anchor = startMouse   // which display to read for text checks
        var loop = 0
        var noDialogStreak = 0

        outer: while cfg.unlimited || loop < max(cfg.loops, 1) {
            loop += 1
            currentLoop = loop
            let total = cfg.unlimited ? "∞" : "\(cfg.loops)"

            for step in steps {
                // If the mouse is no longer where we left it, the user took over: stop.
                if cfg.stopIfMouseMoved, let last = lastClick {
                    let now = Clicker.currentLocation
                    if hypot(now.x - last.x, now.y - last.y) > 30 {
                        stop(reason: "หยุดเพราะมีการขยับเมาส์ (รอบที่ \(loop))")
                        return
                    }
                }

                status = "รอบ \(loop)/\(total): \(step.name)"

                switch step.kind {
                case .clickNPC:
                    let names = step.npcNames
                    let hit: TextHit?
                    do {
                        // Once the NPC was found, don't wait long: its spot is already known.
                        let timeout = lastNPC == nil ? step.timeoutMs : min(step.timeoutMs, 800)
                        hit = try await waitForNPC(names, timeoutMs: timeout, prefer: lastNPC)
                    } catch {
                        if !Task.isCancelled { finish("อ่านจอไม่สำเร็จ: \(error.localizedDescription)") }
                        return
                    }
                    if Task.isCancelled { return }
                    // NPCs stand still: if the label is hidden (e.g. behind the dialog), reuse the last spot.
                    guard let labelCenter = hit?.center ?? lastNPC else {
                        finish("หยุด: หา \(names.joined(separator: " / ")) ไม่เจอในรอบที่ \(loop) (ส่งไปแล้ว \(loop - 1) รอบ)")
                        return
                    }
                    let p = CGPoint(x: labelCenter.x, y: labelCenter.y + step.npcOffsetY)
                    await Clicker.click(at: p)
                    lastClick = p
                    lastNPC = labelCenter
                    anchor = p

                case .dialogUntilEnd:
                    let result: DialogResult
                    do {
                        result = try await runDialog(step, label: "รอบ \(loop)/\(total)", near: anchor)
                    } catch {
                        if !Task.isCancelled { finish("อ่านจอไม่สำเร็จ: \(error.localizedDescription)") }
                        return
                    }
                    if Task.isCancelled { return }
                    let done = "ส่งสำเร็จ \(completedCount) รอบ"
                    switch result {
                    case .finished:
                        completedCount += 1
                        noDialogStreak = 0
                    case .neverOpened:
                        // Probably server lag on the NPC click: start the round again, but not forever.
                        noDialogStreak += 1
                        if noDialogStreak >= 3 {
                            finish("หยุด: คลิก NPC แล้วบทสนทนาไม่ขึ้น 3 ครั้งติด (\(done))")
                            return
                        }
                        status = "บทสนทนาไม่ขึ้น ลองคลิก NPC ใหม่ (\(noDialogStreak)/3)"
                        continue outer
                    case .missingRequired:
                        finish("หยุด: บทสนทนาจบโดยไม่เจอ “\(step.text)” (\(done))")
                        return
                    case .stopTextSeen:
                        finish("หยุด: เจอข้อความ “\(step.stopText)” (\(done))")
                        return
                    case .tooManyPresses:
                        finish("หยุด: กดเกิน \(step.maxPresses) ครั้งแล้วบทสนทนายังไม่จบ (\(done))")
                        return
                    case .unknownPage(let text):
                        finish("หยุด: ข้อความเปลี่ยนเป็น “\(text)” ซึ่งไม่เคยเจอตอนบันทึก (\(done))")
                        return
                    case .closedBeforeEnd:
                        finish("หยุด: บทสนทนาปิดก่อนถึงหน้าสุดท้าย (\(done))")
                        return
                    }

                case .clickStartMouse:
                    await Clicker.click(at: startMouse)
                    lastClick = startMouse

                case .clickPoint:
                    guard let p = step.point else { continue }
                    await Clicker.click(at: p)
                    lastClick = p

                case .key:
                    guard let code = KeyMap.codes[step.key] else { continue }
                    let expect = step.text.trimmingCharacters(in: .whitespaces)
                    if expect.isEmpty {
                        await Clicker.press(keyCode: code)
                    } else {
                        let result: PressResult
                        do {
                            result = try await pressWhenVisible(expect, keyCode: code, label: "รอบ \(loop)/\(total)",
                                                                step: step, near: anchor)
                        } catch {
                            if !Task.isCancelled { finish("อ่านจอไม่สำเร็จ: \(error.localizedDescription)") }
                            return
                        }
                        if Task.isCancelled { return }
                        switch result {
                        case .pressed:
                            break
                        case .notFound:
                            finish("หยุด: ไม่เจอหน้า “\(expect)” ในรอบที่ \(loop) (ส่งไปแล้ว \(loop - 1) รอบ)")
                            return
                        case .stuck:
                            finish("หยุด: กดแล้วหน้า “\(expect)” ไม่เปลี่ยน (รอบที่ \(loop))")
                            return
                        }
                    }

                case .waitText, .clickText:
                    let hit: TextHit?
                    do {
                        hit = try await waitForText(step.text, timeoutMs: step.timeoutMs, near: anchor)
                    } catch {
                        if !Task.isCancelled { finish("อ่านจอไม่สำเร็จ: \(error.localizedDescription)") }
                        return
                    }
                    if Task.isCancelled { return }
                    guard let hit else {
                        finish("หยุด: ไม่เจอข้อความ “\(step.text)” ในรอบที่ \(loop) (ส่งไปแล้ว \(loop - 1) รอบ)")
                        return
                    }
                    if step.kind == .clickText {
                        await Clicker.click(at: hit.center)
                        lastClick = hit.center
                    }
                }

                let jitter = cfg.jitterMs > 0 ? Int.random(in: 0...cfg.jitterMs) : 0
                guard await wait(ms: step.delayAfterMs + jitter) else { return }
            }
        }

        finish(completedCount > 0 ? "ครบ \(loop) รอบแล้ว (ส่งสำเร็จ \(completedCount))" : "ครบ \(loop) รอบแล้ว")
    }

    private func finish(_ message: String) {
        isRunning = false
        runTask = nil
        status = message
        NSSound.beep()
    }

    /// Repeats OCR until the text is found or the timeout passes.
    private func waitForText(_ text: String, timeoutMs: Int, near point: CGPoint) async throws -> TextHit? {
        let deadline = Date().addingTimeInterval(Double(timeoutMs) / 1000)
        repeat {
            if Task.isCancelled { return nil }
            let hits = try await ScreenReader.readScreen(near: point)
            if let hit = ScreenReader.findFuzzy(text, in: hits) { return hit }
            try? await Task.sleep(nanoseconds: 250_000_000)
        } while Date() < deadline
        return nil
    }

    private enum PressResult { case pressed, notFound, stuck }

    /// Waits until the recorded dialog line is on screen, presses the key, then waits until the
    /// line goes away (page changed). If it doesn't change in `retryMs`, presses again (max 3).
    private func pressWhenVisible(_ text: String, keyCode: CGKeyCode, label: String,
                                  step: QuestStep, near point: CGPoint) async throws -> PressResult {
        let deadline = Date().addingTimeInterval(Double(step.timeoutMs) / 1000)
        status = "\(label): รอหน้า “\(text)”"
        while try await locate(text, near: point) == nil {
            if Task.isCancelled { return .pressed }
            if Date() > deadline { return .notFound }
            try? await Task.sleep(nanoseconds: 120_000_000)
        }
        for attempt in 1...3 {
            status = "\(label): กด \(step.key) — \(text)\(attempt > 1 ? " (กดซ้ำ)" : "")"
            await Clicker.press(keyCode: keyCode)
            let changeDeadline = Date().addingTimeInterval(Double(step.retryMs) / 1000)
            while Date() < changeDeadline {
                try? await Task.sleep(nanoseconds: 120_000_000)
                if Task.isCancelled { return .pressed }
                if try await locate(text, near: point, regionOnly: true) == nil { return .pressed }
            }
        }
        return .stuck
    }

    /// Finds `text` near the last dialog position first (fast), then on the whole screen.
    private func locate(_ text: String, near point: CGPoint, regionOnly: Bool = false) async throws -> TextHit? {
        if let region = dialogRegion {
            if let hit = ScreenReader.findFuzzy(text, in: try await ScreenReader.readRegion(region)) { return hit }
            if regionOnly { return nil }
        }
        let hit = ScreenReader.findFuzzy(text, in: try await ScreenReader.readScreen(near: point))
        if let hit { dialogRegion = Self.region(aroundOption: hit.rect) }
        return hit
    }

    private enum DialogResult {
        case finished, neverOpened, missingRequired, stopTextSeen, tooManyPresses
        case unknownPage(String), closedBeforeEnd
    }

    /// Fuzzy "same line" test for OCR'd option lines.
    private static func sameLine(_ a: String, _ b: String) -> Bool {
        let x = ScreenReader.normalize(a), y = ScreenReader.normalize(b)
        guard !x.isEmpty, !y.isEmpty else { return false }
        return x.contains(y) || y.contains(x) || ScreenReader.similarity(x, y) >= 0.75
    }

    /// Press the option key once per new dialog page until the round ends.
    /// - Pages recorded with images (a recorded tab): the page is identified by how it looks; OCR
    ///   text is only the fallback. A page matching nothing recorded = wording changed → stop.
    /// - A new page must be seen on 2 reads in a row before it's pressed (no acting on a half-drawn page).
    /// - Same page for `retryMs` → the press was lost → press again.
    /// - The last recorded page (or the end text) ends the round and is never pressed twice.
    /// - Without recorded pages or an end text: ends when the dialog disappears.
    private func runDialog(_ step: QuestStep, label: String, near point: CGPoint) async throws -> DialogResult {
        guard let keyCode = KeyMap.codes[step.key] else { return .finished }
        let required = step.text.trimmingCharacters(in: .whitespaces)
        let stopWord = step.stopText.trimmingCharacters(in: .whitespaces)
        let endText = step.endText.trimmingCharacters(in: .whitespaces)
        let known = step.knownPages
        let recordedImages = step.knownPageHashes
        let useImages = recordedImages.contains { $0 != nil }
        /// Images for this run: starts as the recording; a page recognised only by text gets its
        /// current look here (this run only; the tab's recording is never changed).
        var images = recordedImages
        let pollNs: UInt64 = 120_000_000

        var sawRequired = required.isEmpty
        var presses = 0
        var lastPageKey: String?
        var lastPressAt = Date.distantPast
        var candidateKey: String?
        var candidateReads = 0
        var missing = 0
        let openDeadline = Date().addingTimeInterval(Double(step.timeoutMs) / 1000)

        while !Task.isCancelled {
            var shot: ScreenText?
            if let region = dialogRegion { shot = try await ScreenReader.readRegionDetailed(region) }
            if shot == nil { shot = try await ScreenReader.readScreenDetailed(near: point) }
            var hits = shot?.hits ?? []
            var option = ScreenReader.findDialogOption(key: step.key, in: hits)

            // Not in the usual spot: check the whole screen before deciding the dialog is gone.
            if option == nil && dialogRegion != nil && (missing >= 1 || presses == 0) {
                shot = try await ScreenReader.readScreenDetailed(near: point)
                hits = shot?.hits ?? []
                option = ScreenReader.findDialogOption(key: step.key, in: hits)
            }

            if !required.isEmpty && ScreenReader.findFuzzy(required, in: hits) != nil { sawRequired = true }
            if !stopWord.isEmpty && ScreenReader.findFuzzy(stopWord, in: hits) != nil { return .stopTextSeen }

            guard let option, let shot else {
                if presses == 0 {
                    if Date() > openDeadline { return .neverOpened }
                    status = "\(label): รอบทสนทนาขึ้น..."
                } else {
                    missing += 1
                    if missing >= 2 {
                        if !endText.isEmpty || !known.isEmpty { return .closedBeforeEnd }
                        return sawRequired ? .finished : .missingRequired
                    }
                }
                candidateKey = nil
                try? await Task.sleep(nanoseconds: pollNs)
                continue
            }

            missing = 0
            dialogRegion = Self.region(aroundOption: option.rect)

            // ---- Which page is this?
            var pageIndex: Int?
            var how = ""
            var look: Data?
            if useImages {
                look = ImageFingerprint.make(shot.image, crop: shot.pixelRect(for: ScreenReader.pageRect(around: option.rect)))
                if let look {
                    let scored = images.enumerated()
                        .compactMap { i, h in h.map { (index: i, distance: ImageFingerprint.distance(look, $0)) } }
                        .sorted { $0.distance < $1.distance }
                    if let best = scored.first, best.distance <= ImageFingerprint.matchThreshold,
                       scored.count < 2 || scored[1].distance - best.distance >= ImageFingerprint.requiredMargin {
                        pageIndex = best.index
                        how = "ภาพตรง \(Int(((1 - best.distance) * 100).rounded()))%"
                    }
                }
            }
            if pageIndex == nil, let byText = known.firstIndex(where: { Self.sameLine($0, option.text) }) {
                pageIndex = byText
                how = "ข้อความ"
                if useImages, let look {
                    images[byText] = look
                    debugLog("page \(byText + 1): recognised by text, using its current look for this run")
                }
            }
            if pageIndex == nil && !known.isEmpty {
                return .unknownPage(option.text)
            }

            // Pages are compared by index when known, otherwise by their option text.
            let pageKey = pageIndex.map { "#\($0)" } ?? ScreenReader.normalize(option.text)
            let isNewPage: Bool
            if let lastPageKey, pageIndex == nil, !lastPageKey.hasPrefix("#") {
                isNewPage = !Self.sameLine(lastPageKey, option.text)
            } else {
                isNewPage = pageKey != lastPageKey
            }

            // ---- New page: confirm on two reads in a row first.
            if isNewPage {
                if candidateKey == pageKey {
                    candidateReads += 1
                } else {
                    candidateKey = pageKey
                    candidateReads = 1
                }
                if candidateReads < 2 {
                    try? await Task.sleep(nanoseconds: pollNs)
                    continue
                }
            }

            let stuck = Date().timeIntervalSince(lastPressAt) * 1000 > Double(step.retryMs)
            if isNewPage || stuck {
                if presses >= step.maxPresses { return .tooManyPresses }
                let isLastRecorded = pageIndex != nil && !known.isEmpty && pageIndex == known.count - 1
                let isEnd = isLastRecorded || (!endText.isEmpty && Self.sameLine(endText, option.text))
                let pageLabel = pageIndex.map { "หน้า \($0 + 1)/\(known.count) (\(how))" } ?? option.text
                status = "\(label): กด \(step.key) — \(pageLabel)\(isNewPage ? "" : " (กดซ้ำ)")\(isEnd ? " · หน้าสุดท้าย" : "")"
                await Clicker.press(keyCode: keyCode)
                presses += 1
                lastPageKey = pageKey
                lastPressAt = Date()
                candidateKey = nil

                if isEnd {
                    // Give the game a moment to take the press, but never press the last page twice.
                    try? await Task.sleep(nanoseconds: UInt64(min(step.retryMs, 1000)) * 1_000_000)
                    return sawRequired ? .finished : .missingRequired
                }
            }
            try? await Task.sleep(nanoseconds: pollNs)
        }
        return .finished
    }

    /// Area around a dialog option line that covers the dialog text and its options.
    private static func region(aroundOption r: CGRect) -> CGRect {
        CGRect(x: r.minX - 80, y: r.minY - 170, width: 520, height: 340)
    }

    /// Repeats OCR on all displays until one of the NPC names is found or the timeout passes.
    private func waitForNPC(_ names: [String], timeoutMs: Int, prefer: CGPoint?) async throws -> TextHit? {
        let deadline = Date().addingTimeInterval(Double(timeoutMs) / 1000)
        repeat {
            if Task.isCancelled { return nil }
            let screens = try await ScreenReader.readAllDisplays()
            if let hit = ScreenReader.findNPC(names, in: screens, prefer: prefer) { return hit }
            try? await Task.sleep(nanoseconds: 250_000_000)
        } while Date() < deadline
        return nil
    }

    /// Returns false if the run was cancelled.
    private func wait(ms: Int) async -> Bool {
        do {
            try await Task.sleep(nanoseconds: UInt64(max(ms, 0)) * 1_000_000)
            return !Task.isCancelled
        } catch {
            return false
        }
    }

    private func save() {
        if let data = try? JSONEncoder().encode(config) {
            UserDefaults.standard.set(data, forKey: Self.storageKey)
        }
    }
}

/// Temporary diagnostics: printed in the Terminal that runs `swift run`.
func debugLog(_ message: String) {
    print("[TQS \(Date().formatted(date: .omitted, time: .standard))] \(message)")
}
