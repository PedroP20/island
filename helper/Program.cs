// Island helper: fala com o Windows e manda eventos (JSON, um por linha) para o app.
// Tudo é por evento ou com checagens bem espaçadas, para gastar quase nada.
//   mídia (Spotify, navegador...), tela cheia, volume, brilho, microfone/câmera em uso,
//   notificações, mudanças na área de transferência.
// Comandos pelo stdin: toggle | next | prev | seek <segundos>
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Windows.Media.Control;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

Console.OutputEncoding = Encoding.UTF8;
var myPid = (uint)Environment.ProcessId;
var parentPid = (uint)(args.Length > 0 && uint.TryParse(args[0], out var pp) ? pp : 0);

void Emit(Dictionary<string, object> o)
{
    var json = JsonSerializer.Serialize(o);
    lock (Console.Out) { Console.WriteLine(json); Console.Out.Flush(); }
}
void Safe(Action a) { try { a(); } catch { } }

var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

// ---------- comandos ----------
_ = Task.Run(async () =>
{
    string line;
    while ((line = Console.ReadLine()) != null)
    {
        // tamanho total de um download: "dltotal\t<temporário>\t<final>\t<History1>|<History2>..."
        if (line.StartsWith("dltotal\t"))
        {
            var a = line.Split('\t');
            if (a.Length >= 4) _ = Task.Run(() => DownloadTotal(a[1], a[2], a[3].Split('|')));
            continue;
        }
        // comandos de mídia: "<ação>\t<id da sessão>[\t<segundos>]" (sem id = sessão atual do Windows)
        try
        {
            var parts = line.Trim().Split('\t');
            var s = parts.Length > 1 && parts[1] != "" ? FindSession(parts[1]) : mgr.GetCurrentSession();
            if (s == null) continue;
            switch (parts[0])
            {
                case "toggle": await s.TryTogglePlayPauseAsync(); break;
                case "next": await s.TrySkipNextAsync(); break;
                case "prev": await s.TrySkipPreviousAsync(); break;
                case "seek":
                    if (parts.Length > 2 && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var sec))
                        await s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(sec).Ticks);
                    break;
            }
        }
        catch { }
    }
    Environment.Exit(0);
});

// ---------- tela cheia (1x por segundo) + área de transferência + mic/câmera ----------
_ = Task.Run(async () =>
{
    string lastFs = "", lastPriv = "";
    uint lastClip = GetClipboardSequenceNumber();
    int tick = 0;
    while (true)
    {
        // área de transferência: só um número inteiro, custo ~zero
        var seq = GetClipboardSequenceNumber();
        if (seq != lastClip) { lastClip = seq; Emit(new() { ["t"] = "clip" }); }

        if (tick % 2 == 0)
        {
            bool fs = false; int[] mon = null;
            Safe(() =>
            {
                var h = GetForegroundWindow();
                // jogo minimizado ou jogado para fora da tela (alt+tab) ainda deixa o Windows dizendo "tela cheia": ignora
                if (h == IntPtr.Zero || IsIconic(h) || MonitorFromWindow(h, 0) == IntPtr.Zero) return;
                // (o aviso "jogo em tela cheia" do Windows vale para o sistema todo, mesmo com o jogo em
                // segundo plano; por isso a regra é: a janela da FRENTE cobre o monitor inteiro)
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return;
                var monArr = new[] { mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom };
                if (fs) { mon = monArr; return; }
                var cls = new StringBuilder(256); GetClassName(h, cls, 256);
                var c = cls.ToString();
                GetWindowThreadProcessId(h, out uint pid);
                if (c != "Progman" && c != "WorkerW" && c != "Shell_TrayWnd" && pid != myPid && pid != parentPid
                    && GetWindowRect(h, out RECT r) &&
                    r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                    r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom)
                { fs = true; mon = monArr; }
            });
            var fsMsg = new Dictionary<string, object> { ["fullscreen"] = fs, ["mon"] = mon };
            var fsJson = JsonSerializer.Serialize(fsMsg);
            if (fsJson != lastFs) { lastFs = fsJson; Emit(fsMsg); }
        }

        if (tick % 4 == 0)
        {
            var mic = InUse("microphone"); var cam = InUse("webcam");
            var key = string.Join(",", mic) + "|" + string.Join(",", cam);
            if (key != lastPriv) { lastPriv = key; Emit(new() { ["t"] = "priv", ["mic"] = mic, ["cam"] = cam }); }
        }
        tick++;
        await Task.Delay(500);
    }
});

// Lê o tamanho total no histórico do navegador (Chrome/Brave/Edge/Opera/Vivaldi).
// Copia o banco (o navegador mantém ele aberto) e consulta uma linha. 1x por download.
void DownloadTotal(string temp, string final, string[] histories)
{
    long total = 0;
    foreach (var h in histories)
    {
        if (total > 0 || !File.Exists(h)) continue;
        var tmp = Path.Combine(Path.GetTempPath(), $"island-hist-{Guid.NewGuid():N}.db");
        try
        {
            using (var i = new FileStream(h, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var o = File.Create(tmp)) i.CopyTo(o);
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tmp};Mode=ReadOnly;Pooling=False");
            c.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT total_bytes FROM downloads WHERE current_path = $t OR target_path = $f OR target_path = $t ORDER BY id DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$t", temp);
            cmd.Parameters.AddWithValue("$f", final);
            var v = cmd.ExecuteScalar();
            if (v is long l) total = l;
        }
        catch { }
        finally { try { File.Delete(tmp); } catch { } }
    }
    Emit(new() { ["t"] = "dltotal", ["path"] = temp, ["total"] = total });
}

// apps usando microfone/câmera agora (registro do Windows: LastUsedTimeStop == 0)
List<string> InUse(string cap)
{
    var list = new List<string>();
    Safe(() =>
    {
        using var root = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{cap}");
        if (root == null) return;
        void Scan(RegistryKey k, bool nonPackaged)
        {
            foreach (var name in k.GetSubKeyNames())
            {
                if (name == "NonPackaged") continue;
                using var sk = k.OpenSubKey(name);
                if (sk == null) continue;
                var start = Convert.ToInt64(sk.GetValue("LastUsedTimeStart") ?? 0L);
                var stop = Convert.ToInt64(sk.GetValue("LastUsedTimeStop") ?? 1L);
                if (start > 0 && stop == 0)
                {
                    var n = nonPackaged ? Path.GetFileNameWithoutExtension(name.Replace('#', '\\')) : name.Split('_')[0].Split('.').Last();
                    if (!list.Contains(n)) list.Add(n);
                }
            }
        }
        Scan(root, false);
        using var np = root.OpenSubKey("NonPackaged");
        if (np != null) Scan(np, true);
    });
    return list;
}

// ---------- volume (por evento) ----------
MMDevice device = null;
void HookVolume()
{
    Safe(() =>
    {
        var en = new MMDeviceEnumerator();
        device = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        device.AudioEndpointVolume.OnVolumeNotification += (d) =>
            Emit(new() { ["t"] = "vol", ["v"] = Math.Round(d.MasterVolume, 3), ["mute"] = d.Muted });
    });
}
HookVolume();

// ---------- brilho (notebooks; por evento WMI) ----------
Safe(() =>
{
    var w = new System.Management.ManagementEventWatcher(
        new System.Management.ManagementScope(@"root\wmi"),
        new System.Management.EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
    w.EventArrived += (_, e) => Safe(() =>
        Emit(new() { ["t"] = "bright", ["v"] = Convert.ToInt32(e.NewEvent.Properties["Brightness"].Value) }));
    w.Start();
});

// ---------- notificações (checa a cada 2s; só manda as novas) ----------
_ = Task.Run(async () =>
{
    UserNotificationListener l = null;
    try
    {
        l = UserNotificationListener.Current;
        if (await l.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed) l = null;
    }
    catch { l = null; }
    Emit(new() { ["t"] = "notifAccess", ["ok"] = l != null });
    if (l == null) return;

    var seen = new HashSet<uint>();
    bool first = true;
    while (true)
    {
        try
        {
            var list = await l.GetNotificationsAsync(NotificationKinds.Toast);
            var now = new HashSet<uint>();
            foreach (var n in list)
            {
                now.Add(n.Id);
                if (seen.Contains(n.Id) || first) continue;
                // notificações antigas (ex.: reapareceram) não são mostradas
                if ((DateTimeOffset.Now - n.CreationTime).TotalSeconds > 20) continue;
                string app = "", title = "", body = "";
                Safe(() => app = n.AppInfo.DisplayInfo.DisplayName);
                Safe(() =>
                {
                    var b = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                    var texts = b?.GetTextElements();
                    if (texts != null && texts.Count > 0)
                    {
                        title = texts[0].Text;
                        body = string.Join(" ", texts.Skip(1).Select(t => t.Text));
                    }
                });
                if (app == "Island") continue;
                Emit(new() { ["t"] = "notif", ["app"] = app, ["title"] = title, ["body"] = body });
            }
            seen = now; first = false;
        }
        catch { }
        await Task.Delay(2000);
    }
});

// ---------- mídia: TODAS as sessões (Spotify, navegador, vídeos...), 1x por segundo ----------
// Cada sessão ganha um id estável (app + nº de ordem). A capa vai em mensagem separada, só quando muda.
var artState = new Dictionary<string, (string key, bool have, int tries)>();
string prevList = "";
var lastPos = new Dictionary<string, (double pos, long upd)>();
string SessionId(GlobalSystemMediaTransportControlsSession s, Dictionary<string, int> seen)
{
    var app = s.SourceAppUserModelId ?? "app";
    seen[app] = seen.TryGetValue(app, out var n) ? n + 1 : 0;
    return seen[app] == 0 ? app : $"{app}#{seen[app]}";
}
GlobalSystemMediaTransportControlsSession FindSession(string id)
{
    var seen = new Dictionary<string, int>();
    foreach (var s in mgr.GetSessions()) if (SessionId(s, seen) == id) return s;
    return null;
}
// Por evento: o Windows avisa quando algo muda (música nova, play/pause, pulo, app abriu/fechou).
// Só então a lista é relida. Uma checagem de segurança roda a cada 5 s.
var dirty = true;
var hooked = new HashSet<GlobalSystemMediaTransportControlsSession>();
void HookSessions()
{
    foreach (var s in mgr.GetSessions())
    {
        if (!hooked.Add(s)) continue;
        s.MediaPropertiesChanged += (_, _) => dirty = true;
        s.PlaybackInfoChanged += (_, _) => dirty = true;
        s.TimelinePropertiesChanged += (_, _) => dirty = true;
    }
}
mgr.SessionsChanged += (_, _) => { dirty = true; };
mgr.CurrentSessionChanged += (_, _) => { dirty = true; };
HookSessions();
var lastRefresh = DateTime.MinValue;
while (true)
{
    var since = (DateTime.Now - lastRefresh).TotalMilliseconds;
    // capa ainda chegando (Spotify) = tenta de novo a cada 1 s; senão, só com evento ou a cada 5 s
    var waitingArt = artState.Values.Any((a) => !a.have && a.tries < 20);
    if (!(dirty && since >= 700) && !(waitingArt && since >= 1000) && since < 5000) { await Task.Delay(250); continue; }
    dirty = false; lastRefresh = DateTime.Now;
    try { HookSessions(); } catch { }
    var list = new List<Dictionary<string, object>>();
    try
    {
        var seen = new Dictionary<string, int>();
        var cur = mgr.GetCurrentSession();
        foreach (var s in mgr.GetSessions())
        {
            var id = SessionId(s, seen);
            try
            {
                var p = await s.TryGetMediaPropertiesAsync();
                if (string.IsNullOrEmpty(p.Title)) continue;
                var pb = s.GetPlaybackInfo();
                var tl = s.GetTimelineProperties();
                var key = $"{p.Title}|{p.Artist}";
                var a = artState.TryGetValue(id, out var st0) ? st0 : (key: "", have: false, tries: 0);
                if (a.key != key) { a = (key: key, have: false, tries: 0); Emit(new() { ["t"] = "art", ["id"] = id, ["art"] = "" }); }
                // o Spotify publica a capa alguns instantes depois do título: tenta várias vezes
                if (!a.have && a.tries < 20 && p.Thumbnail != null)
                {
                    a.tries++;
                    try
                    {
                        using var st = await p.Thumbnail.OpenReadAsync();
                        using var ms = new MemoryStream();
                        await st.AsStreamForRead().CopyToAsync(ms);
                        if (ms.Length > 100)
                        {
                            var type = string.IsNullOrEmpty(st.ContentType) ? "image/jpeg" : st.ContentType;
                            Emit(new() { ["t"] = "art", ["id"] = id, ["art"] = $"data:{type};base64,{Convert.ToBase64String(ms.ToArray())}" });
                            a.have = true;
                        }
                    }
                    catch { }
                }
                artState[id] = a;
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["app"] = s.SourceAppUserModelId,
                    ["title"] = p.Title, ["artist"] = p.Artist,
                    ["playing"] = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    ["canSeek"] = pb.Controls.IsPlaybackPositionEnabled,
                    ["position"] = Math.Round(tl.Position.TotalSeconds, 1),
                    ["duration"] = Math.Round(tl.EndTime.TotalSeconds, 1),
                    ["updated"] = tl.LastUpdatedTime.ToUnixTimeMilliseconds(),
                    ["current"] = cur != null && s.SourceAppUserModelId == cur.SourceAppUserModelId,
                });
            }
            catch { }
        }
        // esquece capas de sessões que fecharam
        foreach (var gone in artState.Keys.Where((k) => !list.Any((x) => (string)x["id"] == k)).ToList()) artState.Remove(gone);
    }
    catch { }
    // Só envia se algo mudou de verdade. A posição avança sozinha na interface, então
    // atualizações de posição "normais" (sem pulo) não precisam ser enviadas.
    var stable = JsonSerializer.Serialize(list.Select((x) => x.Where((kv) => kv.Key != "position" && kv.Key != "updated")
        .ToDictionary((kv) => kv.Key, (kv) => kv.Value)));
    bool jumped = false;
    foreach (var x in list)
    {
        var id = (string)x["id"]; var pos = (double)x["position"]; var upd = (long)x["updated"];
        var playing = (bool)x["playing"];
        if (lastPos.TryGetValue(id, out var lp))
        {
            var expected = lp.pos + (playing ? (upd - lp.upd) / 1000.0 : 0);
            if (Math.Abs(pos - expected) > 2.5) jumped = true;
        }
        else jumped = true;
    }
    if (stable != prevList || jumped)
    {
        prevList = stable;
        lastPos.Clear();
        foreach (var x in list) lastPos[(string)x["id"]] = ((double)x["position"], (long)x["updated"]);
        Emit(new Dictionary<string, object> { ["t"] = "sessions", ["list"] = list });
    }
    // sessões que fecharam saem da lista de eventos
    hooked.RemoveWhere((s) => { try { return !mgr.GetSessions().Contains(s); } catch { return true; } });
}

[DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
[DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
[DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
[DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

[StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
