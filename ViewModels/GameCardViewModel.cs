using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PatchIt.Client.Models;

namespace PatchIt.Client.ViewModels;

public sealed partial class GameCardViewModel : ObservableObject
{
    public Game Game { get; }

    [ObservableProperty]
    private string coverPath = "";

    [ObservableProperty]
    private bool hasUpdate;

    public GameCardViewModel(Game game)
    {
        Game = game;
        Instructions = game.Instructions
            .Select((text, i) => new InstructionItem(i + 1, text))
            .ToList();
        Changelog = game.Changelog
            .Select((entry, i) => (entry, isLast: i == game.Changelog.Count - 1))
            .Reverse()
            .Select(x => new ChangelogItem(x.entry, x.isLast))
            .ToList();
    }

    public string Id => Game.Id;
    public string Title => Game.Title;
    public string Version => Game.Version;
    public string Description => Game.Description;

    public bool HasTags => Game.Tags.Count > 0;
    public string TagsLabel => string.Join("  ·  ", Game.Tags);

    public string UpdatedLabel => FormatDate(Game.UpdatedDate);
    public bool HasUpdatedDate => UpdatedLabel.Length > 0;
    public string UpdatedFull => HasUpdatedDate ? $"Aggiornata il {UpdatedLabel}" : "";

    public string StatusLabel => Game.Status switch
    {
        "complete" => "Completata",
        "progress" => "In corso",
        "paused" => "In pausa",
        _ => "Pianificata",
    };

    public Brush StatusBrush => Game.Status switch
    {
        "complete" => Palette.Teal,
        "progress" => Palette.Amber,
        "paused" => Palette.Violet,
        _ => Palette.Muted,
    };

    public Brush StatusDimBrush => Game.Status switch
    {
        "complete" => Palette.TealDim,
        "progress" => Palette.AmberDim,
        "paused" => Palette.VioletDim,
        _ => Palette.Line,
    };

    public bool ShowProgress => Game.Progress is > 0 and < 100;
    public double ProgressValue => Game.Progress ?? 0;
    public string ProgressLabel => $"{ProgressValue:0.#}% tradotte";

    public bool IsLocal => Game.DownloadType == "local";
    public bool IsExternal => Game.DownloadType == "external";
    public bool IsUnavailable => Game.DownloadType == "none";
    public bool DownloadEnabled => IsLocal || IsExternal;

    public string DownloadButtonText => IsLocal
        ? HasUpdate ? "Aggiorna patch" : "Scarica patch"
        : IsExternal ? "Apri link esterno" : "Patch non disponibile";

    public bool HasSteam => !string.IsNullOrEmpty(Game.SteamUrl);
    public string SteamUrl => Game.SteamUrl;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Game.Description);
    public bool HasInstructions => Instructions.Count > 0;
    public bool HasChangelog => Changelog.Count > 0;

    public IReadOnlyList<InstructionItem> Instructions { get; }
    public IReadOnlyList<ChangelogItem> Changelog { get; }

    partial void OnHasUpdateChanged(bool value) => OnPropertyChanged(nameof(DownloadButtonText));

    private static string FormatDate(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        return DateOnly.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd/MM/yyyy")
            : iso;
    }
}

internal static class Palette
{
    public static readonly Brush Teal = Frozen("#3FA9A4");
    public static readonly Brush Amber = Frozen("#E8A33D");
    public static readonly Brush Violet = Frozen("#A78BFA");
    public static readonly Brush Muted = Frozen("#8A8F98");
    public static readonly Brush TealDim = Frozen("#23524F");
    public static readonly Brush AmberDim = Frozen("#6B5228");
    public static readonly Brush VioletDim = Frozen("#3A2F66");
    public static readonly Brush Line = Frozen("#2A2F3A");

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
