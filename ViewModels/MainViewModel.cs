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

        UpdateActiveSort();
    }

    public ObservableCollection<FilterOptionViewModel> Filters { get; }

    public ObservableCollection<SortOptionViewModel> SortOptions { get; }

    public ObservableCollection<DownloadHistoryItem> DownloadHistory { get; } = [];

    public int GameCount => Games.Count;

    public int UpdateCount => _all.Count(c => c.HasUpdate);

    public int InstalledCount => _all.Count(c => c.IsDownloaded);

    public bool HasDownloadHistory => DownloadHistory.Count > 0;

    public bool HasSelection => SelectedGame is not null;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool CanUpdateAll =>
        CurrentView == "updates"
        && !IsQueueRunning
        && _all.Any(c => c.HasUpdate && c.IsLocal);

    /// <summary>Vero quando ricerca o filtro stanno nascondendo qualcosa.</summary>
    public bool CanResetFilters => SearchText.Trim().Length > 0 || ActiveFilter is not "all";

    public bool IsLibraryVisible => CurrentView is "library" or "updates" or "installed";

    public bool IsSettingsVisible => CurrentView == "settings";

    public string LibraryTitle => CurrentView switch
    {
        "updates" => "Aggiornamenti",
        "installed" => "Scaricate",
        _ => "Libreria",
    };

    public string LibrarySubtitle => CurrentView switch
    {
        "updates" => "Patch con una nuova versione disponibile",
        "installed" => "Le patch già scaricate e pronte da installare",
        _ => "Sfoglia il catalogo delle traduzioni",
    };

    public string EmptyTitle => CurrentView switch
    {
        "updates" => "Tutto aggiornato",
        "installed" => "Nessuna patch scaricata",
        _ => "Nessun titolo trovato",
    };

    public string EmptyMessage => CurrentView switch
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
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(LibraryTitle));
        OnPropertyChanged(nameof(LibrarySubtitle));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(CanUpdateAll));

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

    [RelayCommand]
    private void Navigate(string view)
    {
        switch (view)
        {
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

                card.InstallFolder = _settings.GetGameInstallDirectory(card.Id)
                    ?? entry?.InstallFolder
                    ?? "";
                card.InstalledFolder = entry?.InstallFolder ?? "";
                card.HasBackup = entry is { BackupPath.Length: > 0 } && Directory.Exists(entry.BackupPath);
            }

            IsReady = true;
            UpdateActiveFilter();
            ApplyFilter();
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

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsDownloading || IsQueueRunning) return;
        if (SelectedGame is not { } card) return;

        if (card.IsExternal)
        {
            OpenUrl(card.Game.DownloadUrl);
            return;
        }

        if (!card.IsLocal) return;

        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();

        card.DownloadStatus = DownloadState.Downloading;
        card.DownloadPercent = 0;
        card.DownloadMessage = "Preparazione del download…";

        try
        {
            await DownloadCardAsync(card, _downloadCts.Token);

            card.DownloadStatus = DownloadState.Idle;
            card.DownloadPercent = 100;
            card.DownloadMessage = string.IsNullOrWhiteSpace(card.Game.Sha256)
                ? $"Salvato in {card.DownloadFolder} — hash non pubblicato, file non verificato."
                : $"Salvato e verificato in {card.DownloadFolder}";

            RaiseCounters();
            RebuildHistory();
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
        }
        catch
        {
            ReportStatus("Cartella ripristinata, ma non è stato possibile salvarlo.");
        }
    }

    [RelayCommand]
    private void ChangeGameInstallFolder(GameCardViewModel? card)
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

        try
        {
            _settings.SetGameInstallDirectory(card.Id, dialog.FolderName);
        }
        catch
        {
            ReportStatus("Cartella del gioco impostata, ma non è stato possibile salvarla.");
        }
    }

    /// <summary>Copia lo zip scaricato nella cartella del gioco, salvando prima i file che sostituisce.</summary>
    [RelayCommand]
    private async Task InstallInGameAsync(GameCardViewModel? card)
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

        if (card.InstallFolder.Length == 0)
        {
            ChangeGameInstallFolder(card);
            if (card.InstallFolder.Length == 0) return;
        }

        var files = PatchInstaller.ListFiles(card.LocalFilePath);
        if (files.Count == 0)
        {
            ReportStatus("Lo zip non contiene file da estrarre.");
            return;
        }

        var preview = string.Join("\n", files.Take(6).Select(f => $"• {f}"));
        if (files.Count > 6) preview += $"\n… e altri {files.Count - 6} file";

        var confirm = MessageBox.Show(
            $"Verranno copiati {files.Count} file in:\n{card.InstallFolder}\n\n{preview}\n\n" +
            "I file esistenti vengono salvati in un backup e si possono ripristinare.",
            "Installa nel gioco",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

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
    private void RestoreInstall(GameCardViewModel? card)
    {
        if (card is null || !card.HasBackup) return;

        var backup = _library.Get(card.Id)?.BackupPath ?? "";

        if (backup.Length == 0 || !Directory.Exists(backup))
        {
            card.HasBackup = false;
            ReportStatus("Backup non trovato: è già stato ripristinato o eliminato.");
            return;
        }

        var confirm = MessageBox.Show(
            $"Ripristinare i file originali di \"{card.Title}\"?\nLe modifiche fatte dall'installazione verranno annullate.",
            "Ripristina backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var touched = PatchInstaller.Restore(backup);
            _library.ClearInstall(card.Id);

            card.HasBackup = false;
            card.InstalledFolder = "";
            card.InstallMessage = $"{touched} file ripristinati nella cartella del gioco.";
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

        ReportStatus(broken == 0
            ? $"{targets.Count} file verificati: tutto integro."
            : $"{broken} file su {targets.Count} non sono integri: apri la scheda del gioco per riscaricarli.");
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
            card.InstalledAt = "";
            card.LocalFilePath = "";
            card.InstalledFolder = "";
            card.HasBackup = false;
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

    /// <summary>Elimina una traduzione: via dalla libreria e file tolto dal disco.</summary>
    [RelayCommand]
    private void RemoveInstalled(GameCardViewModel? card)
    {
        if (card is null || !card.IsDownloaded) return;

        var result = MessageBox.Show(
            $"Vuoi eliminare \"{card.Title}\" dalla libreria e dal disco?\nIl file scaricato verrà cancellato.",
            "Elimina traduzione",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

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

        var confirm = MessageBox.Show(
            $"Scaricare la v{update.Version} e riavviare PatchIT?\n" +
            "L'app si chiuderà, verrà sostituita e ripartirà da sola.",
            "Aggiorna PatchIT",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

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
        {
            query = query.Where(g =>
                g.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                g.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                g.Game.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

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

        OnPropertyChanged(nameof(GameCount));
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(CanUpdateAll));
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
