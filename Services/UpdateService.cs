using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace BurgerDeleter.Services
{
    /// <summary>Information about an available GitHub release.</summary>
    public sealed class UpdateInfo
    {
        /// <summary>Raw tag name, e.g. "v1.3".</summary>
        public string TagName   { get; init; } = string.Empty;

        /// <summary>Parsed version string, e.g. "1.3.0.0".</summary>
        public string Version   { get; init; } = string.Empty;

        /// <summary>Direct download URL of the .exe asset, or empty if none was found.</summary>
        public string AssetUrl  { get; init; } = string.Empty;

        /// <summary>File name of the .exe asset.</summary>
        public string AssetName { get; init; } = string.Empty;
    }

    public static class UpdateService
    {
        private const string ApiUrl = "https://api.github.com/repos/BurgerSterg/BurgerDeleter/releases/latest";

        /// <summary>
        /// Fetches the latest GitHub release and compares it to the running assembly
        /// version.  Returns an <see cref="UpdateInfo"/> if a newer version is available,
        /// or <c>null</c> if the app is up-to-date or the check could not be completed.
        /// </summary>
        public static async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("User-Agent", "BurgerDeleter");

                string json = await http.GetStringAsync(ApiUrl);
                using var doc  = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // ── Parse remote version ──────────────────────────────────────
                string tagName = root.TryGetProperty("tag_name", out var tagProp)
                    ? tagProp.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(tagName)) return null;

                // Strip leading 'v' and normalise to 4 parts so that "1.2" == "1.2.0.0"
                var parts = tagName.TrimStart('v').Split('.').ToList();
                while (parts.Count < 4) parts.Add("0");
                string normalised = string.Join(".", parts.Take(4));

                if (!Version.TryParse(normalised, out var remoteVersion)) return null;

                // ── Compare to local version ──────────────────────────────────
                var localVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (localVersion is null || remoteVersion <= localVersion) return null;

                // ── Find the .exe download asset ──────────────────────────────
                string assetUrl  = string.Empty;
                string assetName = string.Empty;

                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.TryGetProperty("name", out var nameProp)
                            ? nameProp.GetString() ?? string.Empty
                            : string.Empty;

                        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            continue;

                        assetUrl = asset.TryGetProperty("browser_download_url", out var urlProp)
                            ? urlProp.GetString() ?? string.Empty
                            : string.Empty;

                        assetName = name;
                        break;
                    }
                }

                return new UpdateInfo
                {
                    TagName   = tagName,
                    Version   = normalised,
                    AssetUrl  = assetUrl,
                    AssetName = assetName,
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
