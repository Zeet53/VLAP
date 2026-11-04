using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace VLAP;

/// <summary>Starts .py entries through a real interpreter instead of whatever the file association happens to be.</summary>
public static class PythonLauncher
{
    private static string? _interpreter;
    private static bool _tried;

    /// <summary>Full path to py.exe / python.exe, or null when no Python is installed.</summary>
    public static string? Interpreter
    {
        get
        {
            if (_tried) return _interpreter;
            _tried = true;
            _interpreter = Find();
            SettingsStore.Log(_interpreter == null
                ? "python: not found, .py entries fall back to the file association"
                : "python: " + _interpreter);
            return _interpreter;
        }
    }

    private static string? Find()
    {
        var candidates = new List<string>();

        // The py launcher first: it is what Windows itself registers and it knows about every install.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows)) candidates.Add(Path.Combine(windows, "py.exe"));

        foreach (var name in new[] { "py.exe", "python.exe", "python3.exe" })
        {
            var onPath = WhichOnPath(name);
            if (onPath != null) candidates.Add(onPath);
        }

        // Per-user installs do not always make it onto PATH; newest folder first.
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python");
            if (Directory.Exists(root))
                candidates.AddRange(Directory.GetDirectories(root)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "python.exe"))
                    .Where(File.Exists));
        }
        catch (Exception ex) { SettingsStore.Log("python search: " + ex.Message); }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? WhichOnPath(string fileName)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = dir.Trim().Trim('"');
                if (trimmed.Length == 0) continue;

                // %LOCALAPPDATA%\Microsoft\WindowsApps\python.exe is a Store stub, not an interpreter.
                if (trimmed.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;

                var full = Path.Combine(trimmed, fileName);
                if (File.Exists(full)) return full;
            }
        }
        catch { /* a malformed PATH is not worth failing the launch over */ }

        return null;
    }

    /// <summary>Start info that runs the entry's script with the entry's arguments (and elevation, if asked).</summary>
    public static ProcessStartInfo StartInfo(AppEntry app)
    {
        var script = app.Target;
        var python = Interpreter;

        if (python == null)
        {
            // No interpreter: let Windows do what it would have done in Explorer.
            var shell = new ProcessStartInfo { FileName = script, UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(app.Args)) shell.Arguments = app.Args;
            if (app.RunAsAdmin) shell.Verb = "runas";
            return shell;
        }

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = Quote(script) + (string.IsNullOrWhiteSpace(app.Args) ? "" : " " + app.Args.Trim()),
            WorkingDirectory = Path.GetDirectoryName(script) ?? "",
            UseShellExecute = true
        };
        if (app.RunAsAdmin) psi.Verb = "runas";
        return psi;
    }

    private static string Quote(string path) => "\"" + path + "\"";
}
