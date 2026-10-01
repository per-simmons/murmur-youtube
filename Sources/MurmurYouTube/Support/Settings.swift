import Foundation
import Observation

/// Which speech engine transcribes an utterance.
enum SpeechEngineChoice: String, CaseIterable, Sendable {
    case apple
    case parakeet

    /// Apple's engine needs the macOS 26 runtime; on older systems only Parakeet is offered.
    static var allCases: [SpeechEngineChoice] {
        Platform.appleSpeechAvailable ? [.apple, .parakeet] : [.parakeet]
    }

    var displayName: String {
        switch self {
        case .apple: "Apple (streaming)"
        case .parakeet: "Parakeet (batch)"
        }
    }

    /// Apple shows text while you talk; Parakeet only resolves on release.
    var showsLiveText: Bool { self == .apple }
}

@MainActor
@Observable
final class Settings {
    static let shared = Settings()

    var pushToTalkKey: PushToTalkKey {
        didSet { defaults.set(pushToTalkKey.rawValue, forKey: Keys.pushToTalkKey) }
    }

    var engine: SpeechEngineChoice {
        didSet { defaults.set(engine.rawValue, forKey: Keys.engine) }
    }

    /// Run every engine on each recording and show them side by side, instead of
    /// transcribing with one. Nothing is typed into the focused app in this mode.
    var compareMode: Bool {
        didSet { defaults.set(compareMode, forKey: Keys.compareMode) }
    }

    /// Run the cleanup pass before injecting. Off = raw engine output.
    var cleanupEnabled: Bool {
        didSet { defaults.set(cleanupEnabled, forKey: Keys.cleanupEnabled) }
    }

    /// Use the on-device LLM for cleanup instead of the deterministic rule pass.
    var smartCleanup: Bool {
        didSet { defaults.set(smartCleanup, forKey: Keys.smartCleanup) }
    }

    /// Play a short tick when capture starts and stops.
    var soundEnabled: Bool {
        didSet { defaults.set(soundEnabled, forKey: Keys.soundEnabled) }
    }

    private let defaults = UserDefaults.standard

    private enum Keys {
        static let pushToTalkKey = "pushToTalkKey"
        static let cleanupEnabled = "cleanupEnabled"
        static let soundEnabled = "soundEnabled"
        static let engine = "engine"
        static let smartCleanup = "smartCleanup"
        static let compareMode = "compareMode"
    }

    private init() {
        let raw = defaults.string(forKey: Keys.pushToTalkKey) ?? PushToTalkKey.rightOption.rawValue
        pushToTalkKey = PushToTalkKey(rawValue: raw) ?? .rightOption
        // Apple by default: no download, no dependency, live text while speaking.
        let stored = SpeechEngineChoice(rawValue: defaults.string(forKey: Keys.engine) ?? "")
        engine = SpeechEngineChoice.allCases.contains(stored ?? .apple) ? (stored ?? .apple) : .parakeet
        cleanupEnabled = defaults.object(forKey: Keys.cleanupEnabled) as? Bool ?? true
        smartCleanup = FoundationModelFormatter.isAvailable
            && (defaults.object(forKey: Keys.smartCleanup) as? Bool ?? false)
        // Comparison needs both engines, so it is off wherever Apple's isn't available.
        compareMode = Platform.appleSpeechAvailable
            && (defaults.object(forKey: Keys.compareMode) as? Bool ?? false)
        soundEnabled = defaults.object(forKey: Keys.soundEnabled) as? Bool ?? true
    }
}
