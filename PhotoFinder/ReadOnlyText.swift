import AppKit
import SwiftUI

/// Text to read and copy, but not change, as one Tab stop: Windows' read-only boxes in Get Info. VoiceOver reads it
/// line by line, and Tab and Shift-Tab always move on. A selectable SwiftUI Text kept keyboard focus once VoiceOver
/// had been reading in it, so Tab went nowhere (Kelly, 19 Oct).
struct ReadOnlyText: NSViewRepresentable {
    let text: String
    let label: String

    func makeNSView(context: Context) -> NSScrollView {
        let textView = TabbingTextView()
        textView.isEditable = false
        textView.isSelectable = true
        textView.drawsBackground = false
        textView.font = .systemFont(ofSize: NSFont.systemFontSize)
        textView.textContainerInset = NSSize(width: 2, height: 4)
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        textView.textContainer?.widthTracksTextView = true
        textView.setAccessibilityLabel(label)

        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.borderType = .bezelBorder
        scroll.drawsBackground = false
        scroll.documentView = textView
        textView.string = text
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let textView = scroll.documentView as? NSTextView else { return }
        if textView.string != text { textView.string = text }
        textView.setAccessibilityLabel(label)
    }
}

/// Tab and Shift-Tab move to the next and previous control, as in any read-only field, rather than doing nothing.
final class TabbingTextView: NSTextView {
    override func insertTab(_ sender: Any?) {
        window?.selectNextKeyView(self)
    }

    override func insertBacktab(_ sender: Any?) {
        window?.selectPreviousKeyView(self)
    }
}
