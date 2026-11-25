namespace Spawn.Host.ViewModels;

public enum PrimaryNavSection
{
    Library = 0,
    Achievements = 1,
    Saves = 2,
    Trainers = 3,
    Settings = 4,
}

public enum SettingsCategory
{
    GeneralAndLibrary = 0,
    AchievementAndSteamApi = 1,
    OverlayAndSound = 2,
    AppearanceTheme = 3,
    AboutAndVersion = 4,
    TrainerAndSandbox = 5,
    Updates = 6,
    SavesAndCloudSync = 7,
}

public enum GameDetailSubTab
{
    Achievements = 0,
    Saves = 1,
    GameData = 2,
    Trainers = 3,
}

public enum AchievementFilterTab
{
    All = 0,
    Unlocked = 1,
    Locked = 2,
    Hidden = 3,
}
