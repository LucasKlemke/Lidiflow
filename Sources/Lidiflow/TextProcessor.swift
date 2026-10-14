import Foundation

struct Replacement: Codable, Identifiable, Hashable {
    var id = UUID()
    var from: String
    var to: String
}

struct Snippet: Codable, Identifiable, Hashable {
    var id = UUID()
    var trigger: String
    var expansion: String
}

/// Applies the user's dictionary replacements and snippets to a transcription.
enum TextProcessor {
    static func apply(_ text: String, replacements: [Replacement], snippets: [Snippet]) -> String {
        var output = text
        for replacement in replacements where !replacement.from.isEmpty {
            output = replaceWords(replacement.from, with: replacement.to, in: output)
        }

        // A dictation that is only the trigger phrase becomes the whole expansion.
        let spoken = normalize(output)
        if let snippet = snippets.first(where: { !$0.trigger.isEmpty && normalize($0.trigger) == spoken }) {
            return snippet.expansion
        }
        for snippet in snippets where !snippet.trigger.isEmpty {
            output = replaceWords(snippet.trigger, with: snippet.expansion, in: output)
        }
        return output
    }

    /// Vocabulary hint for Whisper so it spells custom words correctly.
    static func whisperPrompt(for words: [String]) -> String? {
        let words = words.map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        return words.isEmpty ? nil : words.joined(separator: ", ") + "."
    }

    private static func replaceWords(_ phrase: String, with replacement: String, in text: String) -> String {
        let pattern = "\\b" + NSRegularExpression.escapedPattern(for: phrase) + "\\b"
        return text.replacingOccurrences(
            of: pattern,
            with: NSRegularExpression.escapedTemplate(for: replacement),
            options: [.regularExpression, .caseInsensitive]
        )
    }

    private static func normalize(_ text: String) -> String {
        text.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .joined()
    }
}
