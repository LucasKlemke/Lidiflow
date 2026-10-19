import Foundation

/// Splits the "Eles" track by voice with sherpa-onnx (meetings, phase 3; see docs/reunioes.md).
enum Diarizer {
    struct Turn: Equatable {
        let start: TimeInterval
        let end: TimeInterval
        let speaker: Int
    }

    enum Failure: LocalizedError {
        case notInstalled(String)
        case failed(String)

        var errorDescription: String? {
            switch self {
            case .notInstalled(let message), .failed(let message): message
            }
        }
    }

    /// Installs what is missing, then returns who spoke when in the track.
    @MainActor
    static func turns(in url: URL) async throws -> [Turn] {
        for dependency in [Dependency.diarizer, .segmentationModel, .voiceModel] {
            await dependency.install()
            if case .failed(let message) = dependency.state { throw Failure.notInstalled(message) }
        }
        guard let program = Dependency.diarizer.binaryPath,
              let segmentation = Dependency.segmentationModel.binaryPath,
              let voice = Dependency.voiceModel.binaryPath else { throw Failure.notInstalled("separador de vozes ausente") }
        let output = try await Task.detached { try run(program, arguments: [
            "--clustering.cluster-threshold=0.4",
            "--segmentation.pyannote-model=\(segmentation)",
            "--embedding.model=\(voice)",
            url.path,
        ]) }.value
        return parse(output)
    }

    /// Lines like `0.031 -- 5.043 speaker_00`.
    static func parse(_ output: String) -> [Turn] {
        output.split(separator: "\n").compactMap { line in
            let parts = line.split(separator: " ")
            guard parts.count == 4, parts[1] == "--", parts[3].hasPrefix("speaker_"),
                  let start = Double(parts[0]), let end = Double(parts[2]),
                  let speaker = Int(parts[3].dropFirst("speaker_".count)) else { return nil }
            return Turn(start: start, end: end, speaker: speaker)
        }
    }

    /// Gives each "Eles" segment the voice that overlaps it most, numbering the voices by first appearance.
    /// Returns the segments and how many voices there are.
    static func assign(_ segments: [Meeting.Segment], turns: [Turn]) -> ([Meeting.Segment], Int) {
        var order: [Int: Int] = [:]
        let assigned = segments.map { segment -> Meeting.Segment in
            guard segment.source == .them else { return segment }
            var overlap: [Int: TimeInterval] = [:]
            for turn in turns {
                let shared = min(segment.end, turn.end) - max(segment.start, turn.start)
                if shared > 0 { overlap[turn.speaker, default: 0] += shared }
            }
            var copy = segment
            if let best = overlap.max(by: { $0.value < $1.value })?.key {
                if order[best] == nil { order[best] = order.count }
                copy.speaker = order[best]
            }
            return copy
        }
        return (assigned, order.count)
    }

    private static func run(_ program: String, arguments: [String]) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: program)
        process.arguments = arguments
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        // Read before waiting: a full pipe would block the program forever.
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw Failure.failed("o separador de vozes terminou com erro \(process.terminationStatus)") }
        return String(decoding: data, as: UTF8.self)
    }
}
