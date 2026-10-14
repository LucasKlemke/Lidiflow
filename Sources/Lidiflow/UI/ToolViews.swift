import SwiftUI

// MARK: - Dictionary

struct DictionaryView: View {
    @ObservedObject var model: AppModel
    @State private var newWord = ""
    @State private var from = ""
    @State private var to = ""

    var body: some View {
        Page {
            PageHeader(title: "Dicionário", accent: "do seu jeito",
                       subtitle: "Ensine nomes, siglas e termos para o Lidiflow escrever certo.")

            SectionCard(title: "Vocabulário", footer: "Essas palavras viram uma dica para o modelo de transcrição.") {
                VStack(alignment: .leading, spacing: 14) {
                    HStack(spacing: 8) {
                        InlineField(prompt: "Nova palavra (ex.: Supabase, LiderHub, OKR)", text: $newWord, onSubmit: addWord)
                        Button("Adicionar", action: addWord)
                            .buttonStyle(PrimaryButtonStyle())
                            .disabled(newWord.trimmingCharacters(in: .whitespaces).isEmpty)
                    }
                    if !model.dictionary.isEmpty {
                        FlowLayout(spacing: 8) {
                            ForEach(model.dictionary, id: \.self) { word in
                                Chip(title: word, systemImage: "xmark") {
                                    model.dictionary.removeAll { $0 == word }
                                }
                                .help("Remover")
                            }
                        }
                    }
                }
                .padding(16)
            }

            SectionCard(title: "Correções", footer: "Troca automática de palavras que a transcrição costuma errar.") {
                HStack(spacing: 8) {
                    InlineField(prompt: "Quando ouvir…", text: $from)
                    Image(systemName: "arrow.right").foregroundStyle(Theme.secondary)
                    InlineField(prompt: "escrever", text: $to, onSubmit: addReplacement)
                    Button("Adicionar", action: addReplacement)
                        .buttonStyle(PrimaryButtonStyle())
                        .disabled(from.isEmpty || to.isEmpty)
                }
                .padding(16)
                ForEach(model.replacements) { item in
                    VStack(spacing: 0) {
                        Divider().overlay(Theme.border)
                        HStack(spacing: 10) {
                            Text(item.from).foregroundStyle(Theme.secondary)
                            Image(systemName: "arrow.right").font(.system(size: 11)).foregroundStyle(Theme.secondary)
                            Text(item.to).foregroundStyle(Theme.text).fontWeight(.medium)
                            Spacer()
                            Button { model.replacements.removeAll { $0.id == item.id } } label: { Image(systemName: "trash") }
                                .buttonStyle(.plain)
                                .foregroundStyle(Theme.secondary)
                        }
                        .padding(.horizontal, 16)
                        .padding(.vertical, 11)
                    }
                }
            }
        }
    }

    private func addWord() {
        let word = newWord.trimmingCharacters(in: .whitespaces)
        guard !word.isEmpty, !model.dictionary.contains(word) else { return }
        model.dictionary.append(word)
        newWord = ""
    }

    private func addReplacement() {
        guard !from.isEmpty, !to.isEmpty else { return }
        model.replacements.append(Replacement(from: from, to: to))
        from = ""
        to = ""
    }
}

// MARK: - Snippets

struct SnippetsView: View {
    @ObservedObject var model: AppModel
    @State private var trigger = ""
    @State private var expansion = ""

    var body: some View {
        Page {
            PageHeader(title: "Snippets", accent: "na ponta da língua",
                       subtitle: "Diga a frase-gatilho e o Lidiflow insere o texto completo: emails, links, assinaturas.")

            SectionCard(title: "Novo snippet") {
                VStack(alignment: .leading, spacing: 10) {
                    InlineField(prompt: "Quando eu disser… (ex.: meu email)", text: $trigger)
                    TextField("Inserir este texto", text: $expansion, axis: .vertical)
                        .textFieldStyle(.plain)
                        .lineLimit(3...6)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 8)
                        .background(RoundedRectangle(cornerRadius: 8).fill(Theme.background))
                        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Theme.border))
                    HStack {
                        Spacer()
                        Button("Adicionar snippet", action: add)
                            .buttonStyle(PrimaryButtonStyle())
                            .disabled(trigger.trimmingCharacters(in: .whitespaces).isEmpty || expansion.isEmpty)
                    }
                }
                .padding(16)
            }

            if !model.snippets.isEmpty {
                SectionCard(title: "Seus snippets") {
                    ForEach(model.snippets) { snippet in
                        VStack(spacing: 0) {
                            HStack(alignment: .top, spacing: 12) {
                                Text("\u{201C}\(snippet.trigger)\u{201D}")
                                    .font(.system(size: 13, weight: .semibold))
                                    .foregroundStyle(Theme.blue)
                                    .frame(width: 160, alignment: .leading)
                                Text(snippet.expansion)
                                    .foregroundStyle(Theme.text)
                                    .lineLimit(3)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                Button { model.snippets.removeAll { $0.id == snippet.id } } label: { Image(systemName: "trash") }
                                    .buttonStyle(.plain)
                                    .foregroundStyle(Theme.secondary)
                            }
                            .padding(.horizontal, 16)
                            .padding(.vertical, 12)
                            if snippet.id != model.snippets.last?.id { Divider().overlay(Theme.border) }
                        }
                    }
                }
            }
        }
    }

    private func add() {
        model.snippets.append(Snippet(trigger: trigger.trimmingCharacters(in: .whitespaces), expansion: expansion))
        trigger = ""
        expansion = ""
    }
}

struct SectionTitle: View {
    let title: String

    init(_ title: String) {
        self.title = title
    }

    var body: some View {
        Text(title.uppercased())
            .font(.system(size: 11, weight: .semibold))
            .tracking(0.6)
            .foregroundStyle(Theme.secondary)
    }
}
