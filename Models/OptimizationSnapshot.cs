using System.Diagnostics;

namespace BurgerDeleter.Models
{
    public class OptimizationSnapshot
    {
        public string GameName { get; set; } = string.Empty;

        /// <summary>Install folder of a detected game; used to match its processes by path.</summary>
        public string? GameInstallPath { get; set; }

        public List<string> AppliedSteps { get; set; } = new();

        public string? PreviousPowerSchemeGuid { get; set; }

        /// <summary>True if we activated the built-in High Performance plan as fallback.</summary>
        public bool UsedHighPerformanceFallback { get; set; }

        public List<RegistryDwordBackup> GameBarRegistry { get; set; } = new();

        /// <summary>Game Mode, scheduler and other DWORD tweaks.</summary>
        public List<RegistryDwordBackup> RegistryTweaks { get; set; } = new();

        /// <summary>String-valued tweaks (mouse acceleration, per-game GPU preference).</summary>
        public List<RegistryStringBackup> RegistryStrings { get; set; } = new();

        /// <summary>True if mouse settings were changed and must be re-applied live on restore.</summary>
        public bool MouseSettingsChanged { get; set; }

        public List<ProcessPriorityBackup> ProcessPriorities { get; set; } = new();

        public List<InterfaceNagleBackup> NagleInterfaces { get; set; } = new();

        public NetshTcpGlobalBackup? NetshTcpBefore { get; set; }
    }

    public class RegistryDwordBackup
    {
        public RegistryHiveKind Hive { get; set; }

        public string KeyPath { get; set; } = string.Empty;
        public string ValueName { get; set; } = string.Empty;

        /// <summary>Original DWORD when <see cref="ValueExisted"/> is true.</summary>
        public int? OriginalValue { get; set; }

        public bool ValueExisted { get; set; }
    }

    public class RegistryStringBackup
    {
        public RegistryHiveKind Hive { get; set; }

        public string KeyPath { get; set; } = string.Empty;
        public string ValueName { get; set; } = string.Empty;

        public string? OriginalValue { get; set; }

        public bool ValueExisted { get; set; }
    }

    public enum RegistryHiveKind
    {
        CurrentUser,
        LocalMachine
    }

    public class ProcessPriorityBackup
    {
        public int ProcessId { get; set; }

        /// <summary><see cref="System.Diagnostics.ProcessPriorityClass"/> name.</summary>
        public string OriginalPriority { get; set; } = nameof(ProcessPriorityClass.Normal);
    }

    public class InterfaceNagleBackup
    {
        /// <summary>Subkey name under Tcpip\Parameters\Interfaces (the GUID folder).</summary>
        public string InterfaceSubKey { get; set; } = string.Empty;

        public bool HadTcpAckFrequency { get; set; }
        public int? OriginalTcpAckFrequency { get; set; }

        public bool HadTCPNoDelay { get; set; }
        public int? OriginalTCPNoDelay { get; set; }
    }

    public class NetshTcpGlobalBackup
    {
        /// <summary>autotuninglevel: disabled, highlyrestricted, restricted, normal, experimental</summary>
        public string AutoTuneLevel { get; set; } = "normal";

        /// <summary>chimney: automatic, enabled, disabled</summary>
        public string Chimney { get; set; } = "automatic";

        /// <summary>rss: enabled, disabled</summary>
        public string Rss { get; set; } = "enabled";
    }
}
