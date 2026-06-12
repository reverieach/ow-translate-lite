using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OwTranslateLite.Core;

public static class KoreanJamoNormalizer
{
    private const double DefaultSubstitutionCost = 1.0;
    private static readonly Lazy<IReadOnlyDictionary<(char Left, char Right), double>> ConfusionCosts = new(LoadConfusionCosts);

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

        double distance = WeightedLevenshteinDistance(normalizedLeft, normalizedRight);
        return Math.Max(0, 1.0 - (distance / maxLength));
    }

    public static double WeightedJamoEditDistance(string left, string right) =>
        WeightedLevenshteinDistance(NormalizeToJamo(RemoveWhitespace(left)), NormalizeToJamo(RemoveWhitespace(right)));

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

    private static double WeightedLevenshteinDistance(string left, string right)
    {
        double[] previous = new double[right.Length + 1];
        double[] current = new double[right.Length + 1];

        for (int j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                double cost = SubstitutionCost(left[i - 1], right[j - 1]);
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static double SubstitutionCost(char left, char right)
    {
        if (left == right)
        {
            return 0;
        }

        return ConfusionCosts.Value.TryGetValue((left, right), out double cost)
            ? cost
            : DefaultSubstitutionCost;
    }

    private static IReadOnlyDictionary<(char Left, char Right), double> LoadConfusionCosts()
    {
        Dictionary<(char Left, char Right), double> costs = DefaultConfusionCosts();
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "KoreanJamoConfusionCosts.json");
        if (!File.Exists(path))
        {
            return costs;
        }

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            KoreanJamoConfusionFile? file = JsonSerializer.Deserialize<KoreanJamoConfusionFile>(json);
            if (file?.Pairs is null)
            {
                return costs;
            }

            foreach (KoreanJamoConfusionPair pair in file.Pairs)
            {
                AddPair(costs, pair.Left, pair.Right, pair.Cost);
            }
        }
        catch
        {
            return costs;
        }

        return costs;
    }

    private static Dictionary<(char Left, char Right), double> DefaultConfusionCosts()
    {
        Dictionary<(char Left, char Right), double> costs = [];

        AddPair(costs, "\u1100", "\u110F", 0.45); // giyeok / kieuk
        AddPair(costs, "\u1103", "\u1110", 0.45); // digeut / tieut
        AddPair(costs, "\u1107", "\u1111", 0.45); // bieup / pieup
        AddPair(costs, "\u110C", "\u110E", 0.45); // jieut / chieut
        AddPair(costs, "\u110B", "\u1112", 0.55); // ieung / hieut
        AddPair(costs, "\u1106", "\u1107", 0.55); // mieum / bieup
        AddPair(costs, "\u1102", "\u1103", 0.60); // nieun / digeut
        AddPair(costs, "\u1103", "\u1105", 0.60); // digeut / rieul
        AddPair(costs, "\u1100", "\u1101", 0.50);
        AddPair(costs, "\u1103", "\u1104", 0.50);
        AddPair(costs, "\u1107", "\u1108", 0.50);
        AddPair(costs, "\u1109", "\u110A", 0.50);
        AddPair(costs, "\u110C", "\u110D", 0.50);

        AddPair(costs, "\u1161", "\u1165", 0.45); // a / eo
        AddPair(costs, "\u1162", "\u1166", 0.45); // ae / e
        AddPair(costs, "\u1163", "\u1167", 0.50); // ya / yeo
        AddPair(costs, "\u1164", "\u1168", 0.50); // yae / ye
        AddPair(costs, "\u1169", "\u116E", 0.55); // o / u
        AddPair(costs, "\u116D", "\u1172", 0.55); // yo / yu
        AddPair(costs, "\u116A", "\u116F", 0.50); // wa / weo
        AddPair(costs, "\u116B", "\u1170", 0.50); // wae / we

        return costs;
    }

    private static void AddPair(Dictionary<(char Left, char Right), double> costs, string left, string right, double cost)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return;
        }

        string normalizedLeft = NormalizeToJamo(left);
        string normalizedRight = NormalizeToJamo(right);
        if (normalizedLeft.Length != 1 || normalizedRight.Length != 1)
        {
            return;
        }

        double clamped = Math.Clamp(cost, 0.1, 1.0);
        char leftChar = normalizedLeft[0];
        char rightChar = normalizedRight[0];
        costs[(leftChar, rightChar)] = clamped;
        costs[(rightChar, leftChar)] = clamped;
    }

    private sealed record KoreanJamoConfusionFile(
        [property: JsonPropertyName("pairs")] IReadOnlyList<KoreanJamoConfusionPair> Pairs);

    private sealed record KoreanJamoConfusionPair(
        [property: JsonPropertyName("left")] string Left,
        [property: JsonPropertyName("right")] string Right,
        [property: JsonPropertyName("cost")] double Cost);
}
