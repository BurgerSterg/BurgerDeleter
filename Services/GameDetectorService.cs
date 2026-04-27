using System.IO;
using System.Text.RegularExpressions;
using BurgerDeleter.Models;

namespace BurgerDeleter.Services
{
    /// <summary>
    /// Detects installed games directly from launcher library files.
    /// No launcher required to be running. Reads raw manifest/config files.
    /// </summary>
    public class GameDetectorService
    {
        public async Task<List<DriveItem>> DetectAllGamesAsync(
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var games = new List<DriveItem>();

                progress?.Report("Checking Steam...");
                games.AddRange(GetSteamGames(ct));

                progress?.Report("Checking Epic Games...");
                games.AddRange(GetEpicGames(ct));

                progress?.Report("Checking GOG Galaxy...");
                games.AddRange(GetGogGames(ct));

                progress?.Report("Checking EA App...");
                games.AddRange(GetEaGames(ct));

                progress?.Report("Checking Riot Games...");
                games.AddRange(GetRiotGames(ct));

                progress?.Report("Checking Xbox / Game Pass...");
                games.AddRange(GetXboxGames(ct));

                games.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
                return games;
            }, ct);
        }

        // ===== STEAM =====

        private List<DriveItem> GetSteamGames(CancellationToken ct)
        {
            var games = new List<DriveItem>();

            // Default Steam path
            var steamPaths = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                @"C:\Steam",
                @"D:\Steam"
            };

            foreach (var steamRoot in steamPaths)
            {
                ct.ThrowIfCancellationRequested();
                var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdfPath)) continue;

                // Parse library paths from libraryfolders.vdf
                var libraryPaths = ParseSteamLibraryPaths(vdfPath, steamRoot);

                foreach (var libraryPath in libraryPaths)
                {
                    ct.ThrowIfCancellationRequested();
                    var steamappsPath = Path.Combine(libraryPath, "steamapps");
                    if (!Directory.Exists(steamappsPath)) continue;

                    foreach (var acfFile in Directory.EnumerateFiles(steamappsPath, "appmanifest_*.acf"))
                    {
                        ct.ThrowIfCancellationRequested();
                        var game = ParseSteamManifest(acfFile, steamappsPath);
                        if (game != null) games.Add(game);
                    }
                }
            }

            return games;
        }

        private static List<string> ParseSteamLibraryPaths(string vdfPath, string steamRoot)
        {
            var paths = new List<string> { steamRoot };
            try
            {
                var content = File.ReadAllText(vdfPath);
                // Match "path" "D:\\Games\\Steam" style entries
                var matches = Regex.Matches(content, @"""path""\s+""([^""]+)""");
                foreach (Match m in matches)
                {
                    var path = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(path) && !paths.Contains(path))
                        paths.Add(path);
                }
            }
            catch { }
            return paths;
        }

        private static DriveItem? ParseSteamManifest(string acfPath, string steamappsPath)
        {
            try
            {
                var content = File.ReadAllText(acfPath);

                var nameMatch = Regex.Match(content, @"""name""\s+""([^""]+)""");
                var installDirMatch = Regex.Match(content, @"""installdir""\s+""([^""]+)""");

                if (!nameMatch.Success || !installDirMatch.Success) return null;

                var gameName   = nameMatch.Groups[1].Value;
                var installDir = installDirMatch.Groups[1].Value;
                var fullPath   = Path.Combine(steamappsPath, "common", installDir);

                if (!Directory.Exists(fullPath)) return null;

                long size = GetDirectorySizeSafe(fullPath);

                return new DriveItem
                {
                    Name          = gameName,
                    FullPath      = fullPath,
                    SizeBytes     = size,
                    Category      = ItemCategory.Game,
                    GameLauncher  = "Steam"
                };
            }
            catch { return null; }
        }

        // ===== EPIC GAMES =====

        private List<DriveItem> GetEpicGames(CancellationToken ct)
        {
            var games = new List<DriveItem>();
            var manifestsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");

            if (!Directory.Exists(manifestsPath)) return games;

            foreach (var itemFile in Directory.EnumerateFiles(manifestsPath, "*.item"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var content = File.ReadAllText(itemFile);

                    var nameMatch        = Regex.Match(content, @"""DisplayName""\s*:\s*""([^""]+)""");
                    var installPathMatch = Regex.Match(content, @"""InstallLocation""\s*:\s*""([^""]+)""");

                    if (!nameMatch.Success || !installPathMatch.Success) continue;

                    var gameName    = nameMatch.Groups[1].Value;
                    var installPath = installPathMatch.Groups[1].Value.Replace(@"\\", @"\");

                    if (!Directory.Exists(installPath)) continue;

                    games.Add(new DriveItem
                    {
                        Name         = gameName,
                        FullPath     = installPath,
                        SizeBytes    = GetDirectorySizeSafe(installPath),
                        Category     = ItemCategory.Game,
                        GameLauncher = "Epic Games"
                    });
                }
                catch { }
            }

            return games;
        }

        // ===== GOG GALAXY =====

        private List<DriveItem> GetGogGames(CancellationToken ct)
        {
            var games     = new List<DriveItem>();
            var gogDbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GOG.com", "Galaxy", "storage");

            if (!Directory.Exists(gogDbPath)) return games;

            // GOG stores game folders in numbered subdirectories
            foreach (var gameDir in Directory.EnumerateDirectories(gogDbPath))
            {
                ct.ThrowIfCancellationRequested();
                var infoFile = Path.Combine(gameDir, "galaxy_2.0.db"); // simplified detection
                // GOG game folders themselves are the install dirs
                // Real detection requires SQLite reads -- Cursor will help extend this
                // For now surface large numbered dirs as potential GOG games
                var di = new DirectoryInfo(gameDir);
                long size = GetDirectorySizeSafe(gameDir);
                if (size > 104_857_600) // only if >100MB
                {
                    games.Add(new DriveItem
                    {
                        Name         = di.Name,
                        FullPath     = gameDir,
                        SizeBytes    = size,
                        Category     = ItemCategory.Game,
                        GameLauncher = "GOG Galaxy"
                    });
                }
            }

            return games;
        }

        // ===== EA APP =====

        private List<DriveItem> GetEaGames(CancellationToken ct)
        {
            var games = new List<DriveItem>();

            // EA App stores manifests here
            var manifestsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EA Desktop", "InstallData");

            if (!Directory.Exists(manifestsPath)) return games;

            foreach (var manifestDir in Directory.EnumerateDirectories(manifestsPath))
            {
                ct.ThrowIfCancellationRequested();
                var installerDataFile = Path.Combine(manifestDir, "__Installer", "installerdata.xml");
                if (!File.Exists(installerDataFile)) continue;

                try
                {
                    var content  = File.ReadAllText(installerDataFile);
                    var nameMatch = Regex.Match(content, @"<gameTitle[^>]*>([^<]+)</gameTitle>");
                    var pathMatch = Regex.Match(content, @"<installPath[^>]*>([^<]+)</installPath>");

                    if (!nameMatch.Success) continue;

                    var gameName    = nameMatch.Groups[1].Value;
                    var installPath = pathMatch.Success
                        ? pathMatch.Groups[1].Value
                        : manifestDir;

                    if (!Directory.Exists(installPath)) installPath = manifestDir;

                    games.Add(new DriveItem
                    {
                        Name         = gameName,
                        FullPath     = installPath,
                        SizeBytes    = GetDirectorySizeSafe(installPath),
                        Category     = ItemCategory.Game,
                        GameLauncher = "EA App"
                    });
                }
                catch { }
            }

            return games;
        }

        // ===== RIOT GAMES =====

        private static readonly HashSet<string> RiotNonGameFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Riot Client",
            "Riot Vanguard",
        };

        private List<DriveItem> GetRiotGames(CancellationToken ct)
        {
            var games = new List<DriveItem>();

            var searchRoots = new[]
            {
                @"C:\Riot Games",
                @"C:\Program Files\Riot Games",
                @"C:\Program Files (x86)\Riot Games",
            };

            foreach (var root in searchRoots)
            {
                if (!Directory.Exists(root)) continue;

                foreach (var gameDir in Directory.EnumerateDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var di = new DirectoryInfo(gameDir);
                        if (RiotNonGameFolders.Contains(di.Name)) continue;

                        long size = GetDirectorySizeSafe(gameDir);

                        var item = new DriveItem
                        {
                            Name         = di.Name,
                            FullPath     = gameDir,
                            SizeBytes    = size,
                            Category     = ItemCategory.Game,
                            GameLauncher = "Riot Games"
                        };
                        SafetyClassifierService.Classify(item);
                        games.Add(item);
                    }
                    catch { }
                }
            }

            return games;
        }

        // ===== XBOX / GAME PASS =====

        private List<DriveItem> GetXboxGames(CancellationToken ct)
        {
            var games = new List<DriveItem>();

            // --- C:\XboxGames --- primary Game Pass install location
            const string xboxGamesRoot = @"C:\XboxGames";
            if (Directory.Exists(xboxGamesRoot))
            {
                foreach (var gameDir in Directory.EnumerateDirectories(xboxGamesRoot))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var di   = new DirectoryInfo(gameDir);
                        long size = GetDirectorySizeSafe(gameDir);

                        var item = new DriveItem
                        {
                            Name         = di.Name,
                            FullPath     = gameDir,
                            SizeBytes    = size,
                            Category     = ItemCategory.Game,
                            GameLauncher = "Xbox"
                        };
                        SafetyClassifierService.Classify(item);
                        games.Add(item);
                    }
                    catch { }
                }
            }

            // --- C:\Program Files\WindowsApps --- UWP packages
            // Skip Microsoft.* system packages unless they contain "Xbox" or are large (>1 GB = likely a game)
            const string windowsApps = @"C:\Program Files\WindowsApps";
            if (Directory.Exists(windowsApps))
            {
                IEnumerable<string> dirs;
                try { dirs = Directory.EnumerateDirectories(windowsApps); }
                catch { dirs = Enumerable.Empty<string>(); }

                foreach (var pkgDir in dirs)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var di   = new DirectoryInfo(pkgDir);
                        var name = di.Name;

                        bool isMicrosoft = name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);
                        bool hasXbox     = name.Contains("Xbox",  StringComparison.OrdinalIgnoreCase) ||
                                           name.Contains("Game",  StringComparison.OrdinalIgnoreCase);

                        // Always skip known system / non-game Microsoft packages
                        if (isMicrosoft && !hasXbox)
                        {
                            // Only include if very large (>1 GB) — likely a game
                            long quickSize = GetDirectorySizeSafe(pkgDir);
                            if (quickSize < 1_073_741_824L) continue;

                            // Strip the version+architecture suffix for a readable name
                            var displayName = SanitizePackageName(name);
                            var item = new DriveItem
                            {
                                Name         = displayName,
                                FullPath     = pkgDir,
                                SizeBytes    = quickSize,
                                Category     = ItemCategory.Game,
                                GameLauncher = "Xbox"
                            };
                            SafetyClassifierService.Classify(item);
                            games.Add(item);
                        }
                        else if (!isMicrosoft || hasXbox)
                        {
                            long size = GetDirectorySizeSafe(pkgDir);
                            if (size < 104_857_600) continue; // skip tiny packages (<100 MB)

                            var item = new DriveItem
                            {
                                Name         = SanitizePackageName(name),
                                FullPath     = pkgDir,
                                SizeBytes    = size,
                                Category     = ItemCategory.Game,
                                GameLauncher = "Xbox"
                            };
                            SafetyClassifierService.Classify(item);
                            games.Add(item);
                        }
                    }
                    catch { }
                }
            }

            return games;
        }

        // Turn "2KGames.NBA2K26_1.0.0.0_x64__abc123" into "NBA 2K26"
        private static string SanitizePackageName(string packageFolderName)
        {
            // Strip publisher prefix (everything before first dot), version, architecture, and token suffix
            var parts = packageFolderName.Split('_');
            var basePart = parts[0]; // e.g. "2KGames.NBA2K26" or "Microsoft.XboxApp"

            var dotIdx = basePart.IndexOf('.');
            var name = dotIdx >= 0 ? basePart[(dotIdx + 1)..] : basePart;

            // Insert spaces before capital letters that follow lowercase letters (PascalCase -> words)
            name = Regex.Replace(name, @"(?<=[a-z0-9])(?=[A-Z])", " ");

            return name.Trim();
        }

        // ===== HELPERS =====

        private static long GetDirectorySizeSafe(string path)
        {
            long total = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch { }
                }
            }
            catch { }
            return total;
        }
    }
}
