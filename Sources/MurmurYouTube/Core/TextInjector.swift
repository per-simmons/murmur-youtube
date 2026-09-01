import AppKit
import ApplicationServices
import Foundation

/// Puts text into whatever field currently has keyboard focus.
///
/// Two strategies, in order:
/// 1. **Accessibility** — set `kAXSelectedTextAttribute` on the focused element. Clean and
///    instant, and it leaves the pasteboard untouched.
/// 2. **Pasteboard + ⌘V** — works in Electron apps and anything else with a half-hearted
///    AX implementation. The previous pasteboard contents are restored afterwards.
///
/// The catch that makes this non-obvious: **many apps return `.success` from the AX write
/// and then do nothing.** Electron (Cursor, VS Code, Slack, Discord), Chrome, and most
/// terminal emulators all report `kAXSelectedTextAttribute` as settable, accept the write,
/// and silently drop it. So the return value is not evidence of anything — strategy 1 is
/// only trusted when the insertion point can be *observed* to have moved.
///
/// This all works because the HUD is a non-activating panel: focus never leaves the user's
/// target app, so "the focused element" is still their text field.
@MainActor
enum TextInjector {
    private static var targetApplication: NSRunningApplication?
    private static var targetElement: AXUIElement?

    static func rememberTargetApplication() {
        let frontmost = NSWorkspace.shared.frontmostApplication
        targetApplication = frontmost?.processIdentifier == ProcessInfo.processInfo.processIdentifier
            ? nil
            : frontmost
    }

    static func rememberFocusedElement(in application: NSRunningApplication) {
        targetApplication = application
        let appElement = AXUIElementCreateApplication(application.processIdentifier)
        var focused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(
            appElement,
            kAXFocusedUIElementAttribute as CFString,
            &focused
        ) == .success, let focused else {
            targetElement = nil
            return
        }
        targetElement = unsafeDowncast(focused as AnyObject, to: AXUIElement.self)
    }

    static func insert(_ text: String) {
        guard !text.isEmpty else { return }

        copyToClipboard(text)

        targetApplication?.activate(options: [])

        Task { @MainActor in
            // App activation is asynchronous; wait for the target to regain focus before
            // querying accessibility or posting Command-V.
            try? await Task.sleep(for: .milliseconds(100))

            switch insertViaAccessibility(text) {
            case .inserted:
                Log.inject.info("inserted via AX (\(text.count) chars)")
            case .unverified(let reason):
                Log.inject.info("AX insert not verified (\(reason, privacy: .public)) — pasting")
                insertViaPasteboard(text)
            }
        }
    }

    private static func copyToClipboard(_ text: String) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
        Log.inject.info("copied to clipboard (\(text.count) chars)")
    }

    private enum AXOutcome {
        case inserted
        case unverified(String)
    }

    // MARK: - Strategy 1: Accessibility, verified

    private static func insertViaAccessibility(_ text: String) -> AXOutcome {
        let element: AXUIElement
        if let targetElement {
            element = targetElement
        } else {
            let systemWide = AXUIElementCreateSystemWide()
            var focused: CFTypeRef?
            guard AXUIElementCopyAttributeValue(
                systemWide,
                kAXFocusedUIElementAttribute as CFString,
                &focused
            ) == .success, let focused else {
                return .unverified("no focused element")
            }
            element = unsafeDowncast(focused as AnyObject, to: AXUIElement.self)
        }

        var settable: DarwinBoolean = false
        guard AXUIElementIsAttributeSettable(
            element,
            kAXSelectedTextAttribute as CFString,
            &settable
        ) == .success, settable.boolValue else {
            return .unverified("selected text not settable")
        }

        // Without a readable insertion point there's no way to tell a real insert from a
        // silently-dropped one, so don't gamble — go straight to the fallback.
        guard let before = selectedRange(of: element) else {
            return .unverified("no readable selection range")
        }

        guard AXUIElementSetAttributeValue(
            element,
            kAXSelectedTextAttribute as CFString,
            text as CFString
        ) == .success else {
            return .unverified("set attribute failed")
        }

        guard let after = selectedRange(of: element) else {
            return .unverified("selection range unreadable after write")
        }

        // Deliberately a *movement* check, not an exact-length check. Falling back after a
        // write that actually landed would paste the text a second time, and a duplicated
        // paragraph is far worse than a missing one. Some apps normalize newlines or run
        // autocorrect, so the caret can legitimately advance by something other than the
        // UTF-16 count — only a completely unmoved selection proves nothing happened.
        let unchanged = after.location == before.location && after.length == before.length
        guard !unchanged else {
            return .unverified("selection unmoved at \(before.location)")
        }

        return .inserted
    }

    private static func selectedRange(of element: AXUIElement) -> CFRange? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(
            element,
            kAXSelectedTextRangeAttribute as CFString,
            &value
        ) == .success, let value else { return nil }

        let axValue = unsafeDowncast(value as AnyObject, to: AXValue.self)
        guard AXValueGetType(axValue) == .cfRange else { return nil }

        var range = CFRange()
        guard AXValueGetValue(axValue, .cfRange, &range) else { return nil }
        return range
    }

    // MARK: - Strategy 2: Pasteboard + ⌘V

    private static func insertViaPasteboard(_ text: String) {
        Task { @MainActor in
            // Give the target app a moment to observe the new pasteboard generation before
            // ⌘V arrives, or a fast paste can grab the *previous* contents.
            try? await Task.sleep(for: .milliseconds(40))
            postCommandV()
            Log.inject.info("pasted (\(text.count) chars)")

            // The paste is asynchronous in the target app; restore only once it's had time
            // to read the pasteboard. Keep the transcript available for the user afterward.
            try? await Task.sleep(for: .milliseconds(500))
            copyToClipboard(text)
        }
    }

    private static func postCommandV() {
        guard let source = CGEventSource(stateID: .privateState) else { return }
        let vKey: CGKeyCode = 9 // kVK_ANSI_V

        guard let down = CGEvent(keyboardEventSource: source, virtualKey: vKey, keyDown: true),
              let up = CGEvent(keyboardEventSource: source, virtualKey: vKey, keyDown: false)
        else { return }

        // Set explicitly rather than inheriting live hardware modifier state — the user may
        // still be resting a finger on something.
        down.flags = .maskCommand
        up.flags = .maskCommand

        down.post(tap: .cghidEventTap)
        up.post(tap: .cghidEventTap)
    }

}
