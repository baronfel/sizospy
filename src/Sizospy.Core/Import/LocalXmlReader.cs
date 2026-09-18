using System.Globalization;
using System.Text;

namespace Sizospy.Import;

internal sealed class LocalXmlReader : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly StreamReader _reader;
    private readonly char[] _buffer = new char[BufferSize];
    private readonly StringBuilder _tag = new();
    private readonly List<XmlAttribute> _attributes = [];
    private readonly List<string> _elementStack = [];
    private int _bufferOffset;
    private int _bufferLength;
    private int _lineNumber = 1;
    private int _linePosition;
    private int _tagLineNumber;
    private int _tagLinePosition;
    private bool _insideTag;
    private bool _hasRootElement;
    private bool _rootElementClosed;
    private char _quote;

    public LocalXmlReader(Stream stream)
    {
        _reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
    }

    public string LocalName { get; private set; } = string.Empty;

    public string? GetAttribute(string name)
    {
        foreach (var attribute in _attributes)
        {
            if (attribute.Name.Equals(name, StringComparison.Ordinal))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
    {
        LocalName = string.Empty;
        _attributes.Clear();

        while (true)
        {
            if (_bufferOffset == _bufferLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _bufferLength = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken);
                _bufferOffset = 0;
                if (_bufferLength == 0)
                {
                    ValidateEndOfFile();
                    return false;
                }
            }

            while (_bufferOffset < _bufferLength)
            {
                if ((_bufferOffset & 0xfff) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var value = _buffer[_bufferOffset++];
                AdvancePosition(value);
                if (!_insideTag)
                {
                    if (value == '<')
                    {
                        _insideTag = true;
                        _quote = '\0';
                        _tag.Clear();
                        _tagLineNumber = _lineNumber;
                        _tagLinePosition = _linePosition;
                    }
                    else if (_elementStack.Count == 0 && !char.IsWhiteSpace(value))
                    {
                        throw Error("Text is not allowed outside the root element.");
                    }

                    continue;
                }

                _tag.Append(value);
                UpdateQuote(value);
                if (!IsTagComplete(value))
                {
                    continue;
                }

                _insideTag = false;
                if (ParseTag())
                {
                    return true;
                }
            }
        }
    }

    public void Dispose() => _reader.Dispose();

    private void AdvancePosition(char value)
    {
        if (value == '\n')
        {
            _lineNumber++;
            _linePosition = 0;
        }
        else
        {
            _linePosition++;
        }
    }

    private void UpdateQuote(char value)
    {
        if (_tag.Length == 0 || _tag[0] is '!' or '?')
        {
            return;
        }

        if (_quote == '\0')
        {
            if (value is '\'' or '"')
            {
                _quote = value;
            }
        }
        else if (value == _quote)
        {
            _quote = '\0';
        }
    }

    private bool IsTagComplete(char value)
    {
        if (StartsWith("!--"))
        {
            return EndsWith("-->");
        }

        if (StartsWith("![CDATA["))
        {
            return EndsWith("]]>");
        }

        if (_tag.Length > 0 && _tag[0] == '?')
        {
            return EndsWith("?>");
        }

        return value == '>' && _quote == '\0';
    }

    private bool ParseTag()
    {
        if (StartsWith("!--") || (_tag.Length > 0 && _tag[0] == '?'))
        {
            return false;
        }

        if (StartsWith("![CDATA["))
        {
            if (_elementStack.Count == 0)
            {
                throw Error("CDATA is not allowed outside the root element.");
            }

            return false;
        }

        var text = _tag.ToString();
        var body = text.AsSpan(0, text.Length - 1).Trim();
        if (body.IsEmpty)
        {
            throw Error("An XML tag has no name.");
        }

        if (body[0] == '!')
        {
            throw Error("DTD and XML declarations are not supported.");
        }

        if (body[0] == '/')
        {
            ParseEndTag(body[1..]);
            return false;
        }

        var selfClosing = body[^1] == '/';
        if (selfClosing)
        {
            body = body[..^1].TrimEnd();
        }

        var offset = 0;
        var qualifiedName = ReadName(body, ref offset, "element");
        ParseAttributes(body, ref offset);
        LocalName = GetLocalName(qualifiedName);
        if (_elementStack.Count == 0)
        {
            if (_rootElementClosed)
            {
                throw Error("The XML input contains more than one root element.");
            }

            _hasRootElement = true;
        }

        if (!selfClosing)
        {
            _elementStack.Add(qualifiedName);
        }
        else if (_elementStack.Count == 0)
        {
            _rootElementClosed = true;
        }

        return true;
    }

    private void ParseEndTag(ReadOnlySpan<char> body)
    {
        body = body.Trim();
        var offset = 0;
        var name = ReadName(body, ref offset, "end tag");
        SkipWhitespace(body, ref offset);
        if (offset != body.Length)
        {
            throw Error($"Unexpected content after end tag '{name}'.");
        }

        if (_elementStack.Count == 0)
        {
            throw Error($"End tag '{name}' has no matching start tag.");
        }

        var expected = _elementStack[^1];
        if (!expected.Equals(name, StringComparison.Ordinal))
        {
            throw Error($"End tag '{name}' does not match start tag '{expected}'.");
        }

        _elementStack.RemoveAt(_elementStack.Count - 1);
        if (_elementStack.Count == 0)
        {
            _rootElementClosed = true;
        }
    }

    private void ParseAttributes(ReadOnlySpan<char> body, ref int offset)
    {
        while (true)
        {
            SkipWhitespace(body, ref offset);
            if (offset == body.Length)
            {
                return;
            }

            var name = ReadName(body, ref offset, "attribute");
            SkipWhitespace(body, ref offset);
            if (offset == body.Length || body[offset] != '=')
            {
                throw Error($"Attribute '{name}' is missing '='.");
            }

            offset++;
            SkipWhitespace(body, ref offset);
            if (offset == body.Length || body[offset] is not ('\'' or '"'))
            {
                throw Error($"Attribute '{name}' must have a quoted value.");
            }

            var quote = body[offset++];
            var valueStart = offset;
            while (offset < body.Length && body[offset] != quote)
            {
                offset++;
            }

            if (offset == body.Length)
            {
                throw Error($"Attribute '{name}' has an unterminated value.");
            }

            var value = DecodeEntities(body[valueStart..offset]);
            offset++;
            if (_attributes.Any(attribute => attribute.Name.Equals(name, StringComparison.Ordinal)))
            {
                throw Error($"Attribute '{name}' is specified more than once.");
            }

            _attributes.Add(new XmlAttribute(name, value));
        }
    }

    private string ReadName(ReadOnlySpan<char> text, ref int offset, string description)
    {
        SkipWhitespace(text, ref offset);
        var start = offset;
        while (offset < text.Length && !IsNameTerminator(text[offset]))
        {
            offset++;
        }

        if (start == offset)
        {
            throw Error($"The XML {description} has no name.");
        }

        return text[start..offset].ToString();
    }

    private static bool IsNameTerminator(char value) =>
        char.IsWhiteSpace(value) || value is '/' or '>' or '=' or '<' or '\'' or '"';

    private static void SkipWhitespace(ReadOnlySpan<char> text, ref int offset)
    {
        while (offset < text.Length && char.IsWhiteSpace(text[offset]))
        {
            offset++;
        }
    }

    private static string GetLocalName(string name)
    {
        var separator = name.LastIndexOf(':');
        return separator < 0 ? name : name[(separator + 1)..];
    }

    private string DecodeEntities(ReadOnlySpan<char> value)
    {
        var entityStart = value.IndexOf('&');
        if (entityStart < 0)
        {
            return value.ToString();
        }

        var result = new StringBuilder(value.Length);
        var offset = 0;
        while (entityStart >= 0)
        {
            result.Append(value[offset..(offset + entityStart)]);
            offset += entityStart;
            var entityEnd = value[offset..].IndexOf(';');
            if (entityEnd < 0)
            {
                throw Error("An XML entity is missing its terminating ';'.");
            }

            var entity = value.Slice(offset + 1, entityEnd - 1);
            result.Append(DecodeEntity(entity));
            offset += entityEnd + 1;
            entityStart = value[offset..].IndexOf('&');
        }

        result.Append(value[offset..]);
        return result.ToString();
    }

    private string DecodeEntity(ReadOnlySpan<char> entity)
    {
        if (entity.SequenceEqual("amp")) return "&";
        if (entity.SequenceEqual("apos")) return "'";
        if (entity.SequenceEqual("gt")) return ">";
        if (entity.SequenceEqual("lt")) return "<";
        if (entity.SequenceEqual("quot")) return "\"";

        if (entity.Length > 1 && entity[0] == '#')
        {
            var hexadecimal = entity.Length > 2 && entity[1] is 'x' or 'X';
            var digits = hexadecimal ? entity[2..] : entity[1..];
            if (int.TryParse(
                    digits,
                    hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var codePoint) &&
                codePoint is >= 0 and <= 0x10ffff &&
                codePoint is not (>= 0xd800 and <= 0xdfff))
            {
                return char.ConvertFromUtf32(codePoint);
            }
        }

        throw Error($"Unsupported XML entity '&{entity.ToString()};'.");
    }

    private bool StartsWith(string value)
    {
        if (_tag.Length < value.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (_tag[index] != value[index])
            {
                return false;
            }
        }

        return true;
    }

    private bool EndsWith(string value)
    {
        if (_tag.Length < value.Length)
        {
            return false;
        }

        var start = _tag.Length - value.Length;
        for (var index = 0; index < value.Length; index++)
        {
            if (_tag[start + index] != value[index])
            {
                return false;
            }
        }

        return true;
    }

    private void ValidateEndOfFile()
    {
        if (_insideTag)
        {
            throw Error("The XML input ends inside a tag.");
        }

        if (_elementStack.Count > 0)
        {
            throw Error($"The XML input ends before element '{_elementStack[^1]}' is closed.");
        }

        if (!_hasRootElement)
        {
            throw Error("The XML input has no root element.");
        }
    }

    private LocalXmlException Error(string message) =>
        new(message, _tagLineNumber, _tagLinePosition);

    private readonly record struct XmlAttribute(string Name, string Value);
}

internal sealed class LocalXmlException(
    string message,
    int lineNumber,
    int linePosition) : Exception(message)
{
    public int LineNumber { get; } = lineNumber;

    public int LinePosition { get; } = linePosition;
}
