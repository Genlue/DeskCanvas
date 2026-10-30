using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Search.Locales;
using Search.Models;
using Search.ViewModels;

namespace Search.Services;

public static class SearchMenuHelper
{
    public static void ShowEngineMenu(Button button, SearchViewModel vm)
    {
        var flyout = new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft
        };

        foreach (var engine in vm.AllEngines.Where(e => e.IsEnabled))
        {
            var isCurrent = engine.Id.Equals(vm.CurrentEngine?.Id, StringComparison.OrdinalIgnoreCase);
            // Selection is carried by the weight, not by a "✓ " text prefix (开发规范 §4.4: a check
            // mark is an icon, and a text glyph cannot follow the menu's foreground).
            var item = new MenuItem
            {
                Header = $"{engine.Name} ({engine.Category})",
                FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Normal,
                Icon = new PathIcon
                {
                    Data = Geometry.Parse(SearchIconProvider.GetPath(engine.IconKey)),
                    Foreground = Brush.Parse(engine.IconColor),
                    Width = 14,
                    Height = 14
                }
            };

            var target = engine;
            item.Click += (_, _) => vm.SelectEngine(target);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());

        bool isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        var homeItem = new MenuItem
        {
            Header = isZh ? $"打开 {vm.EngineName} 官网" : $"Open {vm.EngineName} Website",
            Icon = new PathIcon
            {
                Data = Geometry.Parse(SearchIconProvider.ExternalLinkIcon),
                Width = 14,
                Height = 14
            }
        };
        homeItem.Click += (_, _) =>
        {
            if (vm.CurrentEngine != null)
            {
                SearchLauncher.Launch(vm.CurrentEngine, "");
            }
        };
        flyout.Items.Add(homeItem);

        flyout.ShowAt(button);
    }
}
