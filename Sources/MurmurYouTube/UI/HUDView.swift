import SwiftUI

/// Brand palette. Deliberately minimal for the skeleton — this is the surface the real
/// branding pass will replace.
enum Brand {
    static let accent = Color(red: 0.42, green: 0.55, blue: 1.0)
    static let accentWarm = Color(red: 0.76, green: 0.47, blue: 1.0)

    static var gradient: LinearGradient {
        LinearGradient(
            colors: [accent, accentWarm],
            startPoint: .leading,
            endPoint: .trailing
        )
    }
}

struct HUDView: View {
    @Bindable var controller: DictationController

    var body: some View {
        HStack(spacing: 14) {
            Waveform(level: controller.level, isActive: controller.state == .listening)
                .frame(width: 76, height: 26)

            VStack(alignment: .leading, spacing: 0) {
                ForEach(Array(displayLines.enumerated()), id: \.offset) { _, line in
                    Text(line)
                        .font(.system(size: 13, weight: .medium, design: .rounded))
                        .foregroundStyle(isError ? Color.red.opacity(0.9) : .primary.opacity(0.85))
                        .lineLimit(1)
                }
            }
                .frame(maxWidth: .infinity, alignment: .leading)
                .animation(.easeOut(duration: 0.12), value: controller.transcript)
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 14)
        .frame(width: 340, height: 76)
        .background {
            RoundedRectangle(cornerRadius: 22, style: .continuous)
                .fill(.ultraThinMaterial)
                .overlay {
                    RoundedRectangle(cornerRadius: 22, style: .continuous)
                        .strokeBorder(.white.opacity(0.12), lineWidth: 1)
                }
                .shadow(color: .black.opacity(0.28), radius: 18, y: 8)
        }
    }

    private var isError: Bool {
        if case .error = controller.state { return true }
        return false
    }

    private var displayLines: [String] {
        guard controller.state == .listening, !controller.transcript.isEmpty else {
            return [label]
        }

        var lines: [String] = []
        var current = ""
        for word in controller.transcript.split(whereSeparator: { $0.isWhitespace }) {
            let candidate = current.isEmpty ? String(word) : "\(current) \(word)"
            if candidate.count > 32, !current.isEmpty {
                lines.append(current)
                current = String(word)
            } else {
                current = candidate
            }
        }
        if !current.isEmpty { lines.append(current) }
        return Array(lines.suffix(2))
    }

    private var label: String {
        switch controller.state {
        case .starting: "Listening…"
        case .listening: controller.transcript.isEmpty ? "Listening…" : controller.transcript
        // Parakeet transcribes in one pass on release, so there's nothing to show until
        // it lands — say what's happening instead of leaving an empty pill.
        case .finishing: controller.transcript.isEmpty ? "Transcribing…" : controller.transcript
        case .error(let message): message
        case .idle: ""
        }
    }
}

/// Level-reactive bars. Each bar gets a fixed phase offset so the group ripples rather
/// than pumping in unison.
private struct Waveform: View {
    let level: Float
    let isActive: Bool

    private static let barCount = 12
    private static let phases: [Double] = (0..<barCount).map { index in
        // Irrational multiplier keeps the offsets from lining up into a visible period.
        (Double(index) * 0.618).truncatingRemainder(dividingBy: 1)
    }

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30.0, paused: !isActive)) { timeline in
            let t = timeline.date.timeIntervalSinceReferenceDate
            HStack(alignment: .center, spacing: 3) {
                ForEach(0..<Self.barCount, id: \.self) { index in
                    Capsule()
                        .fill(Brand.gradient)
                        .frame(width: 3, height: height(for: index, at: t))
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func height(for index: Int, at time: TimeInterval) -> CGFloat {
        let floorHeight: CGFloat = 3
        guard isActive else { return floorHeight }

        let phase = Self.phases[index]
        let wave = sin(time * 6.0 + phase * .pi * 2)
        let amplitude = CGFloat(max(0.04, level))
        // Wave rides on top of the level so bars still breathe during quiet passages.
        let scaled = amplitude * (0.55 + 0.45 * CGFloat(wave))
        return floorHeight + max(0, scaled) * 23
    }
}
