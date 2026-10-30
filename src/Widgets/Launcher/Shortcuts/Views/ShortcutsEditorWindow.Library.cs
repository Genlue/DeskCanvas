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
                foreach (var definition in ActionCatalog.Visible.Where(d => d.Category == category))
                    wrap.Children.Add(BuildLibraryItem(definition));
                LibraryHost.Children.Add(wrap);
            }

            // The single most confusing part of the graph model, stated where the user adds a
            // loop instead of only in the specification document.
            LibraryHost.Children.Add(BuildSectionHeader(Loc.Get("Shortcuts_Library_LoopHeader")));
            LibraryHost.Children.Add(new TextBlock
            {
                Text = Loc.Get("Shortcuts_Library_LoopHint"),
                FontSize = 10.5,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 0, 2, 4),
            });
        }
        else
        {
            // Search covers the name, the one-line purpose, the type id and the port names, so
            // "json" or "剪贴板" both find the right node.
            var matches = ActionCatalog.Visible
                .Where(d => Matches(d, filter))
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

    /// <summary>Name, purpose, type id and port ids all take part in the search.</summary>
    private static bool Matches(ActionDefinition definition, string filter)
    {
        if (Loc.Get(definition.TitleKey).Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        if (definition.Type.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;

        if (definition.SummaryKey != null &&
            Loc.Get(definition.SummaryKey).Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var port in definition.Outputs)
        {
            if (port.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
            if (Loc.Get(port.LabelKey).Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var spec in definition.Params)
        {
            if (Loc.Get(spec.LabelKey).Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// One library entry: icon + name, a one-line purpose, and its input → output shape. The
    /// previous drawer showed only a name, so "which one do I need" was guesswork.
    /// </summary>
    private Button BuildLibraryItem(ActionDefinition definition)
    {
        var button = new Button { Classes = { "library-item" } };

        var column = new StackPanel { Spacing = 1 };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        titleRow.Children.Add(new GlyphView
        {
            Data = GlyphLibrary.GetGeometry(definition.Glyph),
            Stroke = Visuals.Brush(ActionCatalog.CategoryColor(definition.Category)),
            StrokeThickness = 1.7,
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = Loc.Get(definition.TitleKey),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        column.Children.Add(titleRow);

        if (definition.SummaryKey != null)
        {
            column.Children.Add(new TextBlock
            {
                Text = Loc.Get(definition.SummaryKey),
                FontSize = 10,
                Opacity = 0.55,
                MaxWidth = 210,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        var io = IoSummary(definition);
        if (io.Length > 0)
        {
            column.Children.Add(new TextBlock
            {
                Text = io,
                FontSize = 9.5,
                Opacity = 0.45,
                MaxWidth = 210,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        button.Content = column;

        var tip = new System.Text.StringBuilder();
        tip.AppendLine(Loc.Get(definition.TitleKey));
        if (definition.SummaryKey != null) tip.AppendLine(Loc.Get(definition.SummaryKey));
        if (io.Length > 0) tip.AppendLine(io);
        foreach (var spec in definition.Params)
        {
            if (!spec.Primary && !spec.Required) continue;
            tip.Append("· ").Append(Loc.Get(spec.LabelKey));
            if (spec.Required) tip.Append(" *");
            tip.AppendLine();
        }
        if (WorkflowNodeTypes.IsLoop(definition.Type))
            tip.AppendLine(Loc.Get("Shortcuts_Library_LoopHint"));
        ToolTip.SetTip(button, tip.ToString().TrimEnd());

        button.Click += (_, _) => InsertAction(definition.Type);
        return button;
    }

    /// <summary>"文本 → 文本" style summary built from the contract, never hand-written.</summary>
    private static string IoSummary(ActionDefinition definition)
    {
        var inputs = definition.Params.Where(p => p.Primary).Select(p => Loc.Get(p.LabelKey)).ToList();
        var outputs = definition.Outputs
            .Where(o => o.IsData)
            .Select(o => $"{o.Id}:{Loc.Get(ActionCatalog.TypeLabelKey(o.DataType))}")
            .ToList();

        if (definition.Outputs.Count == 0 && inputs.Count == 0) return string.Empty;

        var left = inputs.Count == 0 ? Loc.Get("Shortcuts_Library_NoInput") : string.Join(" · ", inputs);
        var right = outputs.Count == 0 ? Loc.Get("Shortcuts_Library_NoOutput") : string.Join(" · ", outputs);
        return $"{left} → {right}";
    }

    /// <summary>
    /// Add a node at the canvas centre. When a single-output node is selected and still has a
    /// free continuation, the new node is wired to it — that is what makes "keep adding steps"
    /// feel like the old list editor while staying a real graph.
    /// </summary>
    private void InsertAction(string type)
    {
        var definition = ActionCatalog.Get(type);
        if (definition == null || canvas == null) return;

        canvas.AddNode(definition);

        RecentTypes.Remove(type);
        RecentTypes.Insert(0, type);
        if (RecentTypes.Count > 8) RecentTypes.RemoveAt(RecentTypes.Count - 1);

        CommitChange();
        UpdateSelectionUi();
        // 选完即收起：画布恢复可见，新节点就在眼前（旧版抽屉一直挡着画布）。
        CloseLibrary();
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
