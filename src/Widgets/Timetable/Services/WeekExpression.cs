using System.Globalization;

namespace Timetable.Services;

/// <summary>
/// A parsed week rule: either "every week" or a bit mask of active weeks
/// (week N uses bit N-1, so the 64-bit mask covers every supported term length).
/// </summary>
public readonly record struct WeekSet(ulong Mask, bool All)
{
    public static readonly WeekSet AllWeeks = new(0, true);

    /// <summary>True when this rule is active in the given 1-based week.</summary>
    public bool IsActive(int week)
    {
        if (All) return true;
        if (week is < 1 or > 64) return false;
        return (Mask & (1UL << (week - 1))) != 0;
    }

    /// <summary>True when the two rules share at least one week.</summary>
    public bool Intersects(WeekSet other)
    {
        if (All || other.All) return true;
        return (Mask & other.Mask) != 0;
    }

    public bool IsEmpty => !All && Mask == 0;
}

/// <summary>
/// Parses the human-friendly "weeks" expression used by the JSON format:
/// omitted / "all" (every week), "1-16", "1-16单" (odd weeks), "2-16双" (even weeks)
/// and explicit lists like "1,3,5,7"; tokens combine with commas.
/// </summary>
public static class WeekExpression
{
    public static bool TryParse(string? expression, out WeekSet set, out string? error)
    {
        set = WeekSet.AllWeeks;
        error = null;

        if (string.IsNullOrWhiteSpace(expression)) return true;

        var text = expression.Trim();
        if (text is "all" or "*" or "ALL" or "全" or "全部" or "每周") return true;

        // Full-width punctuation and range dashes appear in AI output; normalise them first.
        text = text.Replace('－', '-').Replace('～', '-').Replace('~', '-');

        ulong mask = 0;

        foreach (var rawToken in text.Split([',', '，', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = rawToken.TrimStart('第');
            var odd = false;
            var even = false;

            if (token.EndsWith("单周", StringComparison.Ordinal))
            {
                odd = true;
                token = token[..^2];
            }
            else if (token.EndsWith("双周", StringComparison.Ordinal))
            {
                even = true;
                token = token[..^2];
            }
            else if (token.EndsWith('单'))
            {
                odd = true;
                token = token[..^1];
            }
            else if (token.EndsWith('双'))
            {
                even = true;
                token = token[..^1];
            }

            token = token.TrimEnd('周').Trim();

            int from, to;
            var dashIndex = token.IndexOf('-', StringComparison.Ordinal);
            if (dashIndex >= 0)
            {
                var left = token[..dashIndex].Trim();
                var right = token[(dashIndex + 1)..].Trim();
                if (!TryParseInt(left, out from) || !TryParseInt(right, out to))
                {
                    error = $"无法识别的周次片段“{rawToken}”";
                    return false;
                }
            }
            else if (TryParseInt(token, out from))
            {
                to = from;
            }
            else
            {
                error = $"无法识别的周次片段“{rawToken}”";
                return false;
            }

            if (from < 1 || to < 1 || from > 64 || to > 64 || to < from)
            {
                error = $"周次超出范围（应为 1–64）：{rawToken}";
                return false;
            }

            for (var week = from; week <= to; week++)
            {
                if (odd && week % 2 == 0) continue;
                if (even && week % 2 == 1) continue;
                mask |= 1UL << (week - 1);
            }
        }

        set = new WeekSet(mask, false);
        return true;
    }

    private static bool TryParseInt(string text, out int value)
    {
        // Accept both ASCII digits and full-width digits ("１－１６" is a common AI output).
        text = text.Trim()
            .Replace('０', '0').Replace('１', '1').Replace('２', '2').Replace('３', '3').Replace('４', '4')
            .Replace('５', '5').Replace('６', '6').Replace('７', '7').Replace('８', '8').Replace('９', '9');
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
