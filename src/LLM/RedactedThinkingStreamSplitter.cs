namespace Hermes.Agent.LLM;

using System.Text;

/// <summary>
/// Splits streamed content tokens into public text vs. thinking deltas when models embed
/// <c>&lt;redacted_thinking&gt;...&lt;/redacted_thinking&gt;</c> tags in the content field.
/// </summary>
internal sealed class RedactedThinkingStreamSplitter
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    private bool _inThinkBlock;
    private readonly StringBuilder _buffer = new();

    public IEnumerable<StreamEvent> AppendToken(string token)
    {
        _buffer.Append(token);

        while (_buffer.Length > 0)
        {
            var text = _buffer.ToString();

            if (!_inThinkBlock)
            {
                var openIdx = text.IndexOf(OpenTag, StringComparison.Ordinal);
                if (openIdx >= 0)
                {
                    if (openIdx > 0)
                        yield return new StreamEvent.TokenDelta(text[..openIdx]);
                    _inThinkBlock = true;
                    _buffer.Clear();
                    _buffer.Append(text[(openIdx + OpenTag.Length)..]);
                    continue;
                }

                if (HasPartialOpenTagSuffix(text))
                {
                    var ps = text.LastIndexOf('<');
                    if (ps >= 0 && ps < text.Length)
                    {
                        if (ps > 0)
                            yield return new StreamEvent.TokenDelta(text[..ps]);
                        _buffer.Clear();
                        _buffer.Append(text[ps..]);
                        break;
                    }
                }

                yield return new StreamEvent.TokenDelta(text);
                _buffer.Clear();
            }
            else
            {
                var closeIdx = text.IndexOf(CloseTag, StringComparison.Ordinal);
                if (closeIdx >= 0)
                {
                    if (closeIdx > 0)
                        yield return new StreamEvent.ThinkingDelta(text[..closeIdx]);
                    _inThinkBlock = false;
                    _buffer.Clear();
                    _buffer.Append(text[(closeIdx + CloseTag.Length)..]);
                    continue;
                }

                if (HasPartialCloseTagSuffix(text))
                {
                    var ps = text.LastIndexOf('<');
                    if (ps > 0)
                    {
                        yield return new StreamEvent.ThinkingDelta(text[..ps]);
                        _buffer.Clear();
                        _buffer.Append(text[ps..]);
                        break;
                    }

                    break;
                }

                yield return new StreamEvent.ThinkingDelta(text);
                _buffer.Clear();
            }

            break;
        }
    }

    public StreamEvent? FlushRemainder()
    {
        if (_buffer.Length == 0)
            return null;

        var remaining = _buffer.ToString();
        _buffer.Clear();
        return _inThinkBlock
            ? new StreamEvent.ThinkingDelta(remaining)
            : new StreamEvent.TokenDelta(remaining);
    }

    private static bool HasPartialOpenTagSuffix(string text) =>
        text.Length > 0 && (text[^1] == '<' || text.EndsWith("<t", StringComparison.Ordinal) ||
                            text.EndsWith("<th", StringComparison.Ordinal) ||
                            text.EndsWith("<thi", StringComparison.Ordinal) ||
                            text.EndsWith("<thin", StringComparison.Ordinal) ||
                            text.EndsWith("<think", StringComparison.Ordinal));

    private static bool HasPartialCloseTagSuffix(string text) =>
        text.EndsWith('<') ||
        text.EndsWith("</", StringComparison.Ordinal) ||
        text.EndsWith("</t", StringComparison.Ordinal) ||
        text.EndsWith("</th", StringComparison.Ordinal) ||
        text.EndsWith("</thi", StringComparison.Ordinal) ||
        text.EndsWith("</thin", StringComparison.Ordinal) ||
        text.EndsWith("</think", StringComparison.Ordinal);
}
