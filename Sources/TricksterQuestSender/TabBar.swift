import SwiftUI

/// Browser-style tab strip: click to switch, × to close, + for a new tab,
/// double-click to rename, right-click for more.
struct BrowserTabBar: View {
    @Environment(QuestRunner.self) private var runner
    @State private var hoveredID: UUID?
    @State private var renamingID: UUID?
    @State private var renameText = ""
    @FocusState private var renameFocused: Bool
    /// For detecting double-clicks ourselves (SwiftUI's double + single tap combo is unreliable on macOS).
    @State private var lastTap: (id: UUID, time: Date)?

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(alignment: .bottom, spacing: 0) {
                ForEach(Array(runner.tabs.enumerated()), id: \.element.id) { index, tab in
                    tabItem(tab)
                    separator(after: index)
                }
                Button {
                    runner.addTab()
                } label: {
                    Image(systemName: "plus")
                        .font(.system(size: 12, weight: .medium))
                        .frame(width: 28, height: 28)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .padding(.leading, 4)
                .padding(.bottom, 1)
                .help("แท็บใหม่ (⌘T)")
            }
            .padding(.horizontal, 8)
            .padding(.top, 6)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .frame(height: 36)
        .background(Color(nsColor: .underPageBackgroundColor))
        .disabled(runner.isRunning || runner.isRecording || runner.isAnalyzing)
    }

    private func tabItem(_ tab: Preset) -> some View {
        let selected = tab.id == runner.activeTabID
        let hovered = hoveredID == tab.id
        let showClose = runner.tabs.count > 1 && (selected || hovered)

        return HStack(spacing: 6) {
            if renamingID == tab.id {
                TextField("ชื่อแท็บ", text: $renameText)
                    .textFieldStyle(.roundedBorder)
                    .focused($renameFocused)
                    .onAppear {
                        // Focus once the field is on screen, then select all so typing replaces the name.
                        DispatchQueue.main.async {
                            renameFocused = true
                            NSApp.sendAction(#selector(NSText.selectAll(_:)), to: nil, from: nil)
                        }
                    }
                    .onSubmit { commitRename() }
                    .onExitCommand { renamingID = nil }
                    .onChange(of: renameFocused) { _, focused in
                        // Clicking elsewhere also saves the new name.
                        if !focused && renamingID == tab.id { commitRename() }
                    }
            } else {
                if tab.isRecorded {
                    Image(systemName: "record.circle")
                        .font(.system(size: 10))
                        .foregroundStyle(.red.opacity(0.8))
                        .help("แท็บนี้มาจากการบันทึกการเล่น")
                }
                Text(tab.name)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .foregroundStyle(selected ? .primary : .secondary)
            }
            Spacer(minLength: 0)
            Button {
                runner.closeTab(tab.id)
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 9, weight: .bold))
                    .frame(width: 16, height: 16)
                    .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .opacity(showClose ? 1 : 0)
            .allowsHitTesting(showClose)
            .help("ปิดแท็บ")
        }
        .font(.system(size: 12))
        .padding(.leading, 12)
        .padding(.trailing, 6)
        .frame(width: 170, height: 30)
        .background(
            UnevenRoundedRectangle(topLeadingRadius: 8, topTrailingRadius: 8)
                .fill(selected ? Color(nsColor: .windowBackgroundColor)
                      : (hovered ? Color.primary.opacity(0.07) : Color.clear))
        )
        .contentShape(Rectangle())
        .onHover { inside in
            if inside {
                hoveredID = tab.id
            } else if hoveredID == tab.id {
                hoveredID = nil
            }
        }
        .onTapGesture { handleTap(on: tab) }
        .help("\(tab.name) — ดับเบิลคลิกเพื่อเปลี่ยนชื่อ")
        .contextMenu {
            Button("เปลี่ยนชื่อ") { startRename(tab) }
            Button("ทำสำเนา") { runner.duplicateTab(tab.id) }
            Divider()
            Button("ปิดแท็บ") { runner.closeTab(tab.id) }
                .disabled(runner.tabs.count <= 1)
        }
    }

    /// Thin divider between two unselected tabs, like Chrome/Safari.
    private func separator(after index: Int) -> some View {
        let tabs = runner.tabs
        let show = index < tabs.count - 1
            && tabs[index].id != runner.activeTabID
            && tabs[index + 1].id != runner.activeTabID
        return Rectangle()
            .fill(Color.primary.opacity(show ? 0.15 : 0))
            .frame(width: 1, height: 16)
            .padding(.bottom, 7)
    }

    /// Single click selects right away (no delay); a second click on the same tab within the
    /// system double-click time starts renaming.
    private func handleTap(on tab: Preset) {
        guard renamingID != tab.id else { return }
        let now = Date()
        if let last = lastTap, last.id == tab.id,
           now.timeIntervalSince(last.time) <= NSEvent.doubleClickInterval {
            lastTap = nil
            startRename(tab)
        } else {
            lastTap = (tab.id, now)
            runner.selectTab(tab.id)
        }
    }

    private func startRename(_ tab: Preset) {
        renameText = tab.name
        renamingID = tab.id
    }

    private func commitRename() {
        if let id = renamingID { runner.renameTab(id, to: renameText) }
        renamingID = nil
    }
}
