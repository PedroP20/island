# Island 🏝️ — Dynamic Island para Windows

Uma "Dynamic Island" no estilo do iPhone e do MacBook para o Windows: uma pílula discreta na borda da tela que mostra o que está acontecendo no PC (música, timer, downloads, notificações...). Ela se expande com animações de mola quando você passa o mouse.

Foi feita para ser **limpa, leve e não atrapalhar**: usa cerca de **0,2% de CPU** e **~170 MB de RAM**, e some sozinha em jogos e vídeos em tela cheia.

---

## 📥 Instalação

1. Baixe o `Island-Setup-x.y.z.exe` na página de **[Releases](../../releases)**.
2. Execute o instalador. Se o Windows mostrar "O Windows protegeu o computador", clique em **Mais informações → Executar assim mesmo**. O aviso aparece porque o app não tem assinatura digital paga.
3. Pronto: a ilha aparece no topo da tela e um ícone fica na bandeja do sistema.

**Requisitos:** Windows 10 ou 11 (64 bits) e o [.NET Desktop Runtime 8 ou mais novo](https://dotnet.microsoft.com/download/dotnet) instalado, usado pelo módulo que lê a mídia e o sistema.

---

## ✨ Funções

### 🎵 Música e mídia
Funciona com Spotify, YouTube, navegador e qualquer app que use os controles de mídia do Windows.
- **Ilha fechada:** capa do álbum e barrinhas de equalizador na cor da capa.
- **Ilha aberta:** título, artista, barra de progresso e botões de voltar, tocar/pausar e avançar.
- **Pular para qualquer ponto:** clique na barra de progresso.
- **Letras sincronizadas:** uma linha por vez, vindas do [lrclib.net](https://lrclib.net).
- **Fundo desfocado** na cor da capa.
- Se o app não fornecer a capa, a ilha busca no catálogo do iTunes.
- Opcional: abrir sozinha ao trocar de música (vem desligado).

### 🕐 Relógio e clima
- Com nada rodando, a ilha fechada mostra **só a hora**.
- A página do relógio mostra hora com segundos, dia da semana e data.
- **Clima atual:** ícone, temperatura, descrição, máxima e mínima.
- **Previsão das próximas 6 horas.**
- Cidade automática pelo IP, ou escolhida manualmente nos Ajustes. Dados do [Open-Meteo](https://open-meteo.com), sem cadastro.

### ⏱️ Timer e 🌙 Foco
- **Timer:** atalhos de 1 a 60 minutos ou tempo personalizado, com botões +1 e +5 min. No fim toca um som e a ilha pisca.
- **Foco:** sessões de 25, 50 ou 90 minutos que **silenciam as notificações da ilha**. No fim, avisa quantas foram silenciadas.
- Atalho para o "Não perturbe" do Windows.

### 🗂️ Prateleira e 📋 Copiados
- **Prateleira:** arraste **arquivos ou textos** para a ilha e ela segura. Depois é só arrastar de volta para qualquer lugar.
  - Miniaturas de fotos, vídeos e PDFs.
  - Duplo clique abre o arquivo.
  - Botão "Copiar" para colar com Ctrl+V numa pasta.
  - Pode esvaziar sozinha depois de 1 hora ou 1 dia.
- **Copiados:** histórico das últimas 12 coisas copiadas, textos e imagens.
  - Clique para copiar de novo.
  - O **+** guarda o item na prateleira.
  - Fica **só na memória**, nunca é salvo no disco.

### 🔊 Volume e brilho
Uma barrinha aparece na ilha quando você muda o volume ou o brilho. O brilho só funciona em notebooks.

### 🔔 Notificações
Notificações do WhatsApp, Discord, e-mail etc. aparecem na ilha por alguns segundos. Um clique dispensa.

### 🎙️ Microfone e câmera em uso
Um pontinho **laranja** (microfone) ou **verde** (câmera) aparece enquanto algum app usa. Passe o mouse para ver qual app.

### ⬇️ Downloads
Mostra o tamanho baixado enquanto o download acontece (Chrome, Edge, Firefox, Brave...). No fim, aparecem os botões **Mostrar** e **Guardar** (na prateleira). Downloads abandonados são ignorados.

### 🔋 Bateria
Avisa quando o carregador é conectado ou removido, e quando a bateria chega a 20% ou 10%.

---

## 🎨 Visual e uso

- **Animações de mola**, com movimento de física de verdade, ao abrir, fechar e trocar de página.
- **Bolhinha separada** como no iPhone: quando há mais de uma atividade, as extras aparecem num círculo ao lado, com efeito de "gota".
  - O anel do timer ou do foco vai esvaziando.
  - Mostra quantos itens há na prateleira.
  - Mostra os pontinhos de microfone e câmera.
  - Clicar num ícone abre a página dele.
- **3 temas:** Preta (padrão), Branca e Grafite.
- **5 páginas:** Música · Relógio · Timer/Foco · Prateleira/Copiados · Ajustes.
  - Para navegar: barra de abas com ícones, rodinha do mouse ou setas ← →.
- **Posição livre:** segure e arraste a ilha até qualquer borda (topo, base, esquerda ou direita) e em qualquer monitor.
  - Nas laterais ela fica em pé.
  - A posição fica salva.
- **Ícone na bandeja:**
  - Clique esquerdo liga a ilha ou abre a ilha.
  - Clique direito abre o menu: ativar/desativar, posição, iniciar com o Windows e sair.
- **Ajustes:** cada função pode ser ligada ou desligada.

---

## ⚡ Desempenho

- **Some em tela cheia:** em jogos (incluindo tela cheia exclusiva), vídeos e apresentações, a ilha sai de cima da tela de verdade. Não fica nem transparente por cima, para não causar travamentos.
- **Tudo por evento:** volume, brilho e área de transferência só avisam quando algo muda.
  - A pasta de Downloads só é verificada enquanto há download acontecendo.
  - A letra da música só é atualizada com a página de música aberta.
- **Desativada pela bandeja:** gasta praticamente zero.

---

## 🔒 Privacidade

Nada sai do seu PC, exceto estas consultas anônimas:
- Clima: Open-Meteo, e ipwho.is / ipapi.co / geojs.io para descobrir a cidade.
- Letras das músicas: lrclib.net.
- Capas que faltarem: iTunes.

Notificações, histórico de cópias e arquivos da prateleira ficam só no seu computador.

---

## 🛠️ Para desenvolvedores

Feito com **Electron** (interface) e um módulo em **C# / .NET 8** (`helper/`), que fala com o Windows: mídia, volume, brilho, notificações, microfone/câmera e tela cheia.

```bash
# 1. compilar o módulo do Windows (precisa do .NET SDK 8+)
dotnet publish helper -c Release -o bin

# 2. instalar dependências e rodar
npm install
npm start

# 3. gerar o instalador (sai em dist/)
npm run dist
```

| Arquivo | O que faz |
|---|---|
| `main.js` | janela, bandeja, posição, prateleira, área de transferência, downloads |
| `index.html` | toda a interface e as animações |
| `preload.js` | ponte segura entre a interface e o sistema |
| `helper/Program.cs` | módulo .NET: mídia, volume, brilho, notificações, mic/câmera, tela cheia |
