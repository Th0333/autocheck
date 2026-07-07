using System;
using System.Collections.Generic;
using System.Globalization;

namespace NotebookCheck.Infrastructure.Wmi;

/// <summary>
/// Helpers para extrair valores tipados de uma linha WMI sem propagar exceções.
/// </summary>
internal static class WmiExtensions
{
    public static string? GetString(this IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        var s = v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public static int? GetInt(this IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    public static long? GetLong(this IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    public static ulong? GetULong(this IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        try { return Convert.ToUInt64(v, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    public static bool? GetBool(this IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    public static ushort? GetUShort(this IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        try { return Convert.ToUInt16(v, CultureInfo.InvariantCulture); }
        catch { return null; }
    }
}
