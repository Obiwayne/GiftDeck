using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace GiftDeckGTA
{
    // Tiny JSON helper: parsing through .NET Framework's own JavaScriptSerializer (no extra DLL to ship next
    // to the script), writing by hand so key order is kept and commands.json can be indented.
    public static class Json
    {
        public static Dictionary<string, object> ParseObject(string text)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return serializer.DeserializeObject(text) as Dictionary<string, object>;
        }

        public static object Parse(string text) =>
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text);

        public static string Write(object value, bool indented = false)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, indented, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object value, bool indented, int depth)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case int _:
                case long _:
                case short _:
                case byte _:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case float _:
                case double _:
                case decimal _:
                    sb.Append(Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object> dict:
                    WriteObject(sb, dict, indented, depth);
                    break;
                case IEnumerable list:
                    WriteArray(sb, list, indented, depth);
                    break;
                default:
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
        }

        static void WriteObject(StringBuilder sb, IDictionary<string, object> dict, bool indented, int depth)
        {
            if (dict.Count == 0)
            {
                sb.Append("{}");
                return;
            }
            sb.Append('{');
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                NewLine(sb, indented, depth + 1);
                WriteString(sb, kv.Key);
                sb.Append(indented ? ": " : ":");
                WriteValue(sb, kv.Value, indented, depth + 1);
            }
            NewLine(sb, indented, depth);
            sb.Append('}');
        }

        static void WriteArray(StringBuilder sb, IEnumerable list, bool indented, int depth)
        {
            var items = new List<object>();
            foreach (var item in list) items.Add(item);
            if (items.Count == 0)
            {
                sb.Append("[]");
                return;
            }
            // Short lists of plain values (e.g. choices) stay on one line so the file is readable
            bool inline = indented && items.TrueForAll(i => !(i is IDictionary<string, object>) && (i is string || !(i is IEnumerable)));
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(inline ? ", " : ",");
                if (!inline) NewLine(sb, indented, depth + 1);
                WriteValue(sb, items[i], indented, depth + 1);
            }
            if (!inline) NewLine(sb, indented, depth);
            sb.Append(']');
        }

        static void NewLine(StringBuilder sb, bool indented, int depth)
        {
            if (!indented) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- reading values out of parsed messages ----

        public static object Get(IDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) ? v : null;

        public static string Str(IDictionary<string, object> d, string key)
        {
            var v = Get(d, key);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int Int(IDictionary<string, object> d, string key, int fallback)
        {
            var v = Get(d, key);
            if (v == null) return fallback;
            if (v is string s)
                return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? (int)parsed : fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }
    }
}
