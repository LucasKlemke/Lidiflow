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

    /// Drops the sentences Whisper-family models invent on silence or noise, learned from YouTube
    /// subtitles ("Inscreva-se no canal…", "Legendas pela comunidade Amara.org", "Thanks for watching").
    static func removeHallucinations(_ text: String) -> String {
        var kept: [String] = []
        text.enumerateSubstrings(in: text.startIndex..., options: .bySentences) { sentence, _, _, _ in
            guard let sentence else { return }
            let folded = sentence.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
            if !hallucinations.contains(where: { folded.contains($0) }) { kept.append(sentence) }
        }
        return kept.joined().trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Folded (lowercase, no accents) fragments.
    private static let hallucinations = [
        "inscreva-se no canal", "inscrevam-se no canal", "ative o sininho", "ativem o sininho",
        "deixe seu like", "deixem seu like", "obrigado por assistir", "obrigada por assistir",
        "legendas pela comunidade amara", "legendado por", "amara.org",
        "subscribe to my channel", "subscribe to the channel", "thanks for watching", "thank you for watching",
        "subtitles by the amara", "suscribete al canal", "gracias por ver", "subtitulos realizados por",
    ]

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
