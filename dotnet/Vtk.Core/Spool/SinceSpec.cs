// `--since` window specs for the read-only reporters (`vtk gaps`, `vtk
// discover` — #140). Opt-in: with no window both commands read all history
// exactly as before. Pure parsing, shared so both commands accept the same
// forms and print the same header.
using System.Globalization;

namespace Vtk.Core.Spool;

public static class SinceSpec
{
    private static readonly string[] IsoFormats =
    {
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-ddTHH:mmK",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
    };

    /// <summary>
    /// Parses a window spec into a UTC lower bound: <c>&lt;N&gt;d</c> /
    /// <c>&lt;N&gt;h</c> durations (N ≥ 1) back from <paramref name="nowUtc"/>,
    /// or an ISO-8601 date / datetime (no zone → UTC; an offset or
    /// <c>Z</c> is honored). Anything else → false.
    /// </summary>
    public static bool TryParse(string? spec, DateTime nowUtc, out DateTime sinceUtc)
    {
        sinceUtc = default;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        spec = spec.Trim();

        var unit = spec[^1];
        if (unit is 'd' or 'h')
        {
            if (!int.TryParse(spec[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
                return false;
            // Bound-check before any arithmetic: a duration reaching back past
            // DateTime.MinValue (or overflowing TimeSpan) is a usage error,
            // never a crash — reporters must exit 2, not throw.
            var unitTicks = unit == 'd' ? TimeSpan.TicksPerDay : TimeSpan.TicksPerHour;
            if (n > nowUtc.Ticks / unitTicks) return false;
            sinceUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc) - new TimeSpan(n * unitTicks);
            return true;
        }

        if (DateTime.TryParseExact(spec, IsoFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
        {
            sinceUtc = t;
            return true;
        }
        return false;
    }

    /// <summary>Renders a UTC bound for the window header: <c>yyyy-MM-ddTHH:mm:ssZ</c>.</summary>
    public static string Format(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a logged time falls inside the window. Undated rows
    /// (<c>default</c>) are excluded whenever a window is set — a row that
    /// cannot be placed in time cannot be shown to be recent.
    /// </summary>
    public static bool InWindow(DateTime time, DateTime? sinceUtc) =>
        sinceUtc is not { } s || (time != default && time.ToUniversalTime() >= s);
}
