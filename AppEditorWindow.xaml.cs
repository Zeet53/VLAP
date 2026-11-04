using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using WinForms = System.Windows.Forms;

namespace VLAP;

public partial class AppEditorWindow : Window
{
    /// <summary>One category as a tickable tag in the dialog.</summary>
    public sealed class CategoryPick : System.ComponentModel.INotifyPropertyChanged
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";

        private bool _isOn;
        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (_isOn == value) return;
                _isOn = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsOn)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<CategoryPick> _picks = new();

    /// <summary>Filled in when the dialog is confirmed; the caller writes it onto the entry.</summary>
    public AppEntry? Result { get; private set; }

    public AppEditorWindow(IReadOnlyList<AppCategory> categories, AppEntry app)
    {
        InitializeComponent();

        NameBox.Text = app.Name;
        PathBox.Text = app.Target;
        ArgsBox.Text = app.Args;
        AdminSwitch.IsChecked = app.RunAsAdmin;

        AutoDeleteSwitch.IsChecked = app.AutoDelete;
        AutoDeleteDaysBox.Text = (app.AutoDeleteDays > 0 ? app.AutoDeleteDays : 30).ToString();
        AutoDeleteSwitch.Checked += (_, _) => RefreshAutoDelete();
        AutoDeleteSwitch.Unchecked += (_, _) => RefreshAutoDelete();

        foreach (var category in categories)
            _picks.Add(new CategoryPick
            {
                Key = category.Key,
                Label = category.Label,
                IsOn = app.InCategory(category.Key)
            });
        CategoryChips.ItemsSource = _picks;

        RefreshKindHint();

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) DialogResult = false;
        };
    }

    private bool IsFolder => Directory.Exists(PathBox.Text.Trim());

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void RefreshKindHint()
    {
        var isFolder = IsFolder;
        ArgsRow.Visibility = isFolder ? Visibility.Collapsed : Visibility.Visible;
        AdminRow.Visibility = isFolder ? Visibility.Collapsed : Visibility.Visible;
        AutoDeleteRow.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
        RefreshAutoDelete();

        // Folder rows only exist for folders, so the dialog grows for them instead of leaving a hole.
        Height = isFolder ? 712 : 572;

        HintText.Text = isFolder
            ? "Папка откроется в проводнике"
            : AppPath.IsUrl(PathBox.Text.Trim())
                ? "Откроется ссылка"
                : Path.GetExtension(PathBox.Text.Trim()).Equals(".py", StringComparison.OrdinalIgnoreCase)
                    ? (PythonLauncher.Interpreter == null
                        ? "Python не найден — скрипт откроется как обычный файл"
                        : "Скрипт запустится через " + Path.GetFileName(PythonLauncher.Interpreter))
                    : Path.GetExtension(PathBox.Text.Trim()).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                        ? (AppPath.Describe(PathBox.Text.Trim()).Kind is "lnk" or ""
                            ? "В ярлыке нет пути к файлу — он останется ярлыком"
                            : "Ярлык раскроется в то, на что он указывает")
                        : "";
    }

    /// <summary>The days field only matters while the folder is set to disappear at all.</summary>
    private void RefreshAutoDelete()
    {
        AutoDeleteDaysRow.Visibility = AutoDeleteSwitch.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();

        if (IsFolder)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "Выбери папку",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (Directory.Exists(path)) dialog.SelectedPath = path;
            if (dialog.ShowDialog() == WinForms.DialogResult.OK) SetPath(dialog.SelectedPath);
            return;
        }

        using var fileDialog = new WinForms.OpenFileDialog
        {
            Title = "Выбери приложение",
            Filter = "Приложения|*.exe;*.lnk;*.url;*.py|Все файлы|*.*",
            CheckFileExists = true
        };
        if (File.Exists(path)) fileDialog.FileName = path;
        if (fileDialog.ShowDialog() == WinForms.DialogResult.OK) SetPath(fileDialog.FileName);
    }

    private void SetPath(string path)
    {
        PathBox.Text = path;
        RefreshKindHint();

        // Only take the file name as the title while the user has not made one up.
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = Path.GetFileNameWithoutExtension(path);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var path = PathBox.Text.Trim();

        if (name.Length == 0)
        {
            ShowError("Впиши название — по нему приложение ищется в поиске");
            NameBox.Focus();
            return;
        }

        if (path.Length == 0)
        {
            ShowError("Впиши путь к exe, ярлыку, скрипту .py или папке");
            PathBox.Focus();
            return;
        }

        if (!Directory.Exists(path) && !File.Exists(path) && !AppPath.IsUrl(path))
        {
            ShowError("Такого файла или папки нет: " + path);
            PathBox.Focus();
            return;
        }

        var (kind, executable, icon, target, shortcutArgs) = AppPath.Describe(path);
        if (kind.Length == 0)
        {
            ShowError("Подходят exe, lnk, url, py или папка");
            PathBox.Focus();
            return;
        }

        // A shortcut carries its own arguments; they win only while the field is left empty.
        var args = ArgsBox.Text.Trim();
        if (args.Length == 0 && (kind == "exe" || kind == "py")) args = shortcutArgs;

        var days = 30;
        if (kind == "folder" &&
            (!int.TryParse(AutoDeleteDaysBox.Text.Trim(), out days) || days < 1 || days > 3650))
        {
            ShowError("Срок — целое число дней от 1 до 3650");
            AutoDeleteDaysBox.Focus();
            return;
        }

        Result = new AppEntry
        {
            Name = name,
            Target = target,
            Args = kind == "exe" || kind == "py" ? args : "",
            RunAsAdmin = kind != "folder" && kind != "url" && AdminSwitch.IsChecked == true,
            LaunchKind = kind,
            Executable = executable,
            IconSource = icon,
            Categories = _picks.Where(p => p.IsOn).Select(p => p.Key).ToList(),
            AutoDelete = kind == "folder" && AutoDeleteSwitch.IsChecked == true,
            AutoDeleteDays = kind == "folder" ? days : 30
        };

        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
