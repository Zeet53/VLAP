using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace VLAP;

/// <summary>Reads a path the way the launcher needs it: what it is, what to start, where the icon comes from.</summary>
public static class AppPath
{
    /// <summary>
    /// Kind is "" when the path is neither a folder nor a supported file.
    /// Shortcuts are followed: a .lnk becomes the exe/folder it points at, a .url becomes its URL.
    /// </summary>
    public static (string Kind, string Executable, string IconSource, string Target, string Args) Describe(string path)
        => Describe(path, 0);

    private static (string Kind, string Executable, string IconSource, string Target, string Args)
        Describe(string path, int depth)
    {
        if (string.IsNullOrWhiteSpace(path)) return ("", "", "", "", "");
        if (Directory.Exists(path)) return ("folder", "", "", path, "");

        var extension = Path.GetExtension(path).ToLowerInvariant();

        // A shortcut is only a wrapper around whatever it launches — keep the thing inside, not the wrapper.
        if (extension == ".lnk" && depth < 3)
        {
            var link = ShortcutResolver.ReadLink(path);
            if (link != null)
            {
                var inner = Describe(link.Value.Target, depth + 1);
                if (inner.Kind.Length > 0)
                {
                    var icon = inner.IconSource.Length > 0 ? inner.IconSource : link.Value.IconSource;
                    var args = link.Value.Args.Length > 0 ? link.Value.Args : inner.Args;
                    return (inner.Kind, inner.Executable, icon, inner.Target, args);
                }
            }

            // Store apps and shell: links have no path to remember, so the .lnk itself stays the entry.
            return ("lnk", "", path, path, "");
        }

        if (extension == ".url" && depth < 3)
        {
            var url = ShortcutResolver.ReadUrl(path);
            if (IsUrl(url)) return ("url", "", path, url!, "");
        }

        var kind = extension switch
        {
            ".exe" => "exe",
            ".lnk" => "lnk",
            ".url" => "url",
            ".py" => "py",
            _ => ""
        };

        if (kind.Length == 0) return ("", "", "", "", "");
        return (kind, kind == "exe" ? path : "", path, path, "");
    }

    /// <summary>True for "https://…", "steam://…", "mailto:…" — anything a shell can open by name.</summary>
    public static bool IsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.Length > 1 && !uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AppEntry : INotifyPropertyChanged
{
    /// <summary>Stable identity for the local API; generated once and kept in settings.</summary>
    public string Id { get; set; } = "";

    public static string NewId() => System.Guid.NewGuid().ToString("N");

    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Mono));
        }
    }

    public string Target { get; set; } = "";
    public string Args { get; set; } = "";
    /// <summary>Start through the "runas" verb, so Windows asks for elevation every time.</summary>
    public bool RunAsAdmin { get; set; }
    /// <summary>Resolved executable for .lnk entries, used to detect a running instance.</summary>
    public string Executable { get; set; } = "";
    public string IconSource { get; set; } = "";
    /// <summary>exe | lnk | url | py | folder</summary>
    public string LaunchKind { get; set; } = "exe";

    /// <summary>Folders only: drop the entry once it has not been opened for AutoDeleteDays. Off keeps it forever.</summary>
    public bool AutoDelete { get; set; } = true;

    /// <summary>Folders only: how long the entry lives after the last open, in days. One month by default.</summary>
    public int AutoDeleteDays { get; set; } = 30;

    /// <summary>Folders only: when the countdown was last restarted. Null means it has not been opened yet.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastOpenedAt { get; set; }

    /// <summary>How many times the entry was actually started; drives the «часто используемые» order.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LaunchCount { get; set; }

    /// <summary>When the entry was last started. Null means it has never been started from here.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastUsedAt { get; set; }

    /// <summary>True when opening the entry should restart its deletion countdown.</summary>
    [JsonIgnore] public bool IsFolder => string.Equals(LaunchKind, "folder", StringComparison.OrdinalIgnoreCase);

    /// <summary>Category keys this app is tagged with. An app may sit in several, or in none.</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>The single category older files carried. Read once, moved into Categories, then never written again.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Category { get; set; }

    [JsonIgnore]
    public string CategoryLabel => Categories == null || Categories.Count == 0
        ? "Без категории"
        : string.Join(", ", Categories.Select(CategoryRegistry.Label));

    public void RefreshCategoryLabel() => OnPropertyChanged(nameof(CategoryLabel));

    public bool InCategory(string key) =>
        Categories != null && Categories.Contains(key, StringComparer.OrdinalIgnoreCase);

    private bool _running;
    [JsonIgnore]
    public bool Running
    {
        get => _running;
        set { if (_running != value) { _running = value; OnPropertyChanged(); } }
    }

    private ImageSource? _iconImage;
    [JsonIgnore]
    public ImageSource? IconImage
    {
        get => _iconImage;
        set { _iconImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasIcon)); }
    }

    [JsonIgnore] public bool HasIcon => _iconImage != null;

    [JsonIgnore] public string Mono => BuildMono(Name);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string BuildMono(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var first = parts[0];
        if (parts.Length > 1 && first.Length <= 2)
            return (first + parts[1][..1]).ToUpperInvariant();
        return first.Length >= 2 ? first[..2].ToUpperInvariant() : first.ToUpperInvariant();
    }
}

public sealed class AppCategory : INotifyPropertyChanged
{
    public string Key { get; set; } = "";

    private string _label = "";
    public string Label
    {
        get => _label;
        set
        {
            if (_label == value) return;
            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    /// <summary>How many apps carry this tag — filled in when the settings list refreshes.</summary>
    private int _count;
    [JsonIgnore]
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Maps category keys to their user-visible names, so entries can resolve a label without a settings reference.</summary>
public static class CategoryRegistry
{
    private static Dictionary<string, string> _labels = new(System.StringComparer.OrdinalIgnoreCase);

    public static void Set(IEnumerable<AppCategory> categories)
    {
        var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category.Key)) continue;
            map[category.Key] = string.IsNullOrWhiteSpace(category.Label) ? category.Key : category.Label;
        }
        _labels = map;
    }

    public static bool Exists(string? key) =>
        !string.IsNullOrWhiteSpace(key) && _labels.ContainsKey(key);

    public static string Label(string? key) =>
        key != null && _labels.TryGetValue(key, out var label) ? label : key ?? "";
}

public sealed class Preset
{
    public string Name { get; set; } = "";

    /// <summary>Ids of the apps in this set: renames and same-named entries cannot break it.</summary>
    public List<string> AppIds { get; set; } = new();

    /// <summary>Older files listed app names. Read once on load, translated to ids, then cleared.</summary>
    public List<string> Apps { get; set; } = new();

    /// <summary>Readable app names, filled by the window whenever the list is rebuilt.</summary>
    [JsonIgnore] public string AppsText { get; set; } = "пусто";
}

public sealed class AppSettings
{
    public double UiScale { get; set; } = 1.1;
    public string Hotkey { get; set; } = "Alt+Space";
    public bool Blur { get; set; } = true;

    public bool Autostart { get; set; } = true;

    /// <summary>Light monochrome palette instead of the dark one.</summary>
    public bool LightTheme { get; set; }

    /// <summary>Typing anywhere on the main screen searches; off brings back the visible search field.</summary>
    public bool TypeToSearch { get; set; } = true;

    /// <summary>With type-to-search on: clear the query two seconds after the last key.</summary>
    public bool SearchAutoClear { get; set; } = true;

    /// <summary>Show the preset strip at the bottom of the main screen.</summary>
    public bool ShowPresetsOnMain { get; set; } = true;

    /// <summary>Loopback JSON API for external tools. Off by default; missing in older files means off.</summary>
    public bool HttpApiEnabled { get; set; }

    /// <summary>Order the grid by how often each entry is started instead of the order the user dragged tiles into.</summary>
    public bool SortByUsage { get; set; } = true;

    /// <summary>Settings sections the user folded shut, so they stay shut across restarts.</summary>
    public List<string> CollapsedSections { get; set; } = new();

    /// <summary>What a newly added folder starts with: on by default, dropped after a month of no opens.</summary>
    public bool FolderAutoDelete { get; set; } = true;

    /// <summary>Days a new folder lives until someone opens it. Each folder can still have its own value.</summary>
    public int FolderAutoDeleteDays { get; set; } = 30;
    public List<AppEntry> Apps { get; set; } = new();
    public List<Preset> Presets { get; set; } = new();
    public List<AppCategory> Categories { get; set; } = new();

    /// <summary>Apps the user deleted by hand — rescanning must not bring them back.</summary>
    public List<string> Removed { get; set; } = new();

    /// <summary>
    /// Copies every value from another instance. The settings object is shared with the tray and the
    /// local API, so import, restore and reset must change it in place instead of replacing it.
    /// </summary>
    public void CopyFrom(AppSettings other)
    {
        UiScale = other.UiScale;
        Hotkey = other.Hotkey;
        Blur = other.Blur;
        Autostart = other.Autostart;
        LightTheme = other.LightTheme;
        TypeToSearch = other.TypeToSearch;
        SearchAutoClear = other.SearchAutoClear;
        ShowPresetsOnMain = other.ShowPresetsOnMain;
        HttpApiEnabled = other.HttpApiEnabled;
        SortByUsage = other.SortByUsage;
        FolderAutoDelete = other.FolderAutoDelete;
        FolderAutoDeleteDays = other.FolderAutoDeleteDays;
        CollapsedSections = other.CollapsedSections ?? new List<string>();
        Apps = other.Apps ?? new List<AppEntry>();
        Presets = other.Presets ?? new List<Preset>();
        Categories = other.Categories ?? new List<AppCategory>();
        Removed = other.Removed ?? new List<string>();
    }

    /// <summary>Brings an older file up to date. True when something changed and the file should be rewritten.</summary>
    public bool EnsureIds()
    {
        var changed = false;

        // An imported file can hold explicit nulls where the defaults would otherwise be.
        Apps ??= new List<AppEntry>();
        Presets ??= new List<Preset>();
        Categories ??= new List<AppCategory>();
        Removed ??= new List<string>();

        foreach (var preset in Presets)
        {
            if (preset.AppIds == null) { preset.AppIds = new List<string>(); changed = true; }
            if (preset.Apps == null) { preset.Apps = new List<string>(); changed = true; }
        }

        foreach (var app in Apps)
        {
            if (!string.IsNullOrWhiteSpace(app.Id)) continue;
            app.Id = AppEntry.NewId();
            changed = true;
        }

        return MigrateCategories() || MigratePresets() || changed;
    }

    /// <summary>Categories became tags: the one category an entry used to have becomes its first tag.</summary>
    private bool MigrateCategories()
    {
        var changed = false;

        foreach (var app in Apps)
        {
            if (app.Categories == null)
            {
                app.Categories = new List<string>();
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(app.Category)) continue;

            if (!app.InCategory(app.Category)) app.Categories.Add(app.Category);
            app.Category = null;
            changed = true;
        }

        return changed;
    }

    /// <summary>Presets used to name their apps. Translate those names into ids once and drop the names.</summary>
    private bool MigratePresets()
    {
        var changed = false;

        foreach (var preset in Presets)
        {
            if (preset.Apps.Count == 0) continue;

            foreach (var name in preset.Apps)
            {
                var app = Apps.FirstOrDefault(a =>
                    string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
                if (app == null) continue;
                if (!preset.AppIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase))
                    preset.AppIds.Add(app.Id);
            }

            preset.Apps.Clear();
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Drops folder entries nobody opened for their own period. Runs once at startup, never on a timer.
    /// Only the entry leaves the list — the folder on disk is not touched.
    /// </summary>
    public (List<AppEntry> Gone, bool Touched) PurgeExpiredFolders(DateTime now)
    {
        var gone = new List<AppEntry>();
        var touched = false;

        foreach (var app in Apps.ToList())
        {
            if (!app.IsFolder || !app.AutoDelete || app.AutoDeleteDays <= 0) continue;

            // A folder carried over from an older file has no timestamp yet: start counting today.
            if (app.LastOpenedAt == null)
            {
                app.LastOpenedAt = now;
                touched = true;
                continue;
            }

            if (now - app.LastOpenedAt.Value < TimeSpan.FromDays(app.AutoDeleteDays)) continue;

            Apps.Remove(app);
            gone.Add(app);
        }

        if (gone.Count == 0) return (gone, touched);

        foreach (var preset in Presets)
            preset.AppIds.RemoveAll(id =>
                gone.Any(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)));

        return (gone, true);
    }

    public AppEntry? FindById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}
