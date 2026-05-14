using CommunityToolkit.Mvvm.ComponentModel;

namespace OmniPlay.Core.ViewModels.Library;

public sealed partial class LibrarySortOptionItem : ObservableObject
{
    public LibrarySortOptionItem(LibrarySortOption value, string label)
    {
        Value = value;
        Label = label;
    }

    public LibrarySortOption Value { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool isSelected;
}
