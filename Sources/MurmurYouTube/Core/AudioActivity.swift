import CoreAudio
import Foundation

/// Which processes are currently playing audio out of this Mac.
///
/// This is public CoreAudio, not a private framework: the HAL publishes one `AudioObject`
/// per client process, and `kAudioProcessPropertyIsRunningOutput` says whether that process
/// has a live output stream. No entitlement and no TCC prompt — which is the whole reason
/// it's used here. The obvious alternative, `MediaRemote`'s now-playing state, is private
/// **and** entitlement-gated since macOS 15.4, so it is not available to this app at all.
///
/// What it can and can't tell you: it sees *any* audio, including a browser tab, a game or
/// a video call — not just apps that register as "now playing". It cannot tell you which
/// app owns the media keys, and it has no notion of paused-versus-stopped.
enum AudioActivity {
    struct Client: Hashable, Sendable {
        let pid: pid_t
        let bundleID: String?

        var name: String { bundleID ?? "pid \(pid)" }
    }

    /// Processes with a live output stream, excluding this app's own sounds.
    ///
    /// **The signal lags on the way down.** Measured against Spotify, `isRunningOutput`
    /// stays `true` for roughly 2.5 s after the music actually stops — apps hold the device
    /// open for a moment rather than tearing the stream down at the last sample. So a
    /// non-empty result means "playing, or stopped within the last few seconds", and nothing
    /// irreversible should hang on it alone.
    static func outputProcesses() -> [Client] {
        let ownPID = ProcessInfo.processInfo.processIdentifier
        return processObjects().compactMap { object in
            guard isRunningOutput(object), let pid = pid(of: object), pid != ownPID else {
                return nil
            }
            return Client(pid: pid, bundleID: bundleID(of: object))
        }
    }

    // MARK: - HAL plumbing

    private static let system = AudioObjectID(kAudioObjectSystemObject)

    private static func address(_ selector: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(
            mSelector: selector,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
    }

    private static func processObjects() -> [AudioObjectID] {
        var property = address(kAudioHardwarePropertyProcessObjectList)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &property, 0, nil, &size) == noErr,
              size > 0
        else { return [] }

        var objects = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(system, &property, 0, nil, &size, &objects) == noErr else {
            return []
        }
        return objects
    }

    private static func isRunningOutput(_ object: AudioObjectID) -> Bool {
        var property = address(kAudioProcessPropertyIsRunningOutput)
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(object, &property, 0, nil, &size, &value) == noErr else {
            return false
        }
        return value != 0
    }

    private static func pid(of object: AudioObjectID) -> pid_t? {
        var property = address(kAudioProcessPropertyPID)
        var value: pid_t = 0
        var size = UInt32(MemoryLayout<pid_t>.size)
        guard AudioObjectGetPropertyData(object, &property, 0, nil, &size, &value) == noErr else {
            return nil
        }
        return value
    }

    /// Returns a *retained* CFString per the CoreAudio docs, so it's taken unmanaged and
    /// released here. Helper processes and some daemons have no bundle ID at all.
    private static func bundleID(of object: AudioObjectID) -> String? {
        var property = address(kAudioProcessPropertyBundleID)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        let status = withUnsafeMutablePointer(to: &value) {
            AudioObjectGetPropertyData(object, &property, 0, nil, &size, $0)
        }
        guard status == noErr, let value else { return nil }

        let bundleID = value.takeRetainedValue() as String
        return bundleID.isEmpty ? nil : bundleID
    }
}
