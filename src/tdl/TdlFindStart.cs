#!/usr/bin/env dotnet

#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*

using System.Text.Json;
using System.Text.RegularExpressions;

long ReadLong(JsonElement el) =>
    el.ValueKind == JsonValueKind.Number ? el.GetInt64() :
    el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var n) ? n : 0;

using (var client = new TdLib.TdJsonClient())
{
    client.Send("{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":2}");

    var apiId = Environment.GetEnvironmentVariable("tdl_api_id", EnvironmentVariableTarget.User);
    var apiHash = Environment.GetEnvironmentVariable("tdl_api_hash", EnvironmentVariableTarget.User);
    if (string.IsNullOrWhiteSpace(apiId) || string.IsNullOrWhiteSpace(apiHash))
    {
        Console.Error.WriteLine("[ERROR] 缺少 tdl_api_id 或 tdl_api_hash");
        Environment.Exit(2);
        return;
    }
    var tdlPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tdl").Replace("\\", "/");

    var state = new EventLoop(client);
    state.Start();

    state.Send("{\"@type\":\"setTdlibParameters\",\"api_id\":\"" + apiId + "\",\"api_hash\":\"" + apiHash + "\",\"device_model\":\"PC\",\"system_language_code\":\"en\",\"application_version\":\"1.0.0\",\"database_directory\":\"" + tdlPath + "/db\",\"files_directory\":\"" + tdlPath + "/files\",\"use_file_database\":true,\"use_chat_info_database\":true,\"use_message_database\":true}");

    await WaitFor(state, "Ready", 60);

    // 搜索 /start 命令
    var searchReq = "{\"@type\":\"searchChatMessages\",\"chat_id\":" + 8682502640 + ",\"query\":\"/start\",\"from_message_id\":0,\"offset\":0,\"limit\":1000,\"filter\":null}";
    var result = await state.RpcAsync(searchReq, 30);
    using var doc = JsonDocument.Parse(result);
    if (doc.RootElement.GetProperty("@type").GetString() == "error")
    {
        Console.Error.WriteLine("[ERROR] " + result);
        Environment.Exit(1);
        return;
    }

    var msgs = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
    Console.WriteLine($"找到 {msgs.Count} 条 /start 命令:");
    foreach (var m in msgs)
    {
        var id = ReadLong(m.GetProperty("id"));
        var date = ReadLong(m.GetProperty("date"));
        var dt = DateTimeOffset.FromUnixTimeSeconds(date).ToLocalTime();
        var content = m.GetProperty("content");
        var text = "";
        if (content.TryGetProperty("text", out var t) && t.TryGetProperty("text", out var tt))
            text = tt.GetString() ?? "";
        Console.WriteLine($"  msg_id={id}  {dt:yyyy-MM-dd HH:mm:ss}  text={text}");
    }

    state.Stop();
}

async Task WaitFor(EventLoop state, string target, int timeoutSec)
{
    var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
    while (DateTime.UtcNow < deadline)
    {
        if (state.GetAuthState() == target) return;
        await Task.Delay(200);
    }
}

public class EventLoop
{
    private readonly TdLib.TdJsonClient _client;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    private string _authState = "Init";
    private int _counter;
    private System.Threading.CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public EventLoop(TdLib.TdJsonClient client) { _client = client; }
    public void Start() { _loopTask = Task.Run(Loop); }
    public void Stop() { _cts.Cancel(); try { _loopTask?.Wait(2000); } catch { } }
    public string GetAuthState() => _authState;

    public void Send(string jsonWithoutExtra) { _client.Send(jsonWithoutExtra); }

    public Task<string> RpcAsync(string jsonWithoutExtra, double timeoutSec)
    {
        var key = "r-" + Interlocked.Increment(ref _counter);
        var injected = jsonWithoutExtra.Insert(jsonWithoutExtra.Length - 1, ",\"@extra\":\"" + key + "\"");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        _client.Send(injected);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(timeoutSec));
            if (_pending.TryRemove(key, out var p)) p.TrySetResult("{\"@type\":\"error\",\"code\":408,\"message\":\"timeout\"}");
        });
        return tcs.Task;
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var u = _client.Receive(1.0);
                if (string.IsNullOrEmpty(u)) continue;
                using var doc = JsonDocument.Parse(u);
                var root = doc.RootElement;
                if (root.TryGetProperty("@extra", out var extra))
                {
                    var key = extra.GetString();
                    if (key != null && _pending.TryRemove(key, out var tcs)) tcs.TrySetResult(u);
                }
                else if (root.TryGetProperty("@type", out var t) && t.GetString() == "updateAuthorizationState" && root.TryGetProperty("authorization_state", out var auth))
                {
                    var n = auth.GetProperty("@type").GetString() ?? "";
                    _authState = n switch
                    {
                        "authorizationStateReady" => "Ready",
                        "authorizationStateClosed" => "Closed",
                        _ => n
                    };
                }
            }
            catch { if (_cts.IsCancellationRequested) break; Thread.Sleep(50); }
        }
    }
}
