using System.IO;

namespace BurgerDeleter.Services
{
    public record PlannedMove(string SourcePath, string DestinationPath, string Category);

    public class FileOrganizerService
    {
        // Extension → subfolder name
        private static readonly Dictionary<string, string> ExtensionMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [".jpg"]  = "Images",  [".jpeg"] = "Images",  [".png"]  = "Images",
                [".gif"]  = "Images",  [".webp"] = "Images",  [".bmp"]  = "Images",
                [".svg"]  = "Images",

                [".mp4"]  = "Videos",  [".mkv"]  = "Videos",  [".mov"]  = "Videos",
                [".avi"]  = "Videos",  [".wmv"]  = "Videos",  [".flv"]  = "Videos",

                [".mp3"]  = "Audio",   [".wav"]  = "Audio",   [".flac"] = "Audio",
                [".aac"]  = "Audio",   [".ogg"]  = "Audio",

                [".pdf"]  = "Documents", [".doc"]  = "Documents", [".docx"] = "Documents",
                [".xls"]  = "Documents", [".xlsx"] = "Documents", [".ppt"]  = "Documents",
                [".pptx"] = "Documents", [".txt"]  = "Documents",

                [".zip"]  = "Archives", [".rar"]  = "Archives", [".7z"]  = "Archives",
                [".iso"]  = "Archives", [".tar"]  = "Archives", [".gz"]  = "Archives",

                [".exe"]  = "Installers", [".msi"] = "Installers",

                [".py"]   = "Code", [".js"]   = "Code", [".ts"]   = "Code",
                [".cs"]   = "Code", [".html"] = "Code", [".css"]  = "Code",
                [".json"] = "Code", [".xml"]  = "Code",
            };

        /// <summary>
        /// Returns the list of planned moves without touching the filesystem.
        /// </summary>
        public Task<List<PlannedMove>> PreviewAsync(string folderPath, CancellationToken ct = default)
            => Task.Run(() => BuildPlan(folderPath, ct), ct);

        /// <summary>
        /// Executes the moves and returns the number of files successfully moved.
        /// </summary>
        public async Task<int> OrganizeAsync(
            string folderPath,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var plan = await Task.Run(() => BuildPlan(folderPath, ct), ct);
            int moved = 0;

            foreach (var (src, dst, category) in plan)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Moving {Path.GetFileName(src)} → {category}\\");
                try
                {
                    var dir = Path.GetDirectoryName(dst)!;
                    Directory.CreateDirectory(dir);

                    // Avoid overwriting: append (1), (2), … if destination exists
                    var finalDst = UniqueDestination(dst);
                    File.Move(src, finalDst);
                    moved++;
                }
                catch { /* skip files we can't move */ }
            }

            return moved;
        }

        private static List<PlannedMove> BuildPlan(string folderPath, CancellationToken ct)
        {
            var plan = new List<PlannedMove>();

            if (!Directory.Exists(folderPath)) return plan;

            foreach (var filePath in Directory.EnumerateFiles(folderPath))
            {
                ct.ThrowIfCancellationRequested();

                var ext      = Path.GetExtension(filePath);
                var category = ExtensionMap.TryGetValue(ext, out var cat) ? cat : "Other";
                var destDir  = Path.Combine(folderPath, category);
                var destFile = Path.Combine(destDir, Path.GetFileName(filePath));

                // Don't plan to move something that's already in the right place
                if (string.Equals(
                        Path.GetDirectoryName(filePath), destDir,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                plan.Add(new PlannedMove(filePath, destFile, category));
            }

            return plan;
        }

        private static string UniqueDestination(string path)
        {
            if (!File.Exists(path)) return path;

            var dir  = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext  = Path.GetExtension(path);
            int n    = 1;

            string candidate;
            do { candidate = Path.Combine(dir, $"{name} ({n++}){ext}"); }
            while (File.Exists(candidate));

            return candidate;
        }
    }
}
