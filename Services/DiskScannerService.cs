using System.IO;
using BurgerDeleter.Models;

namespace BurgerDeleter.Services
{
    public class DiskScannerService
    {
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm", ".ts", ".m2ts", ".vob"
        };

        private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".img"
        };

        private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi"
        };

        // These are the folders we scan INTO, not show as single items.
        // Each subfolder inside these becomes its own row (e.g. "Call of Duty" not "Program Files (x86)")
        private static readonly string[] DrillIntoFolders =
        {
            @"C:\Program Files",
            @"C:\Program Files (x86)",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            @"C:\Games",
            @"C:\SteamLibrary",
            @"D:\Games",
            @"D:\SteamLibrary",
        };

        private static readonly HashSet<string> SkipTopLevel = new(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "System Volume Information", "$Recycle.Bin",
            "$WinREAgent", "Recovery", "PerfLogs",
            "Program Files", "Program Files (x86)",
            "Users", "ProgramData"
        };

        public async Task<List<DriveItem>> ScanAsync(
            string rootPath,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                var results = new List<DriveItem>();

                // Drill into known program/app locations -- each subfolder = one item
                foreach (var drillPath in DrillIntoFolders)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(drillPath)) continue;

                    progress?.Report($"Scanning {drillPath}...");

                    try
                    {
                        foreach (var subDir in new DirectoryInfo(drillPath).EnumerateDirectories())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (ShouldSkipDirectory(subDir)) continue;

                            progress?.Report($"Measuring {subDir.Name}...");
                            long size = GetDirectorySizeSafe(subDir, cancellationToken);

                            if (size < 1_048_576) continue; // skip under 1 MB

                            var item = new DriveItem
                            {
                                Name      = subDir.Name,
                                FullPath  = subDir.FullName,
                                SizeBytes = size,
                                Category  = ItemCategory.Folder
                            };
                            SafetyClassifierService.Classify(item);
                            results.Add(item);
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                }

                // Any other large top-level folders not already covered
                try
                {
                    foreach (var subDir in new DirectoryInfo(rootPath).EnumerateDirectories())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (SkipTopLevel.Contains(subDir.Name)) continue;
                        if (ShouldSkipDirectory(subDir)) continue;

                        if (DrillIntoFolders.Any(d =>
                            string.Equals(d, subDir.FullName, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        if (subDir.FullName.StartsWith(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            StringComparison.OrdinalIgnoreCase))
                            continue;

                        progress?.Report($"Measuring {subDir.Name}...");
                        long size = GetDirectorySizeSafe(subDir, cancellationToken);
                        if (size < 104_857_600) continue;

                        var item = new DriveItem
                        {
                            Name      = subDir.Name,
                            FullPath  = subDir.FullName,
                            SizeBytes = size,
                            Category  = ItemCategory.Folder
                        };
                        SafetyClassifierService.Classify(item);
                        results.Add(item);
                    }
                }
                catch (UnauthorizedAccessException) { }

                results.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
                return results;

            }, cancellationToken);
        }

        public async Task<List<DriveItem>> ScanLargeFilesAsync(
            string rootPath,
            long minSizeBytes = 104_857_600,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                var results = new List<DriveItem>();
                ScanFilesRecursive(new DirectoryInfo(rootPath), results, minSizeBytes, progress, cancellationToken);
                results.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
                return results;
            }, cancellationToken);
        }

        private void ScanFilesRecursive(
            DirectoryInfo dir,
            List<DriveItem> results,
            long minSizeBytes,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (ShouldSkipDirectory(dir)) return;

            try
            {
                progress?.Report($"Scanning {dir.FullName}...");

                foreach (var file in dir.EnumerateFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    if (file.Length < minSizeBytes) continue;

                    var ext = file.Extension;
                    ItemCategory category;

                    if (VideoExtensions.Contains(ext))
                        category = ItemCategory.VideoFile;
                    else if (ArchiveExtensions.Contains(ext))
                        category = ItemCategory.ArchiveFile;
                    else if (InstallerExtensions.Contains(ext))
                        category = ItemCategory.Application;
                    else
                        continue;

                    var fileItem = new DriveItem
                    {
                        Name         = file.Name,
                        FullPath     = file.FullName,
                        SizeBytes    = file.Length,
                        Category     = category,
                        LastModified = file.LastWriteTime
                    };
                    SafetyClassifierService.Classify(fileItem);
                    results.Add(fileItem);
                }

                foreach (var subDir in dir.EnumerateDirectories())
                    ScanFilesRecursive(subDir, results, minSizeBytes, progress, ct);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        private static long GetDirectorySizeSafe(DirectoryInfo dir, CancellationToken ct)
        {
            long total = 0;
            try
            {
                foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    try { total += file.Length; }
                    catch { }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return total;
        }

        private static bool ShouldSkipDirectory(DirectoryInfo dir)
        {
            var skipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Windows", "System Volume Information", "$Recycle.Bin",
                "$WinREAgent", "Recovery", "PerfLogs"
            };

            if (skipNames.Contains(dir.Name)) return true;
            if (dir.Attributes.HasFlag(FileAttributes.System)) return true;
            return false;
        }
    }
}
