using Microsoft.Extensions.Options;

namespace Folio.Services;

/// <summary>US stock market state at a moment. Times are UTC; the page formats them in ET.</summary>
/// <param name="ClosedReason">"Weekend", "Overnight", the holiday's name, or null when open.</param>
/// <param name="EarlyClose">True on 1:00 PM ET early-close days.</param>
/// <param name="Enforced">False when <c>Folio:EnforceMarketHours</c> is off and trading is allowed anytime.</param>
public record MarketStatus(
    bool IsOpen,
    string? ClosedReason,
    DateTimeOffset? ClosesAt,
    DateTimeOffset NextOpen,
    bool EarlyClose,
    bool Enforced);

/// <summary>
/// NYSE/Nasdaq regular session: 9:30 AM to 4:00 PM ET on weekdays, minus exchange holidays, with
/// 1:00 PM early closes. The calendar is computed from the exchange's published rules, so it needs no
/// API calls. One-off closures (e.g. a national day of mourning) aren't known in advance.
/// </summary>
public static class MarketHours
{
    public static readonly TimeOnly Open = new(9, 30);
    public static readonly TimeOnly Close = new(16, 0);
    public static readonly TimeOnly EarlyCloseTime = new(13, 0);

    public static readonly TimeZoneInfo Eastern = FindEastern();

    private static TimeZoneInfo FindEastern()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    }

    public static MarketStatus GetStatus(DateTimeOffset now, bool enforced = true)
    {
        var et = TimeZoneInfo.ConvertTime(now, Eastern);
        var day = DateOnly.FromDateTime(et.DateTime);
        var time = TimeOnly.FromDateTime(et.DateTime);

        var holiday = HolidayName(day);
        var close = CloseTime(day);
        if (holiday is null && close is { } c && time >= Open && time < c)
            return new(true, null, ToUtc(day, c), NextOpenAfter(day), c == EarlyCloseTime, enforced);

        string reason = holiday
            ?? (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "Weekend" : "Overnight");
        var next = holiday is null && close is not null && time < Open ? ToUtc(day, Open) : NextOpenAfter(day);
        return new(false, reason, null, next, false, enforced);
    }

    /// <summary>Session close for a trading day, or null if the market doesn't open that day.</summary>
    public static TimeOnly? CloseTime(DateOnly d)
    {
        if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || HolidayName(d) is not null) return null;
        return IsEarlyClose(d) ? EarlyCloseTime : Close;
    }

    public static bool IsTradingDay(DateOnly d) => CloseTime(d) is not null;

    /// <summary>
    /// 1:00 PM closes: the day after Thanksgiving, Christmas Eve, and July 3, when they fall Monday to
    /// Thursday (if they're Fridays the holiday itself is observed that day, or it's a full session).
    /// </summary>
    public static bool IsEarlyClose(DateOnly d)
    {
        if (d == NthWeekday(d.Year, 11, DayOfWeek.Thursday, 4).AddDays(1)) return true;
        var weekdayBeforeHoliday = d.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Thursday;
        return weekdayBeforeHoliday && ((d.Month == 12 && d.Day == 24) || (d.Month == 7 && d.Day == 3));
    }

    /// <summary>The exchange holiday observed on this date, if any.</summary>
    public static string? HolidayName(DateOnly d)
    {
        int y = d.Year;
        // New Year's Day on a Saturday isn't observed the Friday before (that would close the year's last session).
        if (d == ObservedSunday(new DateOnly(y, 1, 1))) return "New Year's Day";
        if (d == NthWeekday(y, 1, DayOfWeek.Monday, 3)) return "Martin Luther King Jr. Day";
        if (d == NthWeekday(y, 2, DayOfWeek.Monday, 3)) return "Washington's Birthday";
        if (d == EasterSunday(y).AddDays(-2)) return "Good Friday";
        if (d == LastWeekday(y, 5, DayOfWeek.Monday)) return "Memorial Day";
        if (y >= 2022 && d == Observed(new DateOnly(y, 6, 19))) return "Juneteenth";
        if (d == Observed(new DateOnly(y, 7, 4))) return "Independence Day";
        if (d == NthWeekday(y, 9, DayOfWeek.Monday, 1)) return "Labor Day";
        if (d == NthWeekday(y, 11, DayOfWeek.Thursday, 4)) return "Thanksgiving Day";
        if (d == Observed(new DateOnly(y, 12, 25))) return "Christmas Day";
        return null;
    }

    private static DateTimeOffset NextOpenAfter(DateOnly day)
    {
        var d = day.AddDays(1);
        while (!IsTradingDay(d)) d = d.AddDays(1);
        return ToUtc(d, Open);
    }

    private static DateTimeOffset ToUtc(DateOnly d, TimeOnly t)
    {
        var local = d.ToDateTime(t, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Eastern.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Saturday holidays move to Friday, Sunday holidays to Monday.</summary>
    private static DateOnly Observed(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Saturday => d.AddDays(-1),
        DayOfWeek.Sunday => d.AddDays(1),
        _ => d,
    };

    private static DateOnly ObservedSunday(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? d.AddDays(1) : d;

    private static DateOnly NthWeekday(int year, int month, DayOfWeek dow, int n)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)dow - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7 * (n - 1));
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek dow)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return last.AddDays(-(((int)last.DayOfWeek - (int)dow + 7) % 7));
    }

    /// <summary>Gregorian Easter (anonymous algorithm).</summary>
    private static DateOnly EasterSunday(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(y, month, day);
    }
}

/// <summary>Market status for "now", honouring <see cref="FolioOptions.EnforceMarketHours"/>.</summary>
public class MarketClock(TimeProvider time, IOptions<FolioOptions> options)
{
    public bool Enforced => options.Value.EnforceMarketHours;

    public MarketStatus Status() => MarketHours.GetStatus(time.GetUtcNow(), Enforced);

    /// <summary>True when trades may execute now: the market is open, or hours aren't enforced.</summary>
    public bool CanTrade() => !Enforced || Status().IsOpen;

    /// <summary>"Opens Monday, Nov 16 at 9:30 AM ET" (or "today at …").</summary>
    public string DescribeNextOpen()
    {
        var s = Status();
        var et = TimeZoneInfo.ConvertTime(s.NextOpen, MarketHours.Eastern);
        var today = TimeZoneInfo.ConvertTime(time.GetUtcNow(), MarketHours.Eastern).Date;
        var en = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var when = et.Date == today ? "today" : et.Date == today.AddDays(1) ? "tomorrow" : et.ToString("dddd, MMM d", en);
        return $"{when} at {et.ToString("h:mm tt", en)} ET";
    }
}
