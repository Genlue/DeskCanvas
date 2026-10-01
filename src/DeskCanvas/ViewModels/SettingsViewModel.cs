using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ReactiveUI;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using DeskCanvas.Views.Pages;

namespace DeskCanvas.ViewModels;

public class SettingsViewModel : ReactiveObject
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly IAssemblyProvider assemblyProvider;
    private readonly ILayoutProvider layoutProvider;
    private readonly DisplayMonitorService displayMonitor;
    private readonly IWidgetFactory<Window, UserControl> widgetFactory;
    private readonly ProfileService profileService;
    private readonly UpdateService updateService;

    private UserControl? currentPage;
    public UserControl? CurrentPage
    {
        get => currentPage;
        set => this.RaiseAndSetIfChanged(ref currentPage, value);
    }

    private string? currentPageTitle;
    public string? CurrentPageTitle
    {
        get => currentPageTitle;
        set => this.RaiseAndSetIfChanged(ref currentPageTitle, value);
    }

    public PageViewModel[] AllItems { get; }

    public PageViewModel DefaultItem => AllItems.FirstOrDefault(x => x.Type == typeof(Appearance)) ?? AllItems.First(x => x.IsItem);

    public SettingsViewModel(
        IAppSettingsProvider appSettingsProvider,
        IAssemblyProvider assemblyProvider,
        ILayoutProvider layoutProvider,
        DisplayMonitorService displayMonitor,
        IWidgetFactory<Window, UserControl> widgetFactory,
        ProfileService profileService,
        UpdateService updateService)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.assemblyProvider = assemblyProvider;
        this.layoutProvider = layoutProvider;
        this.displayMonitor = displayMonitor;
        this.widgetFactory = widgetFactory;
        this.profileService = profileService;
        this.updateService = updateService;
        profileService.ActiveProfileChanged += (_, _) => pageCache.Clear();

        // 1. Load and deduplicate widget assemblies
        var loadedAssemblies = assemblyProvider
            .GetAssemblyInfos(Const.WidgetsFolder)
            .ToDictionary(
                group => group.Key,
                group => group.MaxBy(assembly => assembly.Version)!);

        List<AssemblyInfo> GetAssemblies(params string[] names) =>
            names.Where(n => loadedAssemblies.ContainsKey(n))
                 .Select(n => loadedAssemblies[n])
                 .ToList();

        var allAssemblies = loadedAssemblies.Values.ToList();
        var timeAssemblies = GetAssemblies("Clock", "Calendar", "Progress", "Pomodoro");
        var weatherAssemblies = GetAssemblies("Weather");
        var mapAssemblies = GetAssemblies("Map");
        var memoAssemblies = GetAssemblies("Notes", "Reminders");
        var desktopAssemblies = GetAssemblies("Folders");
        var monitorAssemblies = GetAssemblies("Monitor", "Batteries");
        var mediaAssemblies = GetAssemblies("Music", "Picture");
        var toolsAssemblies = GetAssemblies("Tools", "Search");
        var fixedAssemblies = GetAssemblies("FixedWidgets");
        var stackAssemblies = GetAssemblies("StackWidgets");

        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Clock", "Calendar", "Progress", "Pomodoro", "Weather", "Map", "Notes", "Reminders", "Folders", "Monitor", "Batteries", "Music", "Picture", "Tools", "Search", "FixedWidgets", "StackWidgets"
        };
        var otherAssemblies = loadedAssemblies.Values
            .Where(a => !knownNames.Contains(a.AssemblyName))
            .ToList();

        // 2. Build structured category navigation (macOS Widget Drawer style)
        var items = new List<PageViewModel>
        {
            // --- Section 1: 系统设置 ---
            new(null, null, "系统设置"),
            new(typeof(General), GetIcon(nameof(General)), Locale.Settings_General),
            new(typeof(Appearance), GetIcon(nameof(Appearance)), Locale.Settings_Appearance),
            new(typeof(MultiScreen), GetIcon(nameof(MultiScreen)), Locale.Settings_MultiScreen),
            new(typeof(Advanced), GetIcon(nameof(Advanced)), Locale.Settings_Advanced),
            new(typeof(Profiles), SafeParseIcon("M3 13h8V3H3v10zm0 8h8v-6H3v6zm10 0h8V11h-8v10zm0-18v6h8V3h-8z"), Locale.Settings_Profiles ?? "配置方案"),
            new(typeof(About), GetIcon(nameof(About)), Locale.Settings_About),

            // Separator between sections
            new(null, null, string.Empty),

            // --- Section 2: 小组件库 ---
            new(null, null, "小组件库"),
            new(typeof(Gallery), SafeParseIcon("M4 4h7v7H4V4zm9 0h7v7h-7V4zm-9 9h7v7H4v-7zm9 0h7v7h-7v-7z"), "全部组件", null, allAssemblies),
            new(typeof(Gallery), SafeParseIcon("M12 2C6.5 2 2 6.5 2 12s4.5 10 10 10 10-4.5 10-10S17.5 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8zm.5-13H11v6l5.2 3.2.8-1.3-4.5-2.7V7z"), "时钟与日历", null, timeAssemblies),
            new(typeof(Gallery), SafeParseIcon("M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96z"), "天气与气象", null, weatherAssemblies),
            new(typeof(Gallery), SafeParseIcon("M20.5 3l-.16.03L15 5.1 9 3 3.36 4.9c-.21.07-.36.25-.36.48V20.5c0 .28.22.5.5.5l.16-.03L9 18.9l6 2.1 5.64-1.9c.21-.07.36-.25.36-.48V3.5c0-.28-.22-.5-.5-.5zM15 19l-6-2.11V5l6 2.11V19z"), "地图与出行", null, mapAssemblies),
            new(typeof(Gallery), SafeParseIcon("M19 3h-4.18C14.4 1.84 13.3 1 12 1c-1.3 0-2.4.84-2.82 2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-7 0c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1 .45-1 1-1zm2 14H7v-2h7v2zm3-4H7v-2h10v2zm0-4H7V7h10v2z"), "便签与待办", null, memoAssemblies),
            new(typeof(Gallery), SafeParseIcon("M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z"), "桌面与文件", null, desktopAssemblies),
            new(typeof(Gallery), SafeParseIcon("M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-7 14l-2.5-4.5-1.5 2.5H5v-2h2.5l1.5-2.5 2.5 4.5 3-6 2 4H19v2h-3.5l-2-4-1.5 3.5z"), "系统监控", null, monitorAssemblies),
            new(typeof(Gallery), SafeParseIcon("M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z"), "媒体与相册", null, mediaAssemblies),
            new(typeof(Gallery), SafeParseIcon("M22.7 19l-9.1-9.1c.9-2.3.4-5-1.5-6.9-2-2-5-2.4-7.4-1.3L9 6 6 9 1.7 4.7C.6 7.1 1 10.1 3 12.1c1.9 1.9 4.6 2.4 6.9 1.5l9.1 9.1c.4.4 1 .4 1.4 0l2.3-2.3c.4-.4.4-1 0-1.4z"), "实用工具", null, toolsAssemblies),
            new(typeof(Gallery), SafeParseIcon("M3 3h8v8H3V3zm10 0h8v8h-8V3zM3 13h8v8H3v-8zm10 0h8v8h-8v-8z"), "全景聚合", null, fixedAssemblies),
            new(typeof(Gallery), SafeParseIcon("M11.99 18.54l-7.37-5.73L3 14.07l9 7 9-7-1.63-1.27-7.38 5.74zM12 16l7.36-5.73L21 9.07l-9-7-9 7 1.63 1.2L12 16z"), "重叠组件", null, stackAssemblies),
        };

        if (otherAssemblies.Count > 0)
        {
            items.Add(new(typeof(Gallery), SafeParseIcon("M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z"), "其他扩展", null, otherAssemblies));
        }

        AllItems = items.ToArray();
    }

    private static StreamGeometry? SafeParseIcon(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        try { return StreamGeometry.Parse(data); }
        catch { return null; }
    }

    private static StreamGeometry? GetIcon(string name) =>
        (StreamGeometry?)(Application.Current!.TryFindResource(name, out var icon) ? icon : null);

    private readonly Dictionary<PageViewModel, UserControl> pageCache = new();

    public void SetCurrentPage(PageViewModel? value)
    {
        if (value == null || !value.IsItem) return;

        if (!pageCache.TryGetValue(value, out var page))
        {
            // IsItem (checked above) guarantees a Type; bail out without caching otherwise
            // instead of letting Activator.CreateInstance receive a null type.
            if (value.Type is not { } pageType)
            {
                CurrentPage = null;
                CurrentPageTitle = value.Text;
                return;
            }

            if (pageType == typeof(Gallery))
            {
                var assemblies = value.AssemblyInfos 
                    ?? (value.AssemblyInfo != null ? (IEnumerable<AssemblyInfo>)[value.AssemblyInfo] : Array.Empty<AssemblyInfo>());
                page = new Gallery(appSettingsProvider, layoutProvider, assemblyProvider, assemblies, widgetFactory, displayMonitor);
            }
            else
            {
                page = pageType switch
                {
                    var type when type == typeof(General) => new General(appSettingsProvider, updateService),
                    var type when type == typeof(Profiles) => new Profiles(profileService),
                    var type when type == typeof(Advanced) => new Advanced(appSettingsProvider, layoutProvider, displayMonitor, profileService),
                    var type when type == typeof(MultiScreen) => new MultiScreen(appSettingsProvider, layoutProvider, displayMonitor),
                    _ => (UserControl?) Activator.CreateInstance(pageType, appSettingsProvider)
                };
            }

            if (page != null)
            {
                pageCache[value] = page;
            }
        }

        CurrentPage = page;
        CurrentPageTitle = value.Text;
    }
}