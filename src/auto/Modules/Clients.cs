// ============================================================================
// Modules/Clients.cs — WorkBuddy / Trae 业务客户端
//
// 由 checkin.cs 通过 #:include 复用，勿单独运行。
// 移植自 iicookies/auto-checkin 的 checkin.py，接口端点与响应解析保持一致。
// ============================================================================

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoCheckin;

namespace AutoCheckin;

/// <summary>单个产品的签到结果。</summary>
public sealed class CheckinResult
{
    public string Product { get; init; } = "";
    public string? Status { get; set; }
    public string? Credits { get; set; }

    // 原始响应，仅 --debug 时打印
    public JsonNode? RawStatus;
    public JsonNode? RawCheckin;
    public JsonNode? RawCredits;

    /// <summary>UI 兜底结果（开启 --ui-fallback 时）。</summary>
    public (bool Ok, string Status)? UiFallback;
}

/// <summary>腾讯 CodeBuddy 国内版（WorkBuddy）签到。</summary>
public sealed class WorkBuddyClient
{
    private const string Base = "https://www.codebuddy.cn";
    private const string AuthBase = "https://copilot.tencent.com";
    public const string UserAgent = "CLI/2.63.2 CodeBuddy/2.63.2";

    /// <summary>提前 24 小时判定 token 即将过期，触发刷新。</summary>
    private const double RefreshMargin = 24 * 3600;

    /// <summary>会话失效特征。</summary>
    private static readonly string[] DeadMarkers = ["Offline user session not found", "12153"];

    private readonly string _uid;
    private string _accessToken;
    private string _refreshToken;
    private double _expiresAt;
    private readonly string _domain;

    public WorkBuddyClient(JsonObject cred)
    {
        _uid = cred.S("uid") ?? "unknown";
        _accessToken = cred.S("accessToken")
            ?? throw new InvalidOperationException("凭证缺少 accessToken");
        _refreshToken = cred.S("refreshToken") ?? "";
        _expiresAt = cred.D("expiresAt") ?? 0;
        _domain = cred.S("domain") ?? "codebuddy.cn";
    }

    private static double Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private bool NeedsRefresh() => _expiresAt - Now < RefreshMargin;

    private Dictionary<string, string> AuthHeaders() => new()
    {
        ["User-Agent"] = UserAgent,
        // 实测必须带 Bearer 前缀，无前缀返回 401
        ["Authorization"] = $"Bearer {_accessToken}",
        ["Content-Type"] = "application/json",
    };

    /// <summary>用 refreshToken 换新 accessToken，成功则更新内存并返回 true。</summary>
    private bool Refresh()
    {
        var body = new JsonObject { ["refreshToken"] = _refreshToken };
        JsonObject data;
        try
        {
            data = Http.Request("POST", $"{AuthBase}/v2/plugin/auth/token/refresh",
                new Dictionary<string, string> { ["User-Agent"] = UserAgent, ["Content-Type"] = "application/json" },
                body);
        }
        catch (CheckinRequestException ex)
        {
            Log.Dbg($"WorkBuddy 刷新请求失败: {ex.Message}");
            return false;
        }

        var d = data.O("data");
        var newAccess = d.S("accessToken", "access_token");
        var newRefresh = d.S("refreshToken", "refresh_token") ?? _refreshToken;
        var expiresIn = d.D("expiresIn", "expires_in") ?? 0;

        if (string.IsNullOrEmpty(newAccess))
        {
            Log.Dbg($"WorkBuddy 刷新返回无 token: {data.Dump()}");
            return false;
        }

        _accessToken = newAccess;
        _refreshToken = newRefresh;
        _expiresAt = Now + (expiresIn > 0 ? expiresIn : 7 * 24 * 3600);
        Log.Dbg($"WorkBuddy token 已刷新，新过期时间 {Log.Stamp(_expiresAt)}");
        return true;
    }

    public CheckinResult Run()
    {
        var result = new CheckinResult { Product = "WorkBuddy" };

        if (NeedsRefresh())
        {
            Log.WriteLine("  [WorkBuddy] token 即将过期，尝试刷新...");
            if (!Refresh())
            {
                result.Status = "token 失效，请重新登录";
                return result;
            }
            CredentialStore.Persist(_uid, "workbuddy", ToCred());
        }

        // 先查状态
        JsonObject st;
        try
        {
            st = Http.Request("POST", $"{Base}/v2/billing/meter/checkin-activity-status", AuthHeaders(), new JsonObject());
            Log.Dbg($"checkin-activity-status: {st.Dump()}");
            result.RawStatus = st;
        }
        catch (CheckinRequestException ex)
        {
            result.Status = $"查询状态失败: {ex.Message}";
            return result;
        }

        var raw = st.Dump();
        if (DeadMarkers.Any(m => raw.Contains(m, StringComparison.Ordinal)))
        {
            result.Status = "会话已失效，请重新登录 (login.cs)";
            return result;
        }

        // 执行签到
        try
        {
            var r = Http.Request("POST", $"{Base}/v2/billing/meter/daily-checkin", AuthHeaders(), new JsonObject());
            Log.Dbg($"daily-checkin: {r.Dump()}");
            result.RawCheckin = r;
            result.Status = ParseCheckinResult(r);
        }
        catch (CheckinRequestException ex)
        {
            result.Status = $"签到请求失败: {ex.Message}";
            return result;
        }

        // 查询积分
        try
        {
            var cr = Http.Request("POST", $"{Base}/v2/billing/meter/get-user-resource", AuthHeaders(), new JsonObject());
            Log.Dbg($"get-user-resource: {cr.Dump()}");
            result.RawCredits = cr;
            result.Credits = ParseCredits(cr);
        }
        catch (CheckinRequestException ex)
        {
            result.Credits = $"查询积分失败: {ex.Message}";
        }

        return result;
    }

    private static string ParseCheckinResult(JsonObject r)
    {
        var code = r.D("code");
        var msg = r.S("message", "msg") ?? "";
        var data = r.O("data");
        var blob = r.Dump();

        if (code is 0 or 200 || r.N("success") is not null)
        {
            var credit = data.D("credit");
            if (credit is not null)
            {
                var streak = data.D("streak_days");
                var extra = streak > 0 ? $"，连续{streak:0}天" : "";
                return $"签到成功 +{credit:0}积分{extra}";
            }
            return "签到成功";
        }

        // 已签到类提示
        foreach (var kw in new[] { "已签到", "已领取", "明日", "already", "claimed", "checked", "today_checked_in" })
        {
            if (!msg.Contains(kw, StringComparison.OrdinalIgnoreCase) && !blob.Contains(kw, StringComparison.OrdinalIgnoreCase))
                continue;
            if (data.N("today_checked_in") is not null || data.N("checked_in") is not null)
                return "今日已签到";
            return $"今日已签到 ({msg})";
        }
        return $"签到结果: code={code} msg={msg}";
    }

    private static string ParseCredits(JsonObject r)
    {
        var data = r.O("data");
        // get-user-resource 返回 data.Response.Data.TotalDosage
        var total = data.O("Response").O("Data").D("TotalDosage");
        if (total is not null) return $"当前积分: {total:0}";

        // 兜底其他字段名
        var credits = data.D("credits") ?? data.D("totalCredits") ?? data.D("balance") ?? data.D("TotalCount");
        if (credits is null) return data.Dump(120);
        return $"当前积分: {credits:0}";
    }

    private JsonObject ToCred() => new()
    {
        ["uid"] = _uid,
        ["accessToken"] = _accessToken,
        ["refreshToken"] = _refreshToken,
        ["expiresAt"] = _expiresAt,
        ["domain"] = _domain,
    };
}

/// <summary>字节 Trae IDE（Trae Work 积分）签到。</summary>
public sealed class TraeClient
{
    private const string Base = "https://api.trae.cn";
    private const string AuthBase = "https://api.trae.com.cn";
    private const string UserAgent = "Trae/0.1.43";
    private const double RefreshMargin = 24 * 3600;

    private string _userId;
    private string _accessToken;
    private string _refreshToken;
    private double _expiresAt;
    private string _deviceId;
    private readonly string _screenName;

    // 文件名用加载时的名字，避免后来补上 userId 后写出第二份凭证
    private readonly string _credName;

    /// <summary>refresh 被服务端明确拒绝（区别于网络异常），需要重新登录。</summary>
    public bool RefreshRejected { get; private set; }

    public TraeClient(JsonObject cred)
    {
        _userId = cred.S("userId") ?? "";
        _accessToken = cred.S("accessToken")
            ?? throw new InvalidOperationException("凭证缺少 accessToken");
        _refreshToken = cred.S("refreshToken") ?? "";
        _expiresAt = cred.D("expiresAt") ?? 0;
        _screenName = cred.S("screenName") ?? "";
        _credName = !string.IsNullOrEmpty(_screenName) ? _screenName
            : !string.IsNullOrEmpty(_userId) ? _userId : "trae";

        // 优先用桌面客户端的真实设备号，兜底才用凭证里的
        _deviceId = DeviceId.Resolve(cred.S("deviceId") ?? "");

        if (string.IsNullOrEmpty(_userId))
            _userId = UserIdFromJwt(_accessToken) ?? "";
    }

    private static double Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>从 Cloud-IDE-JWT 的 payload.data.id 提取用户 ID（凭证里 userId 常为空）。</summary>
    private static string? UserIdFromJwt(string token)
    {
        var direct = Util.JwtPayloadValue(token, "UserID", "userId");
        if (!string.IsNullOrEmpty(direct)) return direct;
        return Util.JwtPayloadValue(token, "id", "UserID");
    }

    private bool NeedsRefresh() => _expiresAt - Now < RefreshMargin;

    private Dictionary<string, string> AuthHeaders() => new()
    {
        ["Content-Type"] = "application/json",
        ["Authorization"] = $"Cloud-IDE-JWT {_accessToken}",
        // claim 强依赖 x-device-id 做设备去重（一设备一天一次）
        ["x-device-id"] = _deviceId,
        ["X-User-Region"] = "CN",
        ["User-Agent"] = UserAgent,
    };

    /// <summary>刷新 token。失败时置 RefreshRejected 表示被服务端明确拒绝。</summary>
    private bool Refresh()
    {
        RefreshRejected = false;
        var body = new JsonObject
        {
            ["ClientID"] = "en1oxy7wnw8j9n",
            ["RefreshToken"] = _refreshToken,
            ["ClientSecret"] = "-",
            ["UserID"] = _userId,
        };

        JsonObject data;
        try
        {
            data = Http.Request("POST", $"{AuthBase}/cloudide/api/v3/trae/oauth/ExchangeToken",
                new Dictionary<string, string> { ["Content-Type"] = "application/json" }, body);
        }
        catch (CheckinRequestException ex)
        {
            Log.Dbg($"Trae 刷新请求失败: {ex.Message}");
            return false;
        }

        var result = data.O("Result") ?? data.O("data");
        var newAccess = result.S("Token", "AccessToken", "access_token");
        var newRefresh = result.S("RefreshToken", "refresh_token") ?? _refreshToken;
        var expireAt = result.D("TokenExpireAt") ?? result.D("expiresAt");

        if (string.IsNullOrEmpty(newAccess))
        {
            Log.Dbg($"Trae 刷新返回无 token: {data.Dump()}");
            // 响应里带 Error.Code（如 20101 refresh token is invalid）说明被明确拒绝，
            // 与网络异常区分开：前者需重新登录，后者只需下次再试
            var err = data.O("ResponseMetadata").O("Error").D("Code");
            if (err is not null) RefreshRejected = true;
            return false;
        }

        _accessToken = newAccess;
        _refreshToken = newRefresh;
        // TokenExpireAt 是毫秒时间戳，兼容秒
        double ts = expireAt ?? 0;
        _expiresAt = ts <= 0 ? Now + 3600 : (ts > 1e12 ? ts / 1000 : ts);

        Log.Dbg($"Trae token 已刷新，新过期时间 {Log.Stamp(_expiresAt)}");
        return true;
    }

    private void Persist() => CredentialStore.Persist(_credName, "trae", ToCred());

    private void QueryCreditsSafe(CheckinResult result)
    {
        try
        {
            var cr = Http.Request("POST", $"{Base}/trae/api/v2/pay/ide_user_ent_usage", AuthHeaders(), new JsonObject());
            Log.Dbg($"usage: {cr.Dump()}");
            result.RawCredits = cr;
            var parsed = ParseCredits(cr);
            // 认证失败/错误响应时不把错误 JSON 当积分展示
            if (parsed.StartsWith("当前积分", StringComparison.Ordinal)) result.Credits = parsed;
        }
        catch (CheckinRequestException ex)
        {
            result.Credits = $"查询积分失败: {ex.Message}";
        }
    }

    /// <summary>1001 时刷新一次。返回 null 表示刷新成功可重试；返回字符串表示应结束。</summary>
    private string? HandleAuthFailure(CheckinResult result, string where)
    {
        if (RefreshRejected) return "token 失效，请重新登录 (login.cs trae)";

        Log.WriteLine($"  [Trae] {where}认证失败(1001)，尝试刷新 token 后重试...");
        if (Refresh())
        {
            if (string.IsNullOrEmpty(_userId)) _userId = UserIdFromJwt(_accessToken) ?? "";
            Persist();
            return null;
        }
        if (RefreshRejected) return "token 失效，请重新登录 (login.cs trae)";
        return $"认证失败，无法确认领取状态（{where}刷新未成功）";
    }

    public CheckinResult Run()
    {
        var result = new CheckinResult { Product = "Trae" };

        if (NeedsRefresh())
        {
            Log.WriteLine("  [Trae] token 即将过期，尝试刷新...");
            if (Refresh())
            {
                if (string.IsNullOrEmpty(_userId)) _userId = UserIdFromJwt(_accessToken) ?? "";
                Persist();
            }
            else if (RefreshRejected && _expiresAt <= Now)
            {
                result.Status = "token 失效，请重新登录 (login.cs trae)";
                return result;
            }
            else if (RefreshRejected)
            {
                // refresh 被拒，但 access token 尚未过期：继续用旧 token 签到
                Log.WriteLine("  [Trae] refreshToken 已失效，改用尚未过期的 accessToken 继续");
                Persist();
            }
            else
            {
                Log.WriteLine("  [Trae] token 刷新失败，改用现有 token 继续");
                Persist();
            }
        }
        else if (!string.IsNullOrEmpty(_deviceId))
        {
            Persist();
        }

        // 先查状态（免费请求）。已签到立即收手，不消耗 claim。
        JsonObject st;
        try
        {
            st = Http.Request("POST", $"{Base}/trae/api/v2/ug/checkin_credits/status", AuthHeaders(), new JsonObject());
            Log.Dbg($"checkin status: {st.Dump()}");
            result.RawStatus = st;
        }
        catch (CheckinRequestException ex)
        {
            result.Status = $"查询状态失败: {ex.Message}";
            return result;
        }

        if (st.D("code") == 1001)
        {
            var fail = HandleAuthFailure(result, "查询状态");
            if (fail is not null)
            {
                result.Status = fail;
                return result;
            }
            try
            {
                st = Http.Request("POST", $"{Base}/trae/api/v2/ug/checkin_credits/status", AuthHeaders(), new JsonObject());
                Log.Dbg($"checkin status after refresh: {st.Dump()}");
                result.RawStatus = st;
            }
            catch (CheckinRequestException ex)
            {
                result.Status = $"查询状态失败: {ex.Message}";
                return result;
            }
            if (st.D("code") == 1001)
            {
                result.Status = "认证失败，无法确认领取状态";
                return result;
            }
        }

        if (st.N("checked_in") is not null)
        {
            var cr = st.D("credits") ?? 0;
            result.Status = "今日已领取（status 确认）";
            if (cr > 0) result.Status += $" +{cr:0}";
            QueryCreditsSafe(result);
            return result;
        }

        // 领取积分（服务端可能限流返回 9074，重试几次）
        const int claimRetries = 3;
        JsonObject? claim = null;
        var authRetried = false;
        for (var attempt = 1; attempt <= claimRetries; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    // 重试前再查一次：已签到（手动或其他进程）立即收手
                    var st2 = Http.Request("POST", $"{Base}/trae/api/v2/ug/checkin_credits/status", AuthHeaders(), new JsonObject());
                    if (st2.N("checked_in") is not null)
                    {
                        result.RawStatus = st2;
                        result.Status = "今日已领取（重试前确认）";
                        QueryCreditsSafe(result);
                        return result;
                    }
                }

                claim = Http.Request("POST", $"{Base}/trae/api/v2/ug/checkin_credits/claim", AuthHeaders(), new JsonObject());
                Log.Dbg($"claim attempt {attempt}: {claim.Dump()}");
                var code = claim.D("code");

                if (code == 9074 && attempt < claimRetries)
                {
                    var wait = 20 * attempt;
                    Log.WriteLine($"  [Trae] 服务端限流(9074)，{wait}秒后重试({attempt}/{claimRetries})...");
                    Thread.Sleep(wait * 1000);
                    continue;
                }
                if (code == 1001 && !authRetried)
                {
                    var fail = HandleAuthFailure(result, "领取");
                    if (fail is not null)
                    {
                        result.Status = fail;
                        return result;
                    }
                    authRetried = true;
                    continue;
                }
                break;
            }
            catch (CheckinRequestException ex)
            {
                result.Status = $"领取请求失败: {ex.Message}";
                return result;
            }
        }

        result.RawCheckin = claim;
        result.Status = ParseClaimResult(claim);
        QueryCreditsSafe(result);
        return result;
    }

    private static string ParseClaimResult(JsonObject? r)
    {
        if (r is null) return "领取请求无响应";
        var code = r.D("code");
        var msg = r.S("message", "Message") ?? "";
        var data = r.O("data") ?? r.O("Result");
        var blob = r.Dump();

        if (code is 0 or 200 || r.N("success") is not null || data?.N("success") is not null)
            return "领取成功";
        if (code == 9074) return $"服务端限流，领取失败 ({msg})";

        foreach (var kw in new[] { "已签到", "已领取", "明日再来", "already", "checked", "claimed" })
        {
            if (msg.Contains(kw, StringComparison.OrdinalIgnoreCase) || blob.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return $"今日已领取 ({msg})";
        }
        // 1001 是服务端认证失败，不能当作「今日已领取」，否则会掩盖真实失败
        if (code == 1001) return $"认证失败，无法确认领取状态 ({msg})";
        return $"领取结果: code={code} msg={msg}";
    }

    private static string ParseCredits(JsonObject r)
    {
        // ide_user_ent_usage 返回权益包列表，从中汇总 credits_limit
        var packs = r.A("user_entitlement_pack_list");
        if (packs is { Count: > 0 })
        {
            double total = 0;
            var parts = new List<string>();
            foreach (var pk in packs)
            {
                var quota = pk.O("entitlement_base_info")?.O("quota") ?? pk.O("quota");
                var limit = quota?.D("credits_limit") ?? 0;
                if (limit <= 0) continue;
                var name = pk.S("group_name", "display_desc") ?? "";
                parts.Add($"{name}={limit:0}");
                total += limit;
            }
            if (parts.Count > 0)
                return $"当前积分: {total:0} ({string.Join(", ", parts.Take(3))})";
            return packs[0]!.Dump(120);
        }

        var data = r.O("data") ?? r.O("Result");
        var credits = data.D("credits") ?? data.D("balance") ?? data.D("totalCredits") ?? data.D("workCredits");
        if (credits is null) return r.Dump(120);
        return $"当前积分: {credits:0}";
    }

    private JsonObject ToCred() => new()
    {
        ["userId"] = _userId,
        ["accessToken"] = _accessToken,
        ["refreshToken"] = _refreshToken,
        ["expiresAt"] = _expiresAt,
        ["deviceId"] = _deviceId,
        ["screenName"] = _screenName,
    };
}

/// <summary>Trae 设备号解析：优先取桌面客户端里的真实 Aha 数字号。</summary>
public static class DeviceId
{
    // iCubeAuthInfo://icube-dc: 后跟 16 位十进制数字
    private static readonly Regex AhaKeyRe =
        new(@"iCubeAuthInfo://icube-dc:(\d{16})(?!\d)", RegexOptions.Compiled);

    private static readonly string[] ClientDirs =
    [
        "TRAE SOLO CN",
        "Trae CN",
        "Trae",
    ];

    /// <summary>
    /// 优先用 Trae 桌面客户端本地真实设备 ID（16 位 Aha 数字号）。
    /// claim 会校验设备标识，用 UUID / GUID / hex 设备号会被风控判定为非客户端设备，
    /// 返回误导性 9074「当前参与用户太多」。
    /// </summary>
    public static string Resolve(string fallback)
    {
        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        // 优先从 storage.json 提取 Aha 数字号
        foreach (var dir in ClientDirs)
        {
            var storage = Path.Combine(roaming, dir, "User", "globalStorage", "storage.json");
            try
            {
                if (!File.Exists(storage)) continue;
                var text = File.ReadAllText(storage);
                var m = AhaKeyRe.Match(text);
                if (m.Success)
                {
                    Log.Dbg($"Trae Aha 设备号来自 {storage}");
                    return m.Groups[1].Value;
                }
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
        }

        // 兜底：旧版 machineid（UUID，可能触发 9074）
        foreach (var dir in ClientDirs)
        {
            var machineId = Path.Combine(roaming, dir, "machineid");
            try
            {
                if (!File.Exists(machineId)) continue;
                var v = File.ReadAllText(machineId).Trim();
                if (v.Length > 0)
                {
                    Log.Dbg($"Trae 设备号回退 machineid（UUID，可能触发 9074）: {machineId}");
                    return v;
                }
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
        }

        return fallback;
    }
}