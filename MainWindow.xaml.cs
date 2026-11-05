using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace VLAP;

public partial class MainWindow : Window
{
    private static readonly (string Display, string Spec)[] Hotkeys =
    {
        ("Alt + Space", "Alt+Space"),
        ("Ctrl + Space", "Ctrl+Space"),
        ("Shift + Space", "Shift+Space"),
        ("F1", "F1"), ("F2", "F2"), ("F3", "F3"), ("F4", "F4"), ("F5", "F5"), ("F6", "F6"),
        ("F7", "F7"), ("F8", "F8"), ("F9", "F9"), ("F10", "F10"), ("F11", "F11"), ("F12", "F12")
    };

    private static readonly double[] UiScales = { 0.9, 1.0, 1.1, 1.25, 1.4, 1.6 };

    private const double BaseWidth = 948;
    private const double BaseHeight = 638;

    private readonly AppSettings _settings;
    private readonly ObservableCollection<AppEntry> _view = new();
    private readonly List<(RadioButton Button, string Category)> _tabs = new();
    private readonly Dictionary<string, TextBlock> _tabCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _toastTimer;

    private HotkeyManager? _hotkey;
    private DispatcherTimer? _trimTimer;
    private DispatcherTimer? _searchTimer;
    private IntPtr _hwnd;
    private bool _acrylicAvailable;
    private string _category = "all";
    private string _query = "";
    // the term the grid is still filtered by after the ghost letters have auto-cleared
    private string _heldTerm = "";
    private double _appliedScale = 1.0;
    private bool _suppressDeactivate;
    // a click with a mouse button held may be the start of a drag from Explorer onto
    // the HUD: hiding right away would take the drop zone away before the pointer arrives
    private bool _deferredHide;
    private DispatcherTimer? _deferredHideTimer;
    private bool _fileDragOver;
    private DispatcherTimer? _dropHintTimer;
    private bool _initializing;
    private bool _iconsLoaded;
    private bool _categoryEditReady;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); FadeToast(0); };

        AppsList.ItemsSource = _view;
        SetupDropTargets();
        SetupTileDragging();

        _settings.Categories ??= new List<AppCategory>();
        SyncCategoryRegistry();
        NormalizeCategories();

        // resolved on the UI thread once; the tiles then read the frozen cached copy
        IconLoader.Folder();

        BuildTabs();
        BuildPresets();
        RefreshSettingsLists();
        InitControlsFromSettings();
        ApplyBackdropVisual();
        ApplyUiScale(_settings.UiScale);

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => UpdateWindowRegion();
        DpiChanged += (_, _) => UpdateWindowRegion();
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
        Loaded += OnLoaded;

        _categoryEditReady = true;
    }

    // ---------- lifecycle ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hotkey = new HotkeyManager();
        _hotkey.Pressed += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            SettingsStore.Log("hotkey pressed");
            ToggleOverlay();
        }));
        RegisterHotkey();
        ApplyBackdropVisual();
        Filter();
        _ = LoadIconsAsync();
        RefreshRunning();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _acrylicAvailable = Acrylic.Apply(_hwnd, _settings.LightTheme, _settings.Blur);
        ApplyBackdropVisual();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        SettingsStore.Log("deactivated: suppress=" + _suppressDeactivate + ", visible=" + IsVisible);
        if (_suppressDeactivate) return;
        if (!IsVisible) return;
        if (_fileDragOver)
        {
            // a drag is genuinely over the panel; if the button is already up the drag
            // ended without a Drop or DragLeave reaching us — drop the stale flag
            if (AnyMouseButtonDown()) return;
            _fileDragOver = false;
        }

        // Hide only when the user actually clicked somewhere else. A window that
        // pops up on its own (notification, launched app, game going foreground)
        // steals focus without any mouse button being held — keep the HUD open then.
        if (!AnyMouseButtonDown()) return;

        // The button is still down: this is very often the start of a file drag from
        // Explorer or the desktop. Wait for it to come up, then hide unless the files
        // actually landed on the HUD.
        BeginDeferredHide();
    }

    /// <summary>
    /// Hides the HUD once the mouse button that took focus away has been released.
    /// A file drag that reaches the panel cancels this via <see cref="CancelDeferredHide"/>.
    /// Polling is needed because a window that lost activation receives no mouse-up.
    /// </summary>
    private void BeginDeferredHide()
    {
        _deferredHide = true;
        if (_deferredHideTimer == null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                if (!_deferredHide) { timer.Stop(); return; }
                if (_suppressDeactivate || !IsVisible || IsActive)
                {
                    CancelDeferredHide();
                    return;
                }

                // still waiting for the drag (or simply for the click) to finish
                if (AnyMouseButtonDown()) return;

                _fileDragOver = false;
                CancelDeferredHide();
                HideOverlay();
            };
            _deferredHideTimer = timer;
        }
        _deferredHideTimer.Start();
    }

    private void CancelDeferredHide()
    {
        _deferredHide = false;
        _deferredHideTimer?.Stop();
    }

    /// <summary>Global button state: timer-driven checks cannot rely on GetKeyState, which is per-message.</summary>
    private static bool AnyMouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 ||   // left
        (GetAsyncKeyState(0x02) & 0x8000) != 0 ||   // right
        (GetAsyncKeyState(0x04) & 0x8000) != 0;     // middle

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (HotkeyCombo.IsDropDownOpen)
            {
                HotkeyCombo.IsDropDownOpen = false;
                e.Handled = true;
                return;
            }
            if (SettingsView.Visibility == Visibility.Visible)
            {
                CloseSettings();
            }
            else if (_query.Length > 0 || _heldTerm.Length > 0)
            {
                ClearQuery();
            }
            else
            {
                HideOverlay();
            }
            e.Handled = true;
            return;
        }

        // the field is gone in this mode, so the window itself edits the query.
        // one Backspace means "drop the search", not "delete a letter": the whole query goes and
        // the grid comes back to the current category at once
        if (e.Key == Key.Back && TypeSearchActive && !IsTextInputFocused())
        {
            ClearQuery();
            e.Handled = true;
        }
    }

    /// <summary>
    /// With type-to-search on, every printable key that lands on the window becomes part of the query.
    /// </summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!TypeSearchActive || IsTextInputFocused()) return;
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (_query.Length == 0 && char.IsWhiteSpace(e.Text[0])) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;

        // a key after the ghost letters are gone starts a new search instead of extending the hidden one
        _heldTerm = "";
        _query += e.Text;
        RestartSearchTimer();
        Filter();
        e.Handled = true;
    }

    /// <summary>True while the query should be collected from the keyboard instead of a visible field.</summary>
    private bool TypeSearchActive =>
        _settings.TypeToSearch &&
        IsVisible &&
        SettingsView.Visibility != Visibility.Visible;

    /// <summary>A focused text box or combo owns its own typing and must not feed the query.</summary>
    private static bool IsTextInputFocused() =>
        Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox;

    public void Cleanup()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        CancelDeferredHide();
        ResetDropHints();
    }

    // ---------- overlay visibility ----------

    public void ShowOverlay()
    {
        SettingsStore.Log("ShowOverlay: visible=" + IsVisible);
        CancelDeferredHide();
        ResetDropHints();
        _fileDragOver = false;
        PositionWindow();

        Shell.Opacity = 1;
        ResetSearch();
        SetCategory("all");
        RefreshRunning();

        // the usage order may have changed while the HUD was hidden (launches from the API, presets)
        Filter();
        if (!_iconsLoaded) _ = LoadIconsAsync();

        if (!IsVisible) Show();
        Activate();
        Topmost = true;

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
        }
        catch { /* best effort */ }

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_settings.TypeToSearch)
            {
                AppsList.Focus();
                Keyboard.Focus(AppsList);
            }
            else
            {
                SearchInput.Focus();
                Keyboard.Focus(SearchInput);
            }
        }), DispatcherPriority.Input);
    }

    private void HideOverlay()
    {
        if (!IsVisible) return;
        CancelDeferredHide();
        _fileDragOver = false;
        ResetDropHints();
        Hide();
        ResetSearch();
        SettingsToggle.IsChecked = false;
        SettingsView.Visibility = Visibility.Collapsed;
        ScheduleTrim();
    }

    public void TrimWhenIdle() => ScheduleTrim();

    /// <summary>Hands unused pages back to Windows a few seconds after the HUD closes.</summary>
    private void ScheduleTrim()
    {
        _trimTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _trimTimer.Stop();
        _trimTimer.Tick -= TrimTimer_Tick;
        _trimTimer.Tick += TrimTimer_Tick;
        _trimTimer.Start();
    }

    private void TrimTimer_Tick(object? sender, EventArgs e)
    {
        _trimTimer?.Stop();
        if (IsVisible) return;

        GC.Collect(2, GCCollectionMode.Optimized, true, true);
        GC.WaitForPendingFinalizers();
        try
        {
            using var proc = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(proc.Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch { /* best effort */ }
    }

    private void HideOverlayAfter(int milliseconds)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            HideOverlay();
        };
        timer.Start();
    }

    private void ToggleOverlay()
    {
        if (IsVisible) HideOverlay();
        else ShowOverlay();
    }

    private void PositionWindow()
    {
        try
        {
            var cursor = WinForms.Cursor.Position;
            var screen = WinForms.Screen.FromPoint(cursor);
            var wa = screen.WorkingArea;

            double scale = 1.0;
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var dpi = GetDpiForWindow(hwnd);
                    if (dpi >= 96) scale = dpi / 96.0;
                }
                if (Math.Abs(scale - 1.0) < 0.001)
                {
                    var visual = VisualTreeHelper.GetDpi(this);
                    if (visual.DpiScaleX > 0.1) scale = visual.DpiScaleX;
                }
            }
            catch { /* keep 1.0 */ }

            // Never let the HUD outgrow the screen — the chosen scale is the maximum.
            var availW = wa.Width / scale - 16;
            var availH = wa.Height / scale - 16;
            var ui = _settings.UiScale > 0 ? _settings.UiScale : 1.1;
            ui = Math.Min(ui, Math.Min(availW / BaseWidth, availH / BaseHeight));
            ui = Math.Max(0.6, ui);

            ApplyUiScale(ui);

            Left = wa.Left / scale + (wa.Width / scale - Width) / 2;
            Top = wa.Top / scale + (wa.Height / scale - Height) / 2;

            SettingsStore.Log($"pos: screen={wa.Width}x{wa.Height}@{wa.X},{wa.Y} scale={scale:0.##} " +
                              $"ui={ui:0.##} size={Width:0}x{Height:0} left={Left:0} top={Top:0} " +
                              $"visualDpi={VisualTreeHelper.GetDpi(this).DpiScaleX:0.##}");
        }
        catch (Exception ex) { SettingsStore.Log("position: " + ex.Message); }
    }

    // ---------- apps ----------

    private async Task LoadIconsAsync()
    {
        var apps = _settings.Apps.ToList();
        var loaded = await Task.Run(() => apps
            .Select(a => (App: a,
                Icon: a.LaunchKind == "folder" ? IconLoader.Folder() : IconLoader.Load(a.IconSource)))
            .ToList());

        foreach (var (app, icon) in loaded)
        {
            if (icon != null) app.IconImage = icon;
        }
        _iconsLoaded = true;
    }

    private async void RefreshRunning()
    {
        var apps = _settings.Apps.ToList();
        var running = await Task.Run(() =>
        {
            // one process scan for the whole grid instead of one scan per tile
            var names = RunningProcessNames();
            return apps.ToDictionary(a => a, a => IsProcessRunning(a.Executable, names));
        });

        foreach (var pair in running)
            pair.Key.Running = pair.Value;
    }

    /// <summary>Names of everything running right now, read in a single pass.</summary>
    private static HashSet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try { names.Add(process.ProcessName); }
                catch { /* the process exited between enumeration and reading */ }
                finally { process.Dispose(); }
            }
        }
        catch (Exception ex) { SettingsStore.Log("running scan: " + ex.Message); }
        return names;
    }

    private static bool IsProcessRunning(string? executable, HashSet<string>? snapshot = null)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            var name = Path.GetFileNameWithoutExtension(executable);
            if (string.IsNullOrWhiteSpace(name)) return false;
            return snapshot != null
                ? snapshot.Contains(name)
                : Process.GetProcessesByName(name).Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Starts the entry. notify:false keeps API calls silent: no toast, nothing on screen.</summary>
    private bool Launch(AppEntry app, bool notify = true)
    {
        try
        {
            var psi = app.LaunchKind == "py"
                ? PythonLauncher.StartInfo(app)
                : new ProcessStartInfo
                {
                    FileName = app.Target,
                    UseShellExecute = true
                };
            if (app.LaunchKind == "exe" && !string.IsNullOrWhiteSpace(app.Args))
                psi.Arguments = app.Args;
            if (app.LaunchKind != "py" && app.RunAsAdmin) psi.Verb = "runas";

            Process.Start(psi);
            app.Running = true;
            NoteUsage(app);

            // Opening a folder restarts its deletion countdown.
            if (app.IsFolder) RestartFolderTimer(app);
            else SettingsStore.Save(_settings);
            return true;
        }
        catch (Exception ex)
        {
            SettingsStore.Log("launch " + app.Name + ": " + ex);
            if (notify) ShowToast("Не удалось запустить: " + app.Name);
            return false;
        }
    }

    /// <summary>Feeds the «часто используемые» order; counts even while that order is switched off.</summary>
    private static void NoteUsage(AppEntry app)
    {
        app.LaunchCount++;
        app.LastUsedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// A click on an entry that is already running brings its window up instead of starting a second
    /// copy; Ctrl forces a new instance for the apps that need one.
    /// </summary>
    private void LaunchFromUi(AppEntry app)
    {
        var forceNew = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (!forceNew && ActivateRunning(app.Executable)) { AfterLaunch(); return; }
        Launch(app);
        AfterLaunch();
    }

    /// <summary>Brings the main window of an already running app back to the front. False when it has none.</summary>
    private static bool ActivateRunning(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;

        var name = Path.GetFileNameWithoutExtension(executable);
        if (string.IsNullOrWhiteSpace(name)) return false;

        Process[] processes;
        try { processes = Process.GetProcessesByName(name); }
        catch (Exception ex) { SettingsStore.Log("activate " + name + ": " + ex.Message); return false; }

        var self = Environment.ProcessId;
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id == self) continue;
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;

                    if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                    SetForegroundWindow(handle);
                    return true;
                }
                catch { /* the process exited while we were looking at it */ }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return false;
    }

    /// <summary>The clock for a folder entry starts over every time the user actually opens it.</summary>
    private void RestartFolderTimer(AppEntry app)
    {
        app.LastOpenedAt = DateTime.UtcNow;
        SettingsStore.Save(_settings);
    }

    private void AfterLaunch(bool preset = false)
    {
        // a held Alt keeps the HUD open, so several things can be started in one go
        var altHeld = (GetKeyState(0x12) & 0x8000) != 0;
        if (!altHeld)
            HideOverlayAfter(preset ? 260 : 140);
    }

    private void LaunchSelected()
    {
        if (AppsList.SelectedItem is AppEntry app)
            LaunchFromUi(app);
        else if (_view.Count == 1)
            LaunchFromUi(_view[0]);
    }

    private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // the release that ends a drag must not launch anything
        if (DateTime.UtcNow < _suppressTileLaunch) return;

        if (sender is ListBoxItem { DataContext: AppEntry app })
            LaunchFromUi(app);
    }

    // ---------- ordering tiles ----------

    private const string TileDragFormat = "VLAP.AppEntry";

    private Point _dragStart;
    private AppEntry? _dragCandidate;
    private ListBoxItem? _dimmedTile;
    private Border? _dropLine;
    private DateTime _suppressTileLaunch;

    /// <summary>Drag a tile onto another one to move it: the order lives in settings.apps and survives restarts.</summary>
    private void SetupTileDragging()
    {
        AppsList.AllowDrop = true;

        AppsList.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;

            var source = e.OriginalSource as DependencyObject;
            _dragCandidate = (source != null
                    ? ItemsControl.ContainerFromElement(AppsList, source) as ListBoxItem
                    : null)?.DataContext as AppEntry;
            _dragStart = e.GetPosition(AppsList);
        };

        AppsList.PreviewMouseMove += (_, e) =>
        {
            if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;

            var delta = e.GetPosition(AppsList) - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            var app = _dragCandidate;
            _dragCandidate = null;
            DimTile(app, true);

            try
            {
                CancelDeferredHide();
                ResetDropHints();
                DragDrop.DoDragDrop(AppsList, new DataObject(TileDragFormat, app), DragDropEffects.Move);
            }
            finally
            {
                DimTile(app, false);
                HideDropLine();
                // the mouse-up that ended the drag arrives right after this: ignore it
                _suppressTileLaunch = DateTime.UtcNow.AddMilliseconds(400);
            }
        };

        AppsList.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(TileDragFormat)) return;

            e.Effects = DragDropEffects.Move;
            var (target, after) = TileUnder(e.GetPosition(AppsList));
            ShowDropLine(target, after);
            e.Handled = true;
        };

        AppsList.DragLeave += (_, _) => HideDropLine();

        AppsList.Drop += (_, e) =>
        {
            HideDropLine();
            if (e.Data.GetData(TileDragFormat) is not AppEntry dragged) return;

            e.Handled = true;
            var (target, after) = TileUnder(e.GetPosition(AppsList));
            MoveTile(dragged, target, after);
        };
    }

    private void DimTile(AppEntry app, bool dim)
    {
        if (!dim)
        {
            if (_dimmedTile != null) _dimmedTile.Opacity = 1;
            _dimmedTile = null;
            return;
        }

        _dimmedTile = AppsList.ItemContainerGenerator.ContainerFromItem(app) as ListBoxItem;
        if (_dimmedTile != null) _dimmedTile.Opacity = 0.35;
    }

    /// <summary>The tile under the pointer and whether the drop half is its right side; null in empty space.</summary>
    private (AppEntry? Target, bool After) TileUnder(Point point)
    {
        foreach (var app in _view)
        {
            if (AppsList.ItemContainerGenerator.ContainerFromItem(app) is not ListBoxItem item) continue;
            if (!item.IsVisible || item.ActualWidth <= 0) continue;

            var rect = new Rect(item.TranslatePoint(new Point(0, 0), AppsList), item.RenderSize);
            if (!rect.Contains(point)) continue;

            return (app, point.X > rect.Left + rect.Width / 2);
        }

        return (null, false);
    }

    /// <summary>Moves the entry next to the tile it was dropped on, keeping its id and saving the new order.</summary>
    private void MoveTile(AppEntry dragged, AppEntry? target, bool after)
    {
        if (ReferenceEquals(dragged, target)) return;

        var apps = _settings.Apps;
        var from = apps.IndexOf(dragged);
        if (from < 0) return;

        apps.RemoveAt(from);

        var to = target == null ? apps.Count : apps.IndexOf(target);
        if (to < 0) to = apps.Count;
        else if (after) to++;

        apps.Insert(Math.Clamp(to, 0, apps.Count), dragged);

        SettingsStore.Save(_settings);

        // rebuilds the tiles in the new order; the dragged one keeps the selection
        Filter();
        AppsList.SelectedItem = dragged;
    }

    private void ShowDropLine(AppEntry? target, bool after)
    {
        if (target == null ||
            AppsList.ItemContainerGenerator.ContainerFromItem(target) is not ListBoxItem item)
        {
            HideDropLine();
            return;
        }

        if (_dropLine == null)
        {
            _dropLine = new Border
            {
                Width = 2,
                CornerRadius = new CornerRadius(1),
                Background = (Brush)FindResource("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            AppsDropZone.Children.Add(_dropLine);
        }

        var topLeft = item.TranslatePoint(new Point(0, 0), AppsDropZone);
        _dropLine.Height = Math.Max(12, item.ActualHeight - 10);
        _dropLine.Margin = new Thickness(topLeft.X + (after ? item.ActualWidth - 3 : -3), topLeft.Y + 5, 0, 0);
        _dropLine.Visibility = Visibility.Visible;
    }

    private void HideDropLine()
    {
        if (_dropLine != null) _dropLine.Visibility = Visibility.Collapsed;
    }

    // ---------- filtering / tabs ----------

    private void BuildTabs()
    {
        _tabs.Clear();
        _tabCounts.Clear();
        TabsPanel.Children.Clear();

        AddTab("all", "Все");
        foreach (var category in _settings.Categories)
            AddTab(category.Key, category.Label);
    }

    private void AddTab(string key, string label)
    {
        var counts = new TextBlock
        {
            Text = "0",
            FontSize = 10.5,
            Margin = new Thickness(5, 0, 0, 0),
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = label });
        content.Children.Add(counts);

        var button = new RadioButton
        {
            Style = (Style)FindResource("TabToggle"),
            GroupName = "category",
            Tag = key,
            Content = content
        };
        button.Checked += (_, _) =>
        {
            _category = key;
            // a search whose ghost letters are already gone must not follow the user to another tab
            _heldTerm = "";
            Filter();
        };

        _tabs.Add((button, key));
        _tabCounts[key] = counts;
        TabsPanel.Children.Add(button);
    }

    /// <summary>Rebuilds the tab strip after categories changed and keeps the current filter if it still exists.</summary>
    private void RebuildTabs()
    {
        var previous = _category;
        BuildTabs();

        if (!_tabs.Any(t => string.Equals(t.Category, previous, StringComparison.OrdinalIgnoreCase)))
            previous = "all";

        SetCategory(previous);
        Filter();
    }

    private void SetCategory(string category)
    {
        _category = category;
        foreach (var (button, key) in _tabs)
            button.IsChecked = string.Equals(key, category, StringComparison.OrdinalIgnoreCase);
    }

    private void SyncCategoryRegistry()
    {
        CategoryRegistry.Set(_settings.Categories);
        foreach (var app in _settings.Apps) app.RefreshCategoryLabel();
    }

    /// <summary>Drops tags whose category is gone. An app left with no tags is fine — it still lives under «Все».</summary>
    private void NormalizeCategories()
    {
        foreach (var app in _settings.Apps)
        {
            app.Categories ??= new List<string>();
            app.Categories.RemoveAll(key => !CategoryRegistry.Exists(key));
            app.RefreshCategoryLabel();
        }
    }

    /// <summary>How many apps carry each tag; one app counts in every category it is in.</summary>
    private Dictionary<string, int> CountByCategory()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _settings.Apps)
            foreach (var key in app.Categories ?? new List<string>())
                counts[key] = counts.GetValueOrDefault(key) + 1;
        return counts;
    }

    private void UpdateCategoryCounts()
    {
        var counts = CountByCategory();
        foreach (var category in _settings.Categories)
            category.Count = counts.GetValueOrDefault(category.Key);
    }

    private void UpdateTabCounts()
    {
        var counts = CountByCategory();

        foreach (var (_, key) in _tabs)
        {
            if (!_tabCounts.TryGetValue(key, out var text)) continue;
            var count = key == "all" ? _settings.Apps.Count : counts.GetValueOrDefault(key);
            text.Text = count.ToString();
        }
    }

    private void Filter()
    {
        // ghost letters are what the user types; when they have auto-cleared the held term keeps the
        // grid filtered, so tiles stay narrowed down until Backspace drops the search
        var typed = _query.Trim();
        var term = typed.Length > 0 ? typed : _heldTerm;
        IEnumerable<AppEntry> query = _settings.Apps;

        if (_category != "all")
            query = query.Where(a => a.InCategory(_category));
        if (term.Length > 0)
            query = query.Where(a => a.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase));

        // the list order is the order the user arranged the tiles in, so it is not re-sorted here
        var list = query.ToList();

        // «часто используемые» order: the sort is stable, so equal counts keep the hand-made order
        if (_settings.SortByUsage && term.Length == 0)
        {
            list = list.OrderByDescending(a => a.LaunchCount)
                       .ThenByDescending(a => a.LastUsedAt ?? DateTime.MinValue)
                       .ToList();
        }

        _view.Clear();
        foreach (var app in list) _view.Add(app);

        EmptyHint.Text = _settings.Apps.Count == 0
            ? "Перетащи сюда exe или папку"
            : "Ничего не нашлось";
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Visibility = term.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Dota-style ghost letters: the query lives in the middle of the grid, not in a field.
        // only what is actually being typed is shown: a held term keeps filtering without any text
        SearchWatermark.Text = _query;
        SearchWatermark.Visibility = _settings.TypeToSearch && typed.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        FooterCount.Text = $"{list.Count} {Plural(list.Count, "приложение", "приложения", "приложений")}";
        AppsList.SelectedIndex = list.Count > 0 ? 0 : -1;
        UpdateTabCounts();
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var m10 = n % 10;
        var m100 = n % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
        return many;
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        // with type-to-search on the field is hidden and the window collects the keys itself
        if (_settings.TypeToSearch) return;
        _query = SearchInput.Text;
        Filter();
    }

    // ---------- type-to-search ----------

    private void ApplySearchMode()
    {
        _searchTimer?.Stop();
        _query = "";
        _heldTerm = "";
        SearchInput.Text = "";
        SearchWatermark.Text = "";
        SearchWatermark.Visibility = Visibility.Collapsed;
        SearchShell.Visibility = _settings.TypeToSearch ? Visibility.Collapsed : Visibility.Visible;
        Filter();
    }

    private void ResetSearch()
    {
        _searchTimer?.Stop();
        var wasFiltered = _query.Length > 0 || _heldTerm.Length > 0 || !string.IsNullOrEmpty(SearchInput.Text);
        _query = "";
        _heldTerm = "";
        if (!string.IsNullOrEmpty(SearchInput.Text)) SearchInput.Text = "";
        SearchWatermark.Text = "";
        SearchWatermark.Visibility = Visibility.Collapsed;

        // an empty query must mean an unfiltered grid: without this the tiles stay filtered by the
        // last search while the ghost letters are already gone (reopening the window showed that)
        if (wasFiltered) Filter();
    }

    /// <summary>Drops the search completely: ghost letters, the held term and the field all go, the grid returns.</summary>
    private void ClearQuery()
    {
        if (_query.Length == 0 && _heldTerm.Length == 0) return;
        _query = "";
        _heldTerm = "";
        _searchTimer?.Stop();
        if (!string.IsNullOrEmpty(SearchInput.Text)) SearchInput.Text = "";
        Filter();
    }

    /// <summary>Every new key restarts the two-second clock; after it runs out the ghost letters go.</summary>
    private void RestartSearchTimer()
    {
        _searchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _searchTimer.Stop();
        _searchTimer.Tick -= SearchTimer_Tick;
        _searchTimer.Tick += SearchTimer_Tick;
        if (_settings.SearchAutoClear) _searchTimer.Start();
    }

    private void SearchTimer_Tick(object? sender, EventArgs e)
    {
        _searchTimer?.Stop();
        HideGhostLetters();
    }

    /// <summary>
    /// The two-second auto-clear takes the letters off the screen only: the search stays applied and the
    /// grid keeps the narrowed list. Backspace is what gives the apps back.
    /// </summary>
    private void HideGhostLetters()
    {
        if (_query.Length == 0) return;
        _heldTerm = _query.Trim();
        _query = "";
        SearchWatermark.Text = "";
        SearchWatermark.Visibility = Visibility.Collapsed;
        // no Filter() on purpose: rebuilding the grid here would throw the search away
    }

    private void TypeSearchSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.TypeToSearch = TypeSearchSwitch.IsChecked == true;
        SettingsStore.Save(_settings);
        RefreshAutoClearRow();
        ApplySearchMode();
        ShowToast(_settings.TypeToSearch
            ? "Поиск при наборе включён"
            : "Поле поиска вернулось наверх");
    }

    private void AutoClearSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.SearchAutoClear = AutoClearSwitch.IsChecked == true;
        SettingsStore.Save(_settings);
        ShowToast(_settings.SearchAutoClear
            ? "Запрос стирается через 2 с"
            : "Запрос остаётся до Esc");
    }

    /// <summary>Auto-erase only means something while typing anywhere is the search.</summary>
    private void RefreshAutoClearRow()
    {
        AutoClearRow.Visibility = TypeSearchSwitch.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---------- settings sections ----------

    private void ApplyCollapsedSections()
    {
        _settings.CollapsedSections ??= new List<string>();
        foreach (var (section, key) in new (Expander, string)[]
                 {
                     (AppearanceSection, "appearance"),
                     (BehaviorSection, "behavior"),
                     (AppsSection, "apps"),
                     (CategoriesSection, "categories"),
                     (PresetsSection, "presets"),
                     (DataSection, "data")
                 })
        {
            section.IsExpanded = !_settings.CollapsedSections.Contains(key, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Section_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (sender is not Expander { Tag: string key } section) return;

        _settings.CollapsedSections ??= new List<string>();
        _settings.CollapsedSections.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (!section.IsExpanded) _settings.CollapsedSections.Add(key);
        SettingsStore.Save(_settings);
    }

    private void ShowPresetsSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowPresetsOnMain = ShowPresetsSwitch.IsChecked == true;
        SettingsStore.Save(_settings);
        ApplyPresetsFooter();
        ShowToast(_settings.ShowPresetsOnMain
            ? "Наборы снова на главной"
            : "Наборы только в настройках");
    }

    private void SortByUsageSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.SortByUsage = SortByUsageSwitch.IsChecked == true;
        SettingsStore.Save(_settings);
        Filter();
        ShowToast(_settings.SortByUsage
            ? "Сверху то, что запускается чаще"
            : "Вернулся порядок, расставленный вручную");
    }

    private void ApplyPresetsFooter()
    {
        PresetsFooter.Visibility = _settings.ShowPresetsOnMain
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SearchInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        HandleListNavigation(e);
    }

    private void AppsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        HandleListNavigation(e);
    }

    private void HandleListNavigation(KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            if (_view.Count > 0)
                AppsList.SelectedIndex = Math.Min(AppsList.SelectedIndex + 1, _view.Count - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (_view.Count > 0)
                AppsList.SelectedIndex = Math.Max(AppsList.SelectedIndex - 1, 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            LaunchSelected();
            e.Handled = true;
        }
    }

    // ---------- presets ----------

    private void BuildPresets()
    {
        PresetsPanel.Children.Clear();

        foreach (var preset in _settings.Presets)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = "▶",
                FontSize = 8,
                Foreground = (Brush)FindResource("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            content.Children.Add(new TextBlock { Text = preset.Name, VerticalAlignment = VerticalAlignment.Center });

            var button = new Button
            {
                Style = (Style)FindResource("PresetBtn"),
                Content = content,
                Tag = preset,
                ToolTip = PresetNamesText(preset)
            };
            button.Click += Preset_Click;
            PresetsPanel.Children.Add(button);
        }
    }

    /// <summary>Readable contents of a set for tooltips and the settings list.</summary>
    private string PresetNamesText(Preset preset)
    {
        var names = new List<string>();
        foreach (var id in preset.AppIds)
        {
            var app = _settings.FindById(id);
            if (app != null) names.Add(app.Name);
        }
        return names.Count == 0 ? "пусто" : string.Join(" · ", names);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Preset preset }) return;

        var launched = new List<string>();
        foreach (var id in preset.AppIds)
        {
            var app = _settings.FindById(id);
            if (app != null && Launch(app)) launched.Add(app.Name);
        }

        if (launched.Count == 0)
        {
            ShowToast("В наборе «" + preset.Name + "» нет доступных приложений");
            return;
        }

        ShowToast("Набор «" + preset.Name + "»: " + string.Join(" · ", launched));
        AfterLaunch(preset: true);
    }

    private void CreatePreset_Click(object sender, RoutedEventArgs e)
    {
        _suppressDeactivate = true;
        try
        {
            var editor = new PresetEditorWindow(_settings.Apps, null) { Owner = this };
            if (editor.ShowDialog() == true && editor.Result != null)
            {
                _settings.Presets.Add(editor.Result);
                SettingsStore.Save(_settings);
                BuildPresets();
                RefreshSettingsLists();
                ShowToast("Набор «" + editor.Result.Name + "» создан");
            }
        }
        finally
        {
            _suppressDeactivate = false;
            Activate();
        }
    }

    private void RemovePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Preset preset }) return;
        _settings.Presets.Remove(preset);
        SettingsStore.Save(_settings);
        BuildPresets();
        RefreshSettingsLists();
        ShowToast("Набор «" + preset.Name + "» удалён");
    }

    // ---------- settings ----------

    private void InitControlsFromSettings()
    {
        _initializing = true;

        ThemeSwitch.IsChecked = _settings.LightTheme;
        BlurSwitch.IsChecked = _settings.Blur;
        AutostartSwitch.IsChecked = _settings.Autostart;
        HttpApiSwitch.IsChecked = _settings.HttpApiEnabled;
        FolderAutoDeleteSwitch.IsChecked = _settings.FolderAutoDelete;
        FolderDaysBox.Text = (_settings.FolderAutoDeleteDays > 0 ? _settings.FolderAutoDeleteDays : 30).ToString();
        RefreshFolderDays();

        TypeSearchSwitch.IsChecked = _settings.TypeToSearch;
        AutoClearSwitch.IsChecked = _settings.SearchAutoClear;
        ShowPresetsSwitch.IsChecked = _settings.ShowPresetsOnMain;
        SortByUsageSwitch.IsChecked = _settings.SortByUsage;
        RefreshAutoClearRow();
        ApplyPresetsFooter();
        ApplyCollapsedSections();

        HotkeyCombo.ItemsSource = Hotkeys.Select(h => h.Display).ToList();
        var display = Hotkeys.FirstOrDefault(h => h.Spec == _settings.Hotkey).Display;
        HotkeyCombo.SelectedItem = display ?? Hotkeys[0].Display;

        ScaleCombo.ItemsSource = UiScales.Select(s => (int)Math.Round(s * 100) + " %").ToList();
        var current = _settings.UiScale > 0 ? _settings.UiScale : 1.1;
        var index = Array.FindIndex(UiScales, s => Math.Abs(s - current) < 0.001);
        ScaleCombo.SelectedIndex = index >= 0 ? index : 2;

        _initializing = false;
        ApplySearchMode();
    }

    private void ScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var index = ScaleCombo.SelectedIndex;
        if (index < 0 || index >= UiScales.Length) return;

        _settings.UiScale = UiScales[index];
        SettingsStore.Save(_settings);
        PositionWindow();
        ShowToast("Масштаб: " + (int)Math.Round(_settings.UiScale * 100) + " %");
    }

    // ---------- ui scale ----------

    private void ApplyUiScale(double scale)
    {
        if (scale <= 0) scale = 1.1;
        _appliedScale = scale;
        Shell.LayoutTransform = new ScaleTransform(scale, scale);
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;
    }

    public void SyncToggles()
    {
        _initializing = true;
        ThemeSwitch.IsChecked = _settings.LightTheme;
        BlurSwitch.IsChecked = _settings.Blur;
        AutostartSwitch.IsChecked = _settings.Autostart;
        HttpApiSwitch.IsChecked = _settings.HttpApiEnabled;
        FolderAutoDeleteSwitch.IsChecked = _settings.FolderAutoDelete;
        FolderDaysBox.Text = (_settings.FolderAutoDeleteDays > 0 ? _settings.FolderAutoDeleteDays : 30).ToString();
        RefreshFolderDays();
        TypeSearchSwitch.IsChecked = _settings.TypeToSearch;
        AutoClearSwitch.IsChecked = _settings.SearchAutoClear;
        ShowPresetsSwitch.IsChecked = _settings.ShowPresetsOnMain;
        SortByUsageSwitch.IsChecked = _settings.SortByUsage;
        RefreshAutoClearRow();
        ApplyPresetsFooter();
        ApplyCollapsedSections();
        _initializing = false;
    }

    private void SettingsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsToggle.IsChecked == true) OpenSettings();
        else CloseSettings();
    }

    /// <summary>The window has no title bar any more, so its empty surface is the handle.</summary>
    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;

        try { DragMove(); }
        catch (InvalidOperationException) { /* the button let go before the drag started */ }
    }

    /// <summary>Walks up from whatever was clicked; anything that takes input keeps it instead of dragging the window.</summary>
    private static bool IsInteractive(DependencyObject? source)
    {
        for (var node = source; node != null; node = ParentOf(node))
        {
            if (node is ButtonBase or TextBoxBase or ListBoxItem or ScrollBar or ComboBox or Thumb)
                return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    public void ShowSettings()
    {
        ShowOverlay();
        OpenSettings();
    }

    public void OpenSettings()
    {
        ResetDropHints();
        SettingsView.Visibility = Visibility.Visible;
        SettingsToggle.IsChecked = true;
        RefreshSettingsLists();
        UpdateTabCounts();
    }

    private void CloseSettings()
    {
        ResetDropHints();
        SettingsView.Visibility = Visibility.Collapsed;
        SettingsToggle.IsChecked = false;
    }

    private void RefreshSettingsLists()
    {
        SettingsAppsList.ItemsSource = null;
        SettingsAppsList.ItemsSource = _settings.Apps.ToList();
        foreach (var preset in _settings.Presets)
            preset.AppsText = PresetNamesText(preset);
        SettingsPresetsList.ItemsSource = null;
        SettingsPresetsList.ItemsSource = _settings.Presets.ToList();

        UpdateCategoryCounts();
        SettingsCategoriesList.ItemsSource = null;
        SettingsCategoriesList.ItemsSource = _settings.Categories.ToList();

        AppsHint.Text = $"{_settings.Apps.Count} {Plural(_settings.Apps.Count, "приложение", "приложения", "приложений")} в списке";
        UpdateTabCounts();
    }

    private void ThemeSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.LightTheme = ThemeSwitch.IsChecked == true;
        SettingsStore.Save(_settings);
        ThemeManager.Apply(_settings.LightTheme);
        ApplyBackdropVisual();
        ShowToast(_settings.LightTheme ? "Светлая тема" : "Тёмная тема");
    }

    private void BlurSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.Blur = BlurSwitch.IsChecked == true;
        SettingsStore.Save(_settings);
        ApplyBackdropVisual();
        ShowToast(_settings.Blur
            ? (_acrylicAvailable ? "Акрил включён" : "На этой версии Windows акрил недоступен")
            : "Акрил выключен");
    }

    /// <summary>
    /// Asks Windows for a live acrylic backdrop. The acrylic material carries the theme tint
    /// itself, so when it is on the body stays fully transparent; when the system cannot do it
    /// the same border simply becomes an opaque fill.
    /// </summary>
    private void ApplyBackdropVisual()
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd != IntPtr.Zero)
            _acrylicAvailable = Acrylic.Apply(_hwnd, _settings.LightTheme, _settings.Blur);

        var live = _settings.Blur && _acrylicAvailable;
        BodyBackdrop.Background = (Brush)FindResource(live ? "AcrylicHitBrush" : "BodyBgBrush");
        UpdateWindowRegion();
    }

    /// <summary>
    /// A layered window never gets the system's rounded corners, so the window is clipped to a
    /// rounded rectangle by hand; otherwise the acrylic would show square corners.
    /// </summary>
    private void UpdateWindowRegion()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!GetWindowRect(_hwnd, out var rect)) return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;

        // the visible corner radius is 8 DIP on the Shell, which the UI scale stretches
        var dpi = VisualTreeHelper.GetDpi(this);
        var radius = (int)Math.Round(8 * _appliedScale * dpi.DpiScaleX);
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        SetWindowRgn(_hwnd, region, true);
    }

    private void AutostartSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.Autostart = AutostartSwitch.IsChecked == true;
        StartupManager.Set(_settings.Autostart);
        SettingsStore.Save(_settings);
        ShowToast(_settings.Autostart ? "Автозапуск включён" : "Автозапуск выключен");
    }

    private void HttpApiSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var wanted = HttpApiSwitch.IsChecked == true;
        _settings.HttpApiEnabled = wanted;
        if (!ApplyHttpApiSetting())
        {
            _initializing = true;
            HttpApiSwitch.IsChecked = false;
            _initializing = false;
            return;
        }

        SettingsStore.Save(_settings);
        ShowToast(wanted ? "HTTP-сервер включён" : "HTTP-сервер выключен");
    }

    /// <summary>
    /// Starts or stops the local API to match the setting. A busy port turns the setting back off
    /// instead of leaving the switch on while nothing is listening.
    /// </summary>
    private bool ApplyHttpApiSetting()
    {
        if (App.SetHttpApiEnabled(_settings.HttpApiEnabled)) return true;
        if (!_settings.HttpApiEnabled) return true;

        _settings.HttpApiEnabled = false;
        SettingsStore.Save(_settings);
        ShowToast("HTTP-сервер не запущен: порт " + HttpApi.Port + " занят");
        return false;
    }

    /// <summary>The day count only makes sense while the switch is on, so it hides with it.</summary>
    private void RefreshFolderDays()
    {
        FolderDaysRow.Visibility = FolderAutoDeleteSwitch.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void FolderAutoDeleteSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.FolderAutoDelete = FolderAutoDeleteSwitch.IsChecked == true;
        SettingsStore.Save(_settings);
        RefreshFolderDays();
        ShowToast(_settings.FolderAutoDelete
            ? "Новые папки будут убираться через " + _settings.FolderAutoDeleteDays + " дн. без открытий"
            : "Новые папки останутся навсегда");
    }

    private void FolderDaysBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitFolderDays(); Keyboard.ClearFocus(); }
    }

    private void FolderDaysBox_LostFocus(object sender, RoutedEventArgs e) => CommitFolderDays();

    /// <summary>Takes whatever is in the field, keeps it sane, and writes the cleaned value back.</summary>
    private void CommitFolderDays()
    {
        if (_initializing) return;

        var days = int.TryParse(FolderDaysBox.Text.Trim(), out var parsed) ? parsed : _settings.FolderAutoDeleteDays;
        days = Math.Clamp(days, 1, 3650);

        FolderDaysBox.Text = days.ToString();
        if (days == _settings.FolderAutoDeleteDays) return;

        _settings.FolderAutoDeleteDays = days;
        SettingsStore.Save(_settings);
        ShowToast("Срок для новых папок: " + days + " дн.");
    }

    private void HotkeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (HotkeyCombo.SelectedItem is not string display) return;

        var spec = Hotkeys.FirstOrDefault(h => h.Display == display).Spec;
        if (string.IsNullOrEmpty(spec) || spec == _settings.Hotkey) return;

        _settings.Hotkey = spec;
        SettingsStore.Save(_settings);
        RegisterHotkey();
        ShowToast("Хоткей: " + display);
    }

    private void RegisterHotkey()
    {
        if (_hotkey == null) return;
        var ok = _hotkey.Register(_settings.Hotkey);
        if (!ok) ShowToast("Хоткей «" + _settings.Hotkey + "» занят — выбери другой");
    }

    // ---------- export / import / backup / reset ----------

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        _suppressDeactivate = true;
        try
        {
            var dialog = new WinForms.SaveFileDialog
            {
                Title = "Экспорт настроек",
                Filter = "Настройки VLAP|*.json|Все файлы|*.*",
                FileName = "VLAP-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".json",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;

            ShowToast(SettingsStore.WriteFile(dialog.FileName, _settings)
                ? "Копия настроек: " + Path.GetFileName(dialog.FileName)
                : "Не удалось записать файл");
        }
        finally
        {
            _suppressDeactivate = false;
            Activate();
        }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        _suppressDeactivate = true;
        try
        {
            var dialog = new WinForms.OpenFileDialog
            {
                Title = "Импорт настроек",
                Filter = "Настройки VLAP|*.json|Все файлы|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;

            var loaded = SettingsStore.ReadFile(dialog.FileName);
            if (loaded == null)
            {
                ShowToast("Файл не читается как настройки VLAP");
                return;
            }

            if (!Confirm("Заменить все настройки содержимым «" + Path.GetFileName(dialog.FileName) + "»?\n" +
                         "Текущее состояние уйдёт во внутренний бэкап, его вернёт кнопка «Откат»."))
                return;

            // keep what is here now, so the import can be undone
            SettingsStore.WriteSnapshot(_settings);
            loaded.EnsureIds();
            _settings.CopyFrom(loaded);
            SettingsStore.Save(_settings);
            ApplyReloadedSettings();
            ShowToast("Настройки импортированы из " + Path.GetFileName(dialog.FileName));
        }
        finally
        {
            _suppressDeactivate = false;
            Activate();
        }
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        _suppressDeactivate = true;
        try
        {
            var snapshot = SettingsStore.LoadSnapshot();
            if (snapshot == null)
            {
                ShowToast("Внутреннего бэкапа ещё нет");
                return;
            }

            if (!Confirm("Вернуть настройки из внутреннего бэкапа?\nТекущее состояние будет заменено.")) return;

            snapshot.EnsureIds();
            _settings.CopyFrom(snapshot);
            SettingsStore.Save(_settings);
            ApplyReloadedSettings();
            ShowToast("Настройки возвращены из внутреннего бэкапа");
        }
        finally
        {
            _suppressDeactivate = false;
            Activate();
        }
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        _suppressDeactivate = true;
        try
        {
            if (!Confirm("Сбросить все настройки к базовым?\n" +
                         "Приложения, категории и наборы будут убраны. Текущее состояние уйдёт во внутренний бэкап — «Откат» его вернёт."))
                return;

            // the backup stays: a reset is exactly the case «Откат» exists for
            SettingsStore.WriteSnapshot(_settings);
            _settings.CopyFrom(new AppSettings());
            SettingsStore.Save(_settings);
            ApplyReloadedSettings();
            ShowToast("Базовые настройки. Прежние сохранены во внутреннем бэкапе");
        }
        finally
        {
            _suppressDeactivate = false;
            Activate();
        }
    }

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, "VLAP", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>Puts a settings object that arrived from a file on screen without restarting the app.</summary>
    private void ApplyReloadedSettings()
    {
        _settings.EnsureIds();

        _query = "";
        _heldTerm = "";
        _category = "all";

        SyncCategoryRegistry();
        NormalizeCategories();

        ThemeManager.Apply(_settings.LightTheme);
        StartupManager.Set(_settings.Autostart);
        ApplyHttpApiSetting();
        ApplyBackdropVisual();

        BuildTabs();
        SetCategory("all");
        BuildPresets();
        RefreshSettingsLists();
        InitControlsFromSettings();   // also filters the grid through ApplySearchMode
        ApplyUiScale(_settings.UiScale);
        PositionWindow();
        RegisterHotkey();

        _iconsLoaded = false;
        _ = LoadIconsAsync();
        RefreshRunning();
        App.SyncTrayAutostart();
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        _suppressDeactivate = true;
        try
        {
            var dialog = new WinForms.OpenFileDialog
            {
                Title = "Добавить приложение",
                Filter = "Приложения|*.exe;*.lnk;*.url;*.py|Все файлы|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;

            var path = dialog.FileName;
            var name = Path.GetFileNameWithoutExtension(path);

            if (AddPaths(new[] { path }) > 0) ShowToast("Добавлено: " + name);
            else ShowToast("Уже в списке или неподдерживаемый файл");
        }
        finally
        {
            _suppressDeactivate = false;
            Activate();
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        _suppressDeactivate = true;
        try
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "Какую папку добавить в лаунчер",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;

            var path = dialog.SelectedPath;
            if (string.IsNullOrWhiteSpace(path)) return;

            var name = new DirectoryInfo(path).Name;
            if (string.IsNullOrWhiteSpace(name)) name = path.TrimEnd(Path.DirectorySeparatorChar);

            if (AddPaths(new[] { path }) > 0) ShowToast("Папка добавлена: " + name);
            else ShowToast("Эта папка уже в списке");
        }
        catch (Exception ex)
        {
            SettingsStore.Log("add folder: " + ex.Message);
            ShowToast("Не получилось добавить папку");
        }
        finally
        {
            _suppressDeactivate = false;
            Activate();
        }
    }

    // ---------- drag & drop ----------

    private void SetupDropTargets()
    {
        // dropping on the main grid while a category tab is active tags the new entry with it;
        // under «Все» there is no tag
        SetupDropTarget(AppsDropZone,
            on => { if (on) ShowDropHint(AppsDropHint); else HideDropHint(AppsDropHint); },
            () => _category == "all" ? null : _category);
        SetupDropTarget(SettingsAppsDropZone,
            on => { if (on) ShowDropHint(SettingsAppsDropHint); else HideDropHint(SettingsAppsDropHint); });
    }

    /// <summary>Shows one drop hint and starts the safety timer that clears it once no button is held.</summary>
    private void ShowDropHint(Border hint)
    {
        AppsDropHint.Visibility = hint == AppsDropHint ? Visibility.Visible : Visibility.Collapsed;
        SettingsAppsDropHint.Visibility = hint == SettingsAppsDropHint ? Visibility.Visible : Visibility.Collapsed;

        if (_dropHintTimer == null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                // a file drag cannot continue without a held mouse button: the drag was
                // cancelled, dropped somewhere else, or its source died without a DragLeave
                if (AnyMouseButtonDown()) return;
                ResetDropHints();
                _fileDragOver = false;
            };
            _dropHintTimer = timer;
        }
        _dropHintTimer.Start();
    }

    private void HideDropHint(Border hint)
    {
        hint.Visibility = Visibility.Collapsed;
        if (AppsDropHint.Visibility != Visibility.Visible &&
            SettingsAppsDropHint.Visibility != Visibility.Visible)
            _dropHintTimer?.Stop();
    }

    /// <summary>Clears every drop hint. Called whenever the HUD opens, closes or switches screens.</summary>
    private void ResetDropHints()
    {
        AppsDropHint.Visibility = Visibility.Collapsed;
        SettingsAppsDropHint.Visibility = Visibility.Collapsed;
        _dropHintTimer?.Stop();
    }

    private void SetupDropTarget(UIElement zone, Action<bool> highlight, Func<string?>? category = null)
    {
        zone.AllowDrop = true;

        zone.DragEnter += (_, e) => OnZoneDragOver(zone, e, highlight);
        zone.DragOver += (_, e) => OnZoneDragOver(zone, e, highlight);
        zone.DragLeave += (_, e) =>
        {
            // drag events also fire when the pointer moves between children — only react
            // once the pointer has really left this drop zone
            var p = e.GetPosition(zone);
            if (p.X < 0 || p.Y < 0 || p.X > zone.RenderSize.Width || p.Y > zone.RenderSize.Height)
            {
                _fileDragOver = false;
                highlight(false);
            }
        };
        zone.Drop += (_, e) =>
        {
            _fileDragOver = false;
            CancelDeferredHide();
            ResetDropHints();
            if (e.Data.GetDataPresent(TileDragFormat)) return; // a tile being moved, not a file
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

            // read the tab at drop time: the user may have switched while dragging
            var added = AddPaths(paths, category?.Invoke());
            if (added > 0) ShowToast($"Добавлено: {added} {Plural(added, "элемент", "элемента", "элементов")}");
            else ShowToast("Эти файлы уже в списке");
            e.Handled = true;

            // the drop does not activate the window: without this the next click elsewhere
            // would not deactivate it and the HUD would stay open until the hotkey
            Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) Activate(); }),
                DispatcherPriority.Input);
        };
    }

    private void OnZoneDragOver(UIElement zone, DragEventArgs e, Action<bool> highlight)
    {
        if (e.Data.GetDataPresent(TileDragFormat))
        {
            _fileDragOver = false; // tile reordering has its own target
            ResetDropHints();
            return;
        }

        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        if (ok)
        {
            _fileDragOver = true;
            CancelDeferredHide();
        }
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        highlight(ok);
        e.Handled = true;
    }

    private int AddPaths(IEnumerable<string> paths, string? category = null)
    {
        var added = 0;
        foreach (var path in paths)
            if (AddPath(path, category)) added++;

        if (added == 0) return 0;

        // new entries stay at the end: the rest of the list is in whatever order the user dragged it into
        SettingsStore.Save(_settings);
        _iconsLoaded = false;
        _ = LoadIconsAsync();
        RefreshSettingsLists();
        Filter();
        RefreshRunning();
        return added;
    }

    /// <summary>Turns a dropped/picked exe, shortcut or folder into a list entry. False if it is already there.</summary>
    private bool AddPath(string path, string? category = null)
    {
        try
        {
            var (kind, executable, icon, target, args) = AppPath.Describe(path);
            if (kind.Length == 0) return false;
            if (_settings.Apps.Any(a => string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase)))
                return false;

            var name = kind == "folder"
                ? new DirectoryInfo(target).Name
                : Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name)) name = target.TrimEnd(Path.DirectorySeparatorChar);

            _settings.Removed.RemoveAll(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase));
            var entry = new AppEntry
            {
                Id = AppEntry.NewId(),
                Name = name,
                Target = target,
                Args = args,
                IconSource = icon,
                LaunchKind = kind,
                Executable = executable,
                // categories are tags: a new entry starts with none and shows up under «Все»
                // a folder starts its countdown the moment it is added, using the defaults from settings
                LastOpenedAt = kind == "folder" ? DateTime.UtcNow : null,
                AutoDelete = _settings.FolderAutoDelete,
                AutoDeleteDays = _settings.FolderAutoDeleteDays > 0 ? _settings.FolderAutoDeleteDays : 30
            };

            // dropped into a specific category tab — carry that tag; stale keys are ignored
            if (!string.IsNullOrEmpty(category) && CategoryRegistry.Exists(category))
            {
                entry.Categories.Add(category!);
                entry.RefreshCategoryLabel();
            }

            _settings.Apps.Add(entry);
            return true;
        }
        catch (Exception ex)
        {
            SettingsStore.Log("add path " + path + ": " + ex.Message);
            return false;
        }
    }

    // ---------- categories ----------

    /// <summary>Opens the tag picker for one app: every category is a checkbox, so an app can sit in several or none.</summary>
    private void CategoryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppEntry app } button) return;

        if (_settings.Categories.Count == 0)
        {
            ShowToast("Сначала добавь категорию ниже");
            return;
        }

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkMenu"),
            PlacementTarget = button,
            Placement = PlacementMode.Bottom
        };

        foreach (var category in _settings.Categories)
        {
            var key = category.Key;
            var item = new MenuItem
            {
                Style = (Style)FindResource("DarkMenuItem"),
                Header = category.Label,
                IsCheckable = true,
                IsChecked = app.InCategory(key),
                StaysOpenOnClick = true // the menu stays up so several tags can be ticked in a row
            };
            item.Click += (_, _) => ToggleAppTag(app, key);
            menu.Items.Add(item);
        }

        if (_settings.Categories.Count > 0)
        {
            menu.Items.Add(new Separator { Margin = new Thickness(2, 4, 2, 4) });
            var none = new MenuItem
            {
                Style = (Style)FindResource("DarkMenuItem"),
                Header = "Без категории",
                IsCheckable = true,
                IsChecked = app.Categories.Count == 0,
                StaysOpenOnClick = false
            };
            none.Click += (_, _) =>
            {
                app.Categories.Clear();
                AfterTagsChanged(app);
            };
            menu.Items.Add(none);
        }

        menu.IsOpen = true;
    }

    private void ToggleAppTag(AppEntry app, string key)
    {
        if (app.InCategory(key)) app.Categories.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        else app.Categories.Add(key);

        AfterTagsChanged(app);
    }

    private void AfterTagsChanged(AppEntry app)
    {
        app.RefreshCategoryLabel();
        SettingsStore.Save(_settings);
        UpdateCategoryCounts();
        UpdateTabCounts();
        Filter();
    }

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var n = _settings.Categories.Count + 1;
        string key;
        do { key = "cat" + n++; }
        while (_settings.Categories.Any(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)));

        _settings.Categories.Add(new AppCategory { Key = key, Label = "Новая категория" });
        SettingsStore.Save(_settings);

        SyncCategoryRegistry();
        RebuildTabs();
        RefreshSettingsLists();
        ShowToast("Категория добавлена — переименуй её в списке");
    }

    private void CategoryLabel_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_categoryEditReady) return;
        if (sender is not TextBox { Tag: AppCategory category } box) return;

        var text = box.Text.Trim();
        if (text.Length == 0) return;
        if (string.Equals(category.Label, text, StringComparison.Ordinal)) return;

        category.Label = text;
        SettingsStore.Save(_settings);
        SyncCategoryRegistry();
        RebuildTabs();
    }

    private void RemoveCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppCategory category }) return;

        _settings.Categories.Remove(category);
        var affected = 0;

        foreach (var app in _settings.Apps)
        {
            if (!app.InCategory(category.Key)) continue;
            app.Categories.RemoveAll(k => string.Equals(k, category.Key, StringComparison.OrdinalIgnoreCase));
            app.RefreshCategoryLabel();
            affected++;
        }

        if (string.Equals(_category, category.Key, StringComparison.OrdinalIgnoreCase))
            _category = "all";

        SettingsStore.Save(_settings);
        SyncCategoryRegistry();
        RebuildTabs();
        RefreshSettingsLists();
        Filter();
        ShowToast(affected == 0
            ? "«" + category.Label + "» удалена"
            : "«" + category.Label + "» удалена, снята с " + affected + " " + Plural(affected, "приложения", "приложений", "приложений"));
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppEntry app }) return;
        RemoveApp(app);
    }

    private void RemoveApp(AppEntry app)
    {
        _settings.Apps.Remove(app);
        foreach (var preset in _settings.Presets)
            preset.AppIds.RemoveAll(id => string.Equals(id, app.Id, StringComparison.OrdinalIgnoreCase));
        if (!_settings.Removed.Contains(app.Name, StringComparer.OrdinalIgnoreCase))
            _settings.Removed.Add(app.Name);
        SettingsStore.Save(_settings);
        BuildPresets();
        RefreshSettingsLists();
        Filter();
        ShowToast("Убрано: " + app.Name);
    }

    // ---------- editing an entry ----------

    private void EditApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppEntry app }) EditApp(app);
    }

    /// <summary>Right-click on a tile: change it, show its folder, or drop it from the list.</summary>
    private void AppsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var container = source != null
            ? ItemsControl.ContainerFromElement(AppsList, source) as ListBoxItem
            : null;
        if (container?.DataContext is not AppEntry app)
        {
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkMenu"),
            PlacementTarget = container,
            Placement = PlacementMode.MousePoint
        };
        menu.Items.Add(DarkMenuItem("Изменить…", () => EditApp(app)));
        if (app.LaunchKind == "folder" || File.Exists(app.Target))
            menu.Items.Add(DarkMenuItem("Открыть папку", () => OpenContainingFolder(app)));
        if (app.LaunchKind != "folder" && app.LaunchKind != "url")
            menu.Items.Add(DarkMenuItem("От администратора", () => ToggleAdmin(app), app.RunAsAdmin));
        menu.Items.Add(DarkMenuItem("Убрать", () => RemoveApp(app)));

        menu.IsOpen = true;
        e.Handled = true;
    }

    private MenuItem DarkMenuItem(string header, Action action, bool? isChecked = null)
    {
        var item = new MenuItem { Style = (Style)FindResource("DarkMenuItem"), Header = header };
        if (isChecked != null)
        {
            item.IsCheckable = true;
            item.IsChecked = isChecked.Value;
        }
        item.Click += (_, _) => action();
        return item;
    }

    private void ToggleAdmin(AppEntry app)
    {
        app.RunAsAdmin = !app.RunAsAdmin;
        SettingsStore.Save(_settings);
        RefreshSettingsLists();
        ShowToast(app.Name + ": " + (app.RunAsAdmin ? "запуск от администратора" : "обычный запуск"));
    }

    private void EditApp(AppEntry app)
    {
        _suppressDeactivate = true;
        try
        {
            var editor = new AppEditorWindow(_settings.Categories, app) { Owner = this };
            if (editor.ShowDialog() != true || editor.Result == null) return;

            var updated = editor.Result;

            app.Name = updated.Name;
            app.Target = updated.Target;
            app.Args = updated.Args;
            app.LaunchKind = updated.LaunchKind;
            app.Executable = updated.Executable;
            app.IconSource = updated.IconSource;
            app.RunAsAdmin = updated.RunAsAdmin;
            app.AutoDelete = updated.AutoDelete;
            app.AutoDeleteDays = updated.AutoDeleteDays;
            app.Categories = updated.Categories ?? new List<string>();
            app.RefreshCategoryLabel();

            SettingsStore.Save(_settings);
            BuildPresets();
            _iconsLoaded = false;
            _ = LoadIconsAsync();
            RefreshSettingsLists();
            Filter();
            RefreshRunning();
            ShowToast("Изменено: " + app.Name);
        }
        finally
        {
            _suppressDeactivate = false;
            Activate();
        }
    }

    private void OpenContainingFolder(AppEntry app)
    {
        try
        {
            if (app.LaunchKind == "folder" || Directory.Exists(app.Target))
            {
                Process.Start(new ProcessStartInfo { FileName = app.Target, UseShellExecute = true });
                if (app.IsFolder) RestartFolderTimer(app);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + app.Target + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { SettingsStore.Log("open folder " + app.Name + ": " + ex.Message); }
    }

    // ---------- toast ----------

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        FadeToast(1);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void FadeToast(double to)
    {
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 140 : 220)));
    }

    // ---------- helpers ----------

    // ---------- local API ----------

    /// <summary>
    /// Snapshot for GET /apps. The entry fields are copied on the UI thread because settings mutate there;
    /// the process scan is done outside it, since it costs more than a UI tick.
    /// </summary>
    public AppDto[] ApiApps()
    {
        var apps = Dispatcher.Invoke(() => _settings.Apps
            .Select(a => (
                a.Id,
                a.Name,
                Type: string.Equals(a.LaunchKind, "folder", StringComparison.OrdinalIgnoreCase) ? "folder" : "app",
                a.Target,
                Categories: a.Categories?.ToArray() ?? Array.Empty<string>(),
                a.Executable))
            .ToArray());

        var running = RunningProcessNames();
        return apps.Select(a => new AppDto
        {
            Id = a.Id,
            Name = a.Name,
            Type = a.Type,
            Resource = a.Target,
            Categories = a.Categories,
            Running = IsProcessRunning(a.Executable, running)
        }).ToArray();
    }

    /// <summary>GET /presets — every set with the ids it launches.</summary>
    public PresetDto[] ApiPresets() => Dispatcher.Invoke(() =>
        _settings.Presets.Select(p => new PresetDto
        {
            Name = p.Name,
            Apps = p.AppIds.ToArray(),
            AppNames = p.AppIds.Select(id => _settings.FindById(id)?.Name ?? id).ToArray()
        }).ToArray());

    /// <summary>GET /state — what the launcher is showing right now.</summary>
    public object ApiState() => Dispatcher.Invoke(() =>
    {
        var counts = CountByCategory();
        return new
        {
            visible = IsVisible,
            apps = _settings.Apps.Count,
            presets = _settings.Presets.Count,
            categories = _settings.Categories.Select(c => new
            {
                key = c.Key,
                label = c.Label,
                count = counts.GetValueOrDefault(c.Key)
            }).ToArray()
        };
    });

    /// <summary>POST /launch — starts one app in the background: no HUD, no toast, no focus change.</summary>
    public (int Status, object Body) ApiLaunch(string? id) => Dispatcher.Invoke(() =>
    {
        var app = _settings.FindById(id);
        if (app == null)
            return (404, (object)new { error = "unknown app id", id = id ?? "" });

        if (!Launch(app, notify: false))
            return (500, (object)new { ok = false, error = "launch failed", id = app.Id, name = app.Name });

        return (200, (object)new { ok = true, id = app.Id, name = app.Name });
    });

    /// <summary>POST /presets/run — starts every app of the set, silently, like the API launch.</summary>
    public (int Status, object Body) ApiRunPreset(string? name) => Dispatcher.Invoke(() =>
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
            return (400, (object)new { error = "name is required" });

        var preset = _settings.Presets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset == null)
            return (404, (object)new { error = "unknown preset", name });

        var launched = new List<string>();
        var failed = new List<string>();
        foreach (var appId in preset.AppIds)
        {
            var app = _settings.FindById(appId);
            if (app == null) continue;
            if (Launch(app, notify: false)) launched.Add(app.Name);
            else failed.Add(app.Name);
        }

        return (200, (object)new
        {
            ok = launched.Count > 0,
            name = preset.Name,
            launched,
            failed
        });
    });

    /// <summary>DELETE /presets/{name} — drops the set; the apps themselves stay.</summary>
    public (int Status, object Body) ApiDeletePreset(string? name) => Dispatcher.Invoke(() =>
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
            return (400, (object)new { error = "name is required" });

        var preset = _settings.Presets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset == null)
            return (404, (object)new { error = "unknown preset", name });

        _settings.Presets.Remove(preset);
        SettingsStore.Save(_settings);
        BuildPresets();
        RefreshSettingsLists();

        return (200, (object)new { ok = true, name = preset.Name, removed = true });
    });

    /// <summary>POST /scenarios — turns existing app ids into a preset.</summary>
    public (int Status, object Body) ApiCreateScenario(string? name, IReadOnlyList<string>? ids) =>
        Dispatcher.Invoke(() =>
        {
            name = (name ?? "").Trim();
            ids ??= Array.Empty<string>();

            if (name.Length == 0)
                return (400, (object)new { error = "name is required" });
            if (ids.Count == 0)
                return (400, (object)new { error = "apps is required" });

            var byId = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in _settings.Apps)
                if (!string.IsNullOrEmpty(app.Id)) byId[app.Id] = app;

            var unknown = ids.Where(id => !byId.ContainsKey(id))
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (unknown.Count > 0)
                return (400, (object)new { error = "unknown app id", unknown });

            var scenarioIds = new List<string>();
            var names = new List<string>();
            foreach (var id in ids)
            {
                var appName = byId[id].Name;
                if (names.Contains(appName, StringComparer.OrdinalIgnoreCase)) continue;
                names.Add(appName);
                scenarioIds.Add(byId[id].Id);
            }

            var existing = _settings.Presets.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            var updated = existing != null;
            if (existing == null)
            {
                existing = new Preset { Name = name };
                _settings.Presets.Add(existing);
            }
            existing.AppIds = scenarioIds;
            existing.Apps.Clear();

            SettingsStore.Save(_settings);
            BuildPresets();
            RefreshSettingsLists();

            return (updated ? 200 : 201, (object)new { ok = true, name, apps = names, updated });
        });

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);
}
