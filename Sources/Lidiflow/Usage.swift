import Foundation

/// One billed request to a cloud provider: a transcription or an AI pass.
struct UsageRecord: Codable, Identifiable, Hashable {
    enum Kind: String, Codable {
        case transcription, ai
    }

    var id = UUID()
    var date = Date()
    let provider: LLMProvider
    let model: String
    let kind: Kind
    var inputTokens = 0
    var outputTokens = 0
    var audioSeconds: Double = 0
    /// Estimated cost in US dollars; nil when the model isn't in the price table.
    let cost: Double?
}

/// Public list prices, used to estimate what each request cost on the user's key.
enum Pricing {
    /// US$ per minute of audio.
    private static let audio: [String: Double] = [
        "whisper-1": 0.006, "gpt-4o-transcribe": 0.006, "gpt-4o-mini-transcribe": 0.003, "gpt-transcribe": 0.0045,
        "whisper-large-v3-turbo": 0.04 / 60, "whisper-large-v3": 0.111 / 60,
    ]

    /// Prompt-cache reads bill at 10% of the input price and writes at 125% (Anthropic's rates).
    static func cost(model: String, inputTokens: Int, outputTokens: Int,
                     cacheReadTokens: Int = 0, cacheWriteTokens: Int = 0) -> Double? {
        guard let price = ModelCatalog.price(for: model) else { return nil }
        let input = Double(inputTokens) + Double(cacheReadTokens) * 0.1 + Double(cacheWriteTokens) * 1.25
        return (input * price.input + Double(outputTokens) * price.output) / 1_000_000
    }

    static func cost(model: String, provider: LLMProvider, audioSeconds: Double) -> Double? {
        guard let perMinute = audio[model] else { return nil }
        // Groq bills at least 10 seconds per request.
        let billed = provider == .groq ? max(audioSeconds, 10) : audioSeconds
        return billed / 60 * perMinute
    }
}

/// Spending log for the cloud providers, saved next to the dictation history.
final class UsageLog: ObservableObject {
    static let shared = UsageLog()

    @Published private(set) var records: [UsageRecord] = []

    private let url = Settings.supportDirectory.appendingPathComponent("usage.json")

    private init() {
        if let data = try? Data(contentsOf: url),
           let saved = try? JSONDecoder().decode([UsageRecord].self, from: data) {
            records = saved
        }
    }

    /// `inputTokens` excludes the prompt-cache tokens, which bill at their own rates.
    func recordAI(provider: LLMProvider, model: String, inputTokens: Int, outputTokens: Int,
                  cacheReadTokens: Int = 0, cacheWriteTokens: Int = 0) {
        let allInput = inputTokens + cacheReadTokens + cacheWriteTokens
        guard provider.needsKey, allInput + outputTokens > 0 else { return }
        add(UsageRecord(provider: provider, model: model, kind: .ai,
                        inputTokens: allInput, outputTokens: outputTokens,
                        cost: Pricing.cost(model: model, inputTokens: inputTokens, outputTokens: outputTokens,
                                           cacheReadTokens: cacheReadTokens, cacheWriteTokens: cacheWriteTokens)))
    }

    func recordTranscription(provider: LLMProvider, model: String, audioSeconds: Double) {
        add(UsageRecord(provider: provider, model: model, kind: .transcription, audioSeconds: audioSeconds,
                        cost: Pricing.cost(model: model, provider: provider, audioSeconds: audioSeconds)))
    }

    func clear() {
        records.removeAll()
        save()
    }

    private func add(_ record: UsageRecord) {
        DispatchQueue.main.async {
            self.records.insert(record, at: 0)
            if self.records.count > Self.limit { self.records.removeLast(self.records.count - Self.limit) }
            self.save()
        }
    }

    /// Several years of daily dictation; older requests drop off the end.
    private static let limit = 20_000
    private let saveQueue = DispatchQueue(label: "com.lidiflow.usage", qos: .utility)

    /// Encodes and writes off the main thread; the serial queue keeps saves in order.
    private func save() {
        let records = records
        let url = url
        saveQueue.async {
            try? JSONEncoder().encode(records).write(to: url, options: .atomic)
        }
    }
}
