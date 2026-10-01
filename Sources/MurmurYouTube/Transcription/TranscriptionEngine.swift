import AVFoundation
import Foundation

/// One buffer of captured audio, in transit from the audio thread to the speech engine.
///
/// `AVAudioPCMBuffer` isn't `Sendable`, and `AVAudioEngine` recycles the buffer it hands
/// to a tap the moment the callback returns. The unchecked conformance is only sound
/// because `AudioCapture` allocates a **fresh** buffer for every chunk and never touches
/// it again after handing it over — don't construct one of these around a borrowed buffer.
struct AudioChunk: @unchecked Sendable {
    let buffer: AVAudioPCMBuffer
}

/// A snapshot of the running transcript.
///
/// `text` is always the **full transcript so far**, not a delta — engines revise
/// earlier words as more audio arrives, so consumers should replace rather than append.
struct TranscriptionChunk: Sendable {
    let text: String
    /// `true` once the engine has committed everything it will emit for this session.
    let isFinal: Bool
}

/// The seam that keeps Murmur YouTube engine-agnostic.
///
/// Apple's `SpeechAnalyzer` ships with macOS 26 and needs no model download, so it is
/// the default. Parakeet (FluidAudio, CoreML/ANE) scores better on English and is the
/// intended upgrade — implementing this protocol is the whole cost of switching.
protocol TranscriptionEngine: Actor {
    /// Audio format the engine wants buffers delivered in. `AudioCapture` converts to it.
    func preferredInputFormat() async -> sending AVAudioFormat?

    /// Prepare models and open a session. Emits snapshots until `finish()` is called.
    func start() async throws -> AsyncThrowingStream<TranscriptionChunk, Error>

    /// Feed one buffer of captured microphone audio, already in `preferredInputFormat()`.
    func feed(_ chunk: AudioChunk) async

    /// Close the session and flush any pending final results.
    func finish() async
}

enum TranscriptionError: LocalizedError {
    case localeUnsupported(Locale)
    case modelInstallFailed(String)
    case noAudioFormat
    case notRunning

    var errorDescription: String? {
        switch self {
        case .localeUnsupported(let locale):
            return "Dictation isn't available for \(locale.identifier) on this Mac."
        case .modelInstallFailed(let detail):
            return "Couldn't install the speech model: \(detail)"
        case .noAudioFormat:
            return "No compatible audio format available for the speech engine."
        case .notRunning:
            return "The transcription engine isn't running."
        }
    }
}
