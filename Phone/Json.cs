using System.Globalization;
using System.Text;

namespace SideHustle.Phone
{
    /// <summary>
    /// The smallest JSON writer that covers what a page needs. Sideload's bridge carries strings, so the mod side
    /// only ever has to WRITE json - the page parses it with JSON.parse. No reader here on purpose; arguments come
    /// back as plain strings.
    ///
    /// Same shape as WhatsDab/Chat/Json.cs, kept as its own copy rather than shared: a fifty-line helper is not
    /// worth a dependency between two mods that otherwise share nothing.
    /// </summary>
    internal sealed class Json
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private readonly bool _array;
        private bool _first = true;

        private Json(bool array)
        {
            _array = array;
            _sb.Append(array ? '[' : '{');
        }

        internal static Json Object() => new Json(false);
        internal static Json Array() => new Json(true);

        internal Json Add(string key, string value)
        {
            Comma();
            Key(key);
            Escape(value);
            return this;
        }

        internal Json Add(string key, bool value)
        {
            Comma();
            Key(key);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        internal Json Add(string key, int value)
        {
            Comma();
            Key(key);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>Nest an object or array under a key. The nested builder must not be used afterwards.</summary>
        internal Json Add(string key, Json value)
        {
            Comma();
            Key(key);
            _sb.Append(value.ToString());
            return this;
        }

        /// <summary>Append to an array.</summary>
        internal Json Item(Json value)
        {
            Comma();
            _sb.Append(value.ToString());
            return this;
        }

        public override string ToString() => _sb.ToString() + (_array ? ']' : '}');

        private void Comma()
        {
            if (!_first) _sb.Append(',');
            _first = false;
        }

        private void Key(string key)
        {
            Escape(key);
            _sb.Append(':');
        }

        private void Escape(string value)
        {
            if (value == null) { _sb.Append("null"); return; }
            _sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        // Control characters have no literal form in JSON, and a lobby name is whatever a player
                        // typed - including, once, a pasted terminal escape.
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }
    }
}
