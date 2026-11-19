namespace Spawn.Host.ViewModels;

public enum PrimaryNavSection
{
    Library = 0,
    Achievements = 1,
    Settings = 2,
}

public enum SettingsCategory
{
    GeneralAndLibrary = 0,
    AchievementAndSteamApi = 1,
    OverlayAndSound = 2,
    AppearanceTheme = 3,
    AboutAndVersion = 4,
}

public enum GameDetailSubTab
{
    Achievements = 0,
    GameData = 1,
}

public enum AchievementFilterTab
{
    All = 0,
    Unlocked = 1,
    Locked = 2,
    Hidden = 3,
}
