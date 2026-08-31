import AppKit
import IOKit.hidsystem

/// Silences whatever is playing for the duration of a dictation, and starts it again on
/// release. Music over a live microphone is both distracting to talk over and audible to
/// the transcriber.
///
/// It works by posting the system play/pause key — the same event the F8 key sends — so it
/// reaches anything the media keys reach: Music, Spotify, Podcasts, TV, IINA, VLC and video
/// in Safari, Chrome or Arc. There is no per-app scripting and so no Automation prompt; the
/// Accessibility grant the hotkey already needs is enough to post the event.
///
/// **The key is a toggle, not a pause.** Sent at the wrong moment it starts music rather
/// than stopping it, and that is the only failure worth designing around here. Two rules
/// follow, and both are load-bearing:
///
/// 1. Nothing is sent unless `AudioActivity` says something is actually making noise.
/// 2. The pause is *verified* before a resume is queued. If the audio doesn't stop, the
///    toggle is sent a second time to undo whatever it did, and this app forgets about it.
@MainActor
enum MediaPause {
    /// True only once a pause has been observed to land. A failed or misdirected toggle
    /// must never leave a resume queued, because that resume is what would start music the
    /// user never asked for.
    private static var owesResume = false

    /// Pause and resume are separated by an async verification step, and a short hold can
    /// end before it finishes. Chaining every operation through one task is what stops the
    /// two from posting the key concurrently — with a toggle, two events racing land in an
    /// arbitrary order and leave playback in exactly the state we were trying to avoid.
    private static var chain: Task<Void, Never>?

    /// Pauses if — and only if — something is currently playing.
    static func pauseIfPlaying() {
        guard Settings.shared.pauseMedia else { return }
        enqueue {
            guard !owesResume else { return }

            let playing = AudioActivity.outputProcesses()
            guard !playing.isEmpty else { return }

            let names = playing.map(\.name).joined(separator: ", ")
            Log.audio.info("pausing media · \(names, privacy: .public)")
            postPlayPause()

            switch await verifyPause(of: Set(playing.map(\.pid))) {
            case .landed:
                owesResume = true
            case .missed:
                // Nothing stopped. Either the event reached no one, or it reached an app
                // that wasn't the one making the noise — and in that second case the toggle
                // may have *started* something. Sending it once more puts whatever we hit
                // back where it was; if we hit nothing, a second nothing costs nothing.
                Log.audio.info("media did not pause — undoing the toggle")
                postPlayPause()
            }
        }
    }

    /// Resumes what `pauseIfPlaying` stopped. A no-op otherwise, so it is safe to call from
    /// every path that ends a dictation — including the ones that end it badly.
    ///
    /// Deliberately not gated on the setting: if the user switches media pausing off while
    /// a dictation is in flight, the music this app stopped still has to come back.
    static func resumeIfPaused() {
        enqueue {
            guard owesResume else { return }
            owesResume = false
            Log.audio.info("resuming media")
            postPlayPause()
        }
    }

    // MARK: - Verification

    private enum PauseOutcome {
        case landed
        case missed
    }

    /// Watches the audio picture until it says which of the two happened.
    ///
    /// Waiting for silence alone would work, but only after the full deadline — and that
    /// deadline can't be short, because `isRunningOutput` keeps reporting `true` for a
    /// couple of seconds after playback really stops (see `AudioActivity`). So the miss is
    /// also detected directly: everything that was playing is *still* playing and something
    /// new has joined it, which is what a toggle landing on the wrong app looks like. That
    /// cuts the unwanted music from about five seconds to about one.
    ///
    /// This app's own start/stop ticks can't trip that test — `NSSound` output is attributed
    /// to the playing process, and `outputProcesses()` drops this process.
    private static func verifyPause(of pids: Set<pid_t>) async -> PauseOutcome {
        let deadline = ContinuousClock.now + .seconds(4)
        while ContinuousClock.now < deadline {
            try? await Task.sleep(for: .milliseconds(250))
            let current = Set(AudioActivity.outputProcesses().map(\.pid))
            if current.isDisjoint(with: pids) { return .landed }
            if current.count > pids.count, current.isSuperset(of: pids) { return .missed }
        }
        return .missed
    }

    // MARK: - The key itself

    /// Builds the event as an `NSEvent` and posts it through the `CGEvent` bridge.
    ///
    /// Media keys are `NSSystemDefined` events carrying the key code packed into `data1`,
    /// not keyboard events — `CGEvent(keyboardEventSource:virtualKey:keyDown:)` has no way
    /// to express one. The magic in `data1` is the layout the window server expects: key
    /// code in the high half, and `0xA`/`0xB` (down/up) in the byte below it. `data2 == -1`
    /// and the `0xA00`/`0xB00` modifier flags are what real hardware sends.
    private static func postPlayPause() {
        for isDown in [true, false] {
            let flags = NSEvent.ModifierFlags(rawValue: isDown ? 0xA00 : 0xB00)
            let data1 = Int((Int32(NX_KEYTYPE_PLAY) << 16) | ((isDown ? 0xA : 0xB) << 8))

            guard let event = NSEvent.otherEvent(
                with: .systemDefined,
                location: .zero,
                modifierFlags: flags,
                timestamp: 0,
                windowNumber: 0,
                context: nil,
                subtype: Int16(NX_SUBTYPE_AUX_CONTROL_BUTTONS),
                data1: data1,
                data2: -1
            ), let cgEvent = event.cgEvent else {
                Log.audio.error("MediaPause: could not build the play/pause event")
                return
            }
            cgEvent.post(tap: .cghidEventTap)
        }
    }

    // MARK: - Serialization

    private static func enqueue(_ operation: @escaping @Sendable @MainActor () async -> Void) {
        let previous = chain
        chain = Task { @MainActor in
            await previous?.value
            await operation()
        }
    }
}
