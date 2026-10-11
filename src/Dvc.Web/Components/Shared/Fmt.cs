using System.Globalization;

namespace Dvc.Web.Components.Shared;

/// <summary>看板数值格式化助手。</summary>
public static class Fmt
{
    public static string Money(decimal? v) => v?.ToString("N2", CultureInfo.InvariantCulture) ?? "--";

    public static string Eq(decimal? v) => v?.ToString("N2", CultureInfo.InvariantCulture) ?? "--";

    public static string Signed(decimal? v) => v is null ? "--" : (v >= 0 ? "+" : "") + v.Value.ToString("N2", CultureInfo.InvariantCulture);

    public static string Price(decimal? v) => v?.ToString("0.######", CultureInfo.InvariantCulture) ?? "--";

    public static string Bps(decimal? v) => v?.ToString("0.00", CultureInfo.InvariantCulture) ?? "--";

    public static string Time(DateTime? v) => v?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "--";

    public static string TrendClass(string trend) => trend switch
    {
        "BULLISH_STRONG" or "BULLISH" => "trend-bull",
        "BEARISH_STRONG" or "BEARISH" => "trend-bear",
        _ => "trend-flat",
    };
}
