import Foundation

/// Picks what the AI reads to answer a question about meetings: a local word search before any AI (phase 4).
enum MeetingSearch {
    /// Common Portuguese words that say nothing about which meeting is meant.
    private static let stopwords: Set<String> = [
        "de", "da", "do", "das", "dos", "que", "para", "com", "uma", "por", "mas", "foi", "ser", "tem", "como",
        "sobre", "isso", "essa", "esse", "quem", "qual", "quando", "onde", "reuniao", "reunioes",
    ]

    /// "hoje", "ontem", "semana" (last 7 days) or "mes" (last 30): every meeting of the period, summaries only.
    private static func period(in terms: Set<String>, now: Date) -> (start: Date, end: Date)? {
        let today = Calendar.current.startOfDay(for: now)
        let tomorrow = today.addingTimeInterval(86_400)
        if terms.contains("ontem") { return (today.addingTimeInterval(-86_400), today) }
        if terms.contains("hoje") { return (today, tomorrow) }
        if terms.contains("semana") { return (today.addingTimeInterval(-6 * 86_400), tomorrow) }
        if terms.contains("mes") { return (today.addingTimeInterval(-29 * 86_400), tomorrow) }
        return nil
    }

    private static func summaries(of meetings: [Meeting]) -> String {
        let dates = DateFormatter()
        dates.locale = Locale(identifier: "pt_BR")
        dates.dateFormat = "dd/MM/yyyy HH:mm"
        guard !meetings.isEmpty else { return "Nenhuma reunião gravada nesse período." }
        let blocks = meetings.sorted { $0.startedAt > $1.startedAt }
            .map { "### \($0.title) (\(dates.string(from: $0.startedAt)))\n\($0.summary ?? "")" }
        return String(blocks.joined(separator: "\n\n").prefix(8_000))
    }

    static func words(_ text: String) -> [String] {
        text.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { $0.count > 2 && !stopwords.contains($0) }
    }

    /// The context for the question, up to 8,000 characters: for the 3 meetings with the most hits (or the 3 most
    /// recent, summaries only, when nothing matches), the title, date, summary and matching lines with a line of
    /// context on each side.
    static func context(for question: String, in meetings: [Meeting], now: Date = Date()) -> String {
        let terms = Set(words(question))
        let usable = meetings.filter { $0.status == .ready }
        if let since = period(in: terms, now: now) {
            return summaries(of: usable.filter { $0.startedAt >= since.start && $0.startedAt < since.end })
        }
        let scored = usable.map { meeting -> (Meeting, Int) in
            let haystack = words([meeting.title, meeting.summary ?? "", meeting.transcript].joined(separator: " "))
            return (meeting, haystack.filter(terms.contains).count)
        }
        let hits = scored.filter { $0.1 > 0 }.sorted { $0.1 > $1.1 }.prefix(3).map(\.0)
        let chosen = hits.isEmpty ? Array(usable.prefix(3)) : hits
        let dates = DateFormatter()
        dates.locale = Locale(identifier: "pt_BR")
        dates.dateFormat = "dd/MM/yyyy HH:mm"

        var blocks: [String] = []
        for meeting in chosen {
            var block = "### \(meeting.title) (\(dates.string(from: meeting.startedAt)))\n"
            if let summary = meeting.summary { block += summary + "\n" }
            if !hits.isEmpty {
                let lines = meeting.segments.map { "[\(Meeting.clock($0.start))] \(meeting.label(for: $0)): \($0.text)" }
                var keep = Set<Int>()
                for (index, segment) in meeting.segments.enumerated() where !Set(words(segment.text)).isDisjoint(with: terms) {
                    keep.formUnion(max(0, index - 1)...min(lines.count - 1, index + 1))
                }
                if !keep.isEmpty { block += "Falas:\n" + keep.sorted().map { lines[$0] }.joined(separator: "\n") + "\n" }
            }
            blocks.append(block)
        }
        return String(blocks.joined(separator: "\n").prefix(8_000))
    }
}
