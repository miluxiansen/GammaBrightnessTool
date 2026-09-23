using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GammaBrightnessTool;

/// <summary>
/// 地理坐标（纬度/经度，十进制度）。用于"时间调整"功能按物理位置计算日出日落。
/// </summary>
public readonly record struct GeoLocation(double Latitude, double Longitude)
{
    public override string ToString() =>
        $"{Latitude.ToString(CultureInfo.InvariantCulture)}, {Longitude.ToString(CultureInfo.InvariantCulture)}";

    // ====================================================================
    // F27（2026-09-23，设计稿 §十七）：**出口协议族选择**。
    //
    // 用户决策：自动定位 **IPv6 优先**（无 IPv6 退 IPv4），两族都连 ipwho.is。
    // 隐私依据（本机实测，见设计稿 §17.4）：Windows 默认开启
    //   `RandomizeIdentifiers` + `UseTemporaryAddresses` ⇒ 对外连接用**随机临时
    //   地址**且约 24h 随前缀整批轮换；geo-IP 库对移动 IPv6 只到**市级**。
    //   ⇒ 走 IPv6 出口时，服务商只能拿到"某个每日轮换、定位到市的地址"，
    //     **隐私优于 IPv4**（IPv4 geo 可到区县且分配期内稳定）。
    //
    // ⛔ 纪律（见设计稿 §17.4）：
    //   1. **不 Bind 本地源地址** —— 绑定可能选中 `SuffixOrigin=Link` 的
    //      MAC 派生地址（= 设备级指纹）；不绑定则系统自动选随机临时地址。
    //   2. **不设置 User-Agent** —— HttpClient 默认不发 UA，零自报家门。
    //   3. **日志只记出口协议族，绝不记公网 IP**。
    //   4. 仅在用户点"获取位置"按钮时调用，**绝不轮询**（避免形成轨迹）。
    // ====================================================================

    private static HttpClient? _clientV6;
    private static HttpClient? _clientV4;

    /// <summary>探测本机是否具备**可出公网的 IPv6**：Up 状态接口上有
    /// 全局单播地址（GUA，2000::/3）**且**有 IPv6 默认网关。</summary>
    /// <remarks>
    /// ⚠️ IPv6 的默认网关**通常就是链路本地地址（fe80::/10）**——这是正常机制，
    /// 不能像 GUA 那样排除；要排除的是 ULA（fc00::/7）与链路本地的**地址**。
    /// </remarks>
    public static bool HasGlobalIPv6()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = ni.GetIPProperties();
                bool hasGua = props.UnicastAddresses.Any(a => IsGlobalV6(a.Address));
                bool hasV6Gateway = props.GatewayAddresses.Any(
                    g => g.Address.AddressFamily == AddressFamily.InterNetworkV6);
                if (hasGua && hasV6Gateway) return true;
            }
        }
        catch
        {
            // 探测失败按"无 IPv6"处理 ⇒ 走 IPv4 分支（保守且功能不受损）。
        }
        return false;
    }

    /// <summary>是否为**全局单播** IPv6 地址（2000::/3；排除链路本地 fe80::/10 与 ULA fc00::/7）。</summary>
    private static bool IsGlobalV6(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetworkV6 || a.IsIPv6LinkLocal) return false;
        var b = a.GetAddressBytes();
        return b.Length == 16 && (b[0] & 0xE0) == 0x20 && !(b[0] == 0xFC || b[0] == 0xFD);
    }

    /// <summary>
    /// 取（并缓存）按指定协议族**强制出口**的 HttpClient。
    /// 用 <see cref="SocketsHttpHandler.ConnectCallback"/> 直接构造对应族的 Socket：
    /// 普通 HttpClient 会走 Happy Eyeballs 自行挑族（结果不确定），这里要的是**确定性**。
    /// ⛔ 故意**不 Bind 本地源地址**（见 F27 纪律 1）。
    /// </summary>
    private static HttpClient GetFamilyClient(AddressFamily family)
    {
        if (family == AddressFamily.InterNetworkV6)
            return _clientV6 ??= BuildFamilyClient(family);
        return _clientV4 ??= BuildFamilyClient(family);
    }

    private static HttpClient BuildFamilyClient(AddressFamily family)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, ct) =>
            {
                var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(ctx.DnsEndPoint, ct);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// 解析用户输入的坐标字符串。支持两种格式：
    ///   "41.25, -120.9"            （带符号十进制）
    ///   "41.25N, 120.97W"          （带方位后缀）
    /// 解析失败返回 null。
    /// </summary>
    public static GeoLocation? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TryParseSigned(value) ?? TryParseSuffixed(value);
    }

    private static GeoLocation? TryParseSigned(string value)
    {
        const NumberStyles styles = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
        var m = Regex.Match(value, @"^([\+\-]?\d+(?:\.\d+)?)\s*[,\s]\s*([\+\-]?\d+(?:\.\d+)?)$");
        if (m.Success
            && double.TryParse(m.Groups[1].Value, styles, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(m.Groups[2].Value, styles, CultureInfo.InvariantCulture, out var lng))
        {
            return new GeoLocation(lat, lng);
        }
        return null;
    }

    private static GeoLocation? TryParseSuffixed(string value)
    {
        const NumberStyles styles = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
        var m = Regex.Match(value, @"^(\d+(?:\.\d+)?)\s*°?\s*(\w)\s*[,\s]\s*(\d+(?:\.\d+)?)\s*°?\s*(\w)$");
        if (m.Success
            && double.TryParse(m.Groups[1].Value, styles, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(m.Groups[3].Value, styles, CultureInfo.InvariantCulture, out var lng))
        {
            var latSign = m.Groups[2].Value.Equals("N", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
            var lngSign = m.Groups[4].Value.Equals("E", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
            return new GeoLocation(lat * latSign, lng * lngSign);
        }
        return null;
    }

    /// <summary>
    /// 通过 IP 定位获取当前电脑的大致坐标。失败时抛异常，由调用方提示用户。
    ///
    /// P0-2 修复（2026-09-23 代码审查 + 实测）：原实现走**明文 HTTP**
    /// （<c>http://ip-api.com/json</c>），IP 与大致位置明文外发（隐私问题）。
    /// ⚠️ 不能简单换成 <c>https://ip-api.com</c> —— 实测（2026-09-23，广州移动网络）
    /// 该端点 TLS 下返回 <b>403 已禁止</b>（HTTPS 是 ip-api 的 Pro 付费特性），
    /// 直接换协议会把"获取位置"按钮整个弄坏。
    /// 改用支持 HTTPS 的免费端点 <c>https://ipwho.is/</c>：
    /// 实测同网络 681ms 可达，字段 <c>latitude</c>/<c>longitude</c> 为数值型 JSON、
    /// 另有 <c>success</c> 标志可用于失败判定（字段已用响应体原文核对）。
    /// </summary>
    public static async Task<GeoLocation> GetCurrentAsync()
    {
        const string url = "https://ipwho.is/";

        // F27：探测 IPv6 ⇒ 决定出口族尝试顺序。有 IPv6 先走 v6（隐私优，见 F27 注释）；
        // v6 失败再退 v4（同一家服务商，只换协议族 ⇒ 不增加新的第三方）。
        // ⛔ 无 IPv6 时**只**试 v4 —— 不会"顺便"走 v6。
        bool hasV6 = HasGlobalIPv6();
        var order = hasV6
            ? new[] { AddressFamily.InterNetworkV6, AddressFamily.InterNetwork }
            : new[] { AddressFamily.InterNetwork };

        OpLog.Log($"[solar] 定位出口探测：{(hasV6 ? "有 IPv6 ⇒ 先 IPv6 后 IPv4" : "无 IPv6 ⇒ 仅 IPv4")}（ipwho.is）");

        Exception? last = null;
        foreach (var family in order)
        {
            try
            {
                var json = await GetFamilyClient(family).GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                // ipwho.is 失败时返回 {"success": false, "message": ...} —— 显式判定，
                // 避免GetProperty("latitude") 抛 KeyNotFoundException 这种难排查的错误。
                if (root.ValueKind != JsonValueKind.Object ||
                    (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False))
                {
                    string detail = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                    throw new Exception("定位服务返回失败" + (detail.Length > 0 ? $"（{detail}）" : ""));
                }
                var lat = root.GetProperty("latitude").GetDouble();
                var lon = root.GetProperty("longitude").GetDouble();
                OpLog.Log($"[solar] 定位成功（出口={(family == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4")}）");
                return new GeoLocation(lat, lon);
            }
            catch (Exception ex)
            {
                last = ex;
                OpLog.Log($"[solar] 定位经 {(family == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4")} 出口失败：" +
                          $"{ex.GetType().Name}: {ex.Message}");
            }
        }
        throw last ?? new Exception("定位服务不可达");
    }
}

/// <summary>
/// 日出日落计算与插值。基于 edwilliams.org 的太阳时算法（与 LightBulb 同源），
/// 天顶角 90.83°（太阳上缘触地平线的标准定义）。
/// </summary>
public static class SolarTimes
{
    private const double Zenith = 90.83;

    private static double Deg2Rad(double d) => d * (Math.PI / 180.0);
    private static double Rad2Deg(double r) => r * 180.0 / Math.PI;

    /// <summary>
    /// 将角度归一化到 [min, max) 区间（替代 LightBulb 的 PowerKit.Wrap）。
    /// </summary>
    private static double Wrap(double value, double min, double max)
    {
        double range = max - min;
        double v = (value - min) % range;
        if (v < 0) v += range;
        return v + min;
    }

    /// <summary>
    /// 计算指定日期、指定坐标的本地日出/日落时刻（TimeOnly，本地时区）。
    /// </summary>
    public static (TimeOnly Sunrise, TimeOnly Sunset) Calculate(double latitude, double longitude, DateTime date)
    {
        return (
            CalculateEvent(latitude, longitude, date, true),
            CalculateEvent(latitude, longitude, date, false)
        );
    }

    private static TimeOnly CalculateEvent(double latitude, double longitude, DateTime date, bool isSunrise)
    {
        double offsetHours = TimeZoneInfo.Local.GetUtcOffset(date).TotalHours;
        double lngHours = longitude / 15.0;
        double approxHours = isSunrise ? 6 : 18;
        double approxDays = date.DayOfYear + (approxHours - lngHours) / 24.0;

        double meanAnomaly = 0.9856 * approxDays - 3.289;
        double sunLng = Wrap(
            meanAnomaly + 282.634
                + 1.916 * Math.Sin(Deg2Rad(meanAnomaly))
                + 0.020 * Math.Sin(2 * Deg2Rad(meanAnomaly)),
            0, 360);

        double sunRightAsc = Wrap(
            Rad2Deg(Math.Atan(0.91764 * Math.Tan(Deg2Rad(sunLng)))), 0, 360);

        double sunLngQuad = Math.Floor(sunLng / 90.0) * 90.0;
        double sunRightAscQuad = Math.Floor(sunRightAsc / 90.0) * 90.0;
        double sunRightAscHours = (sunRightAsc + (sunLngQuad - sunRightAscQuad)) / 15.0;

        double sinDec = 0.39782 * Math.Sin(Deg2Rad(sunLng));
        double cosDec = Math.Cos(Math.Asin(sinDec));

        // 极地防御：纬度 ±90° 或太阳直射极点时 cosDec*cos(lat) 为 0，直接
        // 相除产生 Infinity/NaN（Math.Clamp(NaN)=NaN → TimeSpan.FromHours(NaN)
        // 抛 ArgumentException，调度定时器未捕获会崩进程）。分母为 0 时无
        // 定义的日出/日落，取 cos=1 → sunrise=00:00 / sunset=00:00，白天区间
        // 为空 → 全天按夜晚值处理（保守且不崩溃）。
        double sinLat = Math.Sin(Deg2Rad(latitude));
        double cosLat = Math.Cos(Deg2Rad(latitude));
        double cosLocalHoursDenom = cosDec * cosLat;
        double cosLocalHours = cosLocalHoursDenom == 0.0
            ? 1.0
            : Math.Clamp(
                (Math.Cos(Deg2Rad(Zenith)) - sinDec * sinLat) / cosLocalHoursDenom,
                -1, 1);

        double sunLocalHours = (
            isSunrise
                ? 360 - Rad2Deg(Math.Acos(cosLocalHours))
                : Rad2Deg(Math.Acos(cosLocalHours))
        ) / 15.0;

        double meanHours = sunLocalHours + sunRightAscHours - 0.06571 * approxDays - 6.622;
        double utcHours = Wrap(meanHours - lngHours, 0, 24);
        double localHours = Wrap(utcHours + offsetHours, 0, 24);

        return TimeOnly.FromTimeSpan(TimeSpan.FromHours(localHours));
    }

    /// <summary>
    /// 根据当前时刻、日出日落与过渡时长，插值出目标值。
    /// 日出过渡用 cos（夜晚值 -> 白天值）、日落过渡用 sin（白天值 -> 夜晚值）。
    /// 过渡时长为 0 时退化为白天/夜晚的瞬时切换。
    /// </summary>
    public static double Interpolate(
        DateTime now,
        TimeOnly sunrise,
        TimeOnly sunset,
        double dayValue,
        double nightValue,
        TimeSpan transition)
    {
        var sunriseDt = now.Date + sunrise.ToTimeSpan();
        var sunsetDt = now.Date + sunset.ToTimeSpan();

        if (transition <= TimeSpan.Zero)
        {
            // 瞬时切换：日出到日落之间为白天，其余为夜晚。
            return (now >= sunriseDt && now < sunsetDt) ? dayValue : nightValue;
        }

        var sunriseStart = sunriseDt - transition; // 日出过渡起点（夜晚 -> 白天）
        var sunsetEnd = sunsetDt + transition;     // 日落过渡终点（白天 -> 夜晚）

        // 日出过渡 [sunriseStart, sunriseDt)
        if (now >= sunriseStart && now < sunriseDt)
        {
            double p = (now - sunriseStart).TotalMinutes / transition.TotalMinutes;
            p = Math.Clamp(p, 0, 1);
            return dayValue + (nightValue - dayValue) * Math.Cos(p * Math.PI / 2);
        }

        // 日落过渡 [sunsetDt, sunsetEnd)
        if (now >= sunsetDt && now < sunsetEnd)
        {
            double p = (now - sunsetDt).TotalMinutes / transition.TotalMinutes;
            p = Math.Clamp(p, 0, 1);
            return dayValue + (nightValue - dayValue) * Math.Sin(p * Math.PI / 2);
        }

        // 白天 [sunriseDt, sunsetDt)
        if (now >= sunriseDt && now < sunsetDt)
            return dayValue;

        // 夜晚（跨午夜，其余时段）
        return nightValue;
    }

}
