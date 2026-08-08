using Typewriter.Abstractions;

namespace Typewriter.Engine;

public static class OutputContentFormatter
{
    public static string Format(
        string content,
        OutputConfiguration output) =>
        Format(content: content, output: output, insertFinalNewline: null);

    /// <summary>
    /// Formats rendered content for output.
    /// </summary>
    /// <param name="content">The rendered content.</param>
    /// <param name="output">The output configuration.</param>
    /// <param name="insertFinalNewline">
    /// A template-level override for <see cref="OutputConfiguration.InsertFinalNewline"/>,
    /// or <see langword="null"/> to use the configured value.
    /// </param>
    /// <returns>The formatted content.</returns>
    public static string Format(
        string content,
        OutputConfiguration output,
        bool? insertFinalNewline)
    {
        ArgumentNullException.ThrowIfNull(argument: content);
        ArgumentNullException.ThrowIfNull(argument: output);

        var wantsFinalNewline = insertFinalNewline ?? output.InsertFinalNewline;
        var wantsCrLf = output.Newline.Equals(value: "crlf", comparisonType: StringComparison.OrdinalIgnoreCase);
        if (!wantsCrLf
            && output.IndentStyle == IndentStyle.Preserve
            && !output.TrimTrailingWhitespace
            && !wantsFinalNewline
            && !content.Contains(value: "\r", comparisonType: StringComparison.Ordinal))
        {
            return content;
        }

        var formatted = content.Contains(value: "\r", comparisonType: StringComparison.Ordinal)
            ? NormalizeToLineFeed(content: content)
            : content;
        if (output.IndentStyle != IndentStyle.Preserve)
        {
            formatted = ApplyIndentation(content: formatted, style: output.IndentStyle, size: output.IndentSize);
        }

        if (output.TrimTrailingWhitespace)
        {
            formatted = TrimTrailingWhitespace(content: formatted);
        }

        if (wantsFinalNewline)
        {
            formatted = EnsureFinalNewline(content: formatted);
        }

        return wantsCrLf
            ? formatted.Replace(oldValue: "\n", newValue: "\r\n", comparisonType: StringComparison.Ordinal)
            : formatted;
    }

    private static string NormalizeToLineFeed(string content)
    {
        return content.Replace(oldValue: "\r\n", newValue: "\n", comparisonType: StringComparison.Ordinal)
            .Replace(oldChar: '\r', newChar: '\n');
    }

    private static string ApplyIndentation(
        string content,
        IndentStyle style,
        int size)
    {
        if (style == IndentStyle.Preserve)
        {
            return content;
        }

        var indentSize = Math.Max(val1: 1, val2: size);
        var lines = content.Split(separator: '\n');
        var unit = DetectSpaceIndentUnit(lines: lines) ?? indentSize;
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = ReindentLine(line: lines[index], style: style, indentSize: indentSize, unit: unit);
        }

        return string.Join(separator: '\n', value: lines);
    }

    private static int? DetectSpaceIndentUnit(string[] lines)
    {
        var unit = 0;
        foreach (var line in lines)
        {
            var (_, spaces, contentIndex) = MeasureLeadingWhitespace(line: line);
            if (contentIndex >= line.Length || spaces == 0)
            {
                continue;
            }

            unit = GreatestCommonDivisor(left: unit, right: spaces);
        }

        return unit == 0 ? null : unit;
    }

    private static string ReindentLine(
        string line,
        IndentStyle style,
        int indentSize,
        int unit)
    {
        var (tabs, spaces, contentIndex) = MeasureLeadingWhitespace(line: line);
        if (contentIndex >= line.Length || (tabs == 0 && spaces == 0))
        {
            return line;
        }

        var levels = tabs + (spaces / unit);
        var remainder = spaces % unit;
        var indentation = style == IndentStyle.Tab
            ? new string(c: '\t', count: levels) + new string(c: ' ', count: remainder)
            : new string(c: ' ', count: (levels * indentSize) + remainder);

        return indentation + line[contentIndex..];
    }

    private static (int Tabs, int Spaces, int ContentIndex) MeasureLeadingWhitespace(string line)
    {
        var tabs = 0;
        var spaces = 0;
        var index = 0;
        while (index < line.Length)
        {
            if (line[index: index] == '\t')
            {
                tabs++;
            }
            else if (line[index: index] == ' ')
            {
                spaces++;
            }
            else
            {
                break;
            }

            index++;
        }

        return (tabs, spaces, index);
    }

    private static int GreatestCommonDivisor(
        int left,
        int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }

    private static string TrimTrailingWhitespace(string content)
    {
        var lines = content.Split(separator: '\n');
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = lines[index].TrimEnd(' ', '\t');
        }

        return string.Join(separator: '\n', value: lines);
    }

    private static string EnsureFinalNewline(string content)
    {
        return content.Length == 0 || content.EndsWith(value: '\n')
            ? content
            : content + "\n";
    }
}
