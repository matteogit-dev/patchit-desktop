using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PatchIt.Client.Models;
using PatchIt.Client.Services;

namespace PatchIt.Client.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>Download/decode delle copertine in parallelo: oltre questo limite
    /// si aprivano tutte le connessioni insieme all'avvio.</summary>
    private const int CoverConcurrency = 4;

    private readonly CatalogService _catalog = new();
    private readonly CoverCacheService _covers = new();
    private readonly DownloadService _downloader = new();
    private readonly UpdateChecker _updates = new();
    private readonly LibraryService _library = new();
    private readonly SettingsService _settings = new();

    private readonly Queue<GameCardViewModel> _queue = new();

    private List<GameCardViewModel> _all = [];
    private bool _loading;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _queueCts;
    private CancellationTokenSource? _searchCts;

    /// <summary>Attesa dopo l'ultimo tasto prima di filtrare.</summary>
    private const int SearchDebounceMs = 180;

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
    private string downloadDirectory = "";

    [ObservableProperty]
    private bool isQueueRunning;

    [ObservableProperty]
    private string queueMessage = "";

    [ObservableProperty]
    private double queueProgress;

    [ObservableProperty]
    private string statusMessage = "";

    [ObservableProperty]
    private bool hasAppUpdate;

    [ObservableProperty]
    private string appUpdateLabel = "";

    [ObservableProperty]
    private string appUpdateUrl = "";

    [ObservableProperty]
    private string catalogNote = "";

    [ObservableProperty]
    private bool isAppUpdating;

    [ObservableProperty]
    private double appUpdateProgress;

    private AppUpdate? _appUpdate;

    [ObservableProperty]
    private bool isVerifyingAll;

    [ObservableProperty]
    private double verifyAllProgress;

    [ObservableProperty]
    private string verifyAllMessage = "";

    /// <summary>Sezione "Impostazioni patch" aperta nella scheda del gioco.</summary>
    [ObservableProperty]
    private bool showPatchSettings;

    /// <summary>Dimensione delle card: "s", "m" o "l". Decisa dall'utente e ricordata.</summary>
    [ObservableProperty]
    private string cardSize = "m";

    [ObservableProperty]
    private bool isPaletteOpen;

    [ObservableProperty]
    private string paletteQuery = "";

    [ObservableProperty]
    private PaletteItem? paletteSelection;

    public bool HasCatalogNote => CatalogNote.Length > 0;

    partial void OnCatalogNoteChanged(string value) =>
        OnPropertyChanged(nameof(HasCatalogNote));

    public bool CanDownloadAppUpdate => HasAppUpdate && !IsAppUpdating;

    public bool CanVerifyAll => !IsVerifyingAll;

    partial void OnIsVerifyingAllChanged(bool value) =>
        OnPropertyChanged(nameof(CanVerifyAll));

    public string AppUpdateButtonText => IsAppUpdating ? "Aggiornamento in corso…" : AppUpdateLabel;

    partial void OnHasAppUpdateChanged(bool value) =>
        OnPropertyChanged(nameof(CanDownloadAppUpdate));

    partial void OnIsAppUpdatingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownloadAppUpdate));
        OnPropertyChanged(nameof(AppUpdateButtonText));
    }

    partial void OnAppUpdateLabelChanged(string value) =>
        OnPropertyChanged(nameof(AppUpdateButtonText));

    public MainViewModel()
    {
        Filters =
        [
            new("all", "Tutti", Palette.Amber),
            new("complete", "Completate", Palette.Teal),
            new("progress", "In corso", Palette.Amber),
            new("paused", "In pausa", Palette.Violet),
            new("planned", "Pianificate", Palette.Muted),
        ];

        SortOptions =
        [
            new("default", "Predefinito"),
            new("name", "A-Z"),
            new("zname", "Z-A"),
            new("updated", "Recenti"),
        ];

        DensityOptions =
        [
            new("s", "Piccole"),
            new("m", "Medie"),
            new("l", "Grandi"),
        ];

        UpdateActiveSort();
        UpdateActiveDensity();
    }

    public ObservableCollection<SortOptionViewModel> DensityOptions { get; }

    public ObservableCollection<FilterOptionViewModel> Filters { get; }

    public ObservableCollection<SortOptionViewModel> SortOptions { get; }

    public ObservableCollection<DownloadHistoryItem> DownloadHistory { get; } = [];

    /// <summary>Conferme non bloccanti in basso a destra.</summary>
    public ObservableCollection<ToastMessage> Toasts { get; } = [];

    /// <summary>Slot finti mostrati mentre il catalogo arriva la prima volta.</summary>
    public int[] SkeletonSlots { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Scaffali della schermata Casa: poche card in evidenza, il resto in libreria.</summary>
    public ObservableCollection<GameCardViewModel> UpdateShelf { get; } = [];

    public ObservableCollection<GameCardViewModel> NewShelf { get; } = [];

    public ObservableCollection<GameCardViewModel> ProgressShelf { get; } = [];

    public ObservableCollection<GameCardViewModel> InstalledShelf { get; } = [];

    public ObservableCollection<PaletteItem> PaletteResults { get; } = [];

    /// <summary>Quante card mostrare per scaffale prima di rimandare alla libreria.</summary>
    private const int ShelfSize = 8;

    public int GameCount => Games.Count;

    public int UpdateCount => _all.Count(c => c.HasUpdate);

    public int InstalledCount => _all.Count(c => c.IsDownloaded);

    public bool HasDownloadHistory => DownloadHistory.Count > 0;

    public bool HasSelection => SelectedGame is not null;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool CanUpdateAll =>
        CurrentView is "updates" or "home"
        && !IsQueueRunning
        && _all.Any(c => c.HasUpdate && c.IsLocal);

    /// <summary>Vero quando ricerca o filtro stanno nascondendo qualcosa.</summary>
    public bool CanResetFilters => SearchText.Trim().Length > 0 || ActiveFilter is not "all";

    public bool IsLibraryVisible => CurrentView is "library" or "updates" or "installed";

    public bool IsHomeVisible => CurrentView == "home";

    /// <summary>Casa si mostra solo a catalogo caricato: durante il primo avvio c'è lo scheletro.</summary>
    public bool IsHomeReady => IsHomeVisible && IsReady;

    /// <summary>Catalogo davvero vuoto: indipendente da ricerca e filtri della libreria.</summary>
    public bool IsCatalogEmpty => IsReady && _all.Count == 0;

    public bool IsSettingsVisible => CurrentView == "settings";

    public bool HasUpdateShelf => UpdateShelf.Count > 0;

    public bool HasNewShelf => NewShelf.Count > 0;

    public bool HasProgressShelf => ProgressShelf.Count > 0;

    public bool HasInstalledShelf => InstalledShelf.Count > 0;

    /// <summary>Larghezza delle card, seguendo la densità scelta.</summary>
    public double CardWidth => CardSize switch
    {
        "s" => 176,
        "l" => 224,
        _ => 200,
    };

    /// <summary>Altezza della copertina: stessa proporzione delle immagini del sito (2.14:1).</summary>
    public double CoverHeight => Math.Round(CardWidth / 2.14);

    partial void OnCardSizeChanged(string value)
    {
        OnPropertyChanged(nameof(CardWidth));
        OnPropertyChanged(nameof(CoverHeight));
        UpdateActiveDensity();

        // durante il caricamento il valore arriva dal file: non risalvarlo
        if (!IsReady) return;

        try
        {
            _settings.SetCardSize(value);
        }
        catch
        {
            // la preferenza è un extra: se non si salva si tiene lo stesso
        }
    }

    partial void OnPaletteQueryChanged(string value) => BuildPaletteResults();

    partial void OnIsPaletteOpenChanged(bool value)
    {
        if (!value) return;

        PaletteQuery = "";
        BuildPaletteResults();
    }

    /// <summary>Primo caricamento: al posto delle card si mostra lo scheletro.</summary>
    public bool IsFirstLoad => IsLoading && !IsReady;

    /// <summary>Ricarica a griglia già visibile: basta una barra sottile in alto.</summary>
    public bool IsRefreshing => IsLoading && IsReady;

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFirstLoad));
        OnPropertyChanged(nameof(IsRefreshing));
    }

    partial void OnIsReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFirstLoad));
        OnPropertyChanged(nameof(IsRefreshing));
        OnPropertyChanged(nameof(IsHomeReady));
        OnPropertyChanged(nameof(IsCatalogEmpty));
    }

    public string LibraryTitle => CurrentView switch
    {
        "home" => "Casa",
        "updates" => "Aggiornamenti",
        "installed" => "Scaricate",
        _ => "Libreria",
    };

    public string LibrarySubtitle => CurrentView switch
    {
        "home" => HomeSubtitle,
        "updates" => "Patch con una nuova versione disponibile",
        "installed" => "Le patch già scaricate e pronte da installare",
        _ => "Sfoglia il catalogo delle traduzioni",
    };

    public string HomeSubtitle => _all.Count == 0
        ? "Il catalogo delle traduzioni, a colpo d'occhio"
        : $"{_all.Count} traduzioni · {UpdateCount} da aggiornare · {InstalledCount} scaricate";

    /// <summary>Vero quando c'è un testo di ricerca attivo.</summary>
    public bool HasSearchTerm => SearchText.Trim().Length > 0;

    public string EmptyTitle => HasSearchTerm
        ? $"Nessun risultato per «{SearchText.Trim()}»"
        : CurrentView switch
        {
            "updates" => "Tutto aggiornato",
            "installed" => "Nessuna patch scaricata",
            _ => "Nessun titolo trovato",
        };

    public string EmptyMessage => HasSearchTerm
        ? "Controlla l'ortografia o prova con un altro termine."
        : CurrentView switch
        {
            "updates" => "Non ci sono patch da aggiornare.",
            "installed" => "Scarica una patch dalla libreria e la ritrovi qui.",
            _ => "Prova a cambiare ricerca o filtro.",
        };

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
        OnPropertyChanged(nameof(HasSelection));
        if (value is null) IsDetailOpen = false;
    }

    /// <summary>
    /// Il filtro si applica quando si smette di scrivere: ricostruire la lista
    /// a ogni tasto faceva scattare la griglia su cataloghi grandi.
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanResetFilters));
        OnPropertyChanged(nameof(HasSearchTerm));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = DebouncedFilterAsync(_searchCts);
    }

    private async Task DebouncedFilterAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(SearchDebounceMs, source.Token);
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            // nuova digitazione: applica il filtro il debounce successivo
        }
        finally
        {
            if (ReferenceEquals(_searchCts, source)) _searchCts = null;
            source.Dispose();
        }
    }

    partial void OnStatusMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnActiveFilterChanged(string value)
    {
        OnPropertyChanged(nameof(CanResetFilters));
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
        OnPropertyChanged(nameof(IsHomeVisible));
        OnPropertyChanged(nameof(IsHomeReady));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(LibraryTitle));
        OnPropertyChanged(nameof(LibrarySubtitle));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(CanUpdateAll));

        // Impostazioni è una pagina intera: la scheda laterale non ha senso lì
        if (value == "settings") IsDetailOpen = false;

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
    private void ResetFilters()
    {
        if (CurrentView != "library") CurrentView = "library";
        SearchText = "";
        ActiveFilter = "all";
        ApplyFilter();
    }

    /// <summary>Svuota solo la ricerca, lasciando il filtro scelto.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = "";
        ApplyFilter();
    }

    [RelayCommand]
    private void Navigate(string view)
    {
        switch (view)
        {
            case "home":
                CurrentView = "home";
                break;

            case "library":
                if (ActiveFilter is "updates" or "installed") ActiveFilter = "all";
                CurrentView = "library";
                break;

            case "updates":
                CurrentView = "updates";
                ActiveFilter = "updates";
                break;

            case "installed":
                CurrentView = "installed";
                ActiveFilter = "installed";
                break;

            case "settings":
                CurrentView = "settings";
                break;
        }
    }

    /// <summary>"Vedi tutti" degli scaffali: apre la libreria già filtrata o ordinata.</summary>
    [RelayCommand]
    private void OpenShelf(string key)
    {
        switch (key)
        {
            case "new":
                SortMode = "updated";
                CurrentView = "library";
                ActiveFilter = "all";
                break;

            case "progress":
                CurrentView = "library";
                ActiveFilter = "progress";
                break;

            case "updates":
            case "installed":
                Navigate(key);
                break;

            default:
                Navigate("library");
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

    /// <summary>Apre la scheda del gioco indicato (card o scaffale).</summary>
    [RelayCommand]
    private void OpenGame(GameCardViewModel? card)
    {
        if (card is null) return;

        SelectedGame = card;
        IsDetailOpen = true;
    }

    /// <summary>Scarica direttamente dalla card, senza doverla selezionare prima.</summary>
    [RelayCommand]
    private async Task DownloadCard(GameCardViewModel? card)
    {
        if (card is null) return;

        SelectedGame = card;
        await DownloadAsync();
    }

    /// <summary>Scorre la selezione con le frecce mentre la scheda è aperta.</summary>
    [RelayCommand]
    private void MoveSelection(int delta)
    {
        if (Games.Count == 0) return;

        var index = SelectedGame is null ? 0 : Games.IndexOf(SelectedGame);
        if (index < 0) index = 0;

        index = Math.Clamp(index + delta, 0, Games.Count - 1);
        SelectedGame = Games[index];
    }

    [RelayCommand]
    private void TogglePalette() => IsPaletteOpen = !IsPaletteOpen;

    [RelayCommand]
    private void ClosePalette() => IsPaletteOpen = false;

    [RelayCommand]
    private void SetDensity(string size) => CardSize = size;

    /// <summary>Esegue la voce scelta nella palette: un gioco o un'azione dell'app.</summary>
    [RelayCommand]
    private void RunPalette(PaletteItem? item)
    {
        if (item is null) return;

        IsPaletteOpen = false;

        if (item.GameId is { } id)
        {
            var card = _all.FirstOrDefault(c => c.Id == id);
            if (card is null) return;

            if (CurrentView == "settings") CurrentView = "library";
            SelectedGame = card;
            IsDetailOpen = true;
            return;
        }

        switch (item.Action)
        {
            case "library": Navigate("library"); break;
            case "updates": Navigate("updates"); break;
            case "installed": Navigate("installed"); break;
            case "settings": Navigate("settings"); break;
            case "updateall": if (CanUpdateAll) UpdateAllCommand.Execute(null); break;
            case "folder": OpenDownloadFolderCommand.Execute(null); break;
            case "verify": if (CanVerifyAll) VerifyAllCommand.Execute(null); break;
            case "reload": if (LoadCommand.CanExecute(null)) LoadCommand.Execute(null); break;
            case "clearcache": ClearCoverCacheCommand.Execute(null); break;
        }
    }

    /// <summary>Azioni sempre disponibili nella palette, oltre ai giochi.</summary>
    private static PaletteItem[] PaletteActions() =>
    [
        new("Libreria", "Tutto il catalogo", "Vai a", Action: "library"),
        new("Aggiornamenti", "Patch con una nuova versione", "Vai a", Action: "updates"),
        new("Scaricate", "Patch già sul disco", "Vai a", Action: "installed"),
        new("Impostazioni", "Cartelle e manutenzione", "Vai a", Action: "settings"),
        new("Aggiorna tutte le patch", "Scarica le nuove versioni in coda", "Azione", Action: "updateall"),
        new("Apri la cartella dei download", "Esplora i file scaricati", "Azione", Action: "folder"),
        new("Verifica i file scaricati", "Controlla gli hash rispetto al catalogo", "Azione", Action: "verify"),
        new("Ricarica il catalogo", "Rileggi l'elenco dal sito", "Azione", Action: "reload"),
        new("Svuota la cache delle copertine", "Le riscarica al prossimo avvio", "Azione", Action: "clearcache"),
    ];

    private void BuildPaletteResults()
    {
        var term = PaletteQuery.Trim();
        PaletteResults.Clear();

        foreach (var action in PaletteActions())
            if (term.Length == 0 || action.Label.Contains(term, StringComparison.OrdinalIgnoreCase))
                PaletteResults.Add(action);

        var games = term.Length == 0
            ? _all.Where(c => c.HasUpdate || c.IsDownloaded).Take(4)
            : _all.Where(c => Matches(c, term)).Take(12);

        foreach (var card in games)
            PaletteResults.Add(new PaletteItem(
                card.Title,
                card.IsDownloaded ? "Nella tua libreria" : card.StatusLabel,
                "Giochi",
                card.Id));

        PaletteSelection = PaletteResults.FirstOrDefault();
    }

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
            CardSize = _settings.CardSize;

            var catalog = await _catalog.LoadAsync();
            ApplyCatalogNote(catalog);
            _all = catalog.Games.Select(g => new GameCardViewModel(g)).ToList();
            var localEntries = _library.GetAll().ToDictionary(e => e.Id);

            foreach (var card in _all)
            {
                localEntries.TryGetValue(card.Id, out var entry);
                var localVersion = entry?.Version;

                card.IsDownloaded = localVersion is not null;
                card.HasUpdate = localVersion is not null && VersionComparer.IsNewer(card.Version, localVersion);
                card.SetLocalVersion(localVersion);
                card.InstalledAt = entry?.DownloadedAt ?? "";
                card.LocalFilePath = entry?.FilePath ?? "";

                var customFolder = _settings.GetGameDirectory(card.Id);
                card.DownloadFolder = customFolder ?? DownloadDirectory;
                card.HasCustomFolder = customFolder is not null;

                // conta solo una scelta esplicita: il posto in cui la patch fu copiata
                // l'ultima volta non risponde alla domanda "dove copio adesso?"
                card.InstallFolder = _settings.GetGameInstallDirectory(card.Id) ?? "";
                card.InstalledFolder = entry?.InstallFolder ?? "";
                card.HasBackup = entry is { BackupPath.Length: > 0 } && Directory.Exists(entry.BackupPath);
            }

            IsReady = true;
            UpdateActiveFilter();
            ApplyFilter();
            RebuildShelves();
            RaiseCounters();
            RebuildHistory();

            _ = LoadCoversAsync();
            _ = CheckAppUpdateAsync();
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

    /// <summary>Conferma aperta in questo momento, se c'è.</summary>
    [ObservableProperty]
    private ConfirmRequest? currentDialog;

    private TaskCompletionSource<bool>? _dialogAnswer;

    public bool IsDialogOpen => CurrentDialog is not null;

    partial void OnCurrentDialogChanged(ConfirmRequest? value) =>
        OnPropertyChanged(nameof(IsDialogOpen));

    /// <summary>Mostra la conferma e attende la risposta, come faceva MessageBox.</summary>
    private Task<bool> AskAsync(ConfirmRequest request)
    {
        _dialogAnswer?.TrySetResult(false);
        _dialogAnswer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CurrentDialog = request;

        return _dialogAnswer.Task;
    }

    [RelayCommand]
    private void ConfirmDialog() => AnswerDialog(true);

    [RelayCommand]
    private void CancelDialog() => AnswerDialog(false);

    private void AnswerDialog(bool answer)
    {
        var pending = _dialogAnswer;
        if (pending is null) return;

        _dialogAnswer = null;
        CurrentDialog = null;
        pending.TrySetResult(answer);
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (SelectedGame is { } card) await DownloadOneAsync(card);
    }

    /// <summary>Scarica una patch: falso se annullata o fallita.</summary>
    private async Task<bool> DownloadOneAsync(GameCardViewModel card)
    {
        if (IsDownloading || IsQueueRunning) return false;

        if (card.IsExternal)
        {
            OpenUrl(card.Game.DownloadUrl);
            return false;
        }

        if (!card.IsLocal) return false;

        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();

        card.DownloadStatus = DownloadState.Downloading;
        card.DownloadPercent = 0;
        card.DownloadMessage = "Preparazione del download…";

        var completed = false;

        try
        {
            await DownloadCardAsync(card, _downloadCts.Token);

            card.DownloadStatus = DownloadState.Idle;
            card.DownloadPercent = 100;
            card.DownloadMessage = string.IsNullOrWhiteSpace(card.Game.Sha256)
                ? $"Salvato in {card.DownloadFolder} — hash non pubblicato, file non verificato."
                : $"Salvato e verificato in {card.DownloadFolder}";

            Notify($"\"{card.Title}\" scaricata in {card.DownloadFolder}.");

            RaiseCounters();
            RebuildHistory();
            completed = true;
        }
        catch (OperationCanceledException)
        {
            card.DownloadStatus = DownloadState.Idle;
            card.DownloadMessage = "Download annullato. Riprova per riprendere da dove si era fermato.";
        }
        catch (Exception ex)
        {
            card.DownloadStatus = DownloadState.Failed;
            card.DownloadMessage = $"Errore: {ex.Message}";
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            IsDownloading = false;
        }

        return completed;
    }

    /// <summary>Azione unica della scheda: scarica se serve, poi copia nel gioco.</summary>
    [RelayCommand]
    private async Task PatchAsync(GameCardViewModel? card)
    {
        card ??= SelectedGame;
        if (card is null || card.IsInstalling) return;

        if (card.IsDownloading)
        {
            CancelDownload();
            return;
        }

        if (card.IsExternal)
        {
            OpenUrl(card.Game.DownloadUrl);
            return;
        }

        if (!card.IsLocal) return;

        // senza la cartella del gioco non si può installare: si scopre prima di scaricare
        if (!EnsureInstallFolder(card)) return;

        if (!card.IsDownloaded || !File.Exists(card.LocalFilePath))
        {
            if (!await DownloadOneAsync(card)) return;
        }

        // prima di copiare si chiede sempre conferma, con la destinazione in chiaro
        await InstallCoreAsync(card, confirm: true);
    }

    /// <summary>Senza una cartella valida la destinazione si chiede all'utente, sempre.</summary>
    private bool EnsureInstallFolder(GameCardViewModel card)
    {
        if (Directory.Exists(card.InstallFolder)) return true;

        PickInstallFolder(card);
        return card.InstallFolder.Length > 0 && Directory.Exists(card.InstallFolder);
    }

    /// <summary>Scarica una patch e la registra nella libreria, aggiornando la card.</summary>
    private async Task DownloadCardAsync(GameCardViewModel card, CancellationToken ct)
    {
        var target = string.IsNullOrWhiteSpace(card.DownloadFolder)
            ? DownloadDirectory
            : card.DownloadFolder;

        Directory.CreateDirectory(target);

        var fileName = Uri.UnescapeDataString(
            new Uri(card.Game.DownloadUrl).AbsolutePath.Split('/').Last());
        var destination = Path.Combine(target, fileName);

        var progress = new Progress<DownloadProgress>(p =>
        {
            card.DownloadStatus = DownloadState.Downloading;
            card.DownloadPercent = p.Percent;
            card.DownloadMessage = p.Stage switch
            {
                DownloadStage.Verifying => "Verifica del file…",
                DownloadStage.Completed => "",
                _ => p.Percent > 0 ? $"Download in corso… {p.Percent:0}%" : "Download in corso…",
            };
        });

        await _downloader.DownloadAsync(
            card.Game.DownloadUrl, destination, card.Game.Sha256, progress, ct);

        _library.Save(card.Game.Id, card.Game.Version, destination);
        card.IsDownloaded = true;
        card.LocalFilePath = destination;
        card.InstalledAt = DateTime.Now.ToString("o");
        card.HasUpdate = false;
        card.SetLocalVersion(card.Game.Version);
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (IsQueueRunning || IsDownloading) return;

        var targets = _all.Where(c => c.HasUpdate && c.IsLocal).ToList();
        if (targets.Count == 0) return;

        _queue.Clear();
        foreach (var card in targets)
        {
            card.DownloadStatus = DownloadState.Queued;
            card.DownloadPercent = 0;
            card.DownloadMessage = "In coda";
            _queue.Enqueue(card);
        }

        var total = targets.Count;
        var done = 0;
        var failed = 0;

        _queueCts = new CancellationTokenSource();
        IsQueueRunning = true;
        QueueProgress = 0;
        QueueMessage = "Avvio della coda…";
        OnPropertyChanged(nameof(CanUpdateAll));

        try
        {
            while (_queue.Count > 0)
            {
                var card = _queue.Peek();
                QueueMessage = $"Aggiornamento {done + failed + 1} di {total}: {card.Title}";

                try
                {
                    await DownloadCardAsync(card, _queueCts.Token);
                    card.DownloadStatus = DownloadState.Idle;
                    card.DownloadPercent = 100;
                    card.DownloadMessage = "Aggiornata";
                    done++;
                }
                catch (OperationCanceledException)
                {
                    card.DownloadStatus = DownloadState.Idle;
                    card.DownloadMessage = "Annullato";
                    throw;
                }
                catch (Exception ex)
                {
                    card.DownloadStatus = DownloadState.Failed;
                    card.DownloadMessage = $"Errore: {ex.Message}";
                    failed++;
                }
                finally
                {
                    _queue.Dequeue();
                    QueueProgress = (done + failed) * 100.0 / total;
                }
            }
        }
        catch (OperationCanceledException)
        {
            ReportStatus($"Aggiornamento interrotto: {done + failed} di {total} elaborate.");
        }
        finally
        {
            // le patch non ancora toccate tornano normali
            foreach (var card in _queue)
            {
                card.DownloadStatus = DownloadState.Idle;
                card.DownloadMessage = "";
            }

            _queue.Clear();
            _queueCts?.Dispose();
            _queueCts = null;
            IsQueueRunning = false;
            QueueMessage = "";

            RaiseCounters();
            RebuildHistory();
            ApplyFilter();
            OnPropertyChanged(nameof(CanUpdateAll));

            if (failed > 0)
                ReportStatus($"{failed} patch non aggiornate: controlla la connessione e riprova.");
            else if (done > 0)
                Notify($"{done} patch aggiornate.");
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
        _queueCts?.Cancel();
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

        // chi non ha una cartella sua segue la nuova predefinita
        foreach (var card in _all)
            if (!card.HasCustomFolder)
                card.DownloadFolder = DownloadDirectory;

        try
        {
            _settings.Save(DownloadDirectory);
            Notify("Cartella di download aggiornata.");
        }
        catch
        {
            ReportStatus("Cartella aggiornata, ma non è stato possibile salvare l'impostazione.");
        }
    }

    /// <summary>Cartella dedicata a un singolo gioco, indipendente dalla predefinita.</summary>
    [RelayCommand]
    private void ChangeGameFolder(GameCardViewModel? card)
    {
        if (card is null) return;

        var dialog = new OpenFolderDialog
        {
            Title = $"Cartella per {card.Title}",
            InitialDirectory = string.IsNullOrWhiteSpace(card.DownloadFolder)
                ? null
                : card.DownloadFolder,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true) return;

        card.DownloadFolder = dialog.FolderName;
        card.HasCustomFolder = true;

        try
        {
            _settings.SetGameDirectory(card.Id, dialog.FolderName);
            Notify($"Cartella salvata per {card.Title}.");
        }
        catch
        {
            ReportStatus("Cartella impostata per il gioco, ma non è stato possibile salvarla.");
        }
    }

    [RelayCommand]
    private void ResetGameFolder(GameCardViewModel? card)
    {
        if (card is null) return;

        card.DownloadFolder = DownloadDirectory;
        card.HasCustomFolder = false;

        try
        {
            _settings.SetGameDirectory(card.Id, null);
            Notify("Usata la cartella predefinita.");
        }
        catch
        {
            ReportStatus("Cartella ripristinata, ma non è stato possibile salvarlo.");
        }
    }

    /// <summary>Selettore della cartella del gioco: la destinazione la indica l'utente.</summary>
    [RelayCommand]
    private void ChangeGameInstallFolder(GameCardViewModel? card) => PickInstallFolder(card);

    private void PickInstallFolder(GameCardViewModel? card)
    {
        if (card is null) return;

        var dialog = new OpenFolderDialog
        {
            Title = $"Cartella del gioco per {card.Title}",
            InitialDirectory = Directory.Exists(card.InstallFolder) ? card.InstallFolder : null,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true) return;

        card.InstallFolder = dialog.FolderName;
        SaveInstallFolder(card, dialog.FolderName);
    }

    private void SaveInstallFolder(GameCardViewModel card, string folder)
    {
        try
        {
            _settings.SetGameInstallDirectory(card.Id, folder);
            Notify("Cartella del gioco salvata.");
        }
        catch
        {
            ReportStatus("Cartella del gioco impostata, ma non è stato possibile salvarla.");
        }
    }

    /// <summary>Copia lo zip scaricato nella cartella del gioco, salvando prima i file che sostituisce.</summary>
    [RelayCommand]
    private async Task InstallInGameAsync(GameCardViewModel? card) =>
        await InstallCoreAsync(card, confirm: true);

    private async Task InstallCoreAsync(GameCardViewModel? card, bool confirm)
    {
        if (card is null || card.IsInstalling || !card.IsLocal) return;

        if (!File.Exists(card.LocalFilePath))
        {
            ReportStatus("Il file scaricato non c'è più: scarica di nuovo la patch.");
            return;
        }

        if (!PatchInstaller.CanExtract(card.LocalFilePath))
        {
            ReportStatus("Il file scaricato non è uno zip: estrailo e copialo seguendo le istruzioni.");
            return;
        }

        if (!EnsureInstallFolder(card)) return;

        var files = PatchInstaller.ListFiles(card.LocalFilePath);
        if (files.Count == 0)
        {
            ReportStatus("Lo zip non contiene file da estrarre.");
            return;
        }

        if (confirm)
        {
            var proceed = await AskAsync(new ConfirmRequest
            {
                Eyebrow = "INSTALLAZIONE NEL GIOCO",
                Title = $"Copia {files.Count} file",
                Message = $"I file della patch di \"{card.Title}\" verranno scritti nella cartella qui sotto.",
                Detail = card.InstallFolder,
                FilesLabel = $"FILE ({files.Count})",
                Files = files,
                Note = "I file esistenti vengono salvati in un backup, ripristinabile da Impostazioni patch.",
                ConfirmText = "Installa",
            });

            if (!proceed) return;
        }

        card.IsInstalling = true;
        card.InstallMessage = "Copia dei file in corso…";

        try
        {
            var result = await Task.Run(() =>
                new PatchInstaller().Install(card.LocalFilePath, card.InstallFolder, card.Id));

            _library.MarkInstalled(card.Id, card.InstallFolder, result.BackupDirectory);
            card.InstalledFolder = card.InstallFolder;
            card.HasBackup = true;
            card.InstallMessage = result.Replaced > 0
                ? $"{result.Files} file copiati, {result.Replaced} sostituiti (backup salvato)."
                : $"{result.Files} file copiati nella cartella del gioco.";

            Notify($"Patch installata in {card.InstallFolder}.");
        }
        catch (Exception ex)
        {
            card.InstallMessage = $"Errore durante l'installazione: {ex.Message}";
        }
        finally
        {
            card.IsInstalling = false;
        }
    }

    [RelayCommand]
    private async Task RestoreInstall(GameCardViewModel? card)
    {
        if (card is null || !card.HasBackup) return;

        var backup = _library.Get(card.Id)?.BackupPath ?? "";

        if (backup.Length == 0 || !Directory.Exists(backup))
        {
            card.HasBackup = false;
            ReportStatus("Backup non trovato: è già stato ripristinato o eliminato.");
            return;
        }

        var proceed = await AskAsync(new ConfirmRequest
        {
            Eyebrow = "RIPRISTINO",
            Title = "Rimetti i file originali",
            Message = $"Le modifiche fatte dall'installazione di \"{card.Title}\" verranno annullate e i file torneranno com'erano.",
            Detail = card.InstalledFolder.Length > 0 ? card.InstalledFolder : backup,
            Note = "Il backup viene consumato: dopo il ripristino non è più disponibile.",
            ConfirmText = "Ripristina",
        });

        if (!proceed) return;

        try
        {
            var touched = PatchInstaller.Restore(backup);
            _library.ClearInstall(card.Id);

            card.HasBackup = false;
            card.InstalledFolder = "";
            card.InstallMessage = $"{touched} file ripristinati nella cartella del gioco.";
            Notify($"File originali ripristinati ({touched}).");
        }
        catch (Exception ex)
        {
            card.InstallMessage = $"Errore nel ripristino: {ex.Message}";
        }
    }

    /// <summary>Ricalcola l'hash di una patch già scaricata e lo confronta col catalogo.</summary>
    [RelayCommand]
    private async Task VerifyFileAsync(GameCardViewModel? card)
    {
        if (card is null || !card.IsDownloaded || card.IsVerifying) return;

        card.IsVerifying = true;

        try
        {
            card.Integrity = IntegrityState.Checking;
            card.IntegrityMessage = "Verifica in corso…";

            if (!File.Exists(card.LocalFilePath))
            {
                card.Integrity = IntegrityState.Missing;
                card.IntegrityMessage = "File non trovato sul disco: scarica di nuovo la patch.";
                return;
            }

            if (string.IsNullOrWhiteSpace(card.Game.Sha256))
            {
                card.Integrity = IntegrityState.Unknown;
                card.IntegrityMessage = "Il catalogo non pubblica l'hash di questa patch: non è verificabile.";
                return;
            }

            var actual = await FileIntegrity.HashAsync(card.LocalFilePath);

            if (actual is null)
            {
                card.Integrity = IntegrityState.Corrupt;
                card.IntegrityMessage = "Impossibile leggere il file: potrebbe essere danneggiato.";
                return;
            }

            card.Integrity = FileIntegrity.Matches(actual, card.Game.Sha256)
                ? IntegrityState.Ok
                : IntegrityState.Corrupt;

            card.IntegrityMessage = card.Integrity == IntegrityState.Ok
                ? "File integro: l'hash corrisponde al catalogo."
                : "Il file non corrisponde all'hash del catalogo: scaricalo di nuovo.";

            if (card.Integrity == IntegrityState.Ok)
                Notify($"{card.Title}: file integro.");
            else
                Notify($"{card.Title}: il file non corrisponde al catalogo.", ToastKind.Info);
        }
        finally
        {
            card.IsVerifying = false;
        }
    }

    [RelayCommand]
    private async Task VerifyAllAsync()
    {
        if (IsVerifyingAll) return;

        var targets = _all
            .Where(c => c.IsDownloaded && !string.IsNullOrWhiteSpace(c.Game.Sha256))
            .ToList();

        if (targets.Count == 0)
        {
            ReportStatus("Nessun file verificabile: non ci sono download con un hash nel catalogo.");
            return;
        }

        IsVerifyingAll = true;
        VerifyAllProgress = 0;
        VerifyAllMessage = $"0 di {targets.Count}";

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                await VerifyFileAsync(targets[i]);
                VerifyAllProgress = (i + 1) * 100.0 / targets.Count;
                VerifyAllMessage = $"{i + 1} di {targets.Count}";
            }
        }
        finally
        {
            IsVerifyingAll = false;
        }

        var broken = targets.Count(c => c.Integrity is IntegrityState.Corrupt or IntegrityState.Missing);

        if (broken == 0)
            Notify($"{targets.Count} file verificati: tutto integro.");
        else
            ReportStatus($"{broken} file su {targets.Count} non sono integri: apri la scheda del gioco per riscaricarli.");
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

        foreach (var card in _all) card.Cover = null;
        await LoadCoversAsync();

        if (failed > 0)
            ReportStatus($"{failed} copertine non eliminate: sono ancora in uso.");
        else
            Notify("Copertine ricaricate.");
    }

    [RelayCommand]
    private async Task ResetLibrary()
    {
        var proceed = await AskAsync(new ConfirmRequest
        {
            Eyebrow = "MANUTENZIONE",
            Title = "Azzera la cronologia",
            Message = "L'app dimenticherà cosa hai scaricato e dove l'hai installato.",
            Note = "Le patch già scaricate sul disco non vengono toccate.",
            ConfirmText = "Azzera",
        });

        if (!proceed) return;

        _library.Clear();

        foreach (var card in _all)
        {
            card.IsDownloaded = false;
            card.HasUpdate = false;
            card.InstalledAt = "";
            card.LocalFilePath = "";
            card.InstalledFolder = "";
            card.HasBackup = false;
            card.SetLocalVersion(null);
        }

        RebuildHistory();
        Notify("Cronologia dei download azzerata.");
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

    /// <summary>Elimina una traduzione: via dalla libreria e file tolto dal disco.</summary>
    [RelayCommand]
    private async Task RemoveInstalled(GameCardViewModel? card)
    {
        if (card is null || !card.IsDownloaded) return;

        var proceed = await AskAsync(new ConfirmRequest
        {
            Eyebrow = "LIBRERIA",
            Title = "Rimuovi la traduzione",
            Message = $"\"{card.Title}\" esce dalla libreria e il file scaricato viene cancellato dal disco. La patch già copiata nel gioco non viene toccata.",
            DetailLabel = "FILE DA ELIMINARE",
            Detail = card.LocalFilePath,
            ConfirmText = "Rimuovi",
        });

        if (!proceed) return;

        var path = card.LocalFilePath;
        _library.Remove(card.Id);

        card.IsDownloaded = false;
        card.HasUpdate = false;
        card.InstalledAt = "";
        card.LocalFilePath = "";
        card.DownloadStatus = DownloadState.Idle;
        card.DownloadPercent = 0;
        card.DownloadMessage = "";
        card.SetLocalVersion(null);

        var deleted = DeleteFromDisk(path);

        RaiseCounters();
        RebuildHistory();
        ApplyFilter();

        if (!deleted)
            ReportStatus("Voce rimossa, ma il file non è stato eliminato: è in uso o non hai i permessi.");
        else
            Notify($"\"{card.Title}\" eliminata dal disco.");
    }

    /// <summary>Cancella il file scaricato e l'eventuale parziale. False se qualcosa è andato storto.</summary>
    private static bool DeleteFromDisk(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        try
        {
            if (File.Exists(path)) File.Delete(path);

            var partial = DownloadService.TempPathFor(path);
            if (File.Exists(partial)) File.Delete(partial);

            return true;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void DismissStatus() => StatusMessage = "";

    private void ReportStatus(string message) => StatusMessage = message;

    private static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(4.5);

    /// <summary>Conferma un'azione riuscita senza interrompere quello che si sta facendo.</summary>
    private void Notify(string message) => Notify(message, ToastKind.Success);

    private void Notify(string message, ToastKind kind)
    {
        var toast = new ToastMessage(message, kind);
        Toasts.Insert(0, toast);
        _ = RemoveToastLaterAsync(toast);
    }

    private async Task RemoveToastLaterAsync(ToastMessage toast)
    {
        await Task.Delay(ToastLifetime);
        Toasts.Remove(toast);
    }

    /// <summary>Segnala da quando è la copia locale quando la rete non risponde.</summary>
    private void ApplyCatalogNote(CatalogResult catalog)
    {
        CatalogNote = catalog.Source == CatalogSource.Cache && catalog.FetchedAt?.LocalDateTime is { } date
            ? $"Dati offline · catalogo del {date:dd/MM/yyyy HH:mm}"
            : "";
    }

    /// <summary>Controllo di cortesia: se fallisce non si segnala nulla.</summary>
    private async Task CheckAppUpdateAsync()
    {
        var current = typeof(MainViewModel).Assembly.GetName().Version;
        var currentText = current is null ? "0.0.0" : $"{current.Major}.{current.Minor}.{current.Build}";

        var update = await _updates.GetLatestAsync(currentText);
        if (update is null) return;

        _appUpdate = update;
        AppUpdateLabel = $"Aggiorna a v{update.Version}";
        AppUpdateUrl = update.Url;
        HasAppUpdate = true;
    }

    /// <summary>Scarica l'exe della release, lo installa al riavvio e chiude l'app.</summary>
    [RelayCommand]
    private async Task DownloadAppUpdateAsync()
    {
        if (IsAppUpdating) return;

        if (_appUpdate is not { AssetUrl.Length: > 0 } update)
        {
            OpenUrl(AppUpdateUrl);
            ReportStatus("Questa release non ha un eseguibile scaricabile: apri la pagina della release.");
            return;
        }

        var proceed = await AskAsync(new ConfirmRequest
        {
            Eyebrow = "AGGIORNAMENTO",
            Title = $"Aggiorna a v{update.Version}",
            Message = "PatchIT scarica la nuova versione, si chiude, viene sostituita e riparte da sola.",
            Note = "Se il download si interrompe non viene applicato nulla.",
            ConfirmText = "Aggiorna e riavvia",
            CancelText = "Più tardi",
        });

        if (!proceed) return;

        IsAppUpdating = true;
        AppUpdateProgress = 0;

        try
        {
            var installer = new UpdateInstaller();
            var progress = new Progress<double>(value => AppUpdateProgress = value);
            var downloaded = await installer.DownloadAsync(update, progress);

            if (!installer.TryStartSwap(downloaded, out var error))
            {
                ReportStatus(error);
                return;
            }

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ReportStatus($"Aggiornamento non riuscito: {ex.Message}. Puoi scaricarlo dalla pagina della release.");
        }
        finally
        {
            IsAppUpdating = false;
        }
    }

    private async Task LoadCoversAsync()
    {
        using var gate = new SemaphoreSlim(CoverConcurrency);

        var tasks = _all.Select(async card =>
        {
            await gate.WaitAsync();
            try
            {
                await LoadCoverAsync(card);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task LoadCoverAsync(GameCardViewModel card)
    {
        var path = await _covers.GetAsync(card.Game.CoverImage, card.Id);
        if (path is null) return;

        var image = await Task.Run(() => LoadCoverImage(path));
        if (image is not null) card.Cover = image;
    }

    /// <summary>
    /// Decodifica con <see cref="BitmapCacheOption.OnLoad"/>: il file viene letto
    /// subito e non resta bloccato dal processo, così la cache si può svuotare
    /// senza copertine "in uso". Il bitmap è congelato e quindi creato fuori dal thread UI.
    /// </summary>
    private static ImageSource? LoadCoverImage(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<GameCardViewModel> query = _all;

        if (ActiveFilter is "complete" or "progress" or "paused" or "planned")
            query = query.Where(g => g.Game.Status == ActiveFilter);
        else if (ActiveFilter == "installed")
            query = query.Where(g => g.IsDownloaded);
        else if (ActiveFilter == "updates")
            query = query.Where(g => g.HasUpdate);

        var term = SearchText.Trim();
        if (term.Length > 0)
            query = query.Where(g => Matches(g, term));

        query = SortMode switch
        {
            "name" => query.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase),
            "zname" => query.OrderByDescending(g => g.Title, StringComparer.CurrentCultureIgnoreCase),
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

    private void UpdateActiveDensity()
    {
        foreach (var option in DensityOptions)
            option.IsActive = option.Key == CardSize;
    }

    /// <summary>Ricerca condivisa fra libreria e palette.</summary>
    private static bool Matches(GameCardViewModel card, string term) =>
        card.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        card.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        card.Game.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase));

    /// <summary>Riempie gli scaffali della schermata Casa con i dati già caricati.</summary>
    private void RebuildShelves()
    {
        UpdateShelf.Clear();
        NewShelf.Clear();
        ProgressShelf.Clear();
        InstalledShelf.Clear();

        foreach (var card in _all.Where(c => c.HasUpdate && c.IsLocal).Take(ShelfSize))
            UpdateShelf.Add(card);

        foreach (var card in _all
                     .OrderByDescending(c => c.Game.UpdatedDate, StringComparer.Ordinal)
                     .Take(ShelfSize))
            NewShelf.Add(card);

        foreach (var card in _all.Where(c => c.Game.Status == "progress").Take(ShelfSize))
            ProgressShelf.Add(card);

        foreach (var card in _all.Where(c => c.IsDownloaded).Take(ShelfSize))
            InstalledShelf.Add(card);

        OnPropertyChanged(nameof(HasUpdateShelf));
        OnPropertyChanged(nameof(HasNewShelf));
        OnPropertyChanged(nameof(HasProgressShelf));
        OnPropertyChanged(nameof(HasInstalledShelf));
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
                _ => 0,
            };
        }

        RebuildShelves();

        OnPropertyChanged(nameof(GameCount));
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(CanUpdateAll));
        OnPropertyChanged(nameof(HomeSubtitle));
        OnPropertyChanged(nameof(IsCatalogEmpty));
        OnPropertyChanged(nameof(LibrarySubtitle));
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
