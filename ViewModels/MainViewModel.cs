using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PatchIt.Client.Models;
using PatchIt.Client.Services;

namespace PatchIt.Client.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly CatalogService _catalog = new();
    private readonly CoverCacheService _covers = new();
    private readonly DownloadService _downloader = new();
    private readonly LibraryService _library = new();
    private readonly SettingsService _settings = new();

    private List<GameCardViewModel> _all = [];
    private bool _loading;

    [ObservableProperty]
    private ObservableCollection<GameCardViewModel> games = [];

    [ObservableProperty]
    private GameCardViewModel? selectedGame;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private string activeFilter = "all";

    [ObservableProperty]
    private string sortMode = "default";

    [ObservableProperty]
    private string currentView = "library";

    [ObservableProperty]
    private bool isLoading = true;

    [ObservableProperty]
    private bool isReady;

    [ObservableProperty]
    private bool isEmpty;

    [ObservableProperty]
    private bool isDetailOpen;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private bool isDownloading;

    [ObservableProperty]
    private double downloadProgress;

    [ObservableProperty]
    private string downloadMessage = "";

    [ObservableProperty]
    private string downloadDirectory = "";

    [ObservableProperty]
    private string statusMessage = "";

    public MainViewModel()
    {
        Filters =
        [
            new("all", "Tutti", Palette.Amber),
            new("complete", "Completate", Palette.Teal),
            new("progress", "In corso", Palette.Amber),
            new("paused", "In pausa", Palette.Violet),
            new("planned", "Pianificate", Palette.Muted),
            new("downloaded", "Scaricati", Palette.Teal),
        ];

        SortOptions =
        [
            new("default", "Predefinito"),
            new("name", "A-Z"),
            new("updated", "Recenti"),
        ];

        UpdateActiveSort();
    }

    public ObservableCollection<FilterOptionViewModel> Filters { get; }

    public ObservableCollection<SortOptionViewModel> SortOptions { get; }

    public ObservableCollection<DownloadHistoryItem> DownloadHistory { get; } = [];

    public int GameCount => Games.Count;

    public int UpdateCount => _all.Count(c => c.HasUpdate);

    public bool HasDownloadHistory => DownloadHistory.Count > 0;

    public bool HasSelection => SelectedGame is not null;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool IsLibraryVisible => CurrentView is "library" or "updates";

    public bool IsSettingsVisible => CurrentView == "settings";

    public string LibraryTitle => CurrentView == "updates" ? "Aggiornamenti" : "Libreria";

    public string LibrarySubtitle => CurrentView == "updates"
        ? "Patch con una nuova versione disponibile"
        : "Sfoglia il catalogo delle traduzioni";

    public string EmptyTitle => CurrentView == "updates"
        ? "Tutto aggiornato"
        : "Nessun titolo trovato";

    public string EmptyMessage => CurrentView == "updates"
        ? "Non ci sono patch da aggiornare."
        : "Prova a cambiare ricerca o filtro.";

    public string AppVersion
    {
        get
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            return version is null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    partial void OnSelectedGameChanged(GameCardViewModel? value)
    {
        DownloadMessage = "";
        DownloadProgress = 0;
        OnPropertyChanged(nameof(HasSelection));
        if (value is null) IsDetailOpen = false;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnStatusMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnActiveFilterChanged(string value)
    {
        UpdateActiveFilter();
        ApplyFilter();
    }

    partial void OnSortModeChanged(string value)
    {
        UpdateActiveSort();
        ApplyFilter();
    }

    partial void OnCurrentViewChanged(string value)
    {
        OnPropertyChanged(nameof(IsLibraryVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(LibraryTitle));
        OnPropertyChanged(nameof(LibrarySubtitle));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));

        // ricalcola "file non trovato" ogni volta che si apre Impostazioni
        if (value == "settings") RebuildHistory();
    }

    [RelayCommand]
    private void SetFilter(string filter)
    {
        if (CurrentView != "library") CurrentView = "library";
        ActiveFilter = filter;
    }

    [RelayCommand]
    private void SetSort(string sort) => SortMode = sort;

    [RelayCommand]
    private void Navigate(string view)
    {
        switch (view)
        {
            case "library":
                if (ActiveFilter == "updates") ActiveFilter = "all";
                CurrentView = "library";
                break;

            case "updates":
                CurrentView = "updates";
                ActiveFilter = "updates";
                break;

            case "settings":
                CurrentView = "settings";
                break;
        }
    }

    [RelayCommand]
    private void OpenDetail()
    {
        if (SelectedGame is not null) IsDetailOpen = true;
    }

    [RelayCommand]
    private void CloseDetail() => IsDetailOpen = false;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            _library.Load();
            _settings.Load();
            DownloadDirectory = _settings.DownloadDirectory;

            var games = await _catalog.LoadAsync();
            _all = games.Select(g => new GameCardViewModel(g)).ToList();
            var localEntries = _library.GetAll().ToDictionary(e => e.Id);

            foreach (var card in _all)
            {
                localEntries.TryGetValue(card.Id, out var entry);
                var localVersion = entry?.Version;

                card.IsDownloaded = localVersion is not null;
                card.HasUpdate = localVersion is not null && VersionComparer.IsNewer(card.Version, localVersion);
                card.SetLocalVersion(localVersion);
            }

            IsReady = true;
            UpdateActiveFilter();
            ApplyFilter();
            RaiseCounters();
            RebuildHistory();

            foreach (var card in _all)
                _ = LoadCoverAsync(card);
        }
        catch (Exception)
        {
            ErrorMessage = "Impossibile caricare il catalogo. Controlla la connessione e riprova.";
        }
        finally
        {
            IsLoading = false;
            _loading = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsDownloading) return;
        if (SelectedGame is not { } card) return;

        if (card.IsExternal)
        {
            OpenUrl(card.Game.DownloadUrl);
            return;
        }

        if (!card.IsLocal) return;

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadMessage = "Preparazione del download…";

        try
        {
            Directory.CreateDirectory(DownloadDirectory);

            var fileName = Uri.UnescapeDataString(
                new Uri(card.Game.DownloadUrl).AbsolutePath.Split('/').Last());
            var destination = Path.Combine(DownloadDirectory, fileName);

            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                DownloadMessage = $"Download in corso… {p:0}%";
            });

            await _downloader.DownloadAsync(card.Game.DownloadUrl, destination, progress);

            _library.Save(card.Game.Id, card.Game.Version, destination);
            card.IsDownloaded = true;
            card.HasUpdate = false;
            card.SetLocalVersion(card.Game.Version);
            RaiseCounters();
            RebuildHistory();

            DownloadProgress = 100;
            DownloadMessage = $"Salvato in {DownloadDirectory}";
        }
        catch (Exception ex)
        {
            DownloadMessage = $"Errore: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void ChangeFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Scegli la cartella di download",
            InitialDirectory = string.IsNullOrWhiteSpace(DownloadDirectory)
                ? null
                : DownloadDirectory,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true) return;

        DownloadDirectory = dialog.FolderName;

        try
        {
            _settings.Save(DownloadDirectory);
        }
        catch
        {
            ReportStatus("Cartella aggiornata, ma non è stato possibile salvare l'impostazione.");
        }
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(DownloadDirectory);
            Process.Start(new ProcessStartInfo(DownloadDirectory) { UseShellExecute = true });
        }
        catch
        {
            ReportStatus("Impossibile aprire la cartella dei download.");
        }
    }

    [RelayCommand]
    private void RevealFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            var folder = Path.GetDirectoryName(path);
            if (folder is not null && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                return;
            }

            ReportStatus("File non trovato: il percorso non esiste più.");
        }
        catch
        {
            ReportStatus("Impossibile aprire il percorso del file.");
        }
    }

    [RelayCommand]
    private async Task ClearCoverCacheAsync()
    {
        var failed = 0;

        try
        {
            foreach (var file in Directory.GetFiles(AppPaths.Covers))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    failed++;
                }
            }
        }
        catch
        {
            // cartella non leggibile: si tenta comunque il ricaricamento
        }

        foreach (var card in _all) card.CoverPath = "";
        await Task.WhenAll(_all.Select(LoadCoverAsync));

        if (failed > 0)
            ReportStatus($"{failed} copertine non eliminate: sono ancora in uso.");
    }

    [RelayCommand]
    private void ResetLibrary()
    {
        var result = MessageBox.Show(
            "Vuoi cancellare la cronologia dei download?\nLe patch già scaricate sul disco non verranno eliminate.",
            "Reset libreria",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        _library.Clear();

        foreach (var card in _all)
        {
            card.IsDownloaded = false;
            card.HasUpdate = false;
            card.SetLocalVersion(null);
        }

        RebuildHistory();
        RaiseCounters();
        ApplyFilter();
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            ReportStatus("Impossibile aprire il link.");
        }
    }

    [RelayCommand]
    private void DismissStatus() => StatusMessage = "";

    private void ReportStatus(string message) => StatusMessage = message;

    private async Task LoadCoverAsync(GameCardViewModel card)
    {
        var path = await _covers.GetAsync(card.Game.CoverImage, card.Id);
        if (path is not null) card.CoverPath = path;
    }

    private void ApplyFilter()
    {
        IEnumerable<GameCardViewModel> query = _all;

        if (ActiveFilter is "complete" or "progress" or "paused" or "planned")
            query = query.Where(g => g.Game.Status == ActiveFilter);
        else if (ActiveFilter == "downloaded")
            query = query.Where(g => g.IsDownloaded);
        else if (ActiveFilter == "updates")
            query = query.Where(g => g.HasUpdate);

        var term = SearchText.Trim();
        if (term.Length > 0)
        {
            query = query.Where(g =>
                g.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                g.Game.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        query = SortMode switch
        {
            "name" => query.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase),
            "updated" => query.OrderByDescending(g => g.Game.UpdatedDate, StringComparer.Ordinal),
            _ => query,
        };

        var list = query.ToList();
        Games = new ObservableCollection<GameCardViewModel>(list);
        IsEmpty = list.Count == 0;

        if (SelectedGame is null || !list.Contains(SelectedGame))
            SelectedGame = list.FirstOrDefault();

        OnPropertyChanged(nameof(GameCount));
    }

    private void UpdateActiveFilter()
    {
        foreach (var filter in Filters)
            filter.IsActive = filter.Key == ActiveFilter;
    }

    private void UpdateActiveSort()
    {
        foreach (var option in SortOptions)
            option.IsActive = option.Key == SortMode;
    }

    private void RaiseCounters()
    {
        foreach (var filter in Filters)
        {
            filter.Count = filter.Key switch
            {
                "all" => _all.Count,
                "complete" => _all.Count(c => c.Game.Status == "complete"),
                "progress" => _all.Count(c => c.Game.Status == "progress"),
                "paused" => _all.Count(c => c.Game.Status == "paused"),
                "planned" => _all.Count(c => c.Game.Status == "planned"),
                "downloaded" => _all.Count(c => c.IsDownloaded),
                _ => 0,
            };
        }

        OnPropertyChanged(nameof(GameCount));
        OnPropertyChanged(nameof(UpdateCount));
    }

    private void RebuildHistory()
    {
        DownloadHistory.Clear();

        foreach (var entry in _library.GetAll())
        {
            var title = _all.FirstOrDefault(c => c.Id == entry.Id)?.Title ?? entry.Id;
            var date = DateTime.TryParse(entry.DownloadedAt, out var parsed)
                ? parsed.ToString("dd/MM/yyyy HH:mm")
                : entry.DownloadedAt;

            DownloadHistory.Add(new DownloadHistoryItem(
                title,
                entry.Version,
                date,
                entry.FilePath,
                File.Exists(entry.FilePath)));
        }

        OnPropertyChanged(nameof(HasDownloadHistory));
    }
}
