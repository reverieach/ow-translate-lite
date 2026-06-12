using System.Text;

namespace OwTranslateLite.Core;

public static class KoreanJamoNormalizer
{
    public static bool ContainsHangul(string value) =>
        value.Any(IsHangul);

    public static string RemoveWhitespace(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char ch in value)
        {
            if (!char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    public static string NormalizeToJamo(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(normalized.Length);
        foreach (char ch in normalized)
        {
            if (TryMapCompatibilityJamo(ch, out string mapped))
            {
                builder.Append(mapped);
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    public static int JamoEditDistance(string left, string right) =>
        LevenshteinDistance(NormalizeToJamo(RemoveWhitespace(left)), NormalizeToJamo(RemoveWhitespace(right)));

    public static double JamoSimilarity(string left, string right)
    {
        string normalizedLeft = NormalizeToJamo(RemoveWhitespace(left));
        string normalizedRight = NormalizeToJamo(RemoveWhitespace(right));
        if (normalizedLeft == normalizedRight)
        {
            return 1;
        }

        int maxLength = Math.Max(normalizedLeft.Length, normalizedRight.Length);
        if (maxLength == 0)
        {
            return 1;
        }

        int distance = LevenshteinDistance(normalizedLeft, normalizedRight);
        return Math.Max(0, 1.0 - ((double)distance / maxLength));
    }

    private static bool IsHangul(char ch) =>
        ch is >= '\uAC00' and <= '\uD7AF' ||
        ch is >= '\u1100' and <= '\u11FF' ||
        ch is >= '\u3130' and <= '\u318F';

    private static bool TryMapCompatibilityJamo(char ch, out string mapped)
    {
        mapped = ch switch
        {
            '\u3131' => "\u1100",
            '\u3132' => "\u1101",
            '\u3134' => "\u1102",
            '\u3137' => "\u1103",
            '\u3138' => "\u1104",
            '\u3139' => "\u1105",
            '\u3141' => "\u1106",
            '\u3142' => "\u1107",
            '\u3143' => "\u1108",
            '\u3145' => "\u1109",
            '\u3146' => "\u110A",
            '\u3147' => "\u110B",
            '\u3148' => "\u110C",
            '\u3149' => "\u110D",
            '\u314A' => "\u110E",
            '\u314B' => "\u110F",
            '\u314C' => "\u1110",
            '\u314D' => "\u1111",
            '\u314E' => "\u1112",
            '\u314F' => "\u1161",
            '\u3150' => "\u1162",
            '\u3151' => "\u1163",
            '\u3152' => "\u1164",
            '\u3153' => "\u1165",
            '\u3154' => "\u1166",
            '\u3155' => "\u1167",
            '\u3156' => "\u1168",
            '\u3157' => "\u1169",
            '\u3158' => "\u116A",
            '\u3159' => "\u116B",
            '\u315A' => "\u116C",
            '\u315B' => "\u116D",
            '\u315C' => "\u116E",
            '\u315D' => "\u116F",
            '\u315E' => "\u1170",
            '\u315F' => "\u1171",
            '\u3160' => "\u1172",
            '\u3161' => "\u1173",
            '\u3162' => "\u1174",
            '\u3163' => "\u1175",
            _ => ""
        };
        return mapped.Length > 0;
    }

    private static int LevenshteinDistance(string left, string right)
    {
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];

        for (int j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
