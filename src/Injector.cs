using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SteamTranslate;

/// <summary>
/// Steam = Chromium (CEF). Находим его отладочный порт, подключаемся по Chrome DevTools Protocol
/// и встраиваем inject.js (кнопку «Перевести») в страницы магазина и сообщества.
/// Порт открывает SteamBooster либо файл Steam\.cef-enable-debugging (порт 8080).
/// </summary>
sealed class Injector
{
    public const string Binding = "sbTranslate";
    static readonly Regex SteamUrl = new Regex(@"^https://([^/]*\.)?(steampowered\.com|steamcommunity\.com)(/|$)", RegexOptions.IgnoreCase);
    static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };

    readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    readonly string _injectJs;
    CdpSession _current;

    const int NoPortGraceSeconds = 20;

    public volatile string Lang = "ru";
    public Action<string, bool> OnStatus = (text, connected) => { };
    /// <summary>Steam работает уже давно, но порта отладки нет: кнопку перевода встроить нельзя.</summary>
    public Action OnSteamWithoutPort = () => { };

    public Injector()
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("inject.js"))
        using (var r = new StreamReader(s, Encoding.UTF8))
            _injectJs = r.ReadToEnd();
    }

    public string BuildSource()
    {
        return "window.__SB_TR_CFG=" + _json.Serialize(new { tl = Lang, all = false }) + ";\n" + _injectJs;
    }

    public Task SetLangAsync(string lang)
    {
        Lang = lang;
        var s = _current;
        return s == null ? Task.CompletedTask : s.ApplyLangAsync();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        bool waitingLogged = false;
        int promptedPid = 0, seenPid = 0;
        var seenSince = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var found = await FindSteamAsync().ConfigureAwait(false);
                if (found == null)
                {
                    int pid = SteamLauncher.RunningPid();
                    if (pid == 0)
                    {
                        seenPid = 0;
                        OnStatus("Жду запуска Steam…", false);
                    }
                    else
                    {
                        if (pid != seenPid) { seenPid = pid; seenSince = DateTime.UtcNow; }
                        OnStatus("Steam запущен без порта отладки", false);
                        if (pid != promptedPid && (DateTime.UtcNow - seenSince).TotalSeconds >= NoPortGraceSeconds)
                        {
                            promptedPid = pid;
                            Log.Write("Steam (pid " + pid + ") работает без порта отладки");
                            OnSteamWithoutPort();
                        }
                    }
                    if (!waitingLogged) Log.Write("жду Steam с открытым портом отладки");
                    waitingLogged = true;
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                    continue;
                }
                seenPid = 0;
                waitingLogged = false;
                Log.Write("подключаюсь к Steam, порт " + found.Item1);
                var session = new CdpSession(this);
                _current = session;
                OnStatus("Подключён к Steam (порт " + found.Item1 + ")", true);
                await session.RunAsync(found.Item2, ct).ConfigureAwait(false);
                Log.Write("Steam отключился");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Write("ошибка соединения: " + e.Message); }
            finally { _current = null; }
            if (!ct.IsCancellationRequested)
            {
                OnStatus("Steam отключился, жду…", false);
                try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
    }

    // ---------- поиск отладочного порта ----------
    static async Task<Tuple<int, string>> FindSteamAsync()
    {
        foreach (var port in CandidatePorts())
        {
            try
            {
                var text = await Http.GetStringAsync("http://127.0.0.1:" + port + "/json/version").ConfigureAwait(false);
                var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(text);
                var ua = d.ContainsKey("User-Agent") ? d["User-Agent"] as string : null;
                var ws = d.ContainsKey("webSocketDebuggerUrl") ? d["webSocketDebuggerUrl"] as string : null;
                if (ua != null && ua.IndexOf("Valve Steam Client", StringComparison.OrdinalIgnoreCase) >= 0 && ws != null)
                    return Tuple.Create(port, ws);
            }
            catch { /* не наш порт */ }
        }
        return null;
    }

    /// <summary>Порты на 127.0.0.1, которые слушают процессы Steam (steam.exe / steamwebhelper.exe).</summary>
    static IEnumerable<int> CandidatePorts()
    {
        var pids = new HashSet<int>();
        foreach (var name in new[] { "steamwebhelper", "steam" })
            foreach (var p in Process.GetProcessesByName(name)) { pids.Add(p.Id); p.Dispose(); }
        if (pids.Count == 0) return Enumerable.Empty<int>();

        var ports = new List<int>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2 /*AF_INET*/, 3 /*TCP_TABLE_OWNER_PID_LISTENER*/, 0);
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, true, 2, 3, 0) != 0) return ports;
            int n = Marshal.ReadInt32(buf);
            for (int i = 0; i < n; i++)
            {
                int off = 4 + i * 24; // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                uint addr = (uint)Marshal.ReadInt32(buf, off + 4);
                int rawPort = Marshal.ReadInt32(buf, off + 8) & 0xFFFF;
                int pid = Marshal.ReadInt32(buf, off + 20);
                if (addr == 0x0100007F && pids.Contains(pid))
                    ports.Add(((rawPort & 0xFF) << 8) | (rawPort >> 8));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return ports.Distinct();
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

    // ---------- одно соединение с Steam ----------
    sealed class Attached
    {
        public string SessionId;
        public string ScriptId;
    }

    sealed class CdpSession
    {
        readonly Injector _owner;
        readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly ClientWebSocket _ws = new ClientWebSocket();
        readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> _pending =
            new ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        readonly ConcurrentDictionary<string, Attached> _attached = new ConcurrentDictionary<string, Attached>();
        readonly ConcurrentDictionary<string, byte> _attaching = new ConcurrentDictionary<string, byte>();
        int _nextId;

        public CdpSession(Injector owner) { _owner = owner; }

        public async Task RunAsync(string wsUrl, CancellationToken ct)
        {
            try
            {
                await _ws.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
                var recv = ReceiveLoopAsync(ct);
                await Send("Target.setDiscoverTargets", new Dictionary<string, object> { { "discover", true } }, null).ConfigureAwait(false);
                await recv.ConfigureAwait(false);
            }
            finally
            {
                foreach (var p in _pending.Values) p.TrySetException(new Exception("соединение закрыто"));
                _pending.Clear();
                _ws.Dispose();
            }
        }

        async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buf = new byte[1 << 16];
            var ms = new MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);
                    Dispatch(Encoding.UTF8.GetString(ms.ToArray()));
                }
            }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }

        public async Task<Dictionary<string, object>> Send(string method, Dictionary<string, object> prm, string sessionId)
        {
            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            var cmd = new Dictionary<string, object> { { "id", id }, { "method", method }, { "params", prm ?? new Dictionary<string, object>() } };
            if (sessionId != null) cmd["sessionId"] = sessionId;
            var bytes = Encoding.UTF8.GetBytes(_json.Serialize(cmd));
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try { await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false); }
            finally { _sendLock.Release(); }
            return await tcs.Task.ConfigureAwait(false);
        }

        static T Get<T>(Dictionary<string, object> d, string key) where T : class
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as T : null;
        }

        void Dispatch(string text)
        {
            var msg = (Dictionary<string, object>)_json.DeserializeObject(text);
            if (msg.ContainsKey("id"))
            {
                TaskCompletionSource<Dictionary<string, object>> tcs;
                if (!_pending.TryRemove(Convert.ToInt32(msg["id"]), out tcs)) return;
                var err = Get<Dictionary<string, object>>(msg, "error");
                if (err != null) tcs.TrySetException(new Exception(Convert.ToString(err.ContainsKey("message") ? err["message"] : "CDP error")));
                else tcs.TrySetResult(Get<Dictionary<string, object>>(msg, "result"));
                return;
            }

            var method = Get<string>(msg, "method");
            var prm = Get<Dictionary<string, object>>(msg, "params");
            switch (method)
            {
                case "Target.targetCreated":
                case "Target.targetInfoChanged":
                    Consider(Get<Dictionary<string, object>>(prm, "targetInfo"));
                    break;
                case "Target.detachedFromTarget":
                    {
                        var sid = Get<string>(prm, "sessionId");
                        foreach (var kv in _attached.Where(k => k.Value.SessionId == sid).ToList())
                        {
                            Attached dummy;
                            _attached.TryRemove(kv.Key, out dummy);
                        }
                        break;
                    }
                case "Target.targetDestroyed":
                    {
                        Attached dummy;
                        _attached.TryRemove(Get<string>(prm, "targetId") ?? "", out dummy);
                        break;
                    }
                case "Runtime.bindingCalled":
                    if (Get<string>(prm, "name") == Binding)
                    {
                        var payload = Get<string>(prm, "payload");
                        var sid = Get<string>(msg, "sessionId");
                        int ctx = prm.ContainsKey("executionContextId") ? Convert.ToInt32(prm["executionContextId"]) : 0;
                        Task.Run(() => OnBindingAsync(sid, ctx, payload));
                    }
                    break;
            }
        }

        void Consider(Dictionary<string, object> info)
        {
            if (info == null || Get<string>(info, "type") != "page") return;
            var url = Get<string>(info, "url") ?? "";
            var targetId = Get<string>(info, "targetId");
            if (targetId != null && SteamUrl.IsMatch(url)) Task.Run(() => AttachAsync(targetId, url));
        }

        async Task AttachAsync(string targetId, string url)
        {
            if (_attached.ContainsKey(targetId) || !_attaching.TryAdd(targetId, 0)) return;
            try
            {
                var r = await Send("Target.attachToTarget", new Dictionary<string, object> { { "targetId", targetId }, { "flatten", true } }, null).ConfigureAwait(false);
                var sid = (string)r["sessionId"];
                var source = _owner.BuildSource();
                await Send("Runtime.enable", null, sid).ConfigureAwait(false);
                await Send("Runtime.addBinding", new Dictionary<string, object> { { "name", Binding } }, sid).ConfigureAwait(false);
                await Send("Page.enable", null, sid).ConfigureAwait(false);
                var s = await Send("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object> { { "source", source } }, sid).ConfigureAwait(false);
                _attached[targetId] = new Attached { SessionId = sid, ScriptId = (string)s["identifier"] };
                await Send("Runtime.evaluate", new Dictionary<string, object> { { "expression", source } }, sid).ConfigureAwait(false);
                Log.Write("кнопка добавлена: " + (url.Length > 90 ? url.Substring(0, 90) : url));
            }
            catch (Exception e) { Log.Write("не удалось подключиться к вкладке: " + e.Message); }
            finally { byte b; _attaching.TryRemove(targetId, out b); }
        }

        async Task OnBindingAsync(string sessionId, int contextId, string payload)
        {
            Dictionary<string, object> req;
            try { req = (Dictionary<string, object>)_json.DeserializeObject(payload); }
            catch { return; }
            int id = Convert.ToInt32(req["id"]);
            string expr;
            try
            {
                var texts = ((IList)req["q"]).Cast<object>().Select(x => (string)x).ToList();
                var res = await Google.TranslateHtmlAsync(texts, (string)req["tl"]).ConfigureAwait(false);
                expr = "window.__sbTrDone(" + id + ",true," + _json.Serialize(res) + ")";
            }
            catch (Exception e)
            {
                Log.Write("ошибка перевода: " + e.Message);
                expr = "window.__sbTrDone(" + id + ",false," + _json.Serialize(e.Message) + ")";
            }
            try
            {
                var p = new Dictionary<string, object> { { "expression", expr } };
                if (contextId != 0) p["contextId"] = contextId;
                await Send("Runtime.evaluate", p, sessionId).ConfigureAwait(false);
            }
            catch { /* страница уже закрыта или перезагружена */ }
        }

        /// <summary>Смена языка: обновляем скрипт для новых страниц и настройку в уже открытых.</summary>
        public async Task ApplyLangAsync()
        {
            foreach (var kv in _attached.ToList())
            {
                try
                {
                    var source = _owner.BuildSource();
                    var sid = kv.Value.SessionId;
                    await Send("Page.removeScriptToEvaluateOnNewDocument", new Dictionary<string, object> { { "identifier", kv.Value.ScriptId } }, sid).ConfigureAwait(false);
                    var s = await Send("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object> { { "source", source } }, sid).ConfigureAwait(false);
                    kv.Value.ScriptId = (string)s["identifier"];
                    var expr = "window.__SB_TR_CFG&&(window.__SB_TR_CFG.tl=" + _json.Serialize(_owner.Lang) + ")";
                    await Send("Runtime.evaluate", new Dictionary<string, object> { { "expression", expr } }, sid).ConfigureAwait(false);
                }
                catch (Exception e) { Log.Write("смена языка: " + e.Message); }
            }
        }
    }
}
