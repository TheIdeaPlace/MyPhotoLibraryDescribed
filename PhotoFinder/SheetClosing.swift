import SwiftUI

extension View {
    /// Command-W (and, unless the sheet has a Cancel button of its own, Escape) closes a sheet, as people expect of any
    /// window on the Mac. A sheet isn't a window to macOS, so File > Close doesn't reach it; without this, Get Info could
    /// only be closed with its Done button.
    func closesWithKeyboard(escape: Bool = true, _ close: @escaping () -> Void) -> some View {
        background {
            Group {
                Button("Close", action: close)
                    .keyboardShortcut("w", modifiers: .command)
                if escape {
                    Button("Close", action: close)
                        .keyboardShortcut(.cancelAction)
                }
            }
            // Out of sight and out of VoiceOver's way: only the keys matter. Not Tab stops either: with keyboard
            // navigation on, Tab landed on them as two silent stops after Done (Kelly, 19 Oct).
            .focusable(false)
            .opacity(0)
            .frame(width: 0, height: 0)
            .accessibilityHidden(true)
        }
    }
}
