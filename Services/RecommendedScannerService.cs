using System.IO;
using System.Text.RegularExpressions;
using BurgerDeleter.Models;

namespace BurgerDeleter.Services
{
    public class RecommendedScannerService
    {
        public async Task<List<DriveItem>> ScanAsync(
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var results = new List<DriveItem>();

                progress?.Report("Checking unused applications...");
                results.AddRange(FindUnusedApps(ct));

                progress?.Report("Checking temp and cache folders...");
                results.AddRange(FindLargeCacheFolders(ct));

                progress?.Report("Checking Downloads folder...");
                results.AddRange(FindLargeDownloads(ct));

                progress?.Report("Checking for duplicate Steam installs...");
                results.AddRange(FindDuplicateSteamInstalls(ct));

                progress?.Report("Checking Windows Update cache...");
                results.AddRange(FindWindowsUpdateCache(ct));

                progress?.Report("Scanning for large log files...");
                results.AddRange(FindLargeLogFiles(ct));

                results.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
                return results;
            }, ct);
        }

        // ===== 1. UNUSED APPLICATIONS (90+ days) =====

        private static List<DriveItem> FindUnusedApps(CancellationToken ct)
        {
            var results = new List<DriveItem>();
            var cutoff  = DateTime.Now.AddDays(-90);

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                local,
            };

            var skipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Temp", "Microsoft", "Google", "Mozilla", "Packages",
                "pip", "npm", "npm-cache", "Yarn", "nuget", "NuGet",
            };

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                ct.ThrowIfCancellationRequested();

                IEnumerable<DirectoryInfo> dirs;
                try { dirs = new DirectoryInfo(root).EnumerateDirectories(); }
                catch { continue; }

                foreach (var appDir in dirs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (skipNames.Contains(appDir.Name)) continue;

                    // Each app directory gets its own try/catch so one bad path
                    // doesn't abort the whole scan.
                    try
                    {
                        if (ShouldSkipPath(appDir.FullName)) continue;

                        var newestAccess = EnumerateFilesSafe(appDir.FullName, "*.exe", ct)
                            .Select(f =>
                            {
                                try { return new FileInfo(f).LastAccessTime; }
                                catch { return DateTime.MinValue; }
                            })
                            .DefaultIfEmpty(DateTime.MinValue)
                            .Max();

                        if (newestAccess == DateTime.MinValue || newestAccess >= cutoff)
                            continue;

                        long size = GetDirectorySizeSafe(appDir.FullName, ct);
                        if (size < 50 * 1_048_576L) continue;

                        int daysAgo = (int)(DateTime.Now - newestAccess).TotalDays;

                        var item = new DriveItem
                        {
                            Name                   = appDir.Name,
                            FullPath               = appDir.FullName,
                            SizeBytes              = size,
                            Category               = ItemCategory.Application,
                            RecommendationReason   = $"Last opened {daysAgo} days ago — {FormatSize(size)}",
                            RecommendationCategory = "Unused App",
                        };
                        SafetyClassifierService.Classify(item);
                        results.Add(item);
                    }
                    catch { }   // skip any single app dir that throws
                }
            }

            return results;
        }

        // ===== 2. LARGE TEMP / CACHE FOLDERS =====

        private static List<DriveItem> FindLargeCacheFolders(CancellationToken ct)
        {
            var results = new List<DriveItem>();
            var local   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            const long minBytes = 100 * 1_048_576L;

            var candidates = new[]
            {
                Path.GetTempPath(),
                Path.Combine(local,   "Microsoft", "Windows", "INetCache"),
                Path.Combine(local,   "Google",    "Chrome",  "User Data", "Default", "Cache"),
                Path.Combine(local,   "Microsoft", "Edge",    "User Data", "Default", "Cache"),
                Path.Combine(local,   "pip",       "cache"),
                Path.Combine(local,   "npm-cache"),
                Path.Combine(local,   "Yarn",      "Cache"),
                Path.Combine(roaming, "npm-cache"),
                Path.Combine(local,   "Microsoft", "Windows", "Explorer"),
            };

            foreach (var folder in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(folder)) continue;

                // Each candidate has its own guard + try/catch
                try
                {
                    if (ShouldSkipPath(folder)) continue;

                    long size = GetDirectorySizeSafe(folder, ct);
                    if (size < minBytes) continue;

                    var di   = new DirectoryInfo(folder);
                    var item = new DriveItem
                    {
                        Name                   = di.Name,
                        FullPath               = folder,
                        SizeBytes              = size,
                        Category               = ItemCategory.Folder,
                        RecommendationReason   = "Cache/temp files — safe to clear, will rebuild automatically.",
                        RecommendationCategory = "Cache",
                    };
                    SafetyClassifierService.Classify(item);
                    results.Add(item);
                }
                catch { }
            }

            return results;
        }

        // ===== 3. LARGE FILES IN DOWNLOADS =====

        private static List<DriveItem> FindLargeDownloads(CancellationToken ct)
        {
            var results   = new List<DriveItem>();
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            if (!Directory.Exists(downloads)) return results;
            if (ShouldSkipPath(downloads))    return results;

            const long minBytes = 200 * 1_048_576L;

            // Top-level files
            try
            {
                foreach (var file in Directory.EnumerateFiles(downloads))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.Length < minBytes) continue;

                        int daysOld = (int)(DateTime.Now - fi.LastWriteTime).TotalDays;
                        var item    = new DriveItem
                        {
                            Name                   = fi.Name,
                            FullPath               = fi.FullName,
                            SizeBytes              = fi.Length,
                            Category               = ItemCategory.OtherFile,
                            LastModified           = fi.LastWriteTime,
                            RecommendationReason   = $"Large file sitting in Downloads for {daysOld} days.",
                            RecommendationCategory = "Downloads",
                        };
                        SafetyClassifierService.Classify(item);
                        results.Add(item);
                    }
                    catch { }
                }
            }
            catch { }

            // Top-level subdirectories (one try/catch per subdir)
            IEnumerable<DirectoryInfo> subDirs;
            try { subDirs = new DirectoryInfo(downloads).EnumerateDirectories(); }
            catch { return results; }

            foreach (var subDir in subDirs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (ShouldSkipPath(subDir.FullName)) continue;

                    long size = GetDirectorySizeSafe(subDir.FullName, ct);
                    if (size < minBytes) continue;

                    int daysOld = (int)(DateTime.Now - subDir.LastWriteTime).TotalDays;
                    var item    = new DriveItem
                    {
                        Name                   = subDir.Name,
                        FullPath               = subDir.FullName,
                        SizeBytes              = size,
                        Category               = ItemCategory.Folder,
                        LastModified           = subDir.LastWriteTime,
                        RecommendationReason   = $"Large folder sitting in Downloads for {daysOld} days.",
                        RecommendationCategory = "Downloads",
                    };
                    SafetyClassifierService.Classify(item);
                    results.Add(item);
                }
                catch { }
            }

            return results;
        }

        // ===== 4. DUPLICATE STEAM INSTALLS =====

        private static List<DriveItem> FindDuplicateSteamInstalls(CancellationToken ct)
        {
            var results = new List<DriveItem>();

            var steamRoots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                @"C:\Steam", @"D:\Steam", @"E:\Steam", @"F:\Steam",
            };

            var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var steamRoot in steamRoots)
            {
                ct.ThrowIfCancellationRequested();
                var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdfPath)) continue;

                var libraryPaths = ParseSteamLibraryPaths(vdfPath, steamRoot);

                foreach (var libPath in libraryPaths)
                {
                    var commonPath = Path.Combine(libPath, "steamapps", "common");
                    if (!Directory.Exists(commonPath)) continue;

                    IEnumerable<string> gameDirs;
                    try { gameDirs = Directory.EnumerateDirectories(commonPath); }
                    catch { continue; }

                    foreach (var gameDir in gameDirs)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (ShouldSkipPath(gameDir)) continue;

                            var name = Path.GetFileName(gameDir);
                            if (!byName.TryGetValue(name, out var paths))
                                byName[name] = paths = new List<string>();
                            if (!paths.Contains(gameDir, StringComparer.OrdinalIgnoreCase))
                                paths.Add(gameDir);
                        }
                        catch { }
                    }
                }
            }

            foreach (var (gameName, paths) in byName)
            {
                if (paths.Count < 2) continue;

                foreach (var path in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        long size = GetDirectorySizeSafe(path, ct);
                        var item  = new DriveItem
                        {
                            Name                   = gameName,
                            FullPath               = path,
                            SizeBytes              = size,
                            Category               = ItemCategory.Game,
                            GameLauncher           = "Steam",
                            RecommendationReason   = "Possible duplicate install detected.",
                            RecommendationCategory = "Duplicate",
                        };
                        SafetyClassifierService.Classify(item);
                        results.Add(item);
                    }
                    catch { }
                }
            }

            return results;
        }

        private static List<string> ParseSteamLibraryPaths(string vdfPath, string steamRoot)
        {
            var paths = new List<string> { steamRoot };
            try
            {
                var content = File.ReadAllText(vdfPath);
                var matches = Regex.Matches(content, @"""path""\s+""([^""]+)""");
                foreach (Match m in matches)
                {
                    var p = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(p) && !paths.Contains(p, StringComparer.OrdinalIgnoreCase))
                        paths.Add(p);
                }
            }
            catch { }
            return paths;
        }

        // ===== 5. WINDOWS UPDATE CACHE =====

        private static List<DriveItem> FindWindowsUpdateCache(CancellationToken ct)
        {
            var results = new List<DriveItem>();
            const string path = @"C:\Windows\SoftwareDistribution\Download";
            const long minBytes = 500 * 1_048_576L;

            if (!Directory.Exists(path)) return results;
            if (ShouldSkipPath(path))    return results;

            try
            {
                long size = GetDirectorySizeSafe(path, ct);
                if (size < minBytes) return results;

                var item = new DriveItem
                {
                    Name                   = @"SoftwareDistribution\Download",
                    FullPath               = path,
                    SizeBytes              = size,
                    Category               = ItemCategory.Folder,
                    RecommendationReason   = "Old Windows update files — safe to clear after updates complete.",
                    RecommendationCategory = "Update Cache",
                };
                SafetyClassifierService.Classify(item);
                results.Add(item);
            }
            catch { }

            return results;
        }

        // ===== 6. LARGE LOG FILES =====

        private static List<DriveItem> FindLargeLogFiles(CancellationToken ct)
        {
            var results = new List<DriveItem>();
            var local   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            const long minBytes = 50 * 1_048_576L;

            foreach (var root in new[] { local, roaming })
            {
                if (!Directory.Exists(root)) continue;
                if (ShouldSkipPath(root))    continue;

                // Use safe recursive enumeration to avoid following junctions
                foreach (var filePath in EnumerateFilesSafe(root, "*.log", ct))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var fi = new FileInfo(filePath);
                        if (fi.Length < minBytes) continue;

                        var item = new DriveItem
                        {
                            Name                   = fi.Name,
                            FullPath               = filePath,
                            SizeBytes              = fi.Length,
                            Category               = ItemCategory.OtherFile,
                            LastModified           = fi.LastWriteTime,
                            RecommendationReason   = "Large log file — safe to delete.",
                            RecommendationCategory = "Log File",
                        };
                        SafetyClassifierService.Classify(item);
                        results.Add(item);
                    }
                    catch { }
                }
            }

            return results;
        }

        // ===== HELPERS =====

        /// <summary>
        /// Returns true if the path is a junction point or symbolic link
        /// (FileAttributes.ReparsePoint).  Returns true on any access error
        /// so callers skip the path rather than crash.
        /// </summary>
        private static bool IsJunctionOrSymlink(string path)
        {
            try
            {
                return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch { return true; }
        }

        // Windows creates several AppData junctions that loop back on themselves.
        // Enumerate them explicitly so they are never descended into.
        private static readonly string[] _alwaysSkipSegments =
        [
            Path.Combine("AppData", "Local",   "Application Data"),
            Path.Combine("AppData", "Local",   "Temporary Internet Files"),
            Path.Combine("AppData", "Roaming", "Microsoft", "Windows", "Start Menu"),
        ];

        /// <summary>
        /// Returns true when a directory should never be enumerated:
        /// junction/symlink, path contains the word "junction", or matches
        /// one of the well-known Windows looping AppData reparse points.
        /// </summary>
        private static bool ShouldSkipPath(string path)
        {
            try
            {
                if (path.Contains("junction", StringComparison.OrdinalIgnoreCase))
                    return true;

                foreach (var seg in _alwaysSkipSegments)
                    if (path.Contains(seg, StringComparison.OrdinalIgnoreCase))
                        return true;

                return IsJunctionOrSymlink(path);
            }
            catch { return true; }
        }

        /// <summary>
        /// Recursively enumerates files matching <paramref name="pattern"/> under
        /// <paramref name="root"/>, skipping junction points and bad paths instead of
        /// throwing or looping infinitely. Each directory access is individually guarded.
        /// </summary>
        private static IEnumerable<string> EnumerateFilesSafe(
            string root, string pattern, CancellationToken ct)
        {
            if (ShouldSkipPath(root)) yield break;

            // Files in this directory
            IEnumerable<string> files = Enumerable.Empty<string>();
            try { files = Directory.EnumerateFiles(root, pattern); }
            catch { }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                yield return f;
            }

            // Recurse into each subdirectory individually
            IEnumerable<string> dirs = Enumerable.Empty<string>();
            try { dirs = Directory.EnumerateDirectories(root); }
            catch { }

            foreach (var dir in dirs)
            {
                ct.ThrowIfCancellationRequested();

                bool skip = false;
                try { skip = ShouldSkipPath(dir); } catch { skip = true; }
                if (skip) continue;

                foreach (var f in EnumerateFilesSafe(dir, pattern, ct))
                    yield return f;
            }
        }

        /// <summary>
        /// Calculates the total size of a directory tree without following
        /// junction points. Every individual subdirectory is guarded.
        /// </summary>
        private static long GetDirectorySizeSafe(string path, CancellationToken ct)
        {
            if (ShouldSkipPath(path)) return 0;

            long total = 0;

            // Files at this level
            try
            {
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    ct.ThrowIfCancellationRequested();
                    try { total += new FileInfo(file).Length; }
                    catch { }
                }
            }
            catch { }

            // Subdirectories — each one individually guarded
            IEnumerable<string> subDirs = Enumerable.Empty<string>();
            try { subDirs = Directory.EnumerateDirectories(path); }
            catch { }

            foreach (var sub in subDirs)
            {
                ct.ThrowIfCancellationRequested();

                bool skip = false;
                try { skip = ShouldSkipPath(sub); } catch { skip = true; }
                if (skip) continue;

                total += GetDirectorySizeSafe(sub, ct);
            }

            return total;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
            if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F0} MB";
            return $"{bytes / 1_024.0:F0} KB";
        }
    }
}
