using System.Globalization;
using System.Windows.Media.Imaging;
using Spawn.Contracts;
using Spawn.Host.Helpers;

namespace Spawn.Host.ViewModels;

public sealed class RecentUnlockItemViewModel
{
    public string GameId { get; }
    public string GameTitle { get; }
    public string ApiName { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public bool IsRare { get; }
    public string RareBadgeText { get; }
    public string UnlockedTimeText { get; }
    public BitmapImage? IconImage { get; }

    public RecentUnlockItemViewModel(
        string gameId,
        string gameTitle,
        AchievementDefinitionDto? definition,
        AchievementUnlockDto unlock)
    {
        GameId = gameId;
        GameTitle = gameTitle;
        ApiName = unlock.ApiName;
        DisplayName = !string.IsNullOrWhiteSpace(definition?.DisplayName) ? definition!.DisplayName : unlock.ApiName;
        Description = !string.IsNullOrWhiteSpace(definition?.Description)
            ? definition!.Description
            : (definition?.HiddenSpoilerDescription ?? "已解锁成就");
        IsRare = definition?.IsRare ?? false;
        RareBadgeText = definition?.GlobalPercent is double pct
            ? string.Create(CultureInfo.InvariantCulture, $"稀有 {pct:0.0}%")
            : "稀有";
        UnlockedTimeText = unlock.UnlockedUtc.HasValue
            ? unlock.UnlockedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "刚刚解锁";
        IconImage = ImageLoaderHelper.LoadFrozen(definition?.IconPath, maxDecodeWidth: 112);
    }
}
