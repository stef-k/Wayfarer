using System.Globalization;
using System.Text;
using Newtonsoft.Json;

namespace Wayfarer.Parsers;

/// <summary>Detects malformed Activity Unicode escapes before Json.NET replaces lone surrogates.</summary>
internal sealed class ActivityJsonTextReader : JsonTextReader
{
    private readonly RawStrings source;
    private bool activityValue;

    internal ActivityJsonTextReader(TextReader reader) : this(new RawStrings(reader)) { }
    private ActivityJsonTextReader(RawStrings reader) : base(reader) => source = reader;

    /// <summary>One transient fact for the current native feature; unrelated string fields keep existing parsing.</summary>
    internal bool InvalidActivityValue { get; set; }

    /// <summary>Consumes raw strings in token order; only properties.Activity at native feature depth is inspected.</summary>
    public override async Task<bool> ReadAsync(CancellationToken cancellationToken = default)
    {
        var read = await base.ReadAsync(cancellationToken);
        string? raw = null;
        if (TokenType is JsonToken.String or JsonToken.PropertyName)
            raw = source.Strings.Dequeue();
        // Preserve Json.NET comment handling and last-property-wins behavior without a rewritten identity.
        if (activityValue && TokenType != JsonToken.Comment)
            InvalidActivityValue = TokenType == JsonToken.String && HasInvalidSurrogate(raw!);
        if (TokenType != JsonToken.Comment)
            activityValue = TokenType == JsonToken.PropertyName && Depth == 4 && (string?)Value == "Activity";
        return read;
    }

    /// <summary>Checks escaped UTF-16 pairs only; valid literal replacement characters are not rejected.</summary>
    private static bool HasInvalidSurrogate(string raw)
    {
        for (var i = 1; i < raw.Length - 1; i++)
        {
            if (raw[i] != '\\') continue;
            i++;
            if (raw[i] != 'u') continue;
            var scalar = (char)ushort.Parse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            i += 4;
            if (char.IsLowSurrogate(scalar)) return true;
            if (!char.IsHighSurrogate(scalar)) continue;
            if (i + 6 >= raw.Length || raw[i + 1] != '\\' || raw[i + 2] != 'u' ||
                !char.IsLowSurrogate((char)ushort.Parse(raw.AsSpan(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)))
                return true;
            i += 6;
        }
        return false;
    }

    /// <summary>Retains only current raw strings and Json.NET's bounded read-ahead, never the entire file.</summary>
    private sealed class RawStrings(TextReader reader) : TextReader
    {
        internal Queue<string> Strings { get; } = [];
        private StringBuilder? current;
        private char quote;
        private bool escaped;
        private bool slash;
        private bool lineComment;
        private bool blockComment;
        private bool star;

        /// <summary>Records bounded async read-ahead without changing the underlying text.</summary>
        public override async Task<int> ReadAsync(char[] buffer, int index, int count)
        {
            var read = await reader.ReadAsync(buffer, index, count);
            foreach (var value in buffer.AsSpan(index, read)) Record(value);
            return read;
        }

        /// <summary>Records the modern TextReader overload used by Json.NET on .NET 10.</summary>
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            foreach (var value in buffer.Span[..read]) Record(value);
            return read;
        }

        /// <summary>Preserves token recording if the parser completes a read synchronously.</summary>
        public override int Read(char[] buffer, int index, int count)
        {
            var read = reader.Read(buffer, index, count);
            foreach (var value in buffer.AsSpan(index, read)) Record(value);
            return read;
        }

        /// <summary>Recognizes quotes/escapes and skips comments already supported by Json.NET.</summary>
        private void Record(char value)
        {
            if (lineComment) { lineComment = value is not '\r' and not '\n'; return; }
            if (blockComment)
            {
                blockComment = !(star && value == '/');
                star = value == '*';
                return;
            }
            if (current is null)
            {
                if (slash && value is '/' or '*')
                {
                    lineComment = value == '/';
                    blockComment = value == '*';
                    slash = false;
                    return;
                }
                slash = value == '/';
                if (value is '"' or '\'') { quote = value; current = new StringBuilder().Append(value); }
                return;
            }
            current.Append(value);
            if (!escaped && value == quote)
            {
                Strings.Enqueue(current.ToString());
                current = null;
            }
            escaped = !escaped && value == '\\';
        }
    }
}
