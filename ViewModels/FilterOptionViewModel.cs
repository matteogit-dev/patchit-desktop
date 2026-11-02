using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PatchIt.Client.ViewModels;

public sealed partial class FilterOptionViewModel : ObservableObject
{
    public FilterOptionViewModel(string key, string label, Brush accent)
    {
        Key = key;
        Label = label;
        Accent = accent;
    }

    public string Key { get; }
    public string Label { get; }
    public Brush Accent { get; }

    [ObservableProperty]
    private int count;

    [ObservableProperty]
    private bool isActive;
}
