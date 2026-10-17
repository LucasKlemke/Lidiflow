<p align="center">
  <img src="docs/images/icon.png" width="128" alt="Ícone do Lidiflow">
</p>

<h1 align="center">Lidiflow</h1>

<p align="center">
  Ditado por voz para macOS, <b>gratuito</b>. Segure uma tecla, fale e solte: o texto aparece onde o cursor estiver.<br>
  Funciona com <b>modelos locais</b> (nada sai do seu Mac) ou com a <b>sua chave de API</b>.
</p>

<p align="center">
  <a href="https://github.com/LucasKlemke/Lidiflow/releases/latest/download/Lidiflow.dmg"><img src="https://img.shields.io/badge/Baixar%20para%20Mac-macOS%2014%2B%20%C2%B7%20Intel%20e%20Apple%20Silicon-1F6BFF?style=for-the-badge&logo=apple&logoColor=white" alt="Baixar o Lidiflow para Mac" height="44"></a>
  &nbsp;
  <a href="#windows"><img src="https://img.shields.io/badge/Windows-em%20breve-9C9CA3?style=for-the-badge&logo=windows&logoColor=white" alt="Lidiflow para Windows: em breve" height="44"></a>
</p>

<p align="center">
  <sub>Grátis · sem cadastro · assinado e verificado pela Apple · <a href="#instalar-no-mac">como instalar em 3 passos</a></sub>
</p>

<p align="center">
  <img src="docs/images/transforms.png" alt="Tela de Transforms do Lidiflow">
</p>

---

## O que é

O Lidiflow é um app de ditado parecido com o [Wispr Flow](https://wisprflow.ai): você fala em qualquer app (Slack, email, Notion, navegador, editor de código) e o texto é transcrito, limpo e colado na hora.

A diferença é que ele é **gratuito** e você escolhe onde a IA roda:

- **No seu Mac:** a transcrição roda no próprio app (modelo da Apple, Parakeet, Nemotron e outros), sem instalar nada. Privado e sem custo. Para a IA local, use o [Ollama](https://ollama.com).
- **Na sua conta:** use a sua chave de API da OpenAI, Anthropic (Claude), Groq, Google Gemini ou xAI (Grok).

> O Lidiflow é um projeto independente, escrito do zero em Swift. Não usa código nem recursos do Wispr Flow e não tem relação com a Wispr.

## Recursos

- **Segure e fale:** segure `Fn` (ou o atalho que você quiser), fale e solte. O texto é colado no app em foco, e o seu clipboard é preservado.
- **Mãos livres:** `Fn + Espaço` começa a gravar sem precisar segurar; aperte de novo para parar.
- **Modo comando:** selecione um texto, segure `Fn + ⌃` e diga o que mudar ("traduz pra inglês", "deixa mais formal").
- **Transforms:** reescritas com um atalho (mais formal, resumir, tópicos, email…). Crie os seus, com sugestões geradas pela IA, ou aplique um transform automaticamente em tudo o que você dita.
- **Atalhos livres:** até 2 atalhos por ação. Pode ser uma tecla F, uma combinação, um modificador sozinho (⇧ direito, Fn) ou um botão extra do mouse.
- **Dicionário e correções:** ensine nomes e siglas para o Lidiflow escrever do seu jeito.
- **Snippets:** diga "meu email" e ele insere o texto completo.
- **Ajuste com IA:** remove "é…", "tipo", repetições e corrige a pontuação, sem responder o que você perguntou.
- **Instruções para a IA:** um texto seu que vai junto no ajuste, no modo comando e nos transforms.
- **Microfone:** escolha qual usar, com uma onda ao vivo para ver se ele está te ouvindo.
- **Histórico** de ditados, estatísticas, tema claro, escuro ou do sistema.

## Modelos

<p align="center">
  <img src="docs/images/models.png" alt="Escolha de modelos do Lidiflow">
</p>

Na aba **Modelos**, cada parte responde a uma pergunta: *No meu Mac* ou *Minha conta (chave de API)*?

| | No meu Mac (grátis) | Minha conta (chave de API) |
|---|---|---|
| **Transcrição** | modelo da Apple (macOS 26+, baixado e gerenciado pelo próprio macOS), Nemotron Streaming 3.5, Parakeet TDT v3, Qwen3-ASR, Cohere Transcribe e Whisper (Base a Large v3), com download dentro do app | OpenAI (`gpt-4o-transcribe`…) ou Groq (`whisper-large-v3-turbo`…) |
| **IA** (ajuste, comando, transforms) | Ollama: Qwen 2.5, Llama 3.x, Mistral, Gemma 2, Phi-3… | OpenAI, Anthropic, Groq, Gemini ou xAI |

As chaves de API ficam guardadas no **Keychain do macOS**, nunca em arquivos.

## Atalhos

<p align="center">
  <img src="docs/images/settings.png" alt="Ajustes e atalhos do Lidiflow">
</p>

| Ação | Padrão |
|---|---|
| Ditar (segurar) | `Fn` |
| Mãos livres | `Fn + Espaço` |
| Modo comando (segurar) | `Fn + ⌃` |
| Transform 1…9 | `Fn + 1…9` (ou o atalho de cada transform) |
| Colar / copiar a última transcrição | `⌃⌘V` / `⌃⌘C` |
| Cancelar | `Esc` |

Todos podem ser trocados em **Ajustes → Atalhos**.

## Instalar no Mac

<table>
  <tr>
    <td align="center" width="33%"><h3>1</h3><b>Baixe</b></td>
    <td align="center" width="33%"><h3>2</h3><b>Arraste para Aplicativos</b></td>
    <td align="center" width="33%"><h3>3</h3><b>Abra e siga a tela de boas-vindas</b></td>
  </tr>
  <tr>
    <td valign="top">Clique em <a href="https://github.com/LucasKlemke/Lidiflow/releases/latest/download/Lidiflow.dmg"><b>Baixar para Mac</b></a>. Vem sempre a versão mais recente.</td>
    <td valign="top">Abra o <code>Lidiflow.dmg</code> e arraste o ícone do Lidiflow para a pasta <b>Aplicativos</b>.</td>
    <td valign="top">Abra o Lidiflow em Aplicativos. Clique em <b>Permitir</b> no microfone e em <b>Abrir Ajustes</b> na Acessibilidade (ative o Lidiflow na lista). A transcrição baixa sozinha.</td>
  </tr>
</table>

Pronto: **segure `Fn`, fale e solte.** O texto aparece onde o cursor estiver.

Requisitos: macOS 14 ou superior, Intel ou Apple Silicon. Não precisa instalar mais nada nem usar o Terminal.

<details>
<summary><b>Problemas na instalação?</b></summary>

- **O macOS pergunta se quer abrir um app baixado da internet:** é o aviso normal da primeira abertura. Clique em **Abrir**. O Lidiflow é assinado e verificado (notarizado) pela Apple.
- **A tecla `Fn` abre os emojis ou o ditado do macOS:** em Ajustes do Sistema → Teclado, deixe **"Ao pressionar 🌐"** como **Não fazer nada**.
- **Não cola o texto:** confira se o Lidiflow está ativado em Ajustes do Sistema → Privacidade e Segurança → **Acessibilidade**.
- **Versões anteriores:** na [página de releases](https://github.com/LucasKlemke/Lidiflow/releases).

</details>

<details>
<summary><b>Opcional, para usuários avançados</b></summary>

- **Modelos Whisper:** precisam do whisper.cpp, instalado com o [Homebrew](https://brew.sh): `brew install whisper-cpp`. Os outros modelos rodam no próprio app.
- **IA local** (ajuste de texto, modo comando e transforms sem chave de API): instale o [Ollama](https://ollama.com/download). Com uma chave de API (OpenAI, Anthropic, Groq, Gemini ou xAI), não precisa.

</details>

## Windows

<p>
  <img src="https://img.shields.io/badge/Windows-em%20breve-9C9CA3?style=for-the-badge&logo=windows&logoColor=white" alt="Lidiflow para Windows: em breve" height="36">
</p>

O instalador para Windows ainda não está disponível. Existe uma versão para Windows 10 e 11 em [`windows/`](windows/README.md) (C# / WPF), que por enquanto só pode ser compilada a partir do código: veja o [README do Windows](windows/README.md).

## Compilar do código

Requisitos: macOS 14 ou superior e Xcode 16+ (Swift 5.10+).

```sh
git clone https://github.com/LucasKlemke/Lidiflow.git
cd Lidiflow
./build.sh --install
open /Applications/Lidiflow.app
```

Na primeira abertura, a tela de boas-vindas pede as permissões e prepara a transcrição. Para vê-la de novo: `open /Applications/Lidiflow.app --args --onboarding` (ou `--onboarding-demo`, como num Mac novo).

## Como funciona

```
tecla segurada ─▶ AVAudioEngine (16 kHz) ─▶ modelo local (Apple, transcribe.cpp, whisper.cpp) / API
                                                     │
                     colar no app em foco ◀─ dicionário, snippets ◀─ ajuste / transform com IA
```

- App nativo em **Swift + SwiftUI**, sem Electron. Os atalhos usam um `CGEventTap`, e o texto é inserido via clipboard + `⌘V`, com o clipboard restaurado logo depois.
- A transcrição local usa o SpeechAnalyzer da Apple (macOS 26+), o [transcribe.cpp](https://github.com/handy-computer/transcribe.cpp) dentro do app (modelos GGUF como Parakeet e Nemotron, na GPU via Metal) ou o `whisper-server` do whisper.cpp, sempre com o modelo carregado para responder rápido.
- O histórico, o dicionário e os transforms ficam em `~/Library/Application Support/Lidiflow`.

## Desenvolvimento

```sh
swift build          # compila
./build.sh           # gera dist/Lidiflow.app
./build.sh --install # copia para /Applications
./build.sh --release # app universal (Intel + Apple Silicon) em dist/Lidiflow-x.y.z.zip e dist/Lidiflow.dmg (requer: brew install create-dmg)
```

Com um certificado *Developer ID Application* no Keychain, o `build.sh` assina o app com ele (hardened runtime), e o `--release` também envia o `.dmg` para a notarização da Apple, usando o perfil `lidiflow` do `xcrun notarytool store-credentials`. Sem certificado, o build é assinado ad-hoc, com o identificador `com.lidiflow.app` fixo, para o macOS manter as permissões entre builds.

## Licença

[MIT](LICENSE). Use, modifique e distribua à vontade.

---

Os logos dos provedores em `Resources/ProviderIcons` são marcas de seus respectivos donos e aparecem só para identificar cada serviço.
