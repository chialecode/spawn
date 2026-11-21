using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spawn.Contracts;
using Spawn.Host.Helpers;

namespace Spawn.Host.ViewModels;

public sealed partial class AchievementRowViewModel : ObservableObject
{
    private readonly Func<AchievementRowViewModel, bool, Task> _onSetUnlockedAsync;
    private readonly Func<AchievementRowViewModel, Task> _onPreviewToastAsync;

    public string GameId { get; }
    public string GameTitle { get; }
    public GameAchievementItemDto Item { get; }

    [ObservableProperty]
    private bool _globalRevealSpoilers;

    [ObservableProperty]
    private bool _isRowSpoilerRevealed;

    public string ApiName => Item.Definition.ApiName;
    public string DisplayName => Item.Definition.DisplayName;
    public bool IsUnlocked => Item.IsUnlocked;
    public bool IsLocked => !Item.IsUnlocked;
    public bool IsHidden => Item.Definition.IsHidden;
    public bool IsRare => Item.Definition.IsRare;
    public bool HasGlobalPercent => Item.Definition.GlobalPercent.HasValue;

    public string GlobalPercentText => Item.Definition.GlobalPercent.HasValue
        ? string.Create(CultureInfo.InvariantCulture, $"全球 {Item.Definition.GlobalPercent.Value:0.0}%")
        : string.Empty;

    public string RareBadgeText => Item.Definition.GlobalPercent.HasValue
        ? string.Create(CultureInfo.InvariantCulture, $"稀有 {Item.Definition.GlobalPercent.Value:0.0}%")
        : "稀有成就";

    public string UnlockedStatusText => Item.IsUnlocked
        ? (Item.UnlockedUtc.HasValue
            ? Item.UnlockedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "已解锁")
        : "未解锁";

    public bool CanClickToRevealSpoiler =>
        !IsUnlocked && IsHidden && !GlobalRevealSpoilers && !IsRowSpoilerRevealed;

    public string EffectiveDescription
    {
        get
        {
            string desc = Item.GetEffectiveDescription(GlobalRevealSpoilers || IsRowSpoilerRevealed);
            return string.IsNullOrWhiteSpace(desc)
                ? (IsHidden ? "隐藏成就（官方未公开具体文字描述）" : "达成游戏内指定条件即可解锁")
                : desc;
        }
    }

    public BitmapImage? IconImage { get; }
    public double IconOpacity { get; }

    public AchievementRowViewModel(
        string gameId,
        string gameTitle,
        GameAchievementItemDto item,
        bool globalRevealSpoilers,
        Func<AchievementRowViewModel, bool, Task> onSetUnlockedAsync,
        Func<AchievementRowViewModel, Task> onPreviewToastAsync)
    {
        GameId = gameId;
        GameTitle = gameTitle;
        Item = item;
        _globalRevealSpoilers = globalRevealSpoilers;
        _onSetUnlockedAsync = onSetUnlockedAsync;
        _onPreviewToastAsync = onPreviewToastAsync;

        if (item.IsUnlocked)
        {
            IconImage = ImageLoaderHelper.LoadFrozen(item.Definition.IconPath ?? item.Definition.IconGrayPath, maxDecodeWidth: 112);
            IconOpacity = 1.0;
        }
        else
        {
            BitmapImage? gray = ImageLoaderHelper.LoadFrozen(item.Definition.IconGrayPath, maxDecodeWidth: 112);
            if (gray is not null)
            {
                IconImage = gray;
                IconOpacity = 0.92;
            }
            else
            {
                IconImage = ImageLoaderHelper.LoadFrozen(item.Definition.IconPath, maxDecodeWidth: 112);
                IconOpacity = 0.45;
            }
        }
    }

    partial void OnGlobalRevealSpoilersChanged(bool value)
    {
        OnPropertyChanged(nameof(CanClickToRevealSpoiler));
        OnPropertyChanged(nameof(EffectiveDescription));
    }

    partial void OnIsRowSpoilerRevealedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanClickToRevealSpoiler));
        OnPropertyChanged(nameof(EffectiveDescription));
    }

    [RelayCommand]
    private void RevealRowSpoiler()
    {
        IsRowSpoilerRevealed = true;
    }

    [RelayCommand]
    private Task MarkUnlockedAsync() => _onSetUnlockedAsync(this, true);

    [RelayCommand]
    private Task ResetUnlockedAsync() => _onSetUnlockedAsync(this, false);

    [RelayCommand]
    private Task PreviewToastAsync() => _onPreviewToastAsync(this);
}
