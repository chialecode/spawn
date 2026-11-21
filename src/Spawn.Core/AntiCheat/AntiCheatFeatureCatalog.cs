using Spawn.Contracts;

namespace Spawn.Core.AntiCheat;

/// <summary>
/// Match strategy used by a single directory/file anti-cheat signature rule.
/// </summary>
public enum SignatureMatchMode
{
    Exact = 0,
    Prefix = 1,
    Suffix = 2,
}

/// <summary>
/// Static filesystem indicator rule evaluated during depth-bounded game directory inspection.
/// </summary>
public sealed record DirectorySignatureRule(
    string Pattern,
    AntiCheatTier Tier,
    string Signature,
    bool MatchDirectory,
    SignatureMatchMode MatchMode = SignatureMatchMode.Exact)
{
    public bool Matches(string entryName, bool isDirectory)
    {
        if (MatchDirectory != isDirectory || string.IsNullOrEmpty(entryName))
        {
            return false;
        }

        return MatchMode switch
        {
            SignatureMatchMode.Exact => string.Equals(entryName, Pattern, StringComparison.OrdinalIgnoreCase),
            SignatureMatchMode.Prefix => entryName.Length > Pattern.Length
                && entryName.StartsWith(Pattern, StringComparison.OrdinalIgnoreCase),
            SignatureMatchMode.Suffix => entryName.Length > Pattern.Length
                && entryName.EndsWith(Pattern, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}

/// <summary>
/// Centralized anti-cheat signature tables: depth-6 directory rules, residual companion daemons,
/// session kernel anti-cheat drivers, Vanguard identifiers, and known external protected game executables.
/// </summary>
public static class AntiCheatFeatureCatalog
{
    /// <summary>Maximum subdirectory recursion depth for Layer 1 game directory signature scanning.</summary>
    public const int DefaultScanMaxDepth = 6;

    /// <summary>Riot Vanguard user-mode client/service process (active when Vanguard-protected games are running).</summary>
    public const string VanguardClientProcessName = "vgc.exe";

    /// <summary>Riot Vanguard boot-time kernel driver image name.</summary>
    public const string VanguardKernelDriverFileName = "vgk.sys";

    /// <summary>Riot Vanguard SCM kernel driver service name.</summary>
    public const string VanguardKernelServiceName = "vgk";

    public static IReadOnlyList<DirectorySignatureRule> DirectorySignatureRules { get; } =
    [
        // Tier 3 — Ring-0 kernel anti-cheat (ACE, Vanguard, Ricochet, EA AntiCheat, NetEase, FACEIT)
        new("SGuard64.exe", AntiCheatTier.Tier3, "ACE:SGuard64", MatchDirectory: false),
        new("SGuardSvc64.exe", AntiCheatTier.Tier3, "ACE:SGuardSvc64", MatchDirectory: false),
        new("ACE-Tray.exe", AntiCheatTier.Tier3, "ACE:ACE-Tray", MatchDirectory: false),
        new("ACE-BASE.sys", AntiCheatTier.Tier3, "ACE:ACE-BASE.sys", MatchDirectory: false),
        new("ACE-GAME.sys", AntiCheatTier.Tier3, "ACE:ACE-GAME.sys", MatchDirectory: false),
        new("ACE-PBC-Game64.dll", AntiCheatTier.Tier3, "ACE:ACE-PBC", MatchDirectory: false),
        new("AntiCheatExpert", AntiCheatTier.Tier3, "ACE:AntiCheatExpert", MatchDirectory: true),
        new("ACE-Guard Client", AntiCheatTier.Tier3, "ACE:GuardClient", MatchDirectory: true),
        new("TenProtect", AntiCheatTier.Tier3, "Tencent:TenProtect", MatchDirectory: true),
        new("TesSafe.sys", AntiCheatTier.Tier3, "Tencent:TesSafe.sys", MatchDirectory: false),
        new("vgk.sys", AntiCheatTier.Tier3, "Vanguard:vgk.sys", MatchDirectory: false),
        new("vgc.exe", AntiCheatTier.Tier3, "Vanguard:vgc.exe", MatchDirectory: false),
        new("vgtray.exe", AntiCheatTier.Tier3, "Vanguard:vgtray.exe", MatchDirectory: false),
        new("Riot Vanguard", AntiCheatTier.Tier3, "Vanguard:RiotVanguard", MatchDirectory: true),
        new("randgrid.sys", AntiCheatTier.Tier3, "Ricochet:randgrid.sys", MatchDirectory: false),
        new("EAAntiCheat.", AntiCheatTier.Tier3, "EAAC:EAAntiCheat", MatchDirectory: false, SignatureMatchMode.Prefix),
        new("NeacSafe64.sys", AntiCheatTier.Tier3, "NetEase:NeacSafe64.sys", MatchDirectory: false),
        new("NeacSafe.sys", AntiCheatTier.Tier3, "NetEase:NeacSafe.sys", MatchDirectory: false),
        new("NeacClient.exe", AntiCheatTier.Tier3, "NetEase:NeacClient", MatchDirectory: false),
        new("FACEITClient.exe", AntiCheatTier.Tier3, "FACEIT:Client", MatchDirectory: false),
        new("faceit.sys", AntiCheatTier.Tier3, "FACEIT:faceit.sys", MatchDirectory: false),

        // Tier 2 — User/light-kernel multiplayer & hybrid anti-cheat (EAC, BattlEye, GameGuard, XIGNCODE3, NGS, PB)
        new("EasyAntiCheat", AntiCheatTier.Tier2, "EasyAntiCheat", MatchDirectory: true),
        new("EasyAntiCheat.exe", AntiCheatTier.Tier2, "EasyAntiCheat", MatchDirectory: false),
        new("EasyAntiCheat_EOS.exe", AntiCheatTier.Tier2, "EasyAntiCheat:EOS", MatchDirectory: false),
        new("EasyAntiCheat_EOS.sys", AntiCheatTier.Tier2, "EasyAntiCheat:EOS.sys", MatchDirectory: false),
        new("EasyAntiCheat.sys", AntiCheatTier.Tier2, "EasyAntiCheat:sys", MatchDirectory: false),
        new("EasyAntiCheat_EOS_Setup.exe", AntiCheatTier.Tier2, "EasyAntiCheat:EOS_Setup", MatchDirectory: false),
        new("EasyAntiCheat_Setup.exe", AntiCheatTier.Tier2, "EasyAntiCheat:Setup", MatchDirectory: false),
        new("EasyAntiCheat_x64.dll", AntiCheatTier.Tier2, "EasyAntiCheat:x64", MatchDirectory: false),
        new("EasyAntiCheat_x86.dll", AntiCheatTier.Tier2, "EasyAntiCheat:x86", MatchDirectory: false),
        new("start_protected_game.exe", AntiCheatTier.Tier2, "EasyAntiCheat:start_protected_game", MatchDirectory: false),
        new("eac_launcher.exe", AntiCheatTier.Tier2, "EasyAntiCheat:Launcher", MatchDirectory: false),
        new("BattlEye", AntiCheatTier.Tier2, "BattlEye", MatchDirectory: true),
        new("BEDaisy.sys", AntiCheatTier.Tier2, "BattlEye:BEDaisy.sys", MatchDirectory: false),
        new("_BE.exe", AntiCheatTier.Tier2, "BattlEye:BE_Launcher", MatchDirectory: false, SignatureMatchMode.Suffix),
        new("BEService.exe", AntiCheatTier.Tier2, "BattlEye:BEService", MatchDirectory: false),
        new("BEService_x64.exe", AntiCheatTier.Tier2, "BattlEye:BEService_x64", MatchDirectory: false),
        new("BEService_x86.exe", AntiCheatTier.Tier2, "BattlEye:BEService_x86", MatchDirectory: false),
        new("BEClient_x64.dll", AntiCheatTier.Tier2, "BattlEye:BEClient_x64", MatchDirectory: false),
        new("BEClient.dll", AntiCheatTier.Tier2, "BattlEye:BEClient", MatchDirectory: false),
        new("GameGuard", AntiCheatTier.Tier2, "nProtect:GameGuard", MatchDirectory: true),
        new("GameGuard.des", AntiCheatTier.Tier2, "nProtect:GameGuard", MatchDirectory: false),
        new("GameMon64.des", AntiCheatTier.Tier2, "nProtect:GameMon64", MatchDirectory: false),
        new("GameMon.des", AntiCheatTier.Tier2, "nProtect:GameMon", MatchDirectory: false),
        new("npggNT64.des", AntiCheatTier.Tier2, "nProtect:npggNT64", MatchDirectory: false),
        new("xhunter1.sys", AntiCheatTier.Tier2, "XIGNCODE3:xhunter1.sys", MatchDirectory: false),
        new("x3.xem", AntiCheatTier.Tier2, "XIGNCODE3:x3.xem", MatchDirectory: false),
        new("xxd-0.xem", AntiCheatTier.Tier2, "XIGNCODE3:xxd", MatchDirectory: false),
        new("BlackCipher64.aes", AntiCheatTier.Tier2, "Nexon:BlackCipher64", MatchDirectory: false),
        new("BlackCipher.aes", AntiCheatTier.Tier2, "Nexon:BlackCipher", MatchDirectory: false),
        new("BlackCall64.aes", AntiCheatTier.Tier2, "Nexon:BlackCall64", MatchDirectory: false),
        new("NexonGameSecurity", AntiCheatTier.Tier2, "Nexon:NGS", MatchDirectory: true),
        new("PnkBstrA.exe", AntiCheatTier.Tier2, "PunkBuster:PnkBstrA", MatchDirectory: false),
        new("PnkBstrB.exe", AntiCheatTier.Tier2, "PunkBuster:PnkBstrB", MatchDirectory: false),
        new("denuvo-anti-cheat.sys", AntiCheatTier.Tier2, "DenuvoAntiCheat", MatchDirectory: false),
        new("DenuvoAntiCheat", AntiCheatTier.Tier2, "DenuvoAntiCheat", MatchDirectory: true),

        // Tier 1 — Online single-player / gacha kernel protection
        new("HoYoKProtect.sys", AntiCheatTier.Tier1, "HoYoKProtect", MatchDirectory: false),
        new("mhyprot2.sys", AntiCheatTier.Tier1, "HoYoProtect:mhyprot2", MatchDirectory: false),
        new("mhyprot3.sys", AntiCheatTier.Tier1, "HoYoProtect:mhyprot3", MatchDirectory: false),
        new("KRSDKExternal.exe", AntiCheatTier.Tier1, "KuroSDK", MatchDirectory: false),
    ];

    /// <summary>
    /// Residual anti-cheat companion daemon processes that must terminate before the 15-second cooldown starts
    /// (and must be absent before any trainer session starts).
    /// </summary>
    public static IReadOnlySet<string> ResidualGuardProcessNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SGuard64.exe",
        "SGuardSvc64.exe",
        "ACE-Tray.exe",
        "EasyAntiCheat.exe",
        "EasyAntiCheat_EOS.exe",
        "start_protected_game.exe",
        "BEService.exe",
        "BEService_x64.exe",
        "BEService_x86.exe",
        "EAAntiCheat.GameService.exe",
        "EAAntiCheat.GameServiceLauncher.exe",
        "NeacClient.exe",
        "GameMon.des",
        "GameMon64.des",
        "GameMon.exe",
        "GameMon64.exe",
        "BlackCipher64.aes",
        "BlackCipher.aes",
        "BlackCall64.aes",
        "xxd-0.xem",
        "PnkBstrA.exe",
        "PnkBstrB.exe",
    };

    /// <summary>
    /// Session-scoped kernel anti-cheat driver filenames loaded during gameplay and unloaded on exit.
    /// Intentionally excludes boot-time persistent <c>vgk.sys</c>, which is handled separately via Vanguard policy.
    /// </summary>
    public static IReadOnlySet<string> SessionAntiCheatDriverFileNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "EasyAntiCheat.sys",
        "EasyAntiCheat_EOS.sys",
        "BEDaisy.sys",
        "ACE-BASE.sys",
        "ACE-GAME.sys",
        "ACE-SSC-DRV64.sys",
        "ACE-DRV64.sys",
        "TesSafe.sys",
        "SGuard64.sys",
        "randgrid.sys",
        "HoYoKProtect.sys",
        "mhyprot2.sys",
        "mhyprot3.sys",
        "xhunter1.sys",
        "NeacSafe64.sys",
        "NeacSafe.sys",
        "EAAntiCheat.sys",
        "Navagio.sys",
        "denuvo-anti-cheat.sys",
        "FACEIT.sys",
        "ESEA.sys",
        "FairPlayKD.sys",
        "BlackCipher64.sys",
        "BlackCall64.sys",
        "npggnt64.sys",
    };

    /// <summary>
    /// Active SCM driver service names mapped to canonical session kernel anti-cheat driver display names,
    /// used when the service's registry <c>ImagePath</c> is inaccessible to standard users.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SessionAntiCheatServiceMap { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EasyAntiCheat"] = "EasyAntiCheat.sys",
            ["EasyAntiCheat_EOS"] = "EasyAntiCheat_EOS.sys",
            ["EasyAntiCheat_EOSSys"] = "EasyAntiCheat_EOS.sys",
            ["BEDaisy"] = "BEDaisy.sys",
            ["ACE-BASE"] = "ACE-BASE.sys",
            ["ACE-GAME"] = "ACE-GAME.sys",
            ["ACE-SSC-DRV64"] = "ACE-SSC-DRV64.sys",
            ["ACE-DRV64"] = "ACE-DRV64.sys",
            ["TesSafe"] = "TesSafe.sys",
            ["SGuard64"] = "SGuard64.sys",
            ["randgrid"] = "randgrid.sys",
            ["HoYoKProtect"] = "HoYoKProtect.sys",
            ["mhyprot2"] = "mhyprot2.sys",
            ["mhyprot3"] = "mhyprot3.sys",
            ["xhunter1"] = "xhunter1.sys",
            ["NeacSafe64"] = "NeacSafe64.sys",
            ["NeacSafe"] = "NeacSafe.sys",
            ["EAAntiCheat"] = "EAAntiCheat.sys",
            ["Navagio"] = "Navagio.sys",
            ["denuvo-anti-cheat"] = "denuvo-anti-cheat.sys",
            ["FACEIT"] = "FACEIT.sys",
            ["ESEA"] = "ESEA.sys",
            ["FairPlayKD"] = "FairPlayKD.sys",
        };

    /// <summary>
    /// Well-known external Tier 2/3 protected game executables monitored by the 2-second watchdog even if
    /// the game was never imported into SPAWN's local library.
    /// </summary>
    public static IReadOnlyDictionary<string, (string DisplayName, AntiCheatTier Tier)> KnownProtectedGameExecutables { get; } =
        new Dictionary<string, (string DisplayName, AntiCheatTier Tier)>(StringComparer.OrdinalIgnoreCase)
        {
            ["cs2.exe"] = ("Counter-Strike 2", AntiCheatTier.Tier2),
            ["VALORANT.exe"] = ("VALORANT", AntiCheatTier.Tier3),
            ["VALORANT-Win64-Shipping.exe"] = ("VALORANT", AntiCheatTier.Tier3),
            ["FortniteClient-Win64-Shipping.exe"] = ("Fortnite", AntiCheatTier.Tier2),
            ["r5apex.exe"] = ("Apex Legends", AntiCheatTier.Tier2),
            ["TslGame.exe"] = ("PUBG: BATTLEGROUNDS", AntiCheatTier.Tier3),
            ["TslGame_BE.exe"] = ("PUBG: BATTLEGROUNDS", AntiCheatTier.Tier3),
            ["RainbowSix.exe"] = ("Tom Clancy's Rainbow Six Siege", AntiCheatTier.Tier2),
            ["RainbowSix_BE.exe"] = ("Tom Clancy's Rainbow Six Siege", AntiCheatTier.Tier2),
            ["destiny2.exe"] = ("Destiny 2", AntiCheatTier.Tier2),
            ["League of Legends.exe"] = ("League of Legends", AntiCheatTier.Tier3),
            ["LOSTARK.exe"] = ("Lost Ark", AntiCheatTier.Tier2),
            ["NarakaBladepoint.exe"] = ("NARAKA: BLADEPOINT", AntiCheatTier.Tier3),
            ["RustClient.exe"] = ("Rust", AntiCheatTier.Tier2),
            ["DeadByDaylight-Win64-Shipping.exe"] = ("Dead by Daylight", AntiCheatTier.Tier2),
            ["cod.exe"] = ("Call of Duty", AntiCheatTier.Tier3),
            ["BF2042.exe"] = ("Battlefield 2042", AntiCheatTier.Tier3),
            ["helldivers2.exe"] = ("HELLDIVERS 2", AntiCheatTier.Tier2),
            ["DeltaForceClient-Win64-Shipping.exe"] = ("Delta Force", AntiCheatTier.Tier3),
            ["dota2.exe"] = ("Dota 2", AntiCheatTier.Tier2),
            ["Overwatch.exe"] = ("Overwatch 2", AntiCheatTier.Tier2),
            ["EscapeFromTarkov.exe"] = ("Escape from Tarkov", AntiCheatTier.Tier2),
            ["EscapeFromTarkov_BE.exe"] = ("Escape from Tarkov", AntiCheatTier.Tier2),
            ["DayZ_x64.exe"] = ("DayZ", AntiCheatTier.Tier2),
            ["DayZ_BE.exe"] = ("DayZ", AntiCheatTier.Tier2),
        };

    public static bool IsResidualGuardProcessName(string? exeFileName)
    {
        if (string.IsNullOrWhiteSpace(exeFileName))
        {
            return false;
        }

        string name = Path.GetFileName(exeFileName.Trim());
        if (ResidualGuardProcessNames.Contains(name))
        {
            return true;
        }

        // Catch BattlEye launcher stubs (*_BE.exe) and EA AntiCheat service binaries (EAAntiCheat.*.exe)
        if (name.Length > "_BE.exe".Length && name.EndsWith("_BE.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.StartsWith("EAAntiCheat.", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVanguardClientProcessName(string? exeFileName)
    {
        if (string.IsNullOrWhiteSpace(exeFileName))
        {
            return false;
        }

        return string.Equals(Path.GetFileName(exeFileName.Trim()), VanguardClientProcessName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVanguardKernelDriver(string? driverOrServiceName)
    {
        if (string.IsNullOrWhiteSpace(driverOrServiceName))
        {
            return false;
        }

        string fileName = Path.GetFileName(driverOrServiceName.Trim().Trim('"'));
        return string.Equals(fileName, VanguardKernelDriverFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, VanguardKernelServiceName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryMatchSessionAntiCheatDriver(string? driverFileOrPath, out string canonicalDriverName)
    {
        canonicalDriverName = string.Empty;
        if (string.IsNullOrWhiteSpace(driverFileOrPath))
        {
            return false;
        }

        string fileName = Path.GetFileName(driverFileOrPath.Trim().Trim('"'));
        if (SessionAntiCheatDriverFileNames.Contains(fileName))
        {
            canonicalDriverName = fileName;
            return true;
        }

        if (SessionAntiCheatServiceMap.TryGetValue(fileName, out string? mapped))
        {
            canonicalDriverName = mapped;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the executable path directly launches an anti-cheat bootstrapper
    /// (such as <c>start_protected_game.exe</c> or <c>*_BE.exe</c>), preventing offline bypass mode from
    /// treating an anti-cheat-protected launch as an offline single-player launch.
    /// </summary>
    public static bool IsAntiCheatLauncherExecutable(string? executablePathOrRelPath)
    {
        if (string.IsNullOrWhiteSpace(executablePathOrRelPath))
        {
            return false;
        }

        string fileName = Path.GetFileName(executablePathOrRelPath.Trim().Trim('"'));
        return IsResidualGuardProcessName(fileName)
            || IsVanguardClientProcessName(fileName)
            || string.Equals(fileName, "eac_launcher.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps an AreWeAntiCheatYet anti-cheat system label to its minimum static <see cref="AntiCheatTier"/>.
    /// </summary>
    public static AntiCheatTier ClassifyAntiCheatSystemName(string? antiCheatName)
    {
        if (string.IsNullOrWhiteSpace(antiCheatName))
        {
            return AntiCheatTier.Tier0;
        }

        string trimmed = antiCheatName.Trim();

        if (trimmed.Contains("Vanguard", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("ACE", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Anti-Cheat Expert", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("AntiCheatExpert", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Ricochet", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("EA AntiCheat", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Javelin", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("NetEase", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Neac", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("FACEIT", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("TenProtect", StringComparison.OrdinalIgnoreCase))
        {
            return AntiCheatTier.Tier3;
        }

        if (trimmed.Contains("HoYo", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("miHoYo", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("mhyprot", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("KuroSDK", StringComparison.OrdinalIgnoreCase))
        {
            return AntiCheatTier.Tier1;
        }

        // Easy Anti-Cheat, BattlEye, VAC, GameGuard, XIGNCODE3, NGS, PunkBuster, Warden, Zakynthos, QB, EQU8, Denuvo AC, etc.
        return AntiCheatTier.Tier2;
    }
}
