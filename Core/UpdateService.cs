using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace OwTranslateLite.Core;

public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/reverieach/ow-translate-lite/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    public async Task<UpdateCheckResult> CheckLatestAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Client.GetAsync(LatestReleaseUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;

        string tag = GetString(root, "tag_name");
        string name = GetString(root, "name");
        string body = GetString(root, "body");
        string htmlUrl = GetString(root, "html_url");
        DateTime? publishedAt = TryGetDateTime(root, "published_at");
        UpdateAsset? packageAsset = null;
        UpdateAsset? sha256Asset = null;

        if (root.TryGetProperty("assets", out JsonElement assets) &&
            assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string assetName = GetString(asset, "name");
                string downloadUrl = GetString(asset, "browser_download_url");
                long size = TryGetInt64(asset, "size");
                if (string.IsNullOrWhiteSpace(assetName) ||
                    string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                UpdateAsset updateAsset = new(assetName, downloadUrl, size);
                if (assetName.EndsWith(".sha256.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sha256Asset ??= updateAsset;
                }
                else if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                         assetName.Contains("portable-win-x64", StringComparison.OrdinalIgnoreCase))
                {
                    packageAsset ??= updateAsset;
                }
            }
        }

        string currentVersion = GetCurrentVersion();
        bool isNewer = IsNewerVersion(tag, currentVersion);
        return new UpdateCheckResult(
            currentVersion,
            tag,
            string.IsNullOrWhiteSpace(name) ? tag : name,
            body,
            htmlUrl,
            publishedAt,
            packageAsset,
            sha256Asset,
            isNewer);
    }

    public static string GetCurrentVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        string version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString() ?? "0.0.0"
            : informational;
        int metadataIndex = version.IndexOf('+');
        return metadataIndex >= 0 ? version[..metadataIndex] : version;
    }

    public static bool IsNewerVersion(string latestVersion, string currentVersion)
    {
        VersionParts latest = VersionParts.Parse(latestVersion);
        VersionParts current = VersionParts.Parse(currentVersion);
        return latest.CompareTo(current) > 0;
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OWTranslatorLite");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long TryGetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.TryGetInt64(out long result)
            ? result
            : 0;

    private static DateTime? TryGetDateTime(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.TryGetDateTime(out DateTime result)
            ? result
            : null;

    private readonly record struct VersionParts(
        int Major,
        int Minor,
        int Patch,
        int Beta,
        string Raw) : IComparable<VersionParts>
    {
        public static VersionParts Parse(string value)
        {
            string raw = value.Trim();
            string normalized = raw.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? raw[1..]
                : raw;
            string[] mainAndPre = normalized.Split('-', 2);
            string[] numeric = mainAndPre[0].Split('.');
            int major = ReadPart(numeric, 0);
            int minor = ReadPart(numeric, 1);
            int patch = ReadPart(numeric, 2);
            int beta = int.MaxValue;
            if (mainAndPre.Length > 1)
            {
                string[] prerelease = mainAndPre[1].Split('.');
                if (prerelease.Length >= 2 &&
                    prerelease[0].Equals("beta", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(prerelease[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int betaNumber))
                {
                    beta = betaNumber;
                }
                else
                {
                    beta = 0;
                }
            }

            return new VersionParts(major, minor, patch, beta, raw);
        }

        public int CompareTo(VersionParts other)
        {
            int result = Major.CompareTo(other.Major);
            if (result != 0)
            {
                return result;
            }

            result = Minor.CompareTo(other.Minor);
            if (result != 0)
            {
                return result;
            }

            result = Patch.CompareTo(other.Patch);
            if (result != 0)
            {
                return result;
            }

            return Beta.CompareTo(other.Beta);
        }

        private static int ReadPart(string[] parts, int index) =>
            parts.Length > index &&
            int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : 0;
    }
}

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string LatestVersion,
    string LatestName,
    string ReleaseNotes,
    string ReleasePageUrl,
    DateTime? PublishedAt,
    UpdateAsset? PackageAsset,
    UpdateAsset? Sha256Asset,
    bool IsNewer);

public sealed record UpdateAsset(string Name, string DownloadUrl, long SizeBytes);
