using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PatchIt.Client.ViewModels;

namespace PatchIt.Client.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
    }

    /// <summary>Su schermi bassi (1366x768) la finestra non deve finire sotto la barra delle applicazioni.</summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;

        if (Height > area.Height) Height = area.Height;
        if (Width > area.Width) Width = area.Width;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        vm.PropertyChanged += OnViewModelPropertyChanged;

        await vm.LoadCommand.ExecuteAsync(null);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // ogni filtro sostituisce la collezione: le card rientrano scaglionate
            case nameof(MainViewModel.Games):
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(AnimateCards));
                break;

            // quando si apre la palette il cursore deve già essere nel campo di ricerca
            case nameof(MainViewModel.IsPaletteOpen):
                if (DataContext is MainViewModel { IsPaletteOpen: true })
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => PaletteInput.Focus()));
                break;
        }
    }

    /// <summary>Le card entrano una dopo l'altra: 22 ms di distanza, al massimo per le prime 14.</summary>
    private void AnimateCards()
    {
        if (!GamesList.IsVisible) return;

        var index = 0;
        foreach (var item in GamesList.Items)
        {
            if (GamesList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                continue;

            var delay = TimeSpan.FromMilliseconds(Math.Min(index, 14) * 22);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            container.Opacity = 0;
            container.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
                {
                    BeginTime = delay,
                    EasingFunction = ease,
                });

            var slide = new TranslateTransform(0, 10);
            container.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(240))
                {
                    BeginTime = delay,
                    EasingFunction = ease,
                });

            index++;
        }
    }

    /// <summary>Il tasto destro seleziona la card sotto il cursore, così il menu
    /// contestuale agisce su quella e non sulla selezione precedente.</summary>
    private void GamesList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem item)
            item.IsSelected = true;
    }

    private void GamesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        if (DataContext is MainViewModel vm)
        {
            vm.OpenDetailCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void PaletteScrim_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ClosePaletteCommand.Execute(null);
    }

    /// <summary>Click fuori dal pannello: la conferma si annulla, non si esegue.</summary>
    private void DialogScrim_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CancelDialogCommand.Execute(null);
    }

    /// <summary>All'apertura il cursore va sulla conferma: ↵ la esegue, esc annulla.</summary>
    private void DialogPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => DialogConfirmButton.Focus()));
    }

    private void PaletteList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (ItemsControl.ContainerFromElement(PaletteList, e.OriginalSource as DependencyObject) is not ListBoxItem)
            return;

        vm.RunPaletteCommand.Execute(PaletteList.SelectedItem);
        e.Handled = true;
    }

    /// <summary>Frecce per scorrere i risultati, Invio per eseguire la voce scelta.</summary>
    private void PaletteInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        switch (e.Key)
        {
            case Key.Down:
                MovePaletteSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                MovePaletteSelection(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                vm.RunPaletteCommand.Execute(vm.PaletteSelection);
                e.Handled = true;
                break;
        }
    }

    private void MovePaletteSelection(int delta)
    {
        if (PaletteList.Items.Count == 0) return;

        var index = PaletteList.SelectedIndex < 0 ? 0 : PaletteList.SelectedIndex + delta;
        index = Math.Clamp(index, 0, PaletteList.Items.Count - 1);
        PaletteList.SelectedIndex = index;
        PaletteList.ScrollIntoView(PaletteList.SelectedItem);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // con una conferma aperta la finestra non fa altro: ↵ conferma, esc annulla
        if (vm.IsDialogOpen)
        {
            if (e.Key == Key.Enter)
            {
                vm.ConfirmDialogCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                vm.CancelDialogCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (e.Key == Key.K && ctrl)
        {
            vm.TogglePaletteCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (vm.IsPaletteOpen)
            {
                vm.ClosePaletteCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.IsDetailOpen)
            {
                vm.CloseDetailCommand.Execute(null);
                e.Handled = true;
                return;
            }

            // con il cursore nella ricerca, Esc svuota il testo invece di chiudere
            if (SearchBox.IsKeyboardFocusWithin && vm.SearchText.Length > 0)
            {
                vm.ClearSearchCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        // con la scheda aperta le frecce cambiano gioco senza tornare alla griglia
        if (vm.IsDetailOpen && e.Key is Key.Down or Key.Up)
        {
            vm.MoveSelectionCommand.Execute(e.Key == Key.Down ? 1 : -1);
            if (vm.SelectedGame is { } card) GamesList.ScrollIntoView(card);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && ctrl)
        {
            if (vm.IsLibraryVisible)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
            else
            {
                vm.TogglePaletteCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            if (vm.LoadCommand.CanExecute(null)) vm.LoadCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar();
    }

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDarkMode = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 20H1+), 19 sulle build precedenti
            if (DwmSetWindowAttribute(hwnd, 20, ref useDarkMode, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref useDarkMode, sizeof(int));
        }
        catch
        {
            // title bar scura è un extra: se non disponibile si ignora
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
