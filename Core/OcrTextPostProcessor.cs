using System.Text.RegularExpressions;
using System.Windows;

namespace OwTranslateLite.Core;

public static partial class OcrTextPostProcessor
{
    public static IReadOnlyList<OcrTextLine> Process(IReadOnlyList<OcrTextLine> lines)
    {
        List<OcrTextLine> result = [];
        foreach (OcrTextLine line in lines.OrderBy(static line => line.Bounds.Top))
        {
            string text = RepairPlayerBoundary(line.Text.Trim());
            if (text.Length == 0)
            {
                continue;
            }

            if (result.Count > 0 &&
                IsContinuationCandidate(text) &&
                LooksLikePlayerMessage(result[^1].Text) &&
                IsLikelyWrappedLine(result[^1].Bounds, line.Bounds))
            {
                OcrTextLine previous = result[^1];
                result[^1] = new OcrTextLine(
                    previous.Text + " " + text,
                    Rect.Union(previous.Bounds, line.Bounds));
                continue;
            }

            result.Add(new OcrTextLine(text, line.Bounds));
        }

        return result;
    }

    private static string RepairPlayerBoundary(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        Match missingRightBracket = MissingRightBracketRegex().Match(text);
        if (missingRightBracket.Success)
        {
            return $"[{missingRightBracket.Groups["speaker"].Value.Trim()}]: {missingRightBracket.Groups["message"].Value.Trim()}";
        }

        Match slashAsBracket = SlashAsBracketRegex().Match(text);
        if (slashAsBracket.Success)
        {
            return $"[{slashAsBracket.Groups["speaker"].Value.Trim()}]: {slashAsBracket.Groups["message"].Value.Trim()}";
        }

        Match missingColon = MissingColonRegex().Match(text);
        if (missingColon.Success && HasChatScript(missingColon.Groups["message"].Value))
        {
            return $"[{missingColon.Groups["speaker"].Value.Trim()}]: {missingColon.Groups["message"].Value.Trim()}";
        }

        return text;
    }

    private static bool LooksLikePlayerMessage(string text) =>
        PlayerMessageRegex().IsMatch(text);

    private static bool IsContinuationCandidate(string text)
    {
        return !LooksLikePlayerMessage(text) &&
               !text.Contains('[') &&
               !text.Contains(']') &&
               !HasCjk(text) &&
               text.Length <= 48 &&
               HasChatScript(text);
    }

    private static bool IsLikelyWrappedLine(Rect previous, Rect current)
    {
        if (current.Top < previous.Top)
        {
            return false;
        }

        double verticalGap = current.Top - previous.Bottom;
        double maxGap = Math.Max(10, previous.Height * 1.6);
        if (verticalGap > maxGap)
        {
            return false;
        }

        return current.Left >= previous.Left - 12 &&
               current.Left <= previous.Right + 24;
    }

    private static bool HasChatScript(string text)
    {
        return text.Any(static ch =>
            ch is >= 'A' and <= 'Z' ||
            ch is >= 'a' and <= 'z' ||
            ch is >= '\u3040' and <= '\u30FF' ||
            ch is >= '\uAC00' and <= '\uD7AF' ||
            ch is >= '\u1100' and <= '\u11FF');
    }

    private static bool HasCjk(string text)
    {
        return text.Any(static ch => ch is >= '\u4E00' and <= '\u9FFF');
    }

    [GeneratedRegex(@"^\[(?<speaker>[^\]\[:：/\\]{2,24})\s*[:：]\s*(?<message>.+)$")]
    private static partial Regex MissingRightBracketRegex();

    [GeneratedRegex(@"^\[(?<speaker>[^\]\[:：/\\]{2,24})\s*[/\\]\s*[:：]?\s*(?<message>.+)$")]
    private static partial Regex SlashAsBracketRegex();

    [GeneratedRegex(@"^\[(?<speaker>[^\]\r\n]{2,24})\]\s+(?<message>.+)$")]
    private static partial Regex MissingColonRegex();

    [GeneratedRegex(@"^\[[^\]\r\n]{2,24}\]\s*[:：]")]
    private static partial Regex PlayerMessageRegex();
}
