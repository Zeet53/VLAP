using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VLAP;

public partial class PresetEditorWindow : Window
{
    private readonly IReadOnlyList<AppEntry> _apps;

    public Preset? Result { get; private set; }

    public PresetEditorWindow(IReadOnlyList<AppEntry> apps, Preset? existing)
    {
        InitializeComponent();
        _apps = apps;

        AppsBox.ItemsSource = apps;
        AppsBox.SelectionChanged += (_, _) =>
            SelectionHint.Text = "Выбрано: " + AppsBox.SelectedItems.Count;

        if (existing != null)
        {
            TitleText.Text = "Изменить набор";
            NameBox.Text = existing.Name;
            Loaded += (_, _) =>
            {
                foreach (var item in apps)
                {
                    if (existing.AppIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
                        AppsBox.SelectedItems.Add(item);
                }
            };
        }
        else
        {
            NameBox.Text = "Новый набор";
            Loaded += (_, _) =>
            {
                if (apps.Count > 0) AppsBox.SelectedItems.Add(apps[0]);
            };
        }

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
            SelectionHint.Text = "Выбрано: " + AppsBox.SelectedItems.Count;
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) DialogResult = false;
        };
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }

        Result = new Preset
        {
            Name = name,
            AppIds = AppsBox.SelectedItems.Cast<AppEntry>().Select(a => a.Id).ToList()
        };
        DialogResult = true;
    }
}
