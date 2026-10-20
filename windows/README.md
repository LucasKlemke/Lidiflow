# Lidiflow para Windows

Versão Windows do Lidiflow, em C# / .NET 10 + WPF. Ela tem os mesmos recursos do app de macOS: segure, fale e solte; mãos livres; modo comando; transforms; dicionário e snippets; modelos locais (whisper.cpp e Ollama) ou chave de API. Os arquivos de dados ficam no mesmo formato, exceto o histórico, que no Windows é criptografado para o usuário.

Requisitos: Windows 10 ou 11 (x64).

## Compilar e instalar

```powershell
winget install Microsoft.DotNet.SDK.10
git clone https://github.com/LucasKlemke/Lidiflow.git
cd Lidiflow
powershell -ExecutionPolicy Bypass -File windows\build.ps1 -Install
```

O script compila em `windows\dist`, instala em `%LOCALAPPDATA%\Programs\Lidiflow`, cria o atalho no Menu Iniciar e abre o app. Sem `-Install`, ele só compila.

Para desenvolver: `dotnet run --project windows\Lidiflow`.

## Primeiro uso

1. Em **Modelos**, clique em **Instalar whisper.cpp**. O app baixa a versão oficial do GitHub. Depois baixe um modelo (recomendado: *Large v3 Turbo (comprimido)*). Se preferir, cole uma chave de API.
2. Para usar IA local, instale o [Ollama para Windows](https://ollama.com/download/windows).
3. Segure `Ctrl + Win` e fale.

## Barra de ditado

Uma barra no pé da tela, no monitor onde está o mouse. Parada, é uma barrinha; com o mouse em cima mostra **Ditar**, o
atalho e um botão de microfone (dita sem segurar a tecla). Gravando, mostra as ondas, **X** para cancelar e **■** para
finalizar e colar. Arraste para encaixá-la embaixo, à esquerda ou à direita (nas laterais fica em pé); o clique direito
tem colar a última transcrição, microfone, posição, esconder por 1 hora e abrir o Lidiflow. Pode ser desligada em
**Ajustes** (aí só aparece durante o ditado). A barra nunca pega o foco, então o texto vai para o app em que você está.

## Onde o ditado é colado

Antes de colar, o Lidiflow pergunta à UI Automation o que está em foco. Num campo de texto, cola. Onde com certeza não há
campo (botão, lista, área de trabalho ou pasta do Explorer) não cola nem mexe na área de transferência: o ditado fica
no histórico e a barra mostra "Clique num campo de texto e use {atalho} para colar". Quando não dá para saber (alguns
navegadores e apps Electron), cola como antes. A área de transferência anterior é restaurada depois de cada colagem.

## Reuniões

Na tela **Reuniões** (ou pelo menu da bandeja, ou pelo clique direito na barra), **Gravar reunião** grava duas trilhas
em `%LOCALAPPDATA%\Lidiflow\meetings\<data-hora>\`: o microfone (**Você**) e o áudio do PC por WASAPI loopback
(**Eles**), em 16 kHz mono. Durante a gravação a barra mostra `● tempo ■`, e ditar continua funcionando. Ao parar, o
Lidiflow acha os trechos com fala, transcreve cada um com o motor do ditado, tira o eco de quem está sem fone e gera o
resumo (decisões, tarefas, pontos em aberto) com a IA de Modelos. Fechar o app no meio salva o que foi gravado e o
processamento continua na próxima vez. Detalhes em [`docs/reunioes.md`](../docs/reunioes.md).

Com **Detectar reuniões** ligado (Ajustes), quando Zoom, Teams, Webex, Slack, Discord ou um navegador com Meet/Teams/Zoom aberto passa a usar o microfone, a barra oferece **Gravar**; quando a chamada acaba, oferece **Parar e resumir**.

Os outros participantes são separados por voz (`Pessoa 1`, `Pessoa 2`…) pelo [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), baixado na primeira reunião para `%LOCALAPPDATA%\Lidiflow\diarization`; no painel **Vozes** da transcrição dá para ouvir cada voz, dar nome e juntar duas que são a mesma pessoa.

Na tela Reuniões, e em cada reunião, dá para **perguntar** sobre o que foi dito (a resposta cita `[título · mm:ss]` e abre a transcrição). As tarefas do resumo podem ser marcadas como feitas, copiadas como lista ou enviadas por e-mail.

## Atalhos padrão

O Windows não enxerga a tecla `Fn` (quem cuida dela é o próprio teclado), então `Ctrl + Win` faz o papel dela:

| Ação | Padrão |
|---|---|
| Ditar (segurar) | `Ctrl + Win` |
| Mãos livres | Dois toques em `Ctrl + Win`, ou `Ctrl + Win + Espaço` (um toque para parar) |
| Modo comando (segurar) | `Ctrl + Win + Alt` |
| Transform 1…9 | `Ctrl + Win + 1…9` |
| Colar / copiar a última transcrição | `Win + Alt + V` / `Win + Alt + C` |
| Cancelar | `Esc` |

Todos podem ser trocados em **Ajustes → Atalhos**.

## Como funciona (equivalências com o macOS)

| macOS | Windows |
|---|---|
| `CGEventTap` | Hooks globais `WH_KEYBOARD_LL` / `WH_MOUSE_LL` |
| Clipboard + `⌘V`, seleção via Acessibilidade | Clipboard + `Ctrl+V` via `SendInput`, seleção via `Ctrl+Insert` (copia sem interromper programas no terminal) |
| `AVAudioEngine` a 16 kHz | WaveIn (NAudio) a 16 kHz mono |
| `whisper-server` do Homebrew | `whisper-server.exe` baixado pelo app |
| Keychain | Gerenciador de Credenciais do Windows |
| `UserDefaults` e `~/Library/Application Support/Lidiflow` | `%APPDATA%\Lidiflow` (`settings.json`, `history.dat` criptografado…) |
| Barra de menus | Ícone na bandeja |

Ao soltar `Win` ou `Alt` depois de um atalho, o app envia uma tecla neutra (`0xE8`) para o Menu Iniciar ou a barra de menus do app não abrirem.

Limitação: o Windows não deixa um app comum colar em janelas abertas como administrador.

O texto que o app põe na área de transferência não entra no histórico do `Win + V` nem na sincronização com a nuvem.

Logs: `%APPDATA%\Lidiflow\lidiflow.log` e `whisper-server.log`.
