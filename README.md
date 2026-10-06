# Windows TrafficLight

Troca os botões de minimizar, maximizar e fechar de todas as janelas do Windows pelos "traffic lights" do macOS.

- 🔴 fecha · 🟡 minimiza · 🟢 maximiza/restaura
- Ao passar o mouse sobre o grupo aparecem os símbolos (× − ⤢), como no Mac
- Janelas inativas mostram os botões em cinza
- A cor de fundo é lida da própria barra de título, então o remendo some no tema claro, no escuro e no Mica
- Arrastar a área vazia move a janela, duplo clique maximiza e o botão direito abre o menu do sistema
- Respeita DPI por monitor, cantos arredondados do Windows 11, janelas "sempre visíveis" e áreas de trabalho virtuais

## Como usar

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
.\dist\TrafficLight.exe
```

Um ícone com três bolinhas aparece na bandeja:

- **clique esquerdo**: liga ou pausa
- **botão direito**: *Ativado*, *Iniciar com o Windows* e *Sair*

O app se registra sozinho para iniciar com o Windows. Se você desmarcar *Iniciar com o Windows*, essa escolha é respeitada. Se o `.exe` mudar de lugar, o registro é atualizado para o caminho novo na próxima vez que ele abrir.

Requer o .NET 10 Desktop Runtime.

## Como funciona

O Windows não permite trocar os botões de outros programas sem injetar código neles. Por isso o app desenha, por cima dos botões nativos, uma pequena janela transparente "possuída" (*owned*) pela janela alvo. Assim ela acompanha automaticamente a ordem das janelas, a minimização e a área de trabalho virtual da janela alvo.

- **Onde estão os botões**: primeiro o app pergunta à própria janela via `WM_NCHITTEST`. Apps com barra de título própria (Chrome, Edge, VS Code, Configurações, WinUI) respondem a isso por causa do Snap Layouts. Se a janela não responder, ele usa `DWMWA_CAPTION_BUTTON_BOUNDS` (caso do Explorer e das janelas Win32 padrão).
- **Eventos**: usa `SetWinEventHook` para foco, movimento, minimizar, mostrar, ocultar e destruir janelas, além de uma verificação a cada 1 s.
- **Isolamento**: cada sobreposição roda na sua própria thread. Se um app travar, só a bolinha daquele app trava.

| Arquivo | Função |
| --- | --- |
| `WindowTracker.cs` | Descobre as janelas, mede os botões e amostra a cor |
| `Overlay.cs` | Janela de sobreposição: thread, mouse e ações |
| `TrafficLightRenderer.cs` | Desenho dos círculos e glifos |
| `TrayApp.cs` | Ícone na bandeja e configurações (HKCU) |

## Limitações

- Janelas de apps rodando como administrador são ignoradas, a menos que o TrafficLight também rode como administrador (o Windows bloqueia os cliques entre níveis de privilégio).
- Os botões ficam à direita, onde estão os do Windows. Movê-los para a esquerda cobriria abas, menus e títulos dos apps.
- Ao arrastar uma janela muito rápido, a sobreposição pode ficar um quadro atrasada.
- Janelas em tela cheia (F11, jogos) e janelas sem barra de título não são alteradas.
