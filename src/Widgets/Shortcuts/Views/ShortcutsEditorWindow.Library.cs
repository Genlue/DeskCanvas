using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Shortcuts.Models;
using Shortcuts.Services;
using Shortcuts.Views.Controls;

namespace Shortcuts.Views;

/// <summary>Bottom action-library drawer, insert position, and the icon/color picker.</summary>
public partial class ShortcutsEditorWindow
{
    private static readonly List<string> RecentTypes = [];

    // ---- library drawer -----------------------------------------------------------

    private void OnAddActionClicked(object? sender, RoutedEventArgs e)
    {
        if (LibraryPanel.IsVisible) CloseLibrary();
        else OpenLibrary(focusSearch: false);
    }

    private void OpenLibrary(bool focusSearch)
    {
        LibraryPanel.IsVisible = true;
        RebuildLibrary();
        if (focusSearch) LibrarySearchBox.Focus();
    }

    private void CloseLibrary() => LibraryPanel.IsVisible = false;

    private void OnLibrarySearchChanged(object? sender, TextChangedEventArgs e) => RebuildLibrary();

    private void RebuildLibrary()
    {
        LibraryHost.Children.Clear();
        var filter = (LibrarySearchBox.Text ?? string.Empty).Trim();

        if (filter.Length == 0)
        {
            if (RecentTypes.Count > 0)
            {
                LibraryHost.Children.Add(BuildSectionHeader(Loc.Get("Shortcuts_Library_Recent")));
                var recentWrap = new WrapPanel();
                foreach (var type in RecentTypes)
                {
                    if (ActionCatalog.Get(type) is { } definition)
                        recentWrap.Children.Add(BuildLibraryItem(definition));
                }
                LibraryHost.Children.Add(recentWrap);
            }

            foreach (var category in ActionCatalog.CategoryOrder)
            {
                LibraryHost.Children.Add(BuildSectionHeader(Loc.Get(ActionCatalog.CategoryLabelKey(category))));
                var wrap = new WrapPanel();
                foreach (var definition in ActionCatalog.All.Where(d => d.Category == category))
                    wrap.Children.Add(BuildLibraryItem(definition));
                LibraryHost.Children.Add(wrap);
            }
        }
        else
        {
            var matches = ActionCatalog.All
                .Where(d => Loc.Get(d.TitleKey).Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                LibraryHost.Children.Add(new TextBlock
                {
                    Text = Loc.Get("Shortcuts_NoMatch"),
                    FontSize = 12,
                    Opacity = 0.45,
                    Margin = new Thickness(2, 10, 0, 0),
                });
            }
            else
            {
                var wrap = new WrapPanel();
                foreach (var definition in matches) wrap.Children.Add(BuildLibraryItem(definition));
                LibraryHost.Children.Add(wrap);
            }
        }
    }

    private static TextBlock BuildSectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Opacity = 0.5,
        Margin = new Thickness(2, 8, 0, 4),
    };

    private Button BuildLibraryItem(ActionDefinition definition)
    {
        var button = new Button { Classes = { "library-item" } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };

        row.Children.Add(new GlyphView
        {
            Data = GlyphLibrary.GetGeometry(definition.Glyph),
            Stroke = Visuals.Brush(ActionCatalog.CategoryColor(definition.Category)),
            StrokeThickness = 1.7,
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = Loc.Get(definition.TitleKey),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });

        button.Content = row;
        button.Click += (_, _) => InsertAction(definition.Type);
        return button;
    }

    private void InsertAction(string type)
    {
        var definition = ActionCatalog.Get(type);
        if (definition == null) return;

        var action = ShortcutAction.Create(definition);
        var (list, index) = ResolveInsertPosition();
        list.Insert(Math.Clamp(index, 0, list.Count), action);
        selectedActionId = action.Id;

        RecentTypes.Remove(type);
        RecentTypes.Insert(0, type);
        if (RecentTypes.Count > 8) RecentTypes.RemoveAt(RecentTypes.Count - 1);

        CommitChange();
    }

    /// <summary>Insert right after the selected action (same level), or append at the end.</summary>
    private (List<ShortcutAction> List, int Index) ResolveInsertPosition()
    {
        if (selectedActionId != null && FindParent(shortcut.Actions, selectedActionId) is { } location)
            return (location.List, location.Index + 1);

        return (shortcut.Actions, shortcut.Actions.Count);
    }

    // ---- icon & color picker ------------------------------------------------------

    private void OnIconButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;

        var panel = new StackPanel { Spacing = 10, Width = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get("Shortcuts_IconPicker_Title"),
            FontSize = 12,
            Opacity = 0.7,
        });

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };

        var glyphButtons = new List<(string Id, Button Button)>();
        var glyphWrap = new WrapPanel();
        foreach (var id in GlyphLibrary.PickerGlyphs)
        {
            var button = new Button { Classes = { "glyph-cell" } };
            var glyphId = id;
            button.Content = new GlyphView
            {
                Data = GlyphLibrary.GetGeometry(id),
                StrokeThickness = 1.7,
                Width = 16,
                Height = 16,
            };
            button.Click += (_, _) =>
            {
                shortcut.Glyph = glyphId;
                CommitChange();
                RestyleGlyphButtons();
            };
            glyphButtons.Add((id, button));
            glyphWrap.Children.Add(button);
        }
        panel.Children.Add(glyphWrap);

        var swatchButtons = new List<(string Hex, Button Button)>();
        var swatchWrap = new WrapPanel();
        foreach (var hex in ActionCatalog.Palette)
        {
            var swatch = hex;
            var button = new Button
            {
                Classes = { "swatch-cell" },
                Background = Visuals.Brush(hex),
            };
            button.Click += (_, _) =>
            {
                shortcut.Color = swatch;
                CommitChange();
                RestyleSwatchButtons();
            };
            swatchButtons.Add((hex, button));
            swatchWrap.Children.Add(button);
        }
        panel.Children.Add(swatchWrap);

        void RestyleGlyphButtons()
        {
            foreach (var (id, button) in glyphButtons)
            {
                var selected = id == shortcut.Glyph;
                button.Background = selected
                    ? new SolidColorBrush(Color.Parse("#33808080"))
                    : Brushes.Transparent;
                if (button.Content is GlyphView glyph)
                    glyph.Stroke = selected
                        ? Visuals.Brush(shortcut.Color)
                        : new SolidColorBrush(Color.Parse("#888780"));
            }
        }

        void RestyleSwatchButtons()
        {
            foreach (var (hex, button) in swatchButtons)
            {
                button.BorderThickness = new Thickness(hex == shortcut.Color ? 2 : 0);
                button.BorderBrush = Brushes.White;
            }
        }

        RestyleGlyphButtons();
        RestyleSwatchButtons();

        flyout.ShowAt(anchor);
    }
}
