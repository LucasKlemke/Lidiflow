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

## Atalhos padrão

O Windows não enxerga a tecla `Fn` (quem cuida dela é o próprio teclado), então `Ctrl + Win` faz o papel dela:

| Ação | Padrão |
|---|---|
| Ditar (segurar) | `Ctrl + Win` |
| Mãos livres | `Ctrl + Win + Espaço` |
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
