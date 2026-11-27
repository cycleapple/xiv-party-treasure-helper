using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace XivTreasureParty.Firebase;

/// <summary>
/// Firebase Realtime Database 的 WebSocket 即時協定（與網頁版 Firebase JS SDK 相同，/.ws?v=5）。
/// 所有 Subscribe 共用一條連線、各為一個監聽；有訂閱才連線，最後一個訂閱關閉就斷線。
/// token 換新時在同一條連線上重送 auth，不必斷線重連。
/// 送給 onEvent 的事件和 REST SSE 相同：put／patch，path 相對於訂閱路徑，在背景執行緒上呼叫。
/// </summary>
public sealed class FirebaseStreamClient : IDisposable
{
    private const int KeepAliveMs = 45000;
    private const int StallMs = 90000;
    private const int AuthCheckMs = 30000;

    private readonly string _dbUrl;
    private readonly FirebaseAuthClient _auth;
    private readonly object _gate = new();
    private readonly List<StreamSubscription> _subscriptions = new();
    private CancellationTokenSource? _loopCts;
    private Connection? _live;
    private bool _disposed;

    public FirebaseStreamClient(string dbUrl, FirebaseAuthClient auth)
    {
        _dbUrl = dbUrl.TrimEnd('/');
        _auth = auth;
    }

    public IDisposable Subscribe(string path, Action<StreamEvent> onEvent, Action<Exception>? onError = null)
    {
        var sub = new StreamSubscription(this, path, onEvent, onError);
        Connection? live;
        lock (_gate)
        {
            if (_disposed) return sub;
            _subscriptions.Add(sub);
            live = _live;
            if (_loopCts == null)
            {
                var cts = new CancellationTokenSource();
                _loopCts = cts;
                _ = Task.Run(() => RunAsync(cts));
            }
        }
        live?.Listen(sub);
        return sub;
    }

    internal void Remove(StreamSubscription sub)
    {
        Connection? unlistenOn = null;
        CancellationTokenSource? stop = null;
        lock (_gate)
        {
            if (!_subscriptions.Remove(sub)) return;
            if (_subscriptions.Count == 0)
            {
                stop = _loopCts;
                _loopCts = null;
                _live = null;
            }
            else if (!_subscriptions.Exists(s => s.WirePath == sub.WirePath))
                unlistenOn = _live;
        }
        unlistenOn?.SendRequest("n", new { p = sub.WirePath }, null);
        try { stop?.Cancel(); } catch { }
    }

    public void Dispose()
    {
        StreamSubscription[] subs;
        CancellationTokenSource? stop;
        lock (_gate)
        {
            _disposed = true;
            subs = _subscriptions.ToArray();
            _subscriptions.Clear();
            stop = _loopCts;
            _loopCts = null;
            _live = null;
        }
        foreach (var sub in subs) sub.MarkDisposed();
        try { stop?.Cancel(); } catch { }
    }

    private StreamSubscription[] Snapshot()
    {
        lock (_gate) return _subscriptions.ToArray();
    }

    private async Task RunAsync(CancellationTokenSource loopCts)
    {
        var ct = loopCts.Token;
        var backoffMs = 1000;
        string? redirectHost = null;
        while (!ct.IsCancellationRequested)
        {
            var host = redirectHost;
            redirectHost = null;
            using var conn = new Connection(new ClientWebSocket(), ct);
            try
            {
                await ServeAsync(conn, loopCts, host).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (RedirectException ex) when (host == null)
            {
                // 照伺服器指定的主機立刻連一次；那台又要求轉址就照一般斷線處理，避免無限轉址。
                redirectHost = ex.Host;
            }
            catch (Exception ex)
            {
                if (conn.WentLive) backoffMs = 1000;
                Plugin.Log.Warning($"[Stream] 連線中斷，{backoffMs}ms 後重連: {ex.Message}");
                foreach (var sub in Snapshot()) sub.NotifyError(ex);
                try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); } catch { return; }
                backoffMs = Math.Min(backoffMs * 2, 30000);
            }
        }
    }

    /// <summary>連一次並服務到連線結束；結束一律以例外表示（含伺服器正常關閉）。</summary>
    private async Task ServeAsync(Connection conn, CancellationTokenSource loopCts, string? host)
    {
        conn.Token = await _auth.EnsureSignedInAsync(conn.Ct).ConfigureAwait(false);
        await conn.Ws.ConnectAsync(BuildSocketUri(host), conn.Ct).ConfigureAwait(false);
        Volatile.Write(ref conn.LastReceiveMs, Environment.TickCount64);

        var sender = Task.Run(() => SendLoopAsync(conn));
        var upkeep = Task.Run(() => UpkeepLoopAsync(conn));
        try
        {
            await ReceiveLoopAsync(conn, loopCts).ConfigureAwait(false);
        }
        catch (Exception) when (conn.Failure is { } failure)
        {
            throw failure;
        }
        finally
        {
            conn.Close();
            lock (_gate)
            {
                if (_live == conn) _live = null;
            }
            try { await Task.WhenAll(sender, upkeep).ConfigureAwait(false); } catch { }
        }
    }

    private Uri BuildSocketUri(string? host)
    {
        var db = new Uri(_dbUrl);
        var scheme = db.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        // 命名空間是資料庫網址的第一段；模擬器（localhost／IP）不帶。
        var ns = db.HostNameType == UriHostNameType.Dns && db.Host.Contains('.') ? "&ns=" + db.Host.Split('.')[0] : "";
        return new Uri($"{scheme}://{host ?? db.Authority}/.ws?v=5{ns}");
    }

    /// <summary>只接受 Firebase 的資料庫網域，避免把 ID token 送到別處。</summary>
    internal static bool IsFirebaseHost(string? host) =>
        !string.IsNullOrEmpty(host)
        && Uri.CheckHostName(host) == UriHostNameType.Dns
        && (host.EndsWith(".firebasedatabase.app", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".firebaseio.com", StringComparison.OrdinalIgnoreCase));

    private async Task ReceiveLoopAsync(Connection conn, CancellationTokenSource loopCts)
    {
        var buffer = new byte[16384];
        using var message = new MemoryStream();
        var frames = new StringBuilder();
        var framesRemaining = 0;
        while (true)
        {
            message.SetLength(0);
            ValueWebSocketReceiveResult r;
            do
            {
                r = await conn.Ws.ReceiveAsync(buffer.AsMemory(), conn.Ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                    throw new FirebaseException($"伺服器關閉了連線（{conn.Ws.CloseStatus}）");
                message.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            Volatile.Write(ref conn.LastReceiveMs, Environment.TickCount64);

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            // 超過 16384 字元的訊息，伺服器先送一個訊框數，再分段送內容。
            if (framesRemaining > 0)
            {
                frames.Append(text);
                if (--framesRemaining > 0) continue;
                text = frames.ToString();
                frames.Clear();
            }
            else if (text.Length <= 6 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                if (count > 1) framesRemaining = count;
                continue;
            }

            HandleMessage(conn, loopCts, text);
        }
    }

    private void HandleMessage(Connection conn, CancellationTokenSource loopCts, string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var body = root.GetProperty("d");
        switch (root.GetProperty("t").GetString())
        {
            case "c":
                HandleControl(conn, loopCts, body);
                break;
            case "d":
                HandleData(conn, body);
                break;
        }
    }

    private void HandleControl(Connection conn, CancellationTokenSource loopCts, JsonElement control)
    {
        var payload = control.TryGetProperty("d", out var d) ? d : default;
        switch (control.GetProperty("t").GetString())
        {
            case "h":
                conn.SendRequest("auth", new { cred = conn.Token }, (status, _) =>
                {
                    if (status != "ok") throw new FirebaseException($"Firebase 拒絕 token（{status}）");
                    GoLive(conn, loopCts);
                });
                break;
            case "r":
            {
                var host = payload.ValueKind == JsonValueKind.String ? payload.GetString() : null;
                if (!IsFirebaseHost(host)) throw new FirebaseException($"伺服器要求改連的主機不是 Firebase 網域，不予理會：{host}");
                throw new RedirectException(host!);
            }
            case "s":
                throw new FirebaseException("伺服器要求關閉連線：" + payload);
        }
    }

    private void GoLive(Connection conn, CancellationTokenSource loopCts)
    {
        StreamSubscription[] subs;
        lock (_gate)
        {
            if (_loopCts != loopCts) return;
            conn.WentLive = true;
            _live = conn;
            subs = _subscriptions.ToArray();
        }
        foreach (var sub in subs) conn.Listen(sub);
    }

    private void HandleData(Connection conn, JsonElement body)
    {
        if (body.TryGetProperty("r", out var rEl))
        {
            if (!rEl.TryGetInt64(out var id) || !conn.Pending.TryRemove(id, out var onResponse)) return;
            var b = body.TryGetProperty("b", out var bEl) ? bEl : default;
            var status = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("s", out var s) ? s.GetString() : null;
            var detail = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("d", out var dd) ? dd.ToString() : "";
            onResponse(status, detail);
            return;
        }

        if (!body.TryGetProperty("a", out var a) || !body.TryGetProperty("b", out var push)) return;
        var path = push.TryGetProperty("p", out var p) ? p.GetString() ?? "" : "";
        switch (a.GetString())
        {
            case "d":
            case "m":
                if (push.TryGetProperty("d", out var value))
                    Dispatch(path, value, a.GetString() == "m");
                break;
            case "c":
                var revoked = "/" + string.Join('/', SplitPath(path));
                foreach (var sub in Snapshot())
                    if (sub.WirePath == revoked) conn.ListenFailed(sub, "伺服器取消監聽");
                break;
            case "ac":
                throw new FirebaseException("Firebase auth revoked");
        }
    }

    private void Dispatch(string pushPath, JsonElement value, bool merge)
    {
        var push = SplitPath(pushPath);
        foreach (var sub in Snapshot())
            foreach (var ev in RelativeEvents(sub.Segments, push, value, merge))
                sub.Deliver(ev);
    }

    internal static string[] SplitPath(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// 伺服器推送（path 從資料庫根算起，d＝set、m＝merge）對某個訂閱等於哪些 SSE 事件。
    /// 推送在訂閱路徑或底下：同型別、path 改成相對；在上層：取出訂閱路徑那段子樹當 put "/"。
    /// </summary>
    internal static IEnumerable<StreamEvent> RelativeEvents(string[] listen, string[] push, JsonElement value, bool merge)
    {
        if (IsPrefix(listen, push))
        {
            yield return new StreamEvent
            {
                Type = merge ? StreamEventType.Patch : StreamEventType.Put,
                Path = "/" + string.Join('/', push, listen.Length, push.Length - listen.Length),
                DataJson = value.GetRawText(),
            };
            yield break;
        }
        if (!IsPrefix(push, listen)) yield break;

        if (!merge)
        {
            yield return new StreamEvent { Type = StreamEventType.Put, Path = "/", DataJson = Descend(value, listen, push.Length) };
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Object) yield break;
        var below = new List<string>();
        foreach (var prop in value.EnumerateObject())
        {
            var key = SplitPath(prop.Name);
            var full = new string[push.Length + key.Length];
            push.CopyTo(full, 0);
            key.CopyTo(full, push.Length);
            if (IsPrefix(full, listen))
                yield return new StreamEvent { Type = StreamEventType.Put, Path = "/", DataJson = Descend(prop.Value, listen, full.Length) };
            else if (IsPrefix(listen, full))
                below.Add(JsonSerializer.Serialize(string.Join('/', full, listen.Length, full.Length - listen.Length)) + ":" + prop.Value.GetRawText());
        }
        if (below.Count > 0)
            yield return new StreamEvent { Type = StreamEventType.Patch, Path = "/", DataJson = "{" + string.Join(',', below) + "}" };
    }

    private static bool IsPrefix(string[] prefix, string[] path)
    {
        if (prefix.Length > path.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (!string.Equals(prefix[i], path[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static string Descend(JsonElement value, string[] target, int from)
    {
        var cur = value;
        for (var i = from; i < target.Length; i++)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(target[i], out var next)) return "null";
            cur = next;
        }
        return cur.GetRawText();
    }

    private static async Task SendLoopAsync(Connection conn)
    {
        try
        {
            await foreach (var text in conn.Outbox.Reader.ReadAllAsync(conn.Ct).ConfigureAwait(false))
            {
                // 刻意不做送出分段：我們送的 auth／q／n 都遠小於 16384 字元。
                await conn.Ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, conn.Ct).ConfigureAwait(false);
                Volatile.Write(ref conn.LastSendMs, Environment.TickCount64);
            }
        }
        catch (OperationCanceledException) when (conn.Ct.IsCancellationRequested) { }
        catch (Exception ex) { conn.Fail(ex); }
    }

    /// <summary>
    /// 每秒一次：閒置 45 秒送 "0"（與 JS SDK 相同）；45 秒沒收到東西送協定 ping（伺服器回 o），
    /// 90 秒都沒收到＝連線已死（半開的 TCP 不會自己報錯）；token 換新後在同一條連線上重送 auth。
    /// </summary>
    private async Task UpkeepLoopAsync(Connection conn)
    {
        var nextAuthCheck = Environment.TickCount64 + AuthCheckMs;
        var lastPing = 0L;
        try
        {
            while (true)
            {
                await Task.Delay(1000, conn.Ct).ConfigureAwait(false);
                var now = Environment.TickCount64;
                var silentMs = now - Volatile.Read(ref conn.LastReceiveMs);
                if (silentMs >= StallMs)
                    throw new TimeoutException($"{StallMs / 1000} 秒沒有收到任何資料，判定連線已斷");
                if (silentMs >= KeepAliveMs && now - lastPing >= KeepAliveMs)
                {
                    lastPing = now;
                    conn.Outbox.Writer.TryWrite("{\"t\":\"c\",\"d\":{\"t\":\"p\",\"d\":{}}}");
                }
                else if (now - Volatile.Read(ref conn.LastSendMs) >= KeepAliveMs)
                    conn.Outbox.Writer.TryWrite("0");
                if (!conn.WentLive) continue;

                if (Environment.TickCount64 >= nextAuthCheck)
                {
                    nextAuthCheck = Environment.TickCount64 + AuthCheckMs;
                    try { await _auth.EnsureSignedInAsync(conn.Ct).ConfigureAwait(false); }
                    catch (Exception ex) when (!conn.Ct.IsCancellationRequested)
                    {
                        Plugin.Log.Debug($"[Stream] 檢查 token 失敗，稍後再試: {ex.Message}");
                    }
                }

                var token = _auth.IdToken;
                if (string.IsNullOrEmpty(token) || token == conn.Token) continue;
                conn.Token = token;
                conn.SendRequest("auth", new { cred = token }, (status, _) =>
                {
                    if (status != "ok") throw new FirebaseException($"Firebase 拒絕換新的 token（{status}）");
                });
            }
        }
        catch (OperationCanceledException) when (conn.Ct.IsCancellationRequested) { }
        catch (Exception ex) { conn.Fail(ex); }
    }

    /// <summary>一條 WebSocket 連線的狀態，連線結束就整個丟掉。回應回呼在接收迴圈上執行，擲例外＝結束這條連線。</summary>
    private sealed class Connection : IDisposable
    {
        public readonly ClientWebSocket Ws;
        public readonly Channel<string> Outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        public readonly ConcurrentDictionary<long, Action<string?, string>> Pending = new();
        private readonly CancellationTokenSource _cts;
        private long _nextRequestId;
        public long LastSendMs = Environment.TickCount64;
        public long LastReceiveMs = Environment.TickCount64;
        public volatile string Token = "";
        public volatile bool WentLive;
        public volatile Exception? Failure;

        public Connection(ClientWebSocket ws, CancellationToken loopCt)
        {
            Ws = ws;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
        }

        public CancellationToken Ct => _cts.Token;

        public void SendRequest(string action, object body, Action<string?, string>? onResponse)
        {
            var id = Interlocked.Increment(ref _nextRequestId);
            if (onResponse != null) Pending[id] = onResponse;
            Outbox.Writer.TryWrite(JsonSerializer.Serialize(new { t = "d", d = new { r = id, a = action, b = body } }));
        }

        public void Listen(StreamSubscription sub) =>
            SendRequest("q", new { p = sub.WirePath, h = "" }, (status, detail) =>
            {
                if (status == "ok") sub.ListenBackoffMs = 1000;
                else ListenFailed(sub, $"{status} {detail}");
            });

        /// <summary>單一監聽被拒或被取消：和 SSE 單條串流一樣 1 秒起倍增到 30 秒後重送，連線本身不斷。</summary>
        public void ListenFailed(StreamSubscription sub, string reason)
        {
            var delayMs = sub.ListenBackoffMs;
            sub.ListenBackoffMs = Math.Min(delayMs * 2, 30000);
            Plugin.Log.Warning($"[Stream {sub.Path}] 監聽中斷，{delayMs}ms 後重試: {reason}");
            sub.NotifyError(new FirebaseException("監聽中斷：" + reason));
            _ = RetryListenAsync(sub, delayMs);
        }

        private async Task RetryListenAsync(StreamSubscription sub, int delayMs)
        {
            try { await Task.Delay(delayMs, Ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!sub.IsDisposed) Listen(sub);
        }

        public void Fail(Exception ex)
        {
            Failure ??= ex;
            Close();
        }

        public void Close()
        {
            Outbox.Writer.TryComplete();
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            Close();
            Ws.Dispose();
            _cts.Dispose();
        }
    }

    private sealed class RedirectException : Exception
    {
        public RedirectException(string host) : base("伺服器要求改連 " + host) => Host = host;

        public string Host { get; }
    }

    public sealed class StreamSubscription : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        private readonly FirebaseStreamClient _owner;
        private readonly Action<StreamEvent> _onEvent;
        private readonly Action<Exception>? _onError;
        private volatile bool _disposed;

        internal StreamSubscription(FirebaseStreamClient owner, string path, Action<StreamEvent> onEvent, Action<Exception>? onError)
        {
            _owner = owner;
            Path = path;
            Segments = SplitPath(path);
            WirePath = "/" + string.Join('/', Segments);
            _onEvent = onEvent;
            _onError = onError;
        }

        internal string Path { get; }
        internal string[] Segments { get; }
        internal string WirePath { get; }
        internal volatile int ListenBackoffMs = 1000;
        internal bool IsDisposed => _disposed;

        internal void Deliver(StreamEvent ev)
        {
            if (_disposed) return;
            try { _onEvent(ev); }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"[Stream {Path}] 處理事件錯誤");
                NotifyError(ex);
            }
        }

        internal void NotifyError(Exception ex)
        {
            if (_disposed) return;
            try { _onError?.Invoke(ex); } catch { }
        }

        internal void MarkDisposed() => _disposed = true;

        public void Dispose()
        {
            _disposed = true;
            _owner.Remove(this);
        }
    }
}

public enum StreamEventType { Put, Patch }

public sealed class StreamEvent
{
    public StreamEventType Type { get; set; }
    public string Path { get; set; } = "/";
    public string DataJson { get; set; } = "null";

    public JsonElement Data
    {
        get
        {
            using var doc = JsonDocument.Parse(DataJson);
            return doc.RootElement.Clone();
        }
    }

    public T? DeserializeData<T>() =>
        string.IsNullOrWhiteSpace(DataJson) || DataJson == "null"
            ? default
            : JsonSerializer.Deserialize<T>(DataJson, FirebaseDatabaseClient.JsonOptions);
}
