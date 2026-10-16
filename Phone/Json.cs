using System.Globalization;
using System.Text;

namespace Yesla.Phone
{
    /// <summary>A minimal JSON writer for the page's state. The page only ever reads; nothing is parsed on this side.</summary>
    internal sealed class Json
    {
        private readonly StringBuilder _sb = new StringBuilder(1024);
        private bool _first = true;

        internal Json Open() { Sep(); _sb.Append('{'); _first = true; return this; }
        internal Json Close() { _sb.Append('}'); _first = false; return this; }
        internal Json OpenArray() { Sep(); _sb.Append('['); _first = true; return this; }
        internal Json CloseArray() { _sb.Append(']'); _first = false; return this; }

        internal Json Key(string key) { Sep(); Str(key); _sb.Append(':'); _first = true; return this; }

        internal Json Prop(string key, string value) { Key(key); _first = false; if (value == null) _sb.Append("null"); else Str(value); return this; }
        internal Json Prop(string key, double value) { Key(key); _first = false; _sb.Append(Num(value)); return this; }
        internal Json Prop(string key, bool value) { Key(key); _first = false; _sb.Append(value ? "true" : "false"); return this; }
        internal Json Null(string key) { Key(key); _first = false; _sb.Append("null"); return this; }

        /// <summary>Bare numbers, for the inside of an array.</summary>
        internal Json Numbers(System.Collections.Generic.List<double> values)
        {
            foreach (double v in values) { Sep(); _sb.Append(Num(v)); }
            return this;
        }

        /// <summary>Strings, for the inside of an array.</summary>
        internal Json Strings(string[] values)
        {
            foreach (string v in values) { Sep(); if (v == null) _sb.Append("null"); else Str(v); }
            return this;
        }

        public override string ToString() => _sb.ToString();

        private void Sep()
        {
            if (!_first) _sb.Append(',');
            _first = false;
        }

        private static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return v.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        private void Str(string s)
        {
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }
    }
}
