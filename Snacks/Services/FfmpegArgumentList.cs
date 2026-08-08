using System.Diagnostics;
using System.Text;

namespace Snacks.Services;

/// <summary>
///     Literal FFmpeg argument tokens. Legacy flag builders can be tokenized during the
///     migration, but execution always populates ProcessStartInfo.ArgumentList and never a shell.
/// </summary>
public sealed class FfmpegArgumentList
{
    private readonly List<string> _arguments = new();
    public IReadOnlyList<string> Arguments => _arguments;

    public FfmpegArgumentList Add(string value)
    {
        _arguments.Add(value);
        return this;
    }

    public FfmpegArgumentList Add(string option, string value)
    {
        _arguments.Add(option);
        _arguments.Add(value);
        return this;
    }

    public FfmpegArgumentList AddRange(IEnumerable<string> values)
    {
        _arguments.AddRange(values);
        return this;
    }

    public FfmpegArgumentList AddLegacyFragment(string? fragment)
    {
        if (!string.IsNullOrWhiteSpace(fragment)) _arguments.AddRange(Tokenize(fragment));
        return this;
    }

    public void ApplyTo(ProcessStartInfo startInfo)
    {
        foreach (var argument in _arguments) startInfo.ArgumentList.Add(argument);
    }

    public override string ToString() => FormatForDisplay(_arguments);

    public static string FormatForDisplay(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(QuoteForDisplay));

    internal static IReadOnlyList<string> Tokenize(string command)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';
        bool escaping = false;

        foreach (var character in command ?? "")
        {
            if (escaping)
            {
                token.Append(character);
                escaping = false;
                continue;
            }
            if (character == '\\' && quote != '\'')
            {
                escaping = true;
                continue;
            }
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else token.Append(character);
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                if (token.Length == 0) continue;
                result.Add(token.ToString());
                token.Clear();
                continue;
            }
            token.Append(character);
        }
        if (escaping) token.Append('\\');
        if (quote != '\0') throw new FormatException("Unterminated quote in generated FFmpeg arguments.");
        if (token.Length > 0) result.Add(token.ToString());
        return result;
    }

    private static string QuoteForDisplay(string value)
    {
        if (value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || "_@%+=:,./-".Contains(c))) return value;
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
