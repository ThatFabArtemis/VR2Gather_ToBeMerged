using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Minimal reader for the JSON records VRTsensors emits.
///
/// Deliberately not JsonUtility: the records are free-form dicts whose fields depend on
/// which channels were named on the sensor's command line, so there is no fixed class to
/// bind to, and declaring one per sensor would need editing every time a channel is renamed.
///
/// Kept out of any MonoBehaviour so it can be compiled and tested without Unity.
/// The records are flat and machine-generated, so this handles that shape and no more.
/// </summary>
public static class SensorJson
{
    /// <summary>
    /// Read one numeric field. Returns false if the field is absent or is not a number.
    /// </summary>
    public static bool TryGetNumber(string json, string field, out double value)
    {
        value = 0;
        if (!TryFindValue(json, field, out int start)) return false;
        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-' || json[end] == '+'
                                     || json[end] == '.' || json[end] == 'e' || json[end] == 'E')) end++;
        if (end == start) return false;
        return double.TryParse(json.Substring(start, end - start), NumberStyles.Float,
                               CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>
    /// Read one string field. Returns null if the field is absent or is not a string.
    /// </summary>
    public static string GetString(string json, string field)
    {
        if (!TryFindValue(json, field, out int start)) return null;
        if (start >= json.Length || json[start] != '"') return null;
        int end = json.IndexOf('"', start + 1);
        return end < 0 ? null : json.Substring(start + 1, end - start - 1);
    }

    /// <summary>
    /// Whether a field is present with the JSON value true.
    /// </summary>
    public static bool IsTrue(string json, string field)
    {
        return TryFindValue(json, field, out int start) && string.CompareOrdinal(json, start, "true", 0, 4) == 0;
    }

    /// <summary>
    /// Names of the top-level fields whose value is a number, in the order they appear.
    /// Lets a record be consumed without knowing which channels were named on the command line.
    /// </summary>
    public static IEnumerable<string> NumericFields(string json)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(json)) return found;
        int i = 0;
        int depth = 0;
        while (i < json.Length)
        {
            char c = json[i];
            if (c == '{' || c == '[') { depth++; i++; continue; }
            if (c == '}' || c == ']') { depth--; i++; continue; }
            if (c != '"') { i++; continue; }

            int keyEnd = json.IndexOf('"', i + 1);
            if (keyEnd < 0) break;
            string key = json.Substring(i + 1, keyEnd - i - 1);
            int at = keyEnd + 1;
            while (at < json.Length && json[at] == ' ') at++;
            if (at >= json.Length || json[at] != ':')
            {
                // A string that is a value, not a key. Skip past it.
                i = keyEnd + 1;
                continue;
            }
            at++;
            while (at < json.Length && json[at] == ' ') at++;
            if (at >= json.Length) break;

            char v = json[at];
            if (v == '"')
            {
                int close = json.IndexOf('"', at + 1);
                i = close < 0 ? json.Length : close + 1;
            }
            else if (v == '{' || v == '[')
            {
                // Nested value: only the bitalino "connected" event has one, and nothing
                // inside it is a channel reading.
                i = SkipNested(json, at);
            }
            else
            {
                int end = at;
                while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']') end++;
                string raw = json.Substring(at, end - at).Trim();
                if (depth == 1 && raw.Length > 0 && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    found.Add(key);
                i = end;
            }
        }
        return found;
    }

    static int SkipNested(string json, int at)
    {
        int depth = 0;
        for (int i = at; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '"') { int close = json.IndexOf('"', i + 1); i = close < 0 ? json.Length : close; continue; }
            if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') { depth--; if (depth == 0) return i + 1; }
        }
        return json.Length;
    }

    /// <summary>
    /// Position of the value of `field`, or false if the field is not present as a key.
    /// </summary>
    static bool TryFindValue(string json, string field, out int start)
    {
        start = 0;
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field)) return false;
        string key = "\"" + field + "\"";
        for (int from = 0; ; )
        {
            int found = json.IndexOf(key, from, StringComparison.Ordinal);
            if (found < 0) return false;
            int after = found + key.Length;
            while (after < json.Length && json[after] == ' ') after++;
            // Only where it is a key: without the colon test, a field named "value" would
            // also match the string "value" appearing as somebody else's value.
            if (after < json.Length && json[after] == ':')
            {
                after++;
                while (after < json.Length && json[after] == ' ') after++;
                start = after;
                return true;
            }
            from = found + key.Length;
        }
    }
}
