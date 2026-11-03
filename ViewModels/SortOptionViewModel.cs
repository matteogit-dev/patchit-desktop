using CommunityToolkit.Mvvm.ComponentModel;

namespace PatchIt.Client.ViewModels;

public sealed partial class SortOptionViewModel : ObservableObject
{
    public SortOptionViewModel(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool isActive;
}
