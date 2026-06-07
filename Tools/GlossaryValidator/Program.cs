using System.Text;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8;

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string glossaryPath = args.Length >= 1
    ? Path.GetFullPath(args[0])
    : Path.Combine(repoRoot, "Resources", "OwGlossary.zh-CN.json");

if (!File.Exists(glossaryPath))
{
    Console.Error.WriteLine($"Glossary not found: {glossaryPath}");
    Environment.ExitCode = 2;
    return;
}

try
{
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(glossaryPath, Encoding.UTF8));
    GlossaryValidationReport report = Validate(document.RootElement);
    PrintReport(glossaryPath, report);
    Environment.ExitCode = report.EmptyTargetEntries.Count == 0 ? 0 : 1;
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"Invalid JSON: {ex.Message}");
    Environment.ExitCode = 1;
}

static GlossaryValidationReport Validate(JsonElement root)
{
    string version = root.TryGetProperty("version", out JsonElement versionElement)
        ? versionElement.GetString() ?? "unknown"
        : "unknown";

    if (!root.TryGetProperty("entries", out JsonElement entriesElement) ||
        entriesElement.ValueKind != JsonValueKind.Array)
    {
        return new GlossaryValidationReport(version, 0, 0, [], [], ["Missing entries array"]);
    }

    List<string> emptyTargetEntries = [];
    List<string> shortAliasWarnings = [];
    Dictionary<string, List<string>> aliases = new(StringComparer.OrdinalIgnoreCase);
    int entryCount = 0;
    int termCount = 0;

    foreach (JsonElement entry in entriesElement.EnumerateArray())
    {
        entryCount++;
        string category = GetString(entry, "category", "unknown");
        string target = GetString(entry, "zh_cn", "");
        if (string.IsNullOrWhiteSpace(target))
        {
            emptyTargetEntries.Add($"entry#{entryCount} category={category}");
        }

        if (!entry.TryGetProperty("terms", out JsonElement termsElement) ||
            termsElement.ValueKind != JsonValueKind.Array)
        {
            emptyTargetEntries.Add($"entry#{entryCount} target={target} missing terms");
            continue;
        }

        foreach (JsonElement termElement in termsElement.EnumerateArray())
        {
            string term = termElement.GetString()?.Trim() ?? "";
            if (term.Length == 0)
            {
                continue;
            }

            termCount++;
            string key = NormalizeAlias(term);
            if (!aliases.TryGetValue(key, out List<string>? owners))
            {
                owners = [];
                aliases[key] = owners;
            }

            owners.Add($"{category}:{target}");
            if (IsShortAliasRisk(term))
            {
                shortAliasWarnings.Add($"{term} -> {category}:{target}");
            }
        }
    }

    IReadOnlyList<string> duplicateAliases = aliases
        .Where(static pair => pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        .Select(static pair => $"{pair.Key} -> {string.Join(" | ", pair.Value.Distinct(StringComparer.OrdinalIgnoreCase))}")
        .OrderBy(static line => line, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return new GlossaryValidationReport(
        version,
        entryCount,
        termCount,
        emptyTargetEntries,
        duplicateAliases,
        shortAliasWarnings.OrderBy(static line => line, StringComparer.OrdinalIgnoreCase).ToArray());
}

static void PrintReport(string glossaryPath, GlossaryValidationReport report)
{
    Console.WriteLine($"Glossary: {glossaryPath}");
    Console.WriteLine($"Version: {report.Version}");
    Console.WriteLine($"Entries: {report.EntryCount}");
    Console.WriteLine($"Terms/Aliases: {report.TermCount}");
    Console.WriteLine($"Empty targets: {report.EmptyTargetEntries.Count}");
    Console.WriteLine($"Duplicate aliases: {report.DuplicateAliases.Count}");
    Console.WriteLine($"Short alias warnings: {report.ShortAliasWarnings.Count}");
    PrintSection("Empty Target Errors", report.EmptyTargetEntries, maxLines: 80);
    PrintSection("Duplicate Alias Warnings", report.DuplicateAliases, maxLines: 120);
    PrintSection("Short Alias Warnings", report.ShortAliasWarnings, maxLines: 120);
}

static void PrintSection(string title, IReadOnlyList<string> lines, int maxLines)
{
    if (lines.Count == 0)
    {
        return;
    }

    Console.WriteLine();
    Console.WriteLine($"== {title} ==");
    foreach (string line in lines.Take(maxLines))
    {
        Console.WriteLine(line);
    }

    if (lines.Count > maxLines)
    {
        Console.WriteLine($"... {lines.Count - maxLines} more");
    }
}

static string GetString(JsonElement element, string propertyName, string fallback)
{
    return element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? fallback
        : fallback;
}

static string NormalizeAlias(string value)
{
    return string.Join(' ', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

static bool IsShortAliasRisk(string value)
{
    string trimmed = value.Trim();
    if (trimmed.Length == 0)
    {
        return false;
    }

    bool hasAscii = trimmed.Any(static ch => ch <= 0x7f && char.IsLetterOrDigit(ch));
    if (hasAscii)
    {
        string ascii = new(trimmed.Where(static ch => ch <= 0x7f && char.IsLetterOrDigit(ch)).ToArray());
        return ascii.Length is > 0 and <= 2;
    }

    return trimmed.Length <= 1;
}

static string FindRepoRoot(string startDirectory)
{
    DirectoryInfo? directory = new(startDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "OwTranslateLite.csproj")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}

internal sealed record GlossaryValidationReport(
    string Version,
    int EntryCount,
    int TermCount,
    IReadOnlyList<string> EmptyTargetEntries,
    IReadOnlyList<string> DuplicateAliases,
    IReadOnlyList<string> ShortAliasWarnings);
