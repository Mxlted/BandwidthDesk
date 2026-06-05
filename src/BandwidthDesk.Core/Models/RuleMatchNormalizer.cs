using System;
using System.Globalization;

namespace BandwidthDesk.Core.Models;

public static class RuleMatchNormalizer
{
    public static string NormalizeForComparison(RuleMatchKind kind, string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return kind switch
        {
            RuleMatchKind.ExecutableName => StripExeSuffix(normalized),
            RuleMatchKind.ProcessId when int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) =>
                pid.ToString(CultureInfo.InvariantCulture),
            _ => normalized,
        };
    }

    public static bool MatchValuesEqual(RuleMatchKind kind, string? left, string? right)
    {
        var comparison = kind == RuleMatchKind.ProcessId
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        return string.Equals(
            NormalizeForComparison(kind, left),
            NormalizeForComparison(kind, right),
            comparison);
    }

    private static string StripExeSuffix(string value)
    {
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
    }
}
