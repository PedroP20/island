const { app, BrowserWindow, ipcMain, screen, Menu, nativeImage, shell, Tray, clipboard } = require('electron');
const fs = require('fs');
const { spawn } = require('child_process');
const readline = require('readline');
const path = require('path');
// no app instalado, executáveis e scripts ficam fora do pacote .asar
const unpacked = (...p) => path.join(__dirname, ...p).replace('app.asar', 'app.asar.unpacked');

// Tamanho da janela por orientação (pequena = menos pixels para redesenhar)
const WIN = { h: [460, 330], v: [430, 440] };
let win, mediaProc, hiddenByFullscreen = false, fsInfo = { fullscreen: false };

// economia: GPU desligada (em janela transparente pequena ela gasta mais do que economiza)
app.disableHardwareAcceleration();
app.setAppUserModelId('com.island.app');
app.commandLine.appendSwitch('disable-features', 'HardwareMediaKeyHandling,MediaSessionService,SpareRendererForSitePerProcess');

if (!app.requestSingleInstanceLock()) app.quit();

// ---------- Posição (borda + ponto ao longo da borda), salva entre sessões ----------
const settingsFile = () => path.join(app.getPath('userData'), 'settings.json');
let settings = { edge: 'top', pos: 0.5, displayId: null, enabled: true };
try { Object.assign(settings, JSON.parse(fs.readFileSync(settingsFile(), 'utf8'))); } catch {}
const saveSettings = () => { try { fs.writeFileSync(settingsFile(), JSON.stringify(settings)); } catch {} };
const isVertical = () => settings.edge === 'left' || settings.edge === 'right';
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));

function currentDisplay() {
  return screen.getAllDisplays().find((d) => d.id === settings.displayId) || screen.getPrimaryDisplay();
}
function targetBounds() {
  const a = currentDisplay().workArea;
  const [w, h] = isVertical() ? WIN.v : WIN.h;
  let x, y;
  if (!isVertical()) {
    x = clamp(a.x + settings.pos * a.width - w / 2, a.x, a.x + a.width - w);
    y = settings.edge === 'top' ? a.y : a.y + a.height - h;
  } else {
    y = clamp(a.y + settings.pos * a.height - h / 2, a.y, a.y + a.height - h);
    x = settings.edge === 'left' ? a.x : a.x + a.width - w;
  }
  return { x: Math.round(x), y: Math.round(y), width: w, height: h };
}

// desliza a janela até o destino (ease-out com leve passada, ~320ms)
let animT = null;
function animateTo(from, to) {
  clearInterval(animT);
  const t0 = Date.now(), D = 320;
  const ease = (t) => { const c = 1.4; return 1 + (c + 1) * Math.pow(t - 1, 3) + c * Math.pow(t - 1, 2); };
  animT = setInterval(() => {
    const t = Math.min(1, (Date.now() - t0) / D), k = ease(t);
    win.setBounds({ x: Math.round(from.x + (to.x - from.x) * k), y: Math.round(from.y + (to.y - from.y) * k),
      width: to.width, height: to.height });
    if (t >= 1) { clearInterval(animT); animT = null; win.setBounds(to); }
  }, 16);
}

function applyLayout(from) {
  const to = targetBounds();
  win.webContents.send('layout', { edge: settings.edge, w: to.width, h: to.height });
  if (from) animateTo(from, to); else win.setBounds(to);
  evaluateFullscreen();
}

function setEdge(edge, pos = settings.pos) {
  settings.edge = edge; settings.pos = pos; saveSettings();
  applyLayout(win.getBounds());
}

// ---------- Arrastar a ilha ----------
let drag = null, dragT = null;
ipcMain.on('drag-start', () => {
  const p = screen.getCursorScreenPoint(), b = win.getBounds();
  drag = { dx: p.x - b.x, dy: p.y - b.y };
  clearInterval(animT);
  win.setIgnoreMouseEvents(false);
  dragT = setInterval(() => {
    const c = screen.getCursorScreenPoint();
    win.setPosition(c.x - drag.dx, c.y - drag.dy);
  }, 16);
});
ipcMain.on('drag-end', () => {
  if (!drag) return;
  clearInterval(dragT); drag = null;
  // encaixa na borda mais próxima do cursor, na tela onde ele está
  const p = screen.getCursorScreenPoint(), d = screen.getDisplayNearestPoint(p), a = d.workArea;
  const dist = { top: p.y - a.y, bottom: a.y + a.height - p.y, left: p.x - a.x, right: a.x + a.width - p.x };
  const edge = Object.keys(dist).reduce((m, k) => (dist[k] < dist[m] ? k : m), 'top');
  const vert = edge === 'left' || edge === 'right';
  settings.displayId = d.id;
  settings.edge = edge;
  settings.pos = vert ? clamp((p.y - a.y) / a.height, 0, 1) : clamp((p.x - a.x) / a.width, 0, 1);
  saveSettings();
  // começa a animação com a ilha "embaixo do cursor", já na orientação nova
  const [w, h] = vert ? WIN.v : WIN.h;
  const from = {
    x: Math.round(edge === 'left' ? p.x - 25 : edge === 'right' ? p.x - (w - 25) : p.x - w / 2),
    y: Math.round(edge === 'top' ? p.y - 25 : edge === 'bottom' ? p.y - (h - 25) : p.y - h / 2),
  };
  applyLayout(from);
});

// ---------- Mídia + detector de tela cheia ----------
function startMedia() {
  if (mediaProc || !settings.enabled) return;
  mediaProc = spawn(unpacked('bin', 'MediaHelper.exe'), [String(process.pid)], { windowsHide: true });
  readline.createInterface({ input: mediaProc.stdout }).on('line', (line) => {
    let msg; try { msg = JSON.parse(line); } catch { return; }
    if (!win || win.isDestroyed()) return;
    if ('fullscreen' in msg) { fsInfo = msg; return evaluateFullscreen(); }
    if (msg.t === 'clip') return onClipboardChanged();
    if (msg.t) return win.webContents.send('sys', msg); // volume, brilho, mic/câmera, notificações
    win.webContents.send('media', msg);
  });
  const proc = mediaProc;
  proc.on('exit', () => {
    if (mediaProc === proc) mediaProc = null;
    if (!app.isQuitting && settings.enabled) setTimeout(startMedia, 2000);
  });
}
function stopMedia() {
  if (!mediaProc) return;
  const p = mediaProc; mediaProc = null; p.kill();
}

// Some de verdade só se a tela cheia for no MESMO monitor da ilha
function evaluateFullscreen() {
  let fsHere = false;
  if (fsInfo.fullscreen && fsInfo.mon) {
    const [l, t, r, b] = fsInfo.mon;
    try {
      const dip = screen.screenToDipRect(null, { x: l, y: t, width: r - l, height: b - t });
      fsHere = screen.getDisplayMatching(dip).id === currentDisplay().id;
    } catch { fsHere = true; }
  } else if (fsInfo.fullscreen) fsHere = true;
  setFullscreen(fsHere);
}
function setFullscreen(fs) {
  if (fs === hiddenByFullscreen) return;
  hiddenByFullscreen = fs;
  refreshVisibility();
}

// visível só se estiver ligada E não houver tela cheia no monitor dela
function refreshVisibility() {
  if (!win || win.isDestroyed()) return;
  const show = settings.enabled && !hiddenByFullscreen;
  if (show) { win.setAlwaysOnTop(true, 'screen-saver'); if (!win.isVisible()) win.showInactive(); }
  else { win.setAlwaysOnTop(false); win.hide(); }
  win.webContents.send('visible', show);
}

// ---------- Ligar / desligar ----------
// Desligada: janela escondida, leitor de mídia encerrado e checagem do mouse parada = ~0% de CPU.
function setEnabled(on) {
  settings.enabled = on; saveSettings();
  if (on) { hiddenByFullscreen = false; fsInfo = { fullscreen: false }; startMedia(); }
  else stopMedia();
  refreshVisibility();
  updateTray();
}

// ---------- Ícone na bandeja ----------
let tray = null;
const trayIcon = (on) => nativeImage.createFromPath(path.join(__dirname, 'assets', on ? 'tray.png' : 'tray-off.png'));
function updateTray() {
  if (!tray) return;
  tray.setImage(trayIcon(settings.enabled));
  tray.setToolTip(settings.enabled ? 'Island — ativa' : 'Island — desativada');
}
function createTray() {
  tray = new Tray(trayIcon(settings.enabled));
  updateTray();
  // clique: liga se estiver desligada; se já estiver ligada, abre a ilha
  tray.on('click', () => {
    if (!settings.enabled) setEnabled(true);
    else if (!hiddenByFullscreen) win.webContents.send('open-island');
  });
  tray.on('right-click', () => tray.popUpContextMenu(buildMenu()));
}

function ps(file, args = []) {
  return spawn('powershell.exe',
    ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', unpacked('scripts', file), ...args],
    { windowsHide: true });
}

function create() {
  win = new BrowserWindow({
    ...targetBounds(),
    frame: false, transparent: true, resizable: false, movable: false,
    skipTaskbar: true, alwaysOnTop: true, hasShadow: false, maximizable: false, minimizable: false,
    show: false, focusable: true,
    webPreferences: { preload: path.join(__dirname, 'preload.js'), spellcheck: false, backgroundThrottling: true },
  });
  win.once('ready-to-show', refreshVisibility);
  win.setIgnoreMouseEvents(true);
  createTray();
  win.loadFile('index.html');
  win.webContents.on('did-finish-load', () => { sendShelf(); sendClips(); applyLayout(); dlScan(); });
  watchDownloads();
  // monitor desconectado ou resolução mudou: reposiciona
  screen.on('display-removed', () => applyLayout());
  screen.on('display-metrics-changed', () => applyLayout());
  startMedia();
}

// ---------- Mouse: checa a posição do cursor (o "forward" de eventos não é confiável no Windows) ----------
let rect = { x: 0, y: 0, w: 0, h: 0 }, inside = false;
ipcMain.on('rect', (_e, r) => { rect = r; });
setInterval(() => {
  if (!win || win.isDestroyed() || hiddenByFullscreen || drag || !settings.enabled) return;
  const p = screen.getCursorScreenPoint(), b = win.getBounds(), pad = Math.min(rect.w, rect.h) > 40 ? 14 : 4;
  const x = p.x - b.x, y = p.y - b.y;
  const now = x >= rect.x - pad && x <= rect.x + rect.w + pad && y >= rect.y - pad && y <= rect.y + rect.h + pad;
  if (now !== inside) {
    inside = now;
    win.setIgnoreMouseEvents(!inside);
    win.webContents.send('hover', inside);
  }
}, 50);
app.on('browser-window-created', (_e, w) =>
  w.webContents.on('console-message', (_ev, _l, msg) => console.log('[renderer]', msg)));
ipcMain.on('media-cmd', (_e, action) => {
  if (mediaProc && (['toggle', 'next', 'prev'].includes(action) || /^seek \d+(\.\d+)?$/.test(action))) mediaProc.stdin.write(action + '\n');
});
ipcMain.on('menu', () => buildMenu().popup({ window: win }));

// mesmo menu na bandeja e no botão direito da ilha
function buildMenu() {
  const login = app.getLoginItemSettings().openAtLogin;
  const pos = (label, edge, p) => ({
    label, type: 'radio',
    checked: settings.edge === edge && (p === undefined || Math.abs(settings.pos - p) < 0.01),
    click: () => setEdge(edge, p ?? settings.pos),
  });
  return Menu.buildFromTemplate([
    settings.enabled
      ? { label: 'Desativar ilha', click: () => setEnabled(false) }
      : { label: 'Ativar ilha', click: () => setEnabled(true) },
    { type: 'separator' },
    { label: 'Posição', enabled: settings.enabled, submenu: [
      pos('Topo (centro)', 'top', 0.5),
      pos('Base (centro)', 'bottom', 0.5),
      pos('Esquerda', 'left'),
      pos('Direita', 'right'),
      { type: 'separator' },
      { label: 'Dica: segure e arraste a ilha até qualquer borda', enabled: false },
    ] },
    { label: 'Iniciar com o Windows', type: 'checkbox', checked: login,
      click: () => app.setLoginItemSettings({ openAtLogin: !login }) },
    { type: 'separator' },
    { label: 'Sair do Island', click: () => app.quit() },
  ]);
}

// ---------- Prateleira: arquivos e textos ----------
// item: { id, type: 'file' | 'text', path?, text?, added }
const shelfFile = () => path.join(app.getPath('userData'), 'shelf.json');
const icons = new Map();   // ícone pequeno (para arrastar)
const thumbs = new Map();  // miniatura (imagens/vídeos/PDF) ou ícone
let shelf = [];
let prefsMain = { shelfClearHours: 0 };
try {
  shelf = JSON.parse(fs.readFileSync(shelfFile(), 'utf8'))
    .map((x) => (typeof x === 'string' ? { type: 'file', path: x, added: Date.now() } : x)); // formato antigo
  shelf.forEach((x, i) => (x.id = x.id || `${Date.now()}-${i}`));
} catch {}
const newId = () => `${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;

async function iconFor(p) {
  if (!icons.has(p)) {
    let img = null;
    try { img = await app.getFileIcon(p, { size: 'large' }); } catch {}
    icons.set(p, img && !img.isEmpty() ? img : nativeImage.createEmpty());
  }
  return icons.get(p);
}
async function thumbFor(p) {
  if (!thumbs.has(p)) {
    let img = null;
    // miniatura de verdade (fotos, vídeos, PDFs) via Windows; senão o ícone do arquivo
    try { img = await nativeImage.createThumbnailFromPath(p, { width: 96, height: 96 }); } catch {}
    if (!img || img.isEmpty()) img = await iconFor(p);
    thumbs.set(p, img.toDataURL());
  }
  return thumbs.get(p);
}
async function sendShelf() {
  // tira arquivos que não existem mais e itens vencidos (limpeza automática)
  const maxAge = prefsMain.shelfClearHours > 0 ? prefsMain.shelfClearHours * 3600e3 : Infinity;
  shelf = shelf.filter((x) => (x.type === 'text' || fs.existsSync(x.path)) && Date.now() - x.added < maxAge);
  try { fs.writeFileSync(shelfFile(), JSON.stringify(shelf)); } catch {}
  const items = await Promise.all(shelf.map(async (x) => x.type === 'text'
    ? { id: x.id, type: 'text', text: x.text }
    : { id: x.id, type: 'file', path: x.path, name: path.basename(x.path), thumb: await thumbFor(x.path),
        isImage: /\.(png|jpe?g|gif|webp|bmp|heic)$/i.test(x.path) }));
  win && !win.isDestroyed() && win.webContents.send('shelf', items);
}
setInterval(() => { if (prefsMain.shelfClearHours > 0 && shelf.length) sendShelf(); }, 10 * 60e3);

const byId = (id) => shelf.find((x) => x.id === id);
ipcMain.on('shelf-add', (_e, paths) => {
  for (const p of paths) if (p && !shelf.some((x) => x.path === p)) shelf.push({ id: newId(), type: 'file', path: p, added: Date.now() });
  sendShelf();
});
ipcMain.on('shelf-add-text', (_e, text) => {
  text = String(text || '').trim();
  if (text && !shelf.some((x) => x.text === text)) shelf.push({ id: newId(), type: 'text', text: text.slice(0, 5000), added: Date.now() });
  sendShelf();
});
ipcMain.on('shelf-remove', (_e, id) => { shelf = shelf.filter((x) => x.id !== id); sendShelf(); });
ipcMain.on('shelf-clear', () => { shelf = []; sendShelf(); });
ipcMain.on('shelf-open', (_e, id) => { const x = byId(id); if (x?.type === 'file') shell.openPath(x.path); });
ipcMain.on('shelf-copy', (_e, id) => {
  const items = id ? [byId(id)].filter(Boolean) : shelf;
  const files = items.filter((x) => x.type === 'file').map((x) => x.path);
  if (items.length === 1 && items[0].type === 'text') return markOwnCopy(() => clipboard.writeText(items[0].text));
  if (files.length) markOwnCopy(() => ps('clip.ps1', files));
});
ipcMain.on('shelf-drag', (e, id) => {
  const x = byId(id);
  if (!x || x.type !== 'file') return;
  e.sender.startDrag({ file: x.path, icon: icons.get(x.path) || nativeImage.createEmpty() });
});
ipcMain.on('prefs', (_e, p) => { Object.assign(prefsMain, p); sendShelf(); });

// ---------- Histórico da área de transferência (só na memória, nunca vai para o disco) ----------
let clipHistory = [], ownCopyUntil = 0;
function markOwnCopy(fn) { ownCopyUntil = Date.now() + 1500; fn(); }
function onClipboardChanged() {
  if (Date.now() < ownCopyUntil) return; // cópia feita pela própria ilha: não duplica
  try {
    const formats = clipboard.availableFormats();
    let item = null;
    if (formats.some((f) => f.startsWith('image/'))) {
      const img = clipboard.readImage();
      if (!img.isEmpty()) {
        const s = img.getSize(), k = Math.min(1, 160 / Math.max(s.width, s.height));
        item = { id: newId(), type: 'image', png: img.toPNG(),
          thumb: img.resize({ width: Math.round(s.width * k), height: Math.round(s.height * k) }).toDataURL(),
          info: `${s.width}×${s.height}` };
      }
    } else if (formats.includes('text/plain')) {
      const t = clipboard.readText();
      if (t && t.trim()) item = { id: newId(), type: 'text', text: t.slice(0, 20000) };
    }
    if (!item) return;
    if (item.type === 'text') clipHistory = clipHistory.filter((x) => x.text !== item.text);
    clipHistory.unshift(item);
    clipHistory = clipHistory.slice(0, 12);
    sendClips();
  } catch {}
}
function sendClips() {
  win && !win.isDestroyed() && win.webContents.send('clips',
    clipHistory.map((x) => ({ id: x.id, type: x.type, text: x.text?.slice(0, 300), thumb: x.thumb, info: x.info })));
}
ipcMain.on('clip-copy', (_e, id) => {
  const x = clipHistory.find((c) => c.id === id); if (!x) return;
  markOwnCopy(() => (x.type === 'image' ? clipboard.writeImage(nativeImage.createFromBuffer(x.png)) : clipboard.writeText(x.text)));
  clipHistory = [x, ...clipHistory.filter((c) => c !== x)]; sendClips();
});
ipcMain.on('clip-remove', (_e, id) => { clipHistory = clipHistory.filter((c) => c.id !== id); sendClips(); });
ipcMain.on('clip-clear', () => { clipHistory = []; sendClips(); });
ipcMain.on('clip-to-shelf', (_e, id) => {
  const x = clipHistory.find((c) => c.id === id); if (!x) return;
  if (x.type === 'text') ipcMain.emit('shelf-add-text', null, x.text);
  else {
    // imagem copiada vira um arquivo PNG guardado na pasta do app
    const dir = path.join(app.getPath('userData'), 'shelf-images'); fs.mkdirSync(dir, { recursive: true });
    const f = path.join(dir, `imagem-${new Date().toISOString().replace(/[:.]/g, '-')}.png`);
    fs.writeFileSync(f, x.png);
    shelf.push({ id: newId(), type: 'file', path: f, added: Date.now() }); sendShelf();
  }
});

// ---------- Downloads: observa a pasta (sem ficar varrendo quando nada está baixando) ----------
const DL_EXT = /\.(crdownload|part|partial|download)$/i;
const UNCONFIRMED = /^(Unconfirmed|Não confirmado) \d+$/i; // nome provisório do Chrome
let dlActive = new Map(), dlTimer = null, dlScanT = null;
function dlScan() {
  const dir = app.getPath('downloads');
  let names = [];
  try { names = fs.readdirSync(dir); } catch { return; }
  const now = Date.now(), active = new Map();
  for (const n of names) {
    if (!DL_EXT.test(n)) continue;
    let size = 0, mtime = 0;
    try { const st = fs.statSync(path.join(dir, n)); size = st.size; mtime = st.mtimeMs; } catch {}
    // temporário parado há mais de 2 min = download abandonado: ignora
    if (now - mtime > 120e3) continue;
    const prev = dlActive.get(n);
    const speed = prev ? Math.max(0, (size - prev.size) / ((now - prev.t) / 1000)) : 0;
    active.set(n, { size, t: now, speed: prev && now - prev.t < 400 ? prev.speed : speed });
  }
  // terminou: o temporário sumiu e o arquivo final existe
  for (const [n] of dlActive) {
    if (active.has(n)) continue;
    const final = path.join(dir, n.replace(DL_EXT, ''));
    if (!UNCONFIRMED.test(path.basename(final)) && fs.existsSync(final))
      win?.webContents.send('download', { done: { name: path.basename(final), path: final } });
  }
  dlActive = active;
  win?.webContents.send('download', { active: [...active].map(([n, v]) => ({
    name: n.replace(DL_EXT, '').replace(UNCONFIRMED, 'Download'), size: v.size, speed: v.speed })) });
  // enquanto houver download, atualiza 1x/s; quando acabar, para
  if (active.size && !dlTimer) dlTimer = setInterval(dlScan, 1000);
  if (!active.size && dlTimer) { clearInterval(dlTimer); dlTimer = null; }
}
function watchDownloads() {
  try {
    fs.watch(app.getPath('downloads'), () => { clearTimeout(dlScanT); dlScanT = setTimeout(dlScan, 300); });
  } catch {}
}
ipcMain.on('dl-show', (_e, p) => shell.showItemInFolder(p));
ipcMain.on('dl-to-shelf', (_e, p) => ipcMain.emit('shelf-add', null, [p]));

// ---------- Foco ----------
ipcMain.on('open-dnd-settings', () => shell.openExternal('ms-settings:notifications'));

app.on('before-quit', () => { app.isQuitting = true; mediaProc && mediaProc.kill(); });
app.whenReady().then(create);
