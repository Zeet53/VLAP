using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace VLAP;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static MainWindow? Overlay { get; private set; }
    public static App? Instance => Current as App;

    private static Mutex? _mutex;
    private HttpApi? _api;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _autostartItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SettingsStore.Log("startup: begin");

        try
        {
            StartCore(e);
        }
        catch (Exception ex)
        {
            SettingsStore.Log("startup FAILED: " + ex);
            throw;
        }
    }

    private void StartCore(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Local\VLAP_SingleInstance", out var created);
        SettingsStore.Log("startup: mutex created=" + created);
        if (!created)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            SettingsStore.Log("UNHANDLED: " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            SettingsStore.Log("FATAL: " + args.ExceptionObject);

        Settings = SettingsStore.Load();
        SettingsStore.EnsureSnapshot();
        var upgraded = Settings.EnsureIds();

        // Folder cleanup is done once here, not by a background timer: it costs nothing while the launcher runs.
        var (expired, touched) = Settings.PurgeExpiredFolders(DateTime.UtcNow);
        var firstRun = !File.Exists(SettingsStore.FilePath);
        if (upgraded || touched) SettingsStore.Save(Settings);

        SettingsStore.Log("startup: settings loaded, apps=" + Settings.Apps.Count + ", firstRun=" + firstRun);

        // the palette has to be in place before the first window lays itself out
        ThemeManager.Apply(Settings.LightTheme);

        StartupManager.Set(Settings.Autostart);

        Overlay = new MainWindow(Settings);
        SettingsStore.Log("startup: window constructed");
        Overlay.Show();
        Overlay.Hide();
        Overlay.TrimWhenIdle();
        SettingsStore.Log("startup: window shown/hidden");

        BuildTray();
        SettingsStore.Log("startup: tray built");

        if (expired.Count > 0)
        {
            var names = string.Join(", ", expired.Select(a => a.Name));
            SettingsStore.Log("auto-delete: folders dropped after their time was up: " + names);
            _tray?.ShowBalloonTip(6000, "VLAP",
                "Убраны папки, которые давно не открывались: " + names, WinForms.ToolTipIcon.Info);
        }

        if (Settings.HttpApiEnabled && !SetHttpApiEnabled(true))
            _tray?.ShowBalloonTip(6000, "VLAP",
                "HTTP-сервер не запущен: порт " + HttpApi.Port + " занят", WinForms.ToolTipIcon.Warning);

        var showRequested = Array.Exists(e.Args, a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase));
        var settingsRequested = Array.Exists(e.Args, a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));

        if (showRequested)
        {
            Overlay.ShowOverlay();
            SettingsStore.Log("startup: overlay shown by --show");
        }
        else if (settingsRequested)
        {
            Overlay.ShowSettings();
            SettingsStore.Log("startup: settings shown by --settings");
        }
        else if (firstRun)
        {
            Overlay.ShowOverlay();
            Overlay.OpenSettings();
            SettingsStore.Log("startup: first-run overlay shown");
        }
        SettingsStore.Log("startup: done");
    }

    private void BuildTray()
    {
        Icon icon;
        try
        {
            var exe = Environment.ProcessPath;
            icon = (exe != null ? Icon.ExtractAssociatedIcon(exe) : null) ?? SystemIcons.Application;
        }
        catch { icon = SystemIcons.Application; }

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Открыть HUD", null, (_, _) => Overlay?.ShowOverlay());
        menu.Items.Add("Настройки", null, (_, _) => Overlay?.ShowSettings());
        menu.Items.Add(new WinForms.ToolStripSeparator());

        _autostartItem = new WinForms.ToolStripMenuItem("Запускать при старте Windows")
        {
            Checked = Settings.Autostart,
            CheckOnClick = true
        };
        _autostartItem.Click += (_, _) =>
        {
            Settings.Autostart = _autostartItem.Checked;
            StartupManager.Set(Settings.Autostart);
            SettingsStore.Save(Settings);
            Overlay?.SyncToggles();
        };
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ShutdownApp());

        _tray = new WinForms.NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "VLAP",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => Overlay?.ShowOverlay();
        _tray.BalloonTipTitle = "VLAP";
        _tray.BalloonTipText = "Работает в фоне. Нажми хоткей, чтобы открыть.";
    }

    /// <summary>Keeps the tray menu in step when settings arrive from a file rather than from that menu.</summary>
    public static void SyncTrayAutostart()
    {
        if (Instance?._autostartItem is { } item) item.Checked = Settings.Autostart;
    }

    public static void ShutdownApp()
    {
        SettingsStore.Save(Settings);
        Overlay?.Cleanup();
        Instance?.DisposeApi();
        Instance?.DisposeTray();
        Current?.Shutdown();
    }

    /// <summary>
    /// Starts or stops the local API at runtime. A closed HttpListener cannot be reopened, so every
    /// enable builds a fresh HttpApi. False means the port could not be opened.
    /// </summary>
    public static bool SetHttpApiEnabled(bool enabled)
    {
        if (Instance == null) return false;
        return enabled ? Instance.StartApi() : Instance.StopApi();
    }

    private bool StartApi()
    {
        DisposeApi();
        if (Overlay == null) return false;

        var api = new HttpApi(Overlay);
        if (!api.Start())
        {
            api.Dispose();
            return false;
        }

        _api = api;
        return true;
    }

    private bool StopApi()
    {
        DisposeApi();
        return true;
    }

    private void DisposeApi()
    {
        _api?.Dispose();
        _api = null;
    }

    private void DisposeTray()
    {
        if (_tray == null) return;
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeApi();
        DisposeTray();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
