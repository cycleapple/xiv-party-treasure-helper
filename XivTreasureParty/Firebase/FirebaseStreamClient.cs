using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XivTreasureParty.Firebase;

/// <summary>
/// Firebase Realtime Database REST streaming (Server-Sent Events)
/// GET {path}.json with Accept: text/event-stream 回傳長連線的 put/patch 事件。
/// 單一 client 可同時開多條 stream，關閉由 CancellationToken 控制。
/// </summary>
public sealed class FirebaseStreamClient : IDisposable
{
    private readonly string _dbUrl;
    private readonly FirebaseAuthClient _auth;
    private readonly ConcurrentDictionary<Guid, StreamSubscription> _subscriptions = new();

    public FirebaseStreamClient(string dbUrl, FirebaseAuthClient auth)
    {
        _dbUrl = dbUrl.TrimEnd('/');
        _auth = auth;
    }

    public IDisposable Subscribe(string path, Action<StreamEvent> onEvent, Action<Exception>? onError = null)
    {
        var sub = new StreamSubscription(this, path, onEvent, onError);
        _subscriptions.TryAdd(sub.Id, sub);
        sub.Start();
        return sub;
    }

    internal void Remove(Guid id) => _subscriptions.TryRemove(id, out _);

    public void Dispose()
    {
        foreach (var sub in _subscriptions.Values)
        {
            try { sub.Dispose(); } catch { }
        }
        _subscriptions.Clear();
    }

    internal string BuildUrlWithoutToken(string path) => $"{_dbUrl}/{path.TrimStart('/')}.json";

    internal FirebaseAuthClient Auth => _auth;

    public sealed class StreamSubscription : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        private readonly FirebaseStreamClient _owner;
        private readonly string _path;
        private readonly Action<StreamEvent> _onEvent;
        private readonly Action<Exception>? _onError;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public StreamSubscription(FirebaseStreamClient owner, string path, Action<StreamEvent> onEvent, Action<Exception>? onError)
        {
            _owner = owner;
            _path = path;
            _onEvent = onEvent;
            _onError = onError;
        }

        public void Start()
        {
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var backoffMs = 1000;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await StreamOnceAsync(ct).ConfigureAwait(false);
                    backoffMs = 1000;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[Stream {_path}] 連線中斷，{backoffMs}ms 後重連: {ex.Message}");
                    _onError?.Invoke(ex);
                    try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); } catch { return; }
                    backoffMs = Math.Min(backoffMs * 2, 30000);
                }
            }
        }

        private async Task StreamOnceAsync(CancellationToken ct)
        {
            var token = await _owner.Auth.EnsureSignedInAsync(ct).ConfigureAwait(false);
            var url = _owner.BuildUrlWithoutToken(_path) + $"?auth={Uri.EscapeDataString(token)}";

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 307 && resp.Headers.Location is { } redirect)
            {
                using var req2 = new HttpRequestMessage(HttpMethod.Get, redirect);
                using var resp2 = await http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp2.EnsureSuccessStatusCode();
                await ReadEventsAsync(resp2, ct).ConfigureAwait(false);
                return;
            }
            resp.EnsureSuccessStatusCode();
            await ReadEventsAsync(resp, ct).ConfigureAwait(false);
        }

        private async Task ReadEventsAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            string? currentEvent = null;
            var dataBuilder = new System.Text.StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;

                if (line.Length == 0)
                {
                    if (currentEvent != null && dataBuilder.Length > 0)
                    {
                        DispatchEvent(currentEvent, dataBuilder.ToString());
                    }
                    currentEvent = null;
                    dataBuilder.Clear();
                    continue;
                }

                if (line.StartsWith("event:"))
                    currentEvent = line.Substring(6).Trim();
                else if (line.StartsWith("data:"))
                {
                    if (dataBuilder.Length > 0) dataBuilder.Append('\n');
                    dataBuilder.Append(line.AsSpan(5).TrimStart());
                }
            }
        }

        private void DispatchEvent(string eventName, string data)
        {
            try
            {
                switch (eventName)
                {
                    case "put":
                    case "patch":
                    {
                        using var doc = JsonDocument.Parse(data);
                        var root = doc.RootElement;
                        var path = root.GetProperty("path").GetString() ?? "/";
                        JsonElement value = root.GetProperty("data");
                        _onEvent(new StreamEvent
                        {
                            Type = eventName == "put" ? StreamEventType.Put : StreamEventType.Patch,
                            Path = path,
                            DataJson = value.GetRawText()
                        });
                        break;
                    }
                    case "keep-alive":
                        break;
                    case "cancel":
                        throw new FirebaseException("Firebase stream cancelled by server: " + data);
                    case "auth_revoked":
                        throw new FirebaseException("Firebase auth revoked");
                    default:
                        Plugin.Log.Debug($"[Stream {_path}] 未知事件類型: {eventName}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"[Stream {_path}] 處理事件錯誤 ({eventName})");
                _onError?.Invoke(ex);
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            _owner.Remove(Id);
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
