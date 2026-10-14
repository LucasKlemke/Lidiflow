import AppKit
import SwiftUI

/// A native macOS pop-up button whose items can carry an icon, e.g. provider logos.
struct PopUpPicker<Value: Hashable>: NSViewRepresentable {
    struct Item {
        let value: Value
        let title: String
        var image: NSImage?
    }

    let items: [Item]
    @Binding var selection: Value

    func makeNSView(context: Context) -> NSPopUpButton {
        let button = NSPopUpButton(frame: .zero, pullsDown: false)
        button.target = context.coordinator
        button.action = #selector(Coordinator.changed(_:))
        button.setContentHuggingPriority(.defaultLow, for: .horizontal)
        return button
    }

    func updateNSView(_ button: NSPopUpButton, context: Context) {
        context.coordinator.parent = self
        let titles = items.map(\.title)
        if button.itemTitles != titles {
            button.removeAllItems()
            for item in items {
                button.addItem(withTitle: item.title)
                button.lastItem?.image = item.image
            }
        }
        if let index = items.firstIndex(where: { $0.value == selection }), button.indexOfSelectedItem != index {
            button.selectItem(at: index)
        }
    }

    func makeCoordinator() -> Coordinator {
        Coordinator(parent: self)
    }

    final class Coordinator: NSObject {
        var parent: PopUpPicker

        init(parent: PopUpPicker) {
            self.parent = parent
        }

        @objc func changed(_ sender: NSPopUpButton) {
            let index = sender.indexOfSelectedItem
            guard parent.items.indices.contains(index) else { return }
            parent.selection = parent.items[index].value
        }
    }
}
