using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VLAP;

public static class SettingsStore
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VLAP");

    /// <summary>Where the launcher kept its files before the rename; read once, then left untouched.</summary>
    private static readonly string LegacyDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CCD");

    public static string FilePath => Path.Combine(Dir, "settings.json");
    public static string BackupPath => Path.Combine(Dir, "settings.bak.json");

    /// <summary>
    /// The user-facing «внутренний бэкап»: written before an import or a reset and never touched by
    /// routine saves, so «Откат» keeps something to return to.
    /// </summary>
    public static string SnapshotPath => Path.Combine(Dir, "settings.snapshot.json");

    public static string LogPath => Path.Combine(Dir, "log.txt");

    /// <summary>log.txt is trimmed back to about half this size once it grows past it.</summary>
    private const long LogMaxBytes = 200 * 1024;

    /// <summary>True while settings.json holds a file we read (or wrote) successfully.</summary>
    private static bool _currentIsGood;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Files written by hand or by another tool may use camelCase keys.</summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static AppSettings Load()
    {
        MigrateLegacyFiles();

        var fromMain = TryRead(FilePath);
        if (fromMain != null)
        {
            _currentIsGood = true;
            return fromMain;
        }

        // The file is there but unreadable: keep it instead of letting the next save overwrite it.
        if (File.Exists(FilePath))
        {
            Quarantine();
            var fromBackup = TryRead(BackupPath);
            if (fromBackup != null)
            {
                Log("load settings: recovered from " + BackupPath);
                Save(fromBackup); // make the recovery the real file again, not just an in-memory state
                return fromBackup;
            }
        }

        _currentIsGood = false;
        return new AppSettings();
    }

    /// <summary>
    /// One-time move from the pre-rename folder: the user keeps their apps, categories, presets and
    /// backups instead of starting empty. Runs only while the new folder has no settings file yet.
    /// </summary>
    private static void MigrateLegacyFiles()
    {
        try
        {
            if (File.Exists(FilePath) || !File.Exists(Path.Combine(LegacyDir, "settings.json"))) return;

            Directory.CreateDirectory(Dir);
            foreach (var name in new[] { "settings.json", "settings.bak.json", "settings.snapshot.json" })
            {
                var from = Path.Combine(LegacyDir, name);
                if (File.Exists(from)) File.Copy(from, Path.Combine(Dir, name), overwrite: false);
            }

            Log("settings: migrated from " + LegacyDir);
        }
        catch (Exception ex) { Log("migrate settings: " + ex.Message); }
    }

    private static AppSettings? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch (Exception ex)
        {
            Log("read settings " + path + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>Reads a settings file chosen by the user; null when it cannot be read.</summary>
    public static AppSettings? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);

            // a random .json object would deserialize into defaults and wipe everything
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (!doc.RootElement.EnumerateObject().Any(p => IsSettingsKey(p.Name)))
                {
                    Log("read settings " + path + ": not a settings file");
                    return null;
                }
            }

            return JsonSerializer.Deserialize<AppSettings>(json, ReadOptions);
        }
        catch (Exception ex)
        {
            Log("read settings " + path + ": " + ex.Message);
            return null;
        }
    }

    private static bool IsSettingsKey(string name) =>
        name.Equals("Apps", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Presets", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Categories", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("UiScale", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Hotkey", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LightTheme", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SortByUsage", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes settings to a path the user picked for export.</summary>
    public static bool WriteFile(string path, AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(path, json);
            Log("settings: exported to " + path);
            return true;
        }
        catch (Exception ex)
        {
            Log("export settings " + path + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>Saves the current state as the internal backup «Откат» returns to.</summary>
    public static bool WriteSnapshot(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(settings, JsonOptions));
            Log("settings: internal snapshot saved");
            return true;
        }
        catch (Exception ex)
        {
            Log("snapshot settings: " + ex.Message);
            return false;
        }
    }

    public static AppSettings? LoadSnapshot() => TryRead(SnapshotPath);

    /// <summary>
    /// The first start after this feature appeared: keep the file that is already there as the
    /// internal backup, so «Откат» has something to return to before any import or reset happens.
    /// </summary>
    public static void EnsureSnapshot()
    {
        try
        {
            if (File.Exists(SnapshotPath) || !File.Exists(FilePath)) return;
            File.Copy(FilePath, SnapshotPath, overwrite: false);
            Log("settings: internal snapshot created from " + FilePath);
        }
        catch (Exception ex)
        {
            Log("snapshot settings: " + ex.Message);
        }
    }

    private static void Quarantine()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var broken = Path.Combine(Dir, "settings.broken-" + stamp + ".json");
            File.Move(FilePath, broken, overwrite: true);
            Log("load settings: unreadable file kept as " + broken);
        }
        catch (Exception ex) { Log("quarantine settings: " + ex.Message); }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);

            // A copy of the last known-good file, so a corrupted save cannot take everything with it.
            if (_currentIsGood && File.Exists(FilePath))
            {
                try { File.Copy(FilePath, BackupPath, overwrite: true); }
                catch (Exception ex) { Log("backup settings: " + ex.Message); }
            }

            File.Move(tmp, FilePath, overwrite: true);
            _currentIsGood = true;
            Log($"settings saved: apps={settings.Apps.Count}, presets={settings.Presets.Count}, categories={settings.Categories.Count}");
        }
        catch (Exception ex) { Log("save settings: " + ex); }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            RotateLog();
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>Keeps log.txt from growing forever: past the cap, only the second half survives.</summary>
    private static void RotateLog()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length <= LogMaxBytes) return;

            var text = File.ReadAllText(LogPath);
            var cut = text.Length - (int)(LogMaxBytes / 2);
            var lineBreak = text.IndexOf('\n', cut);
            if (lineBreak >= 0 && lineBreak + 1 < text.Length) text = text[(lineBreak + 1)..];

            File.WriteAllText(LogPath, "[...] старые записи обрезаны" + Environment.NewLine + text);
        }
        catch { /* a broken rotation must not stop logging */ }
    }
}
