import SwiftUI

/// Bars for the most recent microphone levels, newest on the right, so speech scrolls across like a wave.
/// Used in the island while recording and next to the microphone picker in Ajustes.
struct Waveform: View {
    /// RMS levels (0–1), oldest first.
    let levels: [Float]
    var color: Color
    var height: CGFloat = 22

    var body: some View {
        HStack(alignment: .center, spacing: 2) {
            ForEach(levels.indices, id: \.self) { index in
                Capsule()
                    .fill(color)
                    .frame(width: 3, height: 3 + (height - 3) * Self.loudness(levels[index]))
            }
        }
        .frame(height: height)
        .animation(.easeOut(duration: 0.08), value: levels)
    }

    /// Decibels rather than raw RMS: a normal voice sits around -30 dBFS, which on a linear scale
    /// barely moves the bars. -55 dB (room noise) is flat and -15 dB (loud) fills the bar.
    static func loudness(_ rms: Float) -> CGFloat {
        let decibels = 20 * log10(max(rms, 1e-6))
        return CGFloat(min(max((decibels + 55) / 40, 0), 1))
    }
}

extension Array where Element == Float {
    /// Appends a level and drops the oldest, keeping the waveform's width.
    mutating func pushLevel(_ level: Float) {
        guard !isEmpty else { return }
        removeFirst()
        append(level)
    }
}
