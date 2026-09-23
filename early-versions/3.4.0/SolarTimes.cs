using System.Globalization;
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

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

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
    /// 通过 IP 定位获取当前电脑的大致坐标（ip-api.com，HTTP）。
    /// 失败时抛异常，由调用方提示用户。
    /// </summary>
    public static async Task<GeoLocation> GetCurrentAsync()
    {
        const string url = "http://ip-api.com/json";
        var json = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var lat = root.GetProperty("lat").GetDouble();
        var lon = root.GetProperty("lon").GetDouble();
        return new GeoLocation(lat, lon);
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

    /// <summary>
    /// 解析 "HH:mm" 字符串为 TimeOnly；格式非法时返回 null。
    /// </summary>
    public static TimeOnly? TryParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (TimeOnly.TryParseExact(value.Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t1))
            return t1;
        if (TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t2))
            return t2;
        return null;
    }
}
