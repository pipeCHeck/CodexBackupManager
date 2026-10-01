using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CodexBackupManager.Codex.Inspection;

/// <summary>
/// (Phase 9_5a-02) <c>.codex-global-state.json</c>용 JSON 모델 · 파서 · 직렬화기. Codex Desktop(Electron)이 쓰는
/// JS <c>JSON.stringify</c> 형식(한 줄 compact)을 바이트까지 같게 다시 만든다.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>파서는 원문 텍스트의 위치(<see cref="GlobalStateJsonNode.SourceStart"/>/<see cref="GlobalStateJsonNode.SourceEnd"/>)를
///     모든 값에 남긴다. 숫자는 원문 토큰(<see cref="GlobalStateJsonNumber.Raw"/>)을 그대로 보관한다(재포맷하지 않는다).</item>
///   <item>같은 객체 안의 중복 키는 거부한다(JS 객체는 중복 키를 만들 수 없다 — 의미가 모호한 파일에는 쓰지 않는다).</item>
///   <item><see cref="Serialize(GlobalStateJsonNode, string?)"/>는 원문이 주어지면 바꾸지 않은 값은 원문 조각을 그대로 복사하고,
///     바뀐 컨테이너(<see cref="GlobalStateJsonNode.IsModified"/>)만 다시 쓴다. 원문 없이 부르면 전부 JS 규칙으로 다시 쓴다
///     — 게이트의 왕복 검사는 이 전체 재직렬화 결과를 원본 바이트와 비교한다.</item>
///   <item>문자열은 <c>"</c>, <c>\</c>, 제어 문자만 이스케이프한다(<c>\b \f \n \r \t</c>, 그 밖은 <c>\u00xx</c> 소문자).
///     짝이 없는 서로게이트는 <c>\udxxx</c> 소문자다(ES2019 well-formed <c>JSON.stringify</c>). 그 외 문자는 그대로 쓴다.</item>
/// </list>
/// 이 클래스는 파일을 읽거나 쓰지 않는다.
/// </remarks>
public static class GlobalStateJson
{
    /// <summary>중첩 한도(이 파일 실측 깊이는 한 자리다). 넘으면 파싱 실패.</summary>
    public const int MaxDepth = 256;

    /// <summary>엄격한 UTF-8(BOM 없음, 잘못된 바이트는 예외).</summary>
    public static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>JSON 텍스트를 파싱한다. 문법이 틀리거나 중복 키가 있으면 <see cref="FormatException"/>.</summary>
    public static GlobalStateJsonNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text);
        parser.SkipWhitespace();
        GlobalStateJsonNode root = parser.ParseValue(0);
        parser.SkipWhitespace();
        if (parser.Position != text.Length)
        {
            throw new FormatException("JSON 값 뒤에 남은 문자가 있습니다.");
        }

        return root;
    }

    /// <summary>
    /// 직렬화한다. <paramref name="sourceText"/>가 있으면 바꾸지 않은 값은 그 원문 조각을 그대로 쓴다(원래 표현 유지).
    /// <c>null</c>이면 모든 값을 JS <c>JSON.stringify</c> 규칙으로 다시 쓴다.
    /// </summary>
    public static string Serialize(GlobalStateJsonNode node, string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        var builder = new StringBuilder(sourceText?.Length ?? 256);
        Write(builder, node, sourceText);
        return builder.ToString();
    }

    /// <summary>JS <c>JSON.stringify</c>와 같은 문자열 리터럴(따옴표 포함)을 쓴다.</summary>
    public static void WriteString(StringBuilder builder, string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(value);
        builder.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    continue;
                case '\\':
                    builder.Append("\\\\");
                    continue;
                case '\b':
                    builder.Append("\\b");
                    continue;
                case '\f':
                    builder.Append("\\f");
                    continue;
                case '\n':
                    builder.Append("\\n");
                    continue;
                case '\r':
                    builder.Append("\\r");
                    continue;
                case '\t':
                    builder.Append("\\t");
                    continue;
            }

            if (c < 0x20)
            {
                AppendUnicodeEscape(builder, c);
            }
            else if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    builder.Append(c).Append(value[i + 1]);
                    i++;
                }
                else
                {
                    AppendUnicodeEscape(builder, c);
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                AppendUnicodeEscape(builder, c);
            }
            else
            {
                builder.Append(c);
            }
        }

        builder.Append('"');
    }

    private static void AppendUnicodeEscape(StringBuilder builder, char c)
        => builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));

    private static void Write(StringBuilder builder, GlobalStateJsonNode node, string? sourceText)
    {
        if (sourceText is not null && !node.IsModified && node.SourceStart >= 0)
        {
            builder.Append(sourceText, node.SourceStart, node.SourceEnd - node.SourceStart);
            return;
        }

        switch (node)
        {
            case GlobalStateJsonObject obj:
                builder.Append('{');
                for (int i = 0; i < obj.Members.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    WriteString(builder, obj.Members[i].Key);
                    builder.Append(':');
                    Write(builder, obj.Members[i].Value, sourceText);
                }

                builder.Append('}');
                break;
            case GlobalStateJsonArray array:
                builder.Append('[');
                for (int i = 0; i < array.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    Write(builder, array.Items[i], sourceText);
                }

                builder.Append(']');
                break;
            case GlobalStateJsonString str:
                WriteString(builder, str.Value);
                break;
            case GlobalStateJsonNumber number:
                builder.Append(number.Raw);
                break;
            case GlobalStateJsonLiteral literal:
                builder.Append(literal.Raw);
                break;
            default:
                throw new InvalidOperationException("알 수 없는 JSON 노드입니다.");
        }
    }

    private sealed class Parser(string text)
    {
        public int Position { get; private set; }

        public void SkipWhitespace()
        {
            while (Position < text.Length && text[Position] is ' ' or '\t' or '\n' or '\r')
            {
                Position++;
            }
        }

        public GlobalStateJsonNode ParseValue(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("JSON 중첩이 너무 깊습니다.");
            }

            if (Position >= text.Length)
            {
                throw new FormatException("JSON이 예상보다 일찍 끝났습니다.");
            }

            int start = Position;
            GlobalStateJsonNode node = text[Position] switch
            {
                '{' => ParseObject(depth),
                '[' => ParseArray(depth),
                '"' => new GlobalStateJsonString(ParseStringLiteral()),
                't' => ParseLiteral("true"),
                'f' => ParseLiteral("false"),
                'n' => ParseLiteral("null"),
                _ => ParseNumber(),
            };
            node.SourceStart = start;
            node.SourceEnd = Position;
            return node;
        }

        private GlobalStateJsonObject ParseObject(int depth)
        {
            Position++; // {
            var obj = new GlobalStateJsonObject();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            SkipWhitespace();
            if (Peek() == '}')
            {
                Position++;
                return obj;
            }

            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"')
                {
                    throw new FormatException("객체 키는 문자열이어야 합니다.");
                }

                string key = ParseStringLiteral();
                if (!seen.Add(key))
                {
                    throw new FormatException("같은 객체 안에 중복 키가 있습니다.");
                }

                SkipWhitespace();
                Expect(':');
                SkipWhitespace();
                obj.Members.Add(new KeyValuePair<string, GlobalStateJsonNode>(key, ParseValue(depth + 1)));
                SkipWhitespace();
                char next = Peek();
                Position++;
                if (next == '}')
                {
                    return obj;
                }

                if (next != ',')
                {
                    throw new FormatException("객체 구분자가 올바르지 않습니다.");
                }
            }
        }

        private GlobalStateJsonArray ParseArray(int depth)
        {
            Position++; // [
            var array = new GlobalStateJsonArray();
            SkipWhitespace();
            if (Peek() == ']')
            {
                Position++;
                return array;
            }

            while (true)
            {
                SkipWhitespace();
                array.Items.Add(ParseValue(depth + 1));
                SkipWhitespace();
                char next = Peek();
                Position++;
                if (next == ']')
                {
                    return array;
                }

                if (next != ',')
                {
                    throw new FormatException("배열 구분자가 올바르지 않습니다.");
                }
            }
        }

        private string ParseStringLiteral()
        {
            Expect('"');
            var builder = new StringBuilder();
            while (true)
            {
                if (Position >= text.Length)
                {
                    throw new FormatException("문자열이 닫히지 않았습니다.");
                }

                char c = text[Position++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c < 0x20)
                {
                    throw new FormatException("문자열 안에 이스케이프하지 않은 제어 문자가 있습니다.");
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (Position >= text.Length)
                {
                    throw new FormatException("이스케이프가 끝나지 않았습니다.");
                }

                char escape = text[Position++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (Position + 4 > text.Length ||
                            !int.TryParse(text.AsSpan(Position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                        {
                            throw new FormatException("\\u 이스케이프가 올바르지 않습니다.");
                        }

                        builder.Append((char)code);
                        Position += 4;
                        break;
                    default:
                        throw new FormatException("알 수 없는 이스케이프입니다.");
                }
            }
        }

        private GlobalStateJsonLiteral ParseLiteral(string literal)
        {
            if (string.CompareOrdinal(text, Position, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("알 수 없는 JSON 값입니다.");
            }

            Position += literal.Length;
            return new GlobalStateJsonLiteral(literal);
        }

        private GlobalStateJsonNumber ParseNumber()
        {
            // RFC 8259: -?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?
            int start = Position;
            if (Peek() == '-')
            {
                Position++;
            }

            if (Peek() == '0')
            {
                Position++;
            }
            else if (IsDigit(Peek()) && Peek() != '0')
            {
                ReadDigits();
            }
            else
            {
                throw new FormatException("알 수 없는 JSON 값입니다.");
            }

            if (Peek() == '.')
            {
                Position++;
                if (!IsDigit(Peek()))
                {
                    throw new FormatException("숫자 형식이 올바르지 않습니다.");
                }

                ReadDigits();
            }

            if (Peek() is 'e' or 'E')
            {
                Position++;
                if (Peek() is '+' or '-')
                {
                    Position++;
                }

                if (!IsDigit(Peek()))
                {
                    throw new FormatException("숫자 형식이 올바르지 않습니다.");
                }

                ReadDigits();
            }

            return new GlobalStateJsonNumber(text[start..Position]);
        }

        private void ReadDigits()
        {
            while (IsDigit(Peek()))
            {
                Position++;
            }
        }

        private static bool IsDigit(char c) => c is >= '0' and <= '9';

        private char Peek() => Position < text.Length ? text[Position] : '\0';

        private void Expect(char expected)
        {
            if (Peek() != expected)
            {
                throw new FormatException($"'{expected}'가 필요합니다.");
            }

            Position++;
        }
    }
}

/// <summary>JSON 값 하나(<see cref="GlobalStateJson"/>).</summary>
public abstract class GlobalStateJsonNode
{
    /// <summary>원문에서 이 값이 시작하는 문자 위치. 새로 만든 값은 -1.</summary>
    public int SourceStart { get; internal set; } = -1;

    /// <summary>원문에서 이 값이 끝나는(배타) 문자 위치. 새로 만든 값은 -1.</summary>
    public int SourceEnd { get; internal set; } = -1;

    /// <summary>파싱 뒤 내용이 바뀌었는지(바뀐 컨테이너는 원문 조각 대신 다시 직렬화한다).</summary>
    public bool IsModified { get; private set; }

    /// <summary>이 값이 바뀌었다고 표시한다. 조상도 함께 표시해야 한다(호출자 책임).</summary>
    public void MarkModified() => IsModified = true;
}

/// <summary>객체. 키 순서를 보존한다.</summary>
public sealed class GlobalStateJsonObject : GlobalStateJsonNode
{
    /// <summary>멤버(파일에 나온 순서).</summary>
    public List<KeyValuePair<string, GlobalStateJsonNode>> Members { get; } = [];

    /// <summary>키로 찾는다(없으면 <c>null</c>).</summary>
    public GlobalStateJsonNode? Get(string key)
    {
        foreach (KeyValuePair<string, GlobalStateJsonNode> member in Members)
        {
            if (string.Equals(member.Key, key, StringComparison.Ordinal))
            {
                return member.Value;
            }
        }

        return null;
    }

    /// <summary>키가 있는지.</summary>
    public bool Contains(string key) => Get(key) is not null;
}

/// <summary>배열.</summary>
public sealed class GlobalStateJsonArray : GlobalStateJsonNode
{
    /// <summary>항목.</summary>
    public List<GlobalStateJsonNode> Items { get; } = [];
}

/// <summary>문자열(이스케이프를 푼 값).</summary>
public sealed class GlobalStateJsonString(string value) : GlobalStateJsonNode
{
    /// <summary>값(짝 없는 서로게이트를 포함할 수 있다).</summary>
    public string Value { get; } = value;
}

/// <summary>숫자(원문 토큰 그대로).</summary>
public sealed class GlobalStateJsonNumber(string raw) : GlobalStateJsonNode
{
    /// <summary>원문 토큰(예: <c>1759300000000</c>, <c>0.5</c>, <c>1e+21</c>).</summary>
    public string Raw { get; } = raw;

    /// <summary>정수 토큰(부호·숫자만)인지.</summary>
    public bool IsInteger
    {
        get
        {
            int i = Raw.StartsWith('-') ? 1 : 0;
            if (i >= Raw.Length)
            {
                return false;
            }

            for (; i < Raw.Length; i++)
            {
                if (Raw[i] is < '0' or > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>새 정수 값.</summary>
    public static GlobalStateJsonNumber FromInteger(long value) => new(value.ToString(CultureInfo.InvariantCulture));
}

/// <summary><c>true</c>/<c>false</c>/<c>null</c>.</summary>
public sealed class GlobalStateJsonLiteral(string raw) : GlobalStateJsonNode
{
    /// <summary>원문(<c>true</c>, <c>false</c>, <c>null</c>).</summary>
    public string Raw { get; } = raw;

    /// <summary><c>true</c>인지.</summary>
    public bool IsTrue => Raw == "true";
}
