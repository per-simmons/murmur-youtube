import AVFoundation
import Foundation

/// What this Mac and this build can do. Apple's `SpeechAnalyzer` and Foundation Models are
/// macOS 26 APIs: they only compile against the macOS 26 SDK and only run on macOS 26, so
/// every use goes through this seam. Everywhere else the app runs on Parakeet and the
/// rule-based cleanup, with no behavior change to those paths.
enum Platform {
    static var appleSpeechAvailable: Bool {
        #if canImport(FoundationModels)
        if #available(macOS 26.0, *) { return true }
        #endif
        return false
    }
}

/// Always safe to call: returns the real engine where it exists, otherwise one that fails
/// loudly on `start()` instead of crashing. UI hides the Apple choice when unavailable, so
/// this is a backstop for stale settings.
func makeAppleSpeechEngine() -> any TranscriptionEngine {
    #if canImport(FoundationModels)
    if #available(macOS 26.0, *) { return AppleSpeechEngine() }
    #endif
    return UnavailableSpeechEngine()
}

actor UnavailableSpeechEngine: TranscriptionEngine {
    func preferredInputFormat() async -> sending AVAudioFormat? { nil }

    func start() async throws -> AsyncThrowingStream<TranscriptionChunk, Error> {
        throw TranscriptionError.modelInstallFailed("Apple's speech engine requires macOS 26. Choose Parakeet.")
    }

    func feed(_ chunk: AudioChunk) async {}
    func finish() async {}
}

/// Smart cleanup via the on-device LLM where macOS 26 provides it; otherwise reports
/// itself unavailable and formats with the deterministic rules.
struct FoundationModelFormatter: TextFormatter {
    static var isAvailable: Bool {
        #if canImport(FoundationModels)
        if #available(macOS 26.0, *) { return OnDeviceLLMFormatter.isAvailable }
        #endif
        return false
    }

    static var unavailableReason: String? {
        #if canImport(FoundationModels)
        if #available(macOS 26.0, *) { return OnDeviceLLMFormatter.unavailableReason }
        #endif
        return "Requires macOS 26."
    }

    func format(_ raw: String) async -> String {
        #if canImport(FoundationModels)
        if #available(macOS 26.0, *) { return await OnDeviceLLMFormatter().format(raw) }
        #endif
        return await RuleBasedFormatter().format(raw)
    }
}
