using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PatchIt.Client.Models;
using PatchIt.Client.Services;

namespace PatchIt.Client.ViewModels;

public sealed partial class GameCardViewModel : ObservableObject
{
    public Game Game { get; }

    [ObservableProperty]
    private ImageSource? cover;

    [ObservableProperty]
    private bool hasUpdate;

    [ObservableProperty]
    private bool isDownloaded;

    [ObservableProperty]
    private string installedAt = "";

    [ObservableProperty]
    private string localFilePath = "";

    /// <summary>Cartella effettiva in cui finisce la patch di questo gioco.</summary>
    [ObservableProperty]
    private string downloadFolder = "";

    /// <summary>Vero quando l'utente ha scelto una cartella diversa da quella predefinita.</summary>
    [ObservableProperty]
    private bool hasCustomFolder;

    /// <summary>Cartella del gioco in cui la patch va installata.</summary>
    [ObservableProperty]
    private string installFolder = "";

    /// <summary>Cartella in cui la patch è stata effettivamente copiata.</summary>
    [ObservableProperty]
    private string installedFolder = "";

    /// <summary>Vero se esiste un backup dei file originali da poter ripristinare.</summary>
    [ObservableProperty]
    private bool hasBackup;

    [ObservableProperty]
    private bool isInstalling;

    [ObservableProperty]
    private string installMessage = "";

    [ObservableProperty]
    private IntegrityState integrity;

    [ObservableProperty]
    private string integrityMessage = "";

    /// <summary>Vero mentre l'hash di questo file viene ricalcolato.</summary>
    [ObservableProperty]
    private bool isVerifying;

    public bool CanVerify => IsDownloaded && !IsVerifying;

    public bool HasInstallFolder => InstallFolder.Length > 0;

    public bool IsInstalledInGame => InstalledFolder.Length > 0;

    public bool ShowsInstallMessage => InstallMessage.Length > 0;

    /// <summary>Si può copiare nel gioco solo uno zip scaricato che esiste ancora.</summary>
    public bool CanInstallInGame =>
        IsLocal
        && IsDownloaded
        && !IsInstalling
        && LocalFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        && File.Exists(LocalFilePath);

    public string InstallButtonText => IsInstalledInGame ? "Reinstalla nel gioco" : "Installa nel gioco";

    public string InstallFolderButtonText =>
        HasInstallFolder ? "Cambia cartella del gioco" : "Scegli cartella del gioco";

    public bool ShowsIntegrity => Integrity != IntegrityState.Unknown || IntegrityMessage.Length > 0;

    public Brush IntegrityBrush => Integrity switch
    {
        IntegrityState.Ok => Palette.Teal,
        IntegrityState.Corrupt or IntegrityState.Missing => Palette.Amber,
        _ => Palette.Muted,
    };

    partial void OnInstallFolderChanged(string value)
    {
        OnPropertyChanged(nameof(HasInstallFolder));
        OnPropertyChanged(nameof(InstallFolderButtonText));
    }

    partial void OnInstalledFolderChanged(string value) =>
        OnPropertyChanged(nameof(IsInstalledInGame));

    partial void OnInstallMessageChanged(string value) =>
        OnPropertyChanged(nameof(ShowsInstallMessage));

    partial void OnIsInstallingChanged(bool value) =>
        OnPropertyChanged(nameof(CanInstallInGame));

    partial void OnIntegrityChanged(IntegrityState value) =>
        OnPropertyChanged(nameof(ShowsIntegrity));

    partial void OnIsVerifyingChanged(bool value) =>
        OnPropertyChanged(nameof(CanVerify));

    partial void OnIntegrityMessageChanged(string value) =>
        OnPropertyChanged(nameof(ShowsIntegrity));

    partial void OnLocalFilePathChanged(string value) =>
        OnPropertyChanged(nameof(CanInstallInGame));

    /// <summary>Scaricata e già allineata al catalogo: non c'è nulla da aggiornare.</summary>
    public bool IsUpToDate => IsDownloaded && !HasUpdate;

    public string InstalledDateLabel =>
        DateTime.TryParse(InstalledAt, out var parsed)
            ? parsed.ToString("dd/MM/yyyy HH:mm")
            : InstalledAt;

    public bool HasInstalledDate => IsDownloaded && InstalledDateLabel.Length > 0;

    [ObservableProperty]
    private DownloadState downloadStatus;

    [ObservableProperty]
    private double downloadPercent;

    [ObservableProperty]
    private string downloadMessage = "";

    public bool IsDownloading => DownloadStatus == DownloadState.Downloading;
    public bool IsQueued => DownloadStatus == DownloadState.Queued;
    public bool ShowsDownloadStatus => IsDownloading || DownloadMessage.Length > 0;

    partial void OnDownloadStatusChanged(DownloadState value)
    {
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(ShowsDownloadStatus));
    }

    partial void OnDownloadMessageChanged(string value) =>
        OnPropertyChanged(nameof(ShowsDownloadStatus));

    public GameCardViewModel(Game game)
    {
        Game = game;
        Instructions = game.Instructions
            .Select((text, i) => new InstructionItem(i + 1, text))
            .ToList();
        Changelog = BuildChangelog(null);
    }

    public string Id => Game.Id;
    public string Title => Game.Title;
    public string Version => Game.Version;
    public string Description => Game.Description;

    public bool HasVersion => !string.IsNullOrWhiteSpace(Game.Version);
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
        ? HasUpdate ? "Aggiorna patch" : IsDownloaded ? "Scarica di nuovo" : "Scarica patch"
        : IsExternal ? "Apri link esterno" : "Patch non disponibile";

    public bool HasSteam => !string.IsNullOrEmpty(Game.SteamUrl);
    public string SteamUrl => Game.SteamUrl;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Game.Description);
    public bool HasInstructions => Instructions.Count > 0;
    public bool HasChangelog => Changelog.Count > 0;
    public bool HasDetailsContent => HasDescription || HasInstructions || HasChangelog;

    public IReadOnlyList<InstructionItem> Instructions { get; }
    public IReadOnlyList<ChangelogItem> Changelog { get; private set; }
    public bool HasNewChanges => Changelog.Any(c => c.IsNew);

    /// <summary>Evidenzia le voci di changelog successive alla versione installata.</summary>
    public void SetLocalVersion(string? version)
    {
        Changelog = BuildChangelog(version);
        OnPropertyChanged(nameof(Changelog));
        OnPropertyChanged(nameof(HasNewChanges));
    }

    private IReadOnlyList<ChangelogItem> BuildChangelog(string? localVersion)
    {
        var entries = Game.Changelog;

        return entries
            .Select((entry, i) => (entry, isLast: i == entries.Count - 1))
            .Reverse()
            .Select(x => new ChangelogItem(
                x.entry,
                x.isLast,
                localVersion is not null && VersionComparer.IsNewer(x.entry.Version, localVersion)))
            .ToList();
    }

    partial void OnHasUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(DownloadButtonText));
        OnPropertyChanged(nameof(IsUpToDate));
        OnPropertyChanged(nameof(CanInstallInGame));
    }

    partial void OnIsDownloadedChanged(bool value)
    {
        OnPropertyChanged(nameof(DownloadButtonText));
        OnPropertyChanged(nameof(IsUpToDate));
        OnPropertyChanged(nameof(HasInstalledDate));
        OnPropertyChanged(nameof(CanInstallInGame));
        OnPropertyChanged(nameof(CanVerify));
    }

    partial void OnInstalledAtChanged(string value) =>
        OnPropertyChanged(nameof(HasInstalledDate));

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
