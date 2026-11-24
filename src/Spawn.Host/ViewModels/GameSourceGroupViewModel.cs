using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Spawn.Host.ViewModels;

public sealed partial class GameSourceGroupViewModel : ObservableObject
{
    private int _lastNotifiedCount;

    public string GroupKey { get; }
    public string BaseTitle { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<GameListItemViewModel> Items { get; } = [];

    public int Count => Items.Count;
    public bool HasItems => Items.Count > 0;
    public string HeaderTitle => $"{BaseTitle}";

    public GameSourceGroupViewModel(string groupKey, string baseTitle, bool initiallyExpanded = true)
    {
        GroupKey = groupKey;
        BaseTitle = baseTitle;
        _isExpanded = initiallyExpanded;
        Items.CollectionChanged += (_, _) =>
        {
            int currentCount = Items.Count;
            if (currentCount == _lastNotifiedCount)
            {
                return;
            }

            bool previousHasItems = _lastNotifiedCount > 0;
            _lastNotifiedCount = currentCount;
            OnPropertyChanged(nameof(Count));
            if ((currentCount > 0) != previousHasItems)
            {
                OnPropertyChanged(nameof(HasItems));
            }
        };
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
