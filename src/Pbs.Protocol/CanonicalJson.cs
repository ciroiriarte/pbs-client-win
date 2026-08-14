using System.Collections;
using System.Globalization;
using System.Text;

namespace Pbs.Protocol;

/// <summary>
/// Canonical JSON serialization matching upstream <c>proxmox_serde::json::to_canonical_json</c>:
/// object keys sorted (byte/ordinal order) recursively, fully compact (no insignificant whitespace),
/// integers as their decimal string, and serde-json string escaping (escape only <c>"</c>, <c>\</c>,
/// and control characters &lt; 0x20; other characters — including non-ASCII — emitted as raw UTF-8).
/// Used to build the exact byte stream that the backup manifest signature is computed over (#6).
/// </summary>
public static class CanonicalJson
{
    public static byte[] Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(value, sb);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Write(object? value, StringBuilder sb)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                WriteString(s, sb);
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case IDictionary<string, object?> map:
                WriteObject(map, sb);
                break;
            case IEnumerable list:   // arrays preserve order (checked after string/dictionary)
                WriteArray(list, sb);
                break;
            default:
                throw new NotSupportedException($"canonical json: unsupported value type {value.GetType()}");
        }
    }

    private static void WriteObject(IDictionary<string, object?> map, StringBuilder sb)
    {
        sb.Append('{');
        var first = true;
        foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            WriteString(key, sb);
            sb.Append(':');
            Write(map[key], sb);
        }
        sb.Append('}');
    }

    private static void WriteArray(IEnumerable list, StringBuilder sb)
    {
        sb.Append('[');
        var first = true;
        foreach (var item in list)
        {
            if (!first) sb.Append(',');
            first = false;
            Write(item, sb);
        }
        sb.Append(']');
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
