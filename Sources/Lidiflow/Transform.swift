import Foundation

/// A saved instruction that rewrites text. Without its own shortcut, the first nine
/// are bound to base key + 1…9 by position.
struct Transform: Codable, Identifiable, Hashable {
    var id = UUID()
    var name: String
    var prompt: String
    var shortcut: KeyCombo? = nil

    static let defaults = [
        Transform(name: "Mais formal", prompt: "Reescreva em tom formal e profissional."),
        Transform(name: "Mais casual", prompt: "Reescreva em tom casual e amigável, como uma conversa."),
        Transform(name: "Corrigir", prompt: "Corrija ortografia, gramática e pontuação sem mudar o estilo."),
        Transform(name: "Resumir", prompt: "Resuma em poucas frases, mantendo o essencial."),
        Transform(name: "Tópicos", prompt: "Transforme em uma lista de tópicos clara e objetiva."),
        Transform(name: "Email", prompt: "Transforme em um email bem estruturado, com saudação e despedida."),
        Transform(name: "Inglês", prompt: "Traduza para o inglês, mantendo o tom."),
        Transform(name: "Mais curto", prompt: "Deixe o texto mais curto e direto, sem perder informação importante."),
    ]
}
