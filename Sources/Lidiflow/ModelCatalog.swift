import Foundation

/// What the app knows about each cloud AI model: list price and a rough intelligence × speed rating.
/// One table, so a model's price and its bars can't drift apart.
enum ModelCatalog {
    struct Price {
        /// US$ per 1M tokens.
        let input: Double
        let output: Double
    }

    /// 0–100. Relative estimates, so the bigger, slower models and the cheaper mini/nano/flash ones
    /// can be told apart at a glance; not a benchmark.
    struct Rating {
        let intelligence: Int
        let speed: Int
    }

    private struct Entry {
        let id: String
        var price: Price?
        var rating: Rating?
    }

    private static func model(_ id: String, _ input: Double, _ output: Double, _ intelligence: Int, _ speed: Int) -> Entry {
        Entry(id: id, price: Price(input: input, output: output), rating: Rating(intelligence: intelligence, speed: speed))
    }

    private static func priced(_ id: String, _ input: Double, _ output: Double) -> Entry {
        Entry(id: id, price: Price(input: input, output: output))
    }

    private static func rated(_ id: String, _ intelligence: Int, _ speed: Int) -> Entry {
        Entry(id: id, rating: Rating(intelligence: intelligence, speed: speed))
    }

    private static let entries: [String: Entry] = Dictionary(uniqueKeysWithValues: [
        // OpenAI
        model("gpt-6-astra", 10, 50, 97, 40), model("gpt-6.1-sol", 2, 10, 93, 62),
        model("gpt-6-sol", 2, 10, 91, 62), model("gpt-6-luna", 0.10, 0.50, 74, 95),
        model("gpt-5.6-sol", 4, 20, 92, 50), model("gpt-5.6-terra", 2, 12, 86, 66), model("gpt-5.6-luna", 0.20, 1.20, 70, 92),
        model("gpt-5.5", 5, 30, 90, 45), priced("gpt-5.5-pro", 30, 180),
        model("gpt-5.4", 2.50, 15, 86, 55), model("gpt-5.4-mini", 0.75, 4.50, 76, 82),
        model("gpt-5.4-nano", 0.20, 1.25, 64, 92), priced("gpt-5.4-pro", 30, 180),
        priced("gpt-5.3-codex", 1.75, 14), model("gpt-5.2", 1.75, 14, 84, 55), priced("gpt-5.2-pro", 21, 168),
        model("gpt-5.1", 1.25, 10, 82, 55), model("gpt-5", 1.25, 10, 80, 50), model("gpt-5-mini", 0.25, 2, 70, 78),
        model("gpt-5-nano", 0.05, 0.40, 58, 90), priced("gpt-5-pro", 15, 120),
        model("gpt-4.1", 2, 8, 72, 70), model("gpt-4.1-mini", 0.40, 1.60, 62, 85), model("gpt-4.1-nano", 0.10, 0.40, 50, 95),
        model("gpt-4o", 2.50, 10, 68, 72), priced("gpt-4o-2024-05-13", 5, 15), model("gpt-4o-mini", 0.15, 0.60, 55, 88),
        model("o1", 15, 60, 74, 25), priced("o1-pro", 150, 600), model("o1-mini", 1.10, 4.40, 62, 55),
        model("o3", 2, 8, 80, 35), priced("o3-pro", 20, 80), model("o3-mini", 1.10, 4.40, 66, 60),
        model("o4-mini", 1.10, 4.40, 72, 60),
        model("gpt-4-turbo", 10, 30, 58, 55), model("gpt-4", 30, 60, 50, 40),
        model("gpt-3.5-turbo", 0.50, 1.50, 35, 90), priced("gpt-3.5-turbo-1106", 1, 2),
        // Anthropic
        model("claude-fable-5-1", 10, 50, 99, 30), model("claude-fable-5", 10, 50, 98, 30),
        model("claude-opus-5-5", 4, 20, 95, 45), model("claude-opus-5", 5, 25, 93, 40),
        model("claude-opus-4-8", 5, 25, 90, 40), model("claude-opus-4-7", 5, 25, 89, 40),
        model("claude-opus-4-6", 5, 25, 88, 40), model("claude-opus-4-5", 5, 25, 87, 40),
        model("claude-opus-4-1", 15, 75, 84, 30), model("claude-opus-4-0", 15, 75, 82, 30), model("claude-opus-4", 15, 75, 82, 30),
        model("claude-sonnet-5-5", 2, 10, 90, 65), model("claude-sonnet-5", 2, 10, 87, 62),
        model("claude-sonnet-4-6", 3, 15, 84, 60), model("claude-sonnet-4-5", 3, 15, 82, 60),
        model("claude-sonnet-4-0", 3, 15, 78, 60), model("claude-sonnet-4", 3, 15, 78, 60),
        model("claude-haiku-5-5", 0.10, 0.50, 75, 92), model("claude-haiku-4-5", 1, 5, 68, 85),
        // Groq (very fast hardware)
        model("llama-3.3-70b-versatile", 0.59, 0.79, 62, 97), model("llama-3.1-8b-instant", 0.05, 0.08, 40, 100),
        rated("openai/gpt-oss-120b", 74, 96), rated("openai/gpt-oss-20b", 60, 99), rated("qwen/qwen3-32b", 66, 95),
        // Google
        model("gemini-2.5-pro", 1.25, 10, 85, 50), model("gemini-2.5-flash", 0.30, 2.50, 74, 85),
        model("gemini-2.5-flash-lite", 0.10, 0.40, 60, 95),
        // xAI
        model("grok-4", 3, 15, 88, 45), model("grok-4-fast-reasoning", 0.20, 0.50, 80, 85),
        model("grok-4-fast-non-reasoning", 0.20, 0.50, 72, 92), model("grok-4-1-fast-reasoning", 0.20, 0.50, 82, 85),
        model("grok-4-1-fast-non-reasoning", 0.20, 0.50, 74, 92),
        model("grok-3", 3, 15, 78, 60), model("grok-3-mini", 0.30, 0.50, 65, 80),
        // Ollama, on this Mac: free, and the speed depends on the machine.
        rated("qwen2.5:3b", 35, 85), rated("llama3.2:3b", 32, 88), rated("phi3:mini", 30, 86), rated("qwen2.5:7b", 48, 65),
        rated("llama3.1:8b", 45, 62), rated("mistral:7b", 42, 66), rated("gemma2:9b", 50, 55),
    ].map { ($0.id, $0) })

    static func price(for model: String) -> Price? { entry(for: model)?.price }
    static func rating(for model: String) -> Rating? { entry(for: model)?.rating }

    /// The exact id, or the id with a snapshot suffix (gpt-4o-2024-08-06, claude-sonnet-4-20250514,
    /// gpt-4-0613, gpt-5-latest). Never a different model: grok-4-fast doesn't match grok-4.
    private static func entry(for model: String) -> Entry? {
        if let exact = entries[model] { return exact }
        guard let match = model.wholeMatch(of: snapshot) else { return nil }
        return entries[String(match.output.1)]
    }

    private static let snapshot = try! Regex(#"^(.+?)-(?:\d{4}-\d{2}-\d{2}|\d{8}|\d{4}|latest)$"#,
                                             as: (Substring, Substring).self)
}
