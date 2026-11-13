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
        try
        {
            var s = mgr.GetCurrentSession();
            if (s == null) continue;
            var parts = line.Trim().Split(' ');
            switch (parts[0])
            {
                case "toggle": await s.TryTogglePlayPauseAsync(); break;
                case "next": await s.TrySkipNextAsync(); break;
                case "prev": await s.TrySkipPreviousAsync(); break;
                case "seek":
                    if (parts.Length > 1 && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var sec))
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
                if (SHQueryUserNotificationState(out int st) == 0 && (st == 2 || st == 3 || st == 4)) fs = true;
                var h = GetForegroundWindow();
                if (h == IntPtr.Zero) return;
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

// ---------- mídia (1x por segundo) ----------
string lastKey = "", prev = "";
bool haveArt = false; int tries = 0;
while (true)
{
    var o = new Dictionary<string, object> { ["active"] = false };
    string artOut = null;
    try
    {
        var s = mgr.GetCurrentSession();
        if (s != null)
        {
            var p = await s.TryGetMediaPropertiesAsync();
            var pb = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();
            var key = $"{p.Title}|{p.Artist}";
            if (key != lastKey && !string.IsNullOrEmpty(p.Title)) { lastKey = key; artOut = ""; haveArt = false; tries = 0; }
            if (!haveArt && tries < 20 && p.Thumbnail != null)
            {
                tries++;
                try
                {
                    using var st = await p.Thumbnail.OpenReadAsync();
                    using var ms = new MemoryStream();
                    await st.AsStreamForRead().CopyToAsync(ms);
                    if (ms.Length > 100)
                    {
                        var type = string.IsNullOrEmpty(st.ContentType) ? "image/jpeg" : st.ContentType;
                        artOut = $"data:{type};base64,{Convert.ToBase64String(ms.ToArray())}";
                        haveArt = true;
                    }
                }
                catch { }
            }
            o = new Dictionary<string, object>
            {
                ["active"] = !string.IsNullOrEmpty(p.Title),
                ["title"] = p.Title, ["artist"] = p.Artist, ["app"] = s.SourceAppUserModelId,
                ["playing"] = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                ["canSeek"] = pb.Controls.IsPlaybackPositionEnabled,
                ["position"] = Math.Round(tl.Position.TotalSeconds, 1),
                ["duration"] = Math.Round(tl.EndTime.TotalSeconds, 1),
                ["updated"] = tl.LastUpdatedTime.ToUnixTimeMilliseconds(),
            };
        }
    }
    catch { }
    var json = JsonSerializer.Serialize(o);
    if (json != prev || artOut != null)
    {
        prev = json;
        if (artOut != null) o["art"] = artOut;
        Emit(o);
    }
    await Task.Delay(1000);
}

[DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
[DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
[DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
[DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

[StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
