using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
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
    private bool isLoading = true;

    [ObservableProperty]
    private bool isReady;

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

    public int GameCount => Games.Count;

    public int UpdateCount => _all.Count(c => c.HasUpdate);

    public bool HasSelection => SelectedGame is not null;

    partial void OnSelectedGameChanged(GameCardViewModel? value)
    {
        DownloadMessage = "";
        DownloadProgress = 0;
        OnPropertyChanged(nameof(HasSelection));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnActiveFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void SetFilter(string filter) => ActiveFilter = filter;

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

            foreach (var card in _all)
            {
                var localVersion = _library.GetVersion(card.Id);
                card.HasUpdate = localVersion is not null && localVersion != card.Version;
            }

            IsReady = true;
            ApplyFilter();
            RaiseCounters();

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
            card.HasUpdate = false;
            RaiseCounters();

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
        _settings.Save(DownloadDirectory);
    }

    [RelayCommand]
    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // link non apribile: nessuna azione
        }
    }

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

        var term = SearchText.Trim();
        if (term.Length > 0)
        {
            query = query.Where(g =>
                g.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                g.Game.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        var list = query.ToList();
        Games = new ObservableCollection<GameCardViewModel>(list);

        if (SelectedGame is null || !list.Contains(SelectedGame))
            SelectedGame = list.FirstOrDefault();

        OnPropertyChanged(nameof(GameCount));
    }

    private void RaiseCounters()
    {
        OnPropertyChanged(nameof(GameCount));
        OnPropertyChanged(nameof(UpdateCount));
    }
}
