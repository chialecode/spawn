namespace Spawn.Host.ViewModels;

public enum PrimaryNavSection
{
    Library = 0,
    Achievements = 1,
    Trainers = 2,
    Settings = 3,
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
}

public enum GameDetailSubTab
{
    Achievements = 0,
    GameData = 1,
    Trainers = 2,
}

public enum AchievementFilterTab
{
    All = 0,
    Unlocked = 1,
    Locked = 2,
    Hidden = 3,
}
