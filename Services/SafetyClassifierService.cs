using System.IO;
using BurgerDeleter.Models;

namespace BurgerDeleter.Services
{
    public static class SafetyClassifierService
    {
        private static readonly HashSet<string> DangerSegments = new(StringComparer.OrdinalIgnoreCase)
        {
            "System32", "SysWOW64", "Windows", "WindowsApps"
        };

        private static readonly HashSet<string> DangerNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "boot", "EFI"
        };

        private static readonly HashSet<string> ManagedElsewhereNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Steam", "Epic Games", "XboxGames", "GOG Galaxy",
            "EA Games", "Riot Games", "Battle.net"
        };

        private static readonly string[] CautionNameFragments =
        {
            "NVIDIA", "Microsoft", "Visual Studio", "dotnet", "DirectX", "VCRedist"
        };

        public static void Classify(DriveItem item)
        {
            var path = item.FullPath;
            var name = item.Name;

            // --- Danger ---
            if (DangerNames.Contains(name))
            {
                item.SafetyLevel = SafetyLevel.Danger;
                item.SafetyReason = name switch
                {
                    "Windows" => "Deleting the Windows folder will make the OS completely unbootable.",
                    "boot"    => "The boot folder contains bootloader files; removing it will prevent Windows from starting.",
                    "EFI"     => "The EFI partition holds firmware startup data; deleting it bricks the boot sequence.",
                    _         => "This is a critical system item."
                };
                return;
            }

            if (PathContainsSegment(path, DangerSegments, out var dangerSeg))
            {
                item.SafetyLevel = SafetyLevel.Danger;
                item.SafetyReason = dangerSeg switch
                {
                    "System32"    => "Deleting System32 will make Windows unbootable immediately.",
                    "SysWOW64"    => "SysWOW64 contains 32-bit system libraries required by many applications.",
                    "Windows"     => "Deleting the Windows folder will make the OS completely unbootable.",
                    "WindowsApps" => "WindowsApps contains Microsoft Store app binaries managed by the OS.",
                    _             => "This path contains critical system files."
                };
                return;
            }

            // --- ManagedElsewhere ---
            if (ManagedElsewhereNames.Contains(name))
            {
                item.SafetyLevel = SafetyLevel.ManagedElsewhere;
                item.SafetyReason = "Manage individual games in the Games tab instead.";
                return;
            }

            if (path.StartsWith(@"C:\XboxGames", StringComparison.OrdinalIgnoreCase) ||
                PathContainsSegment(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Steam" }, out _) &&
                path.Contains("Program Files", StringComparison.OrdinalIgnoreCase))
            {
                item.SafetyLevel = SafetyLevel.ManagedElsewhere;
                item.SafetyReason = "Manage individual games in the Games tab instead.";
                return;
            }

            // --- Caution ---
            var appData    = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localApp   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            if (path.StartsWith(appData,    StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(localApp,   StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(commonData, StringComparison.OrdinalIgnoreCase) ||
                path.Contains("ProgramData", StringComparison.OrdinalIgnoreCase))
            {
                item.SafetyLevel = SafetyLevel.Caution;
                item.SafetyReason = "Items in AppData/ProgramData may store app settings or cached data needed at runtime.";
                return;
            }

            foreach (var fragment in CautionNameFragments)
            {
                if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    item.SafetyLevel = SafetyLevel.Caution;
                    item.SafetyReason = fragment switch
                    {
                        "NVIDIA"         => "Removing NVIDIA drivers may break display output until reinstalled.",
                        "Microsoft"      => "Microsoft runtime components may be required by other installed software.",
                        "Visual Studio"  => "Visual Studio components may be shared across multiple workloads.",
                        "dotnet"         => "The .NET runtime is required by many applications on this machine.",
                        "DirectX"        => "DirectX components are required by games and multimedia applications.",
                        "VCRedist"       => "Visual C++ redistributables are required by a wide range of installed programs.",
                        _                => "This component may be a shared dependency."
                    };
                    return;
                }
            }

            // --- Safe ---
            item.SafetyLevel = SafetyLevel.Safe;
            item.SafetyReason = "No critical dependencies detected.";
        }

        private static bool PathContainsSegment(
            string path,
            HashSet<string> segments,
            out string matchedSegment)
        {
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var part in parts)
            {
                if (segments.Contains(part))
                {
                    matchedSegment = part;
                    return true;
                }
            }
            matchedSegment = string.Empty;
            return false;
        }
    }
}
