using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SlmapsServerPlugin
{
    // Small JSON writer and reader so the plugin carries no extra dependency.
    internal sealed class JsonObjectBuilder
    {
        private readonly StringBuilder _sb = new StringBuilder("{");
        private bool _first = true;

        public JsonObjectBuilder Add(string name, string value)
        {
            AppendName(name);
            if (value == null)
            {
                _sb.Append("null");
            }
            else
            {
                Json.AppendQuoted(_sb, value);
            }
            return this;
        }

        public JsonObjectBuilder Add(string name, int value)
        {
            AppendName(name);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonObjectBuilder Add(string name, double value)
        {
            AppendName(name);
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                _sb.Append("null");
            }
            else
            {
                _sb.Append(Math.Round(value, 1).ToString("0.0", CultureInfo.InvariantCulture));
            }
            return this;
        }

        public JsonObjectBuilder AddNull(string name)
        {
            AppendName(name);
            _sb.Append("null");
            return this;
        }

        public override string ToString()
        {
            return _sb.ToString() + "}";
        }

        private void AppendName(string name)
        {
            if (!_first)
            {
                _sb.Append(',');
            }
            _first = false;
            Json.AppendQuoted(_sb, name);
            _sb.Append(':');
        }
    }

    internal static class Json
    {
        private const int MaxDepth = 32;

        public static void AppendQuoted(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        public static Dictionary<string, object> TryParseObject(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            try
            {
                return Parse(text) as Dictionary<string, object>;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public static object Parse(string text)
        {
            if (text == null)
            {
                throw new FormatException("null input");
            }
            Parser p = new Parser(text);
            object value = p.ParseValue(0);
            p.SkipWhitespace();
            if (!p.AtEnd)
            {
                throw new FormatException("trailing characters at " + p.Position);
            }
            return value;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s)
            {
                _s = s;
            }

            public bool AtEnd
            {
                get { return _i >= _s.Length; }
            }

            public int Position
            {
                get { return _i; }
            }

            public void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == (char)0xFEFF)
                    {
                        _i++;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            public object ParseValue(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw new FormatException("nesting too deep");
                }
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new FormatException("unexpected end");
                }
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ParseObject(depth);
                    case '[': return ParseArray(depth);
                    case '"': return ParseString();
                    case 't': ExpectLiteral("true"); return true;
                    case 'f': ExpectLiteral("false"); return false;
                    case 'n': ExpectLiteral("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ParseNumber();
                        }
                        throw new FormatException("unexpected character at " + _i);
                }
            }

            private Dictionary<string, object> ParseObject(int depth)
            {
                Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
                _i++;
                SkipWhitespace();
                if (!AtEnd && _s[_i] == '}')
                {
                    _i++;
                    return result;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != '"')
                    {
                        throw new FormatException("expected property name at " + _i);
                    }
                    string key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    result[key] = ParseValue(depth + 1);
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw new FormatException("unterminated object");
                    }
                    char c = _s[_i++];
                    if (c == ',')
                    {
                        continue;
                    }
                    if (c == '}')
                    {
                        return result;
                    }
                    throw new FormatException("expected ',' or '}' at " + (_i - 1));
                }
            }

            private List<object> ParseArray(int depth)
            {
                List<object> result = new List<object>();
                _i++;
                SkipWhitespace();
                if (!AtEnd && _s[_i] == ']')
                {
                    _i++;
                    return result;
                }
                while (true)
                {
                    result.Add(ParseValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw new FormatException("unterminated array");
                    }
                    char c = _s[_i++];
                    if (c == ',')
                    {
                        continue;
                    }
                    if (c == ']')
                    {
                        return result;
                    }
                    throw new FormatException("expected ',' or ']' at " + (_i - 1));
                }
            }

            private string ParseString()
            {
                _i++;
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                    {
                        throw new FormatException("unterminated string");
                    }
                    char c = _s[_i++];
                    if (c == '"')
                    {
                        return sb.ToString();
                    }
                    if (c == '\\')
                    {
                        if (AtEnd)
                        {
                            throw new FormatException("unterminated escape");
                        }
                        char e = _s[_i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (_i + 4 > _s.Length)
                                {
                                    throw new FormatException("bad unicode escape");
                                }
                                int code;
                                if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                {
                                    throw new FormatException("bad unicode escape");
                                }
                                sb.Append((char)code);
                                _i += 4;
                                break;
                            default:
                                throw new FormatException("bad escape at " + (_i - 1));
                        }
                    }
                    else if (c < 0x20)
                    {
                        throw new FormatException("control character in string");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            private double ParseNumber()
            {
                int start = _i;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                    {
                        _i++;
                    }
                    else
                    {
                        break;
                    }
                }
                double value;
                if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    throw new FormatException("bad number at " + start);
                }
                return value;
            }

            private void Expect(char c)
            {
                if (AtEnd || _s[_i] != c)
                {
                    throw new FormatException("expected '" + c + "' at " + _i);
                }
                _i++;
            }

            private void ExpectLiteral(string literal)
            {
                if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                {
                    throw new FormatException("expected " + literal + " at " + _i);
                }
                _i += literal.Length;
            }
        }
    }
}
