using System.Diagnostics;
using System.IO;
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

        /// <summary><c>%LocalAppData%\BurgerDeleter</c></summary>
        public static string InstallDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BurgerDeleter");

        /// <summary>Stable install path (matches first-run copy and updater script).</summary>
        public static string InstalledExePath =>
            Path.Combine(InstallDirectory, "BurgerDeleter.exe");

        /// <summary>Download staging file for in-place swap after exit.</summary>
        public static string PendingUpdateExePath =>
            Path.Combine(InstallDirectory, "BurgerDeleter_new.exe");

        static string UpdateScriptPath =>
            Path.Combine(InstallDirectory, "burger_update.ps1");

        /// <summary>
        /// Target exe derived from <see cref="AppDomain.CurrentDomain.BaseDirectory"/> — not the
        /// running module path — so updates don’t depend on the locked in-memory image path.
        /// </summary>
        public static string CanonicalExecutablePath =>
            Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BurgerDeleter.exe"));

        /// <summary>PowerShell helper: copy staged exe over install, launch, self-delete.</summary>
        private static readonly string UpdateScriptBody =
            "Start-Sleep -Seconds 4\r\n" +
            "$target = \"$env:LOCALAPPDATA\\BurgerDeleter\\BurgerDeleter.exe\"\r\n" +
            "$new = \"$env:LOCALAPPDATA\\BurgerDeleter\\BurgerDeleter_new.exe\"\r\n" +
            "if (Test-Path $new) {\r\n" +
            "    Copy-Item -Path $new -Destination $target -Force\r\n" +
            "    Remove-Item -Path $new -Force\r\n" +
            "    Start-Process -FilePath $target\r\n" +
            "}\r\n" +
            "Remove-Item -Path $MyInvocation.MyCommand.Path -Force\r\n";

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

        /// <summary>
        /// Writes <c>burger_update.ps1</c> under <see cref="InstallDirectory"/> and starts it hidden.
        /// The app must exit immediately so the script can copy <see cref="PendingUpdateExePath"/>
        /// over <see cref="InstalledExePath"/> without file-lock issues.
        /// </summary>
        public static void LaunchPendingUpdateAndExit()
        {
            Directory.CreateDirectory(InstallDirectory);

            File.WriteAllText(UpdateScriptPath, UpdateScriptBody);

            Process.Start(new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{UpdateScriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow  = true,
            });
        }
    }
}
