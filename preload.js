const { contextBridge, ipcRenderer, webUtils } = require('electron');
const on = (ch) => (cb) => ipcRenderer.on(ch, (_e, v) => cb(v));
const send = (ch) => (v) => ipcRenderer.send(ch, v);

contextBridge.exposeInMainWorld('bridge', {
  onMedia: on('media'), onHover: on('hover'), onShelf: on('shelf'), onLayout: on('layout'),
  onOpen: on('open-island'), onVisible: on('visible'), onSys: on('sys'), onClips: on('clips'), onDownload: on('download'), onDlRecent: on('dlrecent'),
  rect: send('rect'), media: send('media-cmd'), menu: send('menu'), prefs: send('prefs'),
  dragStart: send('drag-start'), dragEnd: send('drag-end'),
  pathOf: (file) => webUtils.getPathForFile(file),
  shelfAdd: send('shelf-add'), shelfAddText: send('shelf-add-text'), shelfRemove: send('shelf-remove'),
  shelfClear: send('shelf-clear'), shelfOpen: send('shelf-open'), shelfCopy: send('shelf-copy'), shelfDrag: send('shelf-drag'),
  clipCopy: send('clip-copy'), clipRemove: send('clip-remove'), clipClear: send('clip-clear'), clipToShelf: send('clip-to-shelf'),
  dlShow: send('dl-show'), dlOpen: send('dl-open'), dlRemove: send('dl-remove'), dlClear: send('dl-clear'), dlDrag: send('dl-drag'), dlToShelf: send('dl-to-shelf'), openDnd: send('open-dnd-settings'), gameMode: send('game-mode'),
});
