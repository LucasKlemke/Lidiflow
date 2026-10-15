import AppKit
import Combine
import SwiftUI

/// The app's main window. The Dock icon is shown only while it is open.
final class MainWindowController: NSObject, NSWindowDelegate {
    private let model: AppModel
    private var window: NSWindow?

    init(model: AppModel) {
        self.model = model
    }

    func show() {
        if window == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 1000, height: 680),
                styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                backing: .buffered,
                defer: false
            )
            window.title = "Lidiflow"
            window.titleVisibility = .hidden
            window.titlebarAppearsTransparent = true
            window.minSize = NSSize(width: 820, height: 560)
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: MainView(model: model))
            window.center()
            window.setFrameAutosaveName("LidiflowMain")
            window.delegate = self
            self.window = window
        }
        NSApp.setActivationPolicy(.regular)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func windowWillClose(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
    }
}

enum SidebarItem: String, CaseIterable, Identifiable {
    case home = "Início"
    case history = "Histórico"
    case transforms = "Transforms"
    case dictionary = "Dicionário"
    case snippets = "Snippets"
    case models = "Modelos"
    case usage = "Gastos"
    case settings = "Ajustes"

    var id: Self { self }

    var icon: String {
        switch self {
        case .home: return "house"
        case .history: return "clock"
        case .transforms: return "wand.and.stars"
        case .dictionary: return "character.book.closed"
        case .snippets: return "text.badge.plus"
        case .models: return "cpu"
        case .usage: return "creditcard"
        case .settings: return "gearshape"
        }
    }

    /// Items grouped under a quiet label in the sidebar.
    static let groups: [(String?, [SidebarItem])] = [
        (nil, [.home, .history]),
        ("Ferramentas", [.transforms, .dictionary, .snippets]),
        ("Configurar", [.models, .usage, .settings]),
    ]
}

struct MainView: View {
    @ObservedObject var model: AppModel
    /// Opens on Início, or on the page named by the `-section` launch argument (e.g. `-section Modelos`).
    @State private var section = SidebarItem(rawValue: UserDefaults.standard.string(forKey: "section") ?? "") ?? .home

    private let permissionTimer = Timer.publish(every: 2, on: .main, in: .common).autoconnect()

    var body: some View {
        HStack(spacing: 0) {
            Sidebar(model: model, section: $section)
            Rectangle().fill(Theme.border).frame(width: 1)
            Group {
                switch section {
                case .home: HomeView(model: model, showHistory: { section = .history }, showModels: { section = .models })
                case .history: HistoryView(model: model)
                case .transforms: TransformsView(model: model)
                case .dictionary: DictionaryView(model: model)
                case .snippets: SnippetsView(model: model)
                case .models: ModelsView(model: model, manager: model.models)
                case .usage: UsageView(log: UsageLog.shared)
                case .settings: SettingsView(model: model)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .ignoresSafeArea()
        .tint(Theme.blue)
        .onReceive(permissionTimer) { _ in model.refreshPermissions() }
    }
}

private struct Sidebar: View {
    @ObservedObject var model: AppModel
    @Binding var section: SidebarItem

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .frame(width: 30, height: 30)
                Text("Lidiflow")
                    .font(.system(size: 17, weight: .semibold))
                    .foregroundStyle(Theme.text)
            }
            .padding(.horizontal, 18)
            .padding(.top, 52)
            .padding(.bottom, 20)

            VStack(alignment: .leading, spacing: 16) {
                ForEach(SidebarItem.groups, id: \.1) { title, items in
                    VStack(alignment: .leading, spacing: 2) {
                        if let title {
                            Text(title.uppercased())
                                .font(.system(size: 10, weight: .semibold))
                                .tracking(0.6)
                                .foregroundStyle(Theme.secondary)
                                .padding(.horizontal, 12)
                                .padding(.bottom, 4)
                        }
                        ForEach(items) { item in
                            SidebarButton(item: item, selected: section == item) { section = item }
                        }
                    }
                }
            }
            .padding(.horizontal, 10)

            Spacer()

            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 6) {
                    Circle()
                        .fill(model.isReady ? Color.green : Color.orange)
                        .frame(width: 7, height: 7)
                    Text(model.isReady ? "Pronto" : "Configurando")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(Theme.text)
                }
                if model.isReady {
                    HStack(spacing: 6) {
                        Text("Segure").font(.system(size: 12)).foregroundStyle(Theme.secondary)
                        KeyCaps(keys: model.keys(for: .dictate))
                        Text("para ditar").font(.system(size: 12)).foregroundStyle(Theme.secondary)
                    }
                } else {
                    Text(model.status)
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .card(padding: 12)
            .padding(12)
        }
        .frame(width: 220)
        .frame(maxHeight: .infinity)
        .background(Theme.sidebar)
    }
}

private struct SidebarButton: View {
    let item: SidebarItem
    let selected: Bool
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: item.icon)
                    .font(.system(size: 13, weight: .medium))
                    .frame(width: 18)
                Text(item.rawValue)
                    .font(.system(size: 13, weight: selected ? .semibold : .regular))
                Spacer()
            }
            .foregroundStyle(selected ? Theme.blue : Theme.text)
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(
                RoundedRectangle(cornerRadius: 8)
                    .fill(selected ? Theme.blueTint : hovering ? Theme.hover : .clear)
            )
            .contentShape(RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}
