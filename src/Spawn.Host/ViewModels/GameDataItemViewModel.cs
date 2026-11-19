using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spawn.Contracts;
using Spawn.Host.Helpers;

namespace Spawn.Host.ViewModels;

public sealed partial class GameDataItemViewModel : ObservableObject
{
    private readonly Action<GameDataItemViewModel> _onOpenLocation;
    private readonly Action<GameDataItemViewModel> _onCopyPath;
    private readonly Action _onSelectionChanged;

    public GameDataEntryDto Entry { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string EntryId => Entry.EntryId;
    public GameDataCategory Category => Entry.Category;
    public string SourceBadgeText => Entry.EffectiveSourceLabel;
    public string PathOrRegistryKey => Entry.PathOrRegistryKey;
    public string? Description => Entry.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Entry.Description);
    public bool Exists => Entry.Exists;
    public bool IsRegistryEntry => Entry.IsRegistryEntry;
    public bool CanOpenDirectory => !Entry.IsRegistryEntry;
    public bool IsSafeToClean => Entry.IsSafeToClean;
    public string? SafetyNote => Entry.SafetyNote;
    public bool HasSafetyNote => !string.IsNullOrWhiteSpace(Entry.SafetyNote);

    public string StatusSummaryText
    {
        get
        {
            if (!Entry.Exists)
            {
                return "尚未生成";
            }

            if (Entry.IsRegistryEntry)
            {
                return Entry.FileCount > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"已存在 · {Entry.FileCount} 项键值")
                    : "已存在 · 注册表项";
            }

            if (Entry.FileCount > 0)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{Entry.FileCount} 个文件 · {UiFormatters.FormatByteSize(Entry.TotalBytes)}");
            }

            return UiFormatters.FormatByteSize(Entry.TotalBytes);
        }
    }

    public GameDataItemViewModel(
        GameDataEntryDto entry,
        Action<GameDataItemViewModel> onOpenLocation,
        Action<GameDataItemViewModel> onCopyPath,
        Action onSelectionChanged)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _onOpenLocation = onOpenLocation;
        _onCopyPath = onCopyPath;
        _onSelectionChanged = onSelectionChanged;
        _isSelected = entry.IsSafeToClean && (entry.DefaultSelected || entry.Exists);
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!Entry.IsSafeToClean && value)
        {
            _isSelected = false;
            return;
        }

        _onSelectionChanged();
    }

    [RelayCommand]
    private void OpenLocation() => _onOpenLocation(this);

    [RelayCommand]
    private void CopyPath() => _onCopyPath(this);
}

public sealed class GameDataCategoryGroupViewModel
{
    public GameDataCategory Category { get; }
    public string CategoryTitle { get; }
    public ObservableCollection<GameDataItemViewModel> Items { get; } = [];

    public int TotalCount => Items.Count;
    public int ExistingCount => Items.Count(i => i.Exists);

    public string SummaryCountText =>
        string.Create(CultureInfo.InvariantCulture, $"共 {TotalCount} 处路径 · 已落盘 {ExistingCount} 处");

    public GameDataCategoryGroupViewModel(GameDataCategory category, IEnumerable<GameDataItemViewModel> items)
    {
        Category = category;
        CategoryTitle = UiFormatters.FormatGameDataCategoryTitle(category);
        foreach (var item in items)
        {
            Items.Add(item);
        }
    }
}
