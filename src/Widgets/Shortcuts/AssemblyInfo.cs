using System.Reflection;
using DeskCanvas.Core.Models.Attributes;
using Shortcuts.Locales;
using Shortcuts.Models;
using Shortcuts.Views;
using Shortcuts.Views.Settings;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.1.0")]

[assembly: WidgetInfo(
    typeof(ShortcutsView),
    typeof(ShortcutsModel),
    typeof(ShortcutsSettings),
    "Shortcuts_Title",
    "Shortcuts_Subtitle",
    defaultColumns: 2,
    defaultRows: 2,
    presetSpans: "1x1,2x2,4x2,4x4",
    category: "WidgetCategory_Launcher")]

[assembly: Locale(typeof(Locale), "Shortcuts", "M13 2 3 14h7l-1 8 11-13h-8z")]
