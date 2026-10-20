# Reuniões — fase 1

Gravar uma reunião, transcrever separando **Você** (microfone) de **Eles** (áudio do sistema) e gerar um resumo.
Mac e Windows se comportam igual; este documento é o contrato entre os dois.

## O que a pessoa vê

- **Gravar reunião**: botão no topo da tela **Reuniões**, item no menu do app (barra de menus / bandeja) e no clique
  direito da barra de ditado. Enquanto grava, o mesmo lugar vira **Parar gravação** e a tela mostra o tempo.
- **Barra de ditado durante a gravação** (quando não está ditando): em vez da barrinha parada, uma pílula
  `● 12:34 ■` (ponto azul, tempo em fonte arredondada, botão parar). Embaixo: 112×28. Nas laterais, em pé:
  32×84 (ponto, tempo em duas linhas `12` / `34`, botão). Ditar continua funcionando durante a reunião.
- **Tela Reuniões** (item novo na barra lateral, entre Histórico e Transforms):
  - lista: título (`Reunião de 18/10, 14:05`), duração, estado (`Gravando`, `Transcrevendo 40%`, `Resumindo`,
    `Pronta`, `Só o seu microfone`, `Falhou: <motivo>`);
  - detalhe: **Resumo** (Markdown simples: títulos `##`, listas `-`) e **Transcrição**, uma linha por trecho:
    `[12:04] Você: …` / `[12:09] Eles: …`; botões **Copiar resumo**, **Copiar transcrição**, **Apagar**
    (confirma, apaga a pasta inteira) e **Refazer resumo**.
- **Sem permissão de áudio do sistema** (ou Mac antes do 14.2): grava só o microfone e a reunião mostra
  `Só o seu microfone — libere a gravação do áudio do sistema para captar os outros`.

## Gravação

Duas trilhas, gravadas direto em arquivo enquanto a reunião acontece (uma reunião de 1 h não cabe em memória):

| Trilha | Fonte | Arquivo |
|---|---|---|
| Você | microfone escolhido em Ajustes (mesma regra do automático) | `mic.wav` |
| Eles | áudio do sistema, menos o próprio Lidiflow | `system.wav` |

Formato: WAV PCM 16 bits, mono, 16 kHz (o que o Whisper usa). Converter o que vier (48 kHz estéreo float etc.).

- **Mac**: microfone com uma `AVCaptureSession` própria; sistema com **Core Audio process tap**
  (`CATapDescription` global excluindo o processo do Lidiflow + dispositivo agregado privado + IOProc),
  macOS 14.2+. Requer `NSAudioCaptureUsageDescription` no `Info.plist`.
- **Windows**: microfone com `WaveInEvent` no dispositivo escolhido; sistema com `WasapiLoopbackCapture`
  (NAudio.Wasapi) do dispositivo de saída padrão. Reamostrar para 16 kHz mono.

Pasta da reunião: `<suporte>/meetings/<id>/` com `mic.wav`, `system.wav` e `meeting.json`
(`<suporte>` = `~/Library/Application Support/Lidiflow` no Mac, `%LOCALAPPDATA%\Lidiflow` no Windows — áudio é grande
e não deve sincronizar com o perfil). `<id>` = data e hora `2026-10-18_14-05-12`.

```json
{
  "id": "2026-10-18_14-05-12",
  "title": "Reunião de 18/10, 14:05",
  "startedAt": "2026-10-18T14:05:12-03:00",
  "duration": 2712.4,
  "systemAudio": true,
  "status": "ready",            // recording | transcribing | summarizing | ready | failed
  "progress": 1.0,
  "error": null,
  "segments": [ { "source": "me", "start": 12.4, "end": 15.9, "text": "…" } ],   // source: me | them
  "summary": "## Resumo\n…"
}
```

## Transcrição

1. **Trechos com fala (VAD por energia)**, por trilha: janelas de 30 ms; RMS acima de um limiar adaptativo
   (`max(0.008, min(3 × percentil 10, percentil 90 / 2))` do RMS da trilha — com a mediana, uma trilha quase
   toda falada não teria nenhum trecho detectado) é fala. Juntar falas separadas por menos de 0,8 s; descartar
   trechos com menos de 0,4 s; cortar trechos maiores que 30 s no silêncio mais próximo (ou em 30 s). Margem de
   0,2 s antes e depois.
2. **Transcrever cada trecho** com o motor escolhido (whisper local ou API), com o vocabulário do Dicionário como
   dica, na ordem do tempo, alternando as duas trilhas. Progresso = trechos feitos / total.
3. **Eco**: sem fone, o microfone capta os outros pelo alto-falante. Um trecho **Você** que se sobrepõe no tempo a
   um trecho **Eles** e tem ≥ 60% das palavras iguais (sem acento, minúsculas) é descartado.
4. Ordenar por início; aplicar as Correções do Dicionário; trechos vazios saem.

## Resumo

Pela IA escolhida em Modelos (a mesma do ajuste/transforms). Texto de entrada: a transcrição no formato
`[mm:ss] Você|Eles: texto`. Saída em Markdown, no idioma da reunião:

```
## Resumo
(3 a 6 frases)
## Decisões
- …
## Tarefas
- Quem: o quê (prazo, se dito)
## Pontos em aberto
- …
```

Seções sem conteúdo dizem `- Nenhuma.` A instrução proíbe inventar o que não foi dito. O modelo acima vai na
mensagem, depois da transcrição, para ser **preenchido**; a instrução define que "Você" é quem gravou, que linhas
"Eles" são dos outros (mesmo quando dizem "eu faço") e que cada tarefa começa com `Você:` ou `Participantes:`.
Testado com o Qwen 2.5 3B local: sem o modelo a preencher ele pula seções e dá as tarefas dos outros a "você".
Para reuniões, um modelo maior (Qwen 2.5 7B, ou um da nuvem) resume melhor. Transcrição longa
(> 8.000 caracteres): dividir em partes de até 8.000 caracteres em fronteira de linha, resumir cada parte em notas
(mesmas seções), e gerar o resumo final a partir das notas. Sem IA configurada: a reunião fica `ready` só com a
transcrição e o resumo diz como configurar.

# Fase 2 — detectar a reunião

O Lidiflow percebe que uma chamada começou e oferece gravar; quando ela acaba, oferece parar.

## Sinais (verificados a cada 3 s, só com "Detectar reuniões" ligado em Ajustes — padrão: ligado)

Uma chamada está acontecendo quando **um app de reunião está usando o microfone**:

| App | Mac (bundle id, por prefixo) | Windows (processo) |
|---|---|---|
| Zoom | `us.zoom` | `Zoom.exe` |
| Microsoft Teams | `com.microsoft.teams` | `ms-teams.exe`, `Teams.exe` (e o pacote `MSTeams_8wekyb3d8bbwe`) |
| Webex | `com.cisco.webex` | `CiscoCollabHost.exe`, `webexmta.exe` |
| FaceTime | `com.apple.FaceTime` | — |
| Slack (huddle) | `com.tinyspeck.slackmacgap` | `slack.exe` |
| Discord | `com.hnc.Discord` | `Discord.exe` |
| Navegador | Chrome, Safari, Arc, Edge, Firefox, Brave | `chrome.exe`, `msedge.exe`, `firefox.exe`, `brave.exe`, `arc.exe` |

- **Quem usa o microfone**: Mac — objetos de processo do Core Audio (`kAudioHardwarePropertyProcessObjectList`,
  `kAudioProcessPropertyIsRunningInput`, `kAudioProcessPropertyBundleID`; macOS 14.2+). Windows — o registro do
  Gerenciador de Acesso a Recursos (`HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\
  ConsentStore\microphone`, chaves `NonPackaged\*` e pacotes; em uso quando `LastUsedTimeStop` é 0).
  O próprio Lidiflow nunca conta.
- **Navegador** só conta se alguma janela dele tiver no título `Meet`, `meet.google.com`, `Microsoft Teams`,
  `Teams`, `Zoom`, `Whereby` ou `Jitsi` (Mac: títulos das janelas pela Acessibilidade; Windows: títulos das janelas
  do processo). O nome mostrado é o do serviço (`Google Meet`, `Microsoft Teams`, `Zoom`…); `Meet` é testado
  por último, porque títulos do Zoom e do Teams costumam ter "Meeting". No Mac, se nenhum título
  puder ser lido (o Chrome só expõe as janelas à Acessibilidade quando um app assistivo pede), o navegador usando o
  microfone já conta, com o nome do navegador (`Reunião no Chrome`).
- A chamada **acabou** quando o app deixa de usar o microfone por 8 s seguidos.

## O que a pessoa vê

- **Começou e não está gravando**: a barra (fora do ditado) vira um convite por 20 s:
  `● Reunião no Zoom   [Gravar]  ×` — 360×40, embaixo ou encostado na lateral. "Gravar" começa a gravação com o título `Zoom · 18/10, 14:05`. "×" ou o tempo esgotado dispensam o
  convite **para esta chamada** (uma nova chamada convida de novo).
- **Acabou e está gravando**: convite `A chamada no Zoom acabou   [Parar e resumir]  ×` (mesmos tamanhos), por 30 s.
- **Nas laterais**, os dois convites são o mesmo cartão deitado de 360×40, encostado na borda, para o nome da
  reunião aparecer; só os outros estados da barra ficam em pé.
  "×" continua gravando. Gravações iniciadas à mão também recebem o convite quando a chamada que estava ativa acaba.
- Ajustes: linha "Detectar reuniões" — "Oferece gravar quando uma chamada começa no Zoom, Teams, Meet e outros".

# Fase 3 — quem é quem do lado de lá

A trilha **Eles** é separada por voz (`Pessoa 1`, `Pessoa 2`…), e a pessoa dá nome a cada voz.

## Separador de vozes

[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) v1.13.8, programa `sherpa-onnx-offline-speaker-diarization`,
instalado como as outras dependências (download na primeira reunião que precisar, verificado por SHA-256, em
`<suporte>/diarization/`):

| Peça | Arquivo |
|---|---|
| Programa, Mac | `v1.13.8/sherpa-onnx-v1.13.8-osx-universal2-shared-no-tts.tar.bz2` (39 MB) |
| Programa, Windows | `v1.13.8/sherpa-onnx-v1.13.8-win-x64-shared-MT-Release-no-tts.tar.bz2` (23 MB; `tar.exe` do Windows) |
| Segmentação | `speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2` → `model.onnx` (o completo, não o int8) |
| Voz | `speaker-recongition-models/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx` (arquivo solto) |

(URLs em `https://github.com/k2-fsa/sherpa-onnx/releases/download/<pasta>/<arquivo>`.)

SHA-256: Mac `636b439c75031d892636dfdd2000a0233e4a30a08b030697e91f8aa389431b83`; Windows
`4b0a94f7b5c606b1b64a19a831c2127559e4b3d34e195465ebc7be73d9ed4783`; segmentação
`24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488`; voz
`357a834f702b80161e5b981182c038e18553c1f2ca752ed6cec2052365d4129b`. Os pacotes trazem tudo numa pasta
`sherpa-onnx-v1.13.8-…/` com o programa em `bin/` (no Windows, `bin/` já tem as DLLs ao lado do `.exe`).

Execução sobre `system.wav`, depois da transcrição, só se a reunião tem trechos **Eles**:

```
sherpa-onnx-offline-speaker-diarization --clustering.cluster-threshold=0.4 \
  --segmentation.pyannote-model=<model.onnx> --embedding.model=<campplus.onnx> system.wav
```

A saída tem linhas `0.031 -- 5.043 speaker_00`. Cada trecho **Eles** fica com a voz que mais se sobrepõe a ele no
tempo; trechos sem sobreposição ficam sem voz. As vozes são renumeradas pela ordem em que aparecem (0, 1, 2…).
Limiar 0,4: com gente de verdade separa bem; com vozes parecidas às vezes divide uma pessoa em duas — o que se
resolve juntando na tela (o contrário, desfazer uma mistura, não teria como).

Estado novo entre `transcribing` e `summarizing`: `separating` (`Separando as vozes`). Falha do separador não falha a
reunião: segue sem vozes, com `Eles` como antes.

## Dados

- `segments[].speaker`: número da voz (só em trechos `them`), ou ausente.
- `speakers`: lista de nomes por número de voz; `""` = nome padrão `Pessoa N` (N = número + 1).

## O que a pessoa vê

- Transcrição: os balões dos outros mostram o nome da voz (`Pessoa 1` ou o nome dado) no lugar de `Eles`, cada voz
  com uma cor discreta própria (tons neutros, não o azul, que é de "Você").
- Acima da conversa, o painel **Vozes**: um item por voz com ▶ (toca até 6 s da fala mais longa daquela voz, tirados
  de `system.wav`), o nome editável (salva ao sair do campo / Enter) e **Juntar com…** (menu com as outras vozes; os
  trechos passam para a escolhida e a voz some, renumerando as que vêm depois).
- Clique direito num balão dos outros: **Esta fala é de…** com cada voz e **Nova voz** — muda só aquele trecho
  (para quando o separador erra uma fala, e não uma voz inteira).
- Depois de dar nomes, juntar ou mudar falas, aparece **Refazer o resumo com os nomes**.
- O resumo usa os nomes: a transcrição enviada à IA traz `[mm:ss] Você: …` e `[mm:ss] <nome ou Pessoa N>: …`;
  a instrução passa a dizer que "Você" é quem gravou e que os outros nomes (ou `Pessoa N`) são participantes, e as
  tarefas começam com `Você:` ou com o nome de quem vai fazer (`Participantes:` quando não dá para saber).
  "Copiar transcrição" usa os mesmos rótulos.

## Fora da fase 3

Nomes automáticos pelo Zoom/Meet: exigem ler a interface de cada app de chamada durante a reunião (no navegador, o
Chrome só expõe a página a apps assistivos com uma extensão), e não dá para acertar sem chamadas reais para testar.
Fica para quando houver como testar; os convidados do calendário (fase 4) vão sugerir nomes.

# Fase 4 — calendário, perguntas e tarefas

## Calendário

Retirado: a conexão com o Google Agenda exige registrar o app no Google Cloud (e uma verificação do Google para
uso aberto); os calendários do sistema e o endereço iCal foram descartados. O título da gravação vem do app de
chamada detectado.

## Perguntar sobre as reuniões

- No topo da tela Reuniões (lista) e no detalhe de uma reunião: campo **Pergunte sobre as suas reuniões** /
  **Pergunte sobre esta reunião**, Enter envia.
- Busca local antes da IA: palavras da pergunta (minúsculas, sem acento, sem palavras de até 2 letras e sem as de uma
  lista curta de palavras comuns do português) contadas em título, resumo e transcrição; as 3 reuniões com mais
  acertos (no detalhe, só a reunião aberta). Contexto, até 8.000 caracteres: para cada reunião, o título, a data, o
  resumo e as falas que têm alguma palavra da pergunta, com uma fala antes e uma depois, no formato da transcrição.
  Nenhuma reunião com acerto: as 3 mais recentes, só com o resumo.
- Resposta da IA (a de Modelos): instrução — responder só com o que está nas reuniões, em poucas frases, citando a
  origem como `[título · mm:ss]` quando vier de uma fala; dizer que não encontrou quando não estiver lá.
- A resposta aparece num cartão abaixo do campo, com skeleton enquanto espera; cada citação `[título · mm:ss]` é
  clicável e abre a reunião na aba Transcrição. Não guarda histórico de perguntas.

## Tarefas como ações

- No bloco **Tarefas** do resumo, o quadradinho de cada tarefa marca feita/não feita (texto riscado e cinza),
  guardado em `doneTasks` (lista com o texto das tarefas feitas).
- Ações no cabeçalho do bloco: **Copiar** (lista `- [ ] Dono: tarefa` / `- [x] …`), **E-mail** (abre um e-mail novo
  com assunto `Tarefas — <título>` e a lista no corpo, por `mailto:`) e, no Mac, **Lembretes** (cria um lembrete por
  tarefa não feita, na lista padrão do app Lembretes, com nota `<título da reunião>`; pede permissão de Lembretes na
  primeira vez — `NSRemindersFullAccessUsageDescription`). Depois de criar, mostra `N lembretes criados` por 3 s.

# Tela de Reuniões (versão 3)

Inspirada no Notetaker do Wispr Flow, com a identidade do Lidiflow. Substitui a lista da versão anterior (o detalhe de
uma reunião continua igual).

## Estrutura (de cima para baixo, coluna central; prévia à direita)

1. **Cabeçalho**: título "Reuniões" (Display), botão ⚙ (abre Ajustes) e botão primário **Gravar reunião** (vira
   "Parar e resumir" com o tempo enquanto grava; o banner de gravação da versão anterior continua abaixo do cabeçalho).
2. **Banner "Gravar sozinho?"** — só enquanto `autoRecordMeetings` está desligado e `autoRecordAsked` é falso: ícone
   ◎, título **Quer que o Lidiflow grave sozinho?**, texto "Começa a gravar assim que a chamada começa e para quando ela
   acaba.", botões **Agora não** (texto) e **Ativar** (secundário). Os dois marcam `autoRecordAsked`; Ativar liga
   `autoRecordMeetings`.
3. (O cartão HOJE, com os eventos do calendário, saiu junto com o calendário.)
4. **Anteriores**: rótulo-aba "Anteriores" sublinhado em azul e, à direita, uma lupa que abre um campo de busca
   (filtra por título, resumo e transcrição; Esc ou ✕ fecha). As reuniões agrupadas por dia com cabeçalho mono
   maiúsculo (`QUI, 15 DE OUT`; hoje = `HOJE`, ontem = `ONTEM`); cada linha: ícone de documento num quadrado neutro,
   título, horário `HH:mm` e duração, e a pílula de estado quando não está pronta. Linha selecionada com fundo
   destacado; clique seleciona (abre a prévia), duplo clique ou "Abrir" abre o detalhe.
5. **Pergunta fixa no rodapé**: campo arredondado flutuante "Resuma as reuniões da semana…", Enter envia; a resposta
   aparece num cartão acima do campo (com skeleton e ✕ para fechar), com as citações clicáveis. Some se não houver
   reunião pronta.

**Prévia à direita** (largura ~300, só quando a janela tem espaço; separada por um filete): título (Display),
`<dia da semana> • HH:mm`, filete, rótulo **VISÃO GERAL** e o parágrafo de resumo; **PARTICIPANTES**: "Você" (azul)
e as vozes com o nome e a cor de cada uma; **TAREFAS** (até 5, com o dono);
botão **Abrir reunião**. Sem seleção: a reunião mais recente.

## Gravar sozinho

Com `autoRecordMeetings` ligado, quando a fase 2 detecta uma chamada e não há gravação, a gravação começa direto
(sem convite), com o título do app/evento; quando a chamada acaba, para e resume sozinha (sem convite). Em Ajustes,
linha "Gravar reuniões sozinho" logo abaixo de "Detectar reuniões" (só faz efeito com a detecção ligada).

## Perguntas com período

Se a pergunta menciona `hoje`, `ontem`, `semana` (esta semana, últimos 7 dias) ou `mês` (últimos 30 dias), as
reuniões do período entram todas (as mais recentes primeiro, só o resumo, até os 8.000 caracteres), em vez da busca
por palavras.

# Barra durante reuniões (versão 2)

Ajustes depois do primeiro teste com uma chamada real (Google Meet).

## Barra aberta (mouse em cima)

- **Sem reunião**: `Ditar  Fn  [🎤] [◎]` — o ◎ (`record.circle`, fundo branco a 0,12) começa a gravar a reunião.
  Embaixo 204×36; nas laterais em pé 52×148 (Ditar, Fn, 🎤, ◎).
- **Gravando reunião**: o mouse em cima **não** troca a pílula pelo ditado; abre os controles da reunião:
  `● 12:34   [🎤] [≡] [■]` — 🎤 dita (mãos livres, como sempre), ≡ (`text.bubble`) mostra/esconde a transcrição ao
  vivo, ■ para e resume. Embaixo 220×36; nas laterais em pé 40×156 (●, tempo em duas linhas, 🎤, ≡, ■).

## Transcrição ao vivo

- Durante a gravação, as duas trilhas também vão para memória; a cada 8 s, em cada trilha, os trechos com fala que
  já terminaram (o fim do trecho está a mais de 1 s do fim do que foi gravado) são transcritos pelo motor do ditado e
  saem da memória. Mesma detecção de fala da fase 1 (sobre o que está em memória), sem remoção de eco nem vozes
  (a transcrição final, ao parar, continua sendo feita do zero a partir dos arquivos).
- **Painel ao vivo** (janela flutuante que não rouba foco, 340 de largura × até 260 de altura, fundo preto 0,82,
  cantos 10): cabeçalho `● AO VIVO  12:34` e ✕; as últimas falas, rolando sozinho para a última, `Você` em azul
  claro e `Eles` em branco, texto em branco a 0,9; sem falas ainda: "Ouvindo… as falas aparecem aqui assim que cada um
  terminar de falar." Fica acima da barra (embaixo) ou ao lado dela (laterais), alinhado à barra. Abre pelo ≡ e
  fecha pelo ✕ ou ao parar a gravação.

## Começou sem querer?

Ao parar uma gravação com menos de 15 palavras na transcrição ao vivo (e menos de 2 min), a barra mostra por 15 s um
cartão 380×64: ⚠ `Começou sem querer?` / `Só algumas palavras foram captadas. Manter ou descartar a reunião?`,
botões **Descartar** (texto) e **Manter** (branco). Descartar apaga a reunião (mesmo em processamento); Manter ou o
tempo esgotado deixam como está.

## Aviso de reunião detectada

- Chega **compacto** (240×40): ícone de câmera em azul claro, `Reunião detectada`, `● agora` (ponto verde
  `#7FA88A`). Entra deslizando da borda mais próxima (8 pt, com fade) e, depois de 0,8 s, **se expande** (mola) para
  o cartão 380×64: à esquerda o ícone de câmera num quadrado azul a 0,2; `Reunião detectada` (14, médio) e
  `● Google Meet · agora` (12, branco a 0,6); à direita o botão **Gravar** com o símbolo do Lidiflow (fundo branco, texto
  preto, 34 de altura, cantos 8) e ×.
- Fim da chamada durante a gravação: mesmo cartão com o ícone ■, `A chamada acabou`, `● Google Meet · 12:34
  gravados` e o botão **Parar e resumir**.
- Com "Reduzir movimento", aparece direto no tamanho final.

## Depois

Nomes automáticos pelo Zoom/Meet (ver fase 3), aviso antes de uma reunião do calendário começar, pastas,
compartilhar, histórico de perguntas.

# Ditado sem lugar para colar

Vale para o ditado em geral, não só para reuniões. Quando o ditado termina e o foco não está num campo de texto (a mesma checagem que já decide colar ou não), a barra mostra um card no lugar da antiga dica de uma linha:

- 380×124, fundo preto translúcido, cantos 10, inclusive nas laterais: o card fica deitado encostado na borda, como o aviso de reunião.
- Linha 1: ícone de cursor de texto (azul claro), "Nenhum campo de texto para colar", × à direita (fecha).
- Linha 2: o texto ditado, até 2 linhas com reticências; o texto inteiro aparece no tooltip.
- Linha 3: "Ou cole depois com" + as teclas do atalho de colar o último (some se não houver atalho); à direita, o botão branco "Copiar" (altura 28, cantos 8), que vira "✓ Copiado" e fecha o card 1,2 s depois.
- Copiar troca a área de transferência de propósito: aqui é a pessoa que pediu.
- Some sozinho em 12 s; com o mouse em cima, espera até o mouse sair (checa de 3 em 3 s). Um novo ditado substitui o card.
