using System;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using Storage.Models;
using Storage.Services;
using Storage.ViewModels;

namespace Storage.Views;

public partial class StorageView : UserControl, IWidgetSelfRefreshing, IWidgetSuspendable
{
    private readonly IWidgetLayoutProvider? layoutProvider;
    private StorageViewModel? viewModel;
    private StorageModel model;

    private long lastClickTick;
    private string lastClickedDriveRoot = string.Empty;
    private PixelPoint? lastClickWinPos;

    public StorageView() : this(new StorageModel(), null) { }

    public StorageView(IWidgetLayoutProvider layoutProvider) : this(new StorageModel(), layoutProvider) { }

    public StorageView(StorageModel model) : this(model, null) { }

    public StorageView(StorageModel model, IWidgetLayoutProvider? layoutProvider)
    {
        this.model = model ?? new StorageModel();
        this.layoutProvider = layoutProvider;
        viewModel = new StorageViewModel(this.model);
        viewModel.DrivesUpdated += OnDrivesUpdated;
        DataContext = viewModel;

        InitializeComponent();
        BindRingItems(viewModel);
        AttachRingDoubleClickHandlers();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ActualThemeVariantChanged += (_, _) => viewModel?.RefreshBrushes();
        ApplyLayout();
    }

    private void BindRingItems(StorageViewModel vm)
    {
        RingItem0.DataContext = vm.RingSlots[0];
        RingItem1.DataContext = vm.RingSlots[1];
        RingItem2.DataContext = vm.RingSlots[2];
        RingItem3.DataContext = vm.RingSlots[3];
    }

    private void AttachRingDoubleClickHandlers()
    {
        AttachDriveClickHandler(RingItem0, () => viewModel?.RingSlots[0]);
        AttachDriveClickHandler(RingItem1, () => viewModel?.RingSlots[1]);
        AttachDriveClickHandler(RingItem2, () => viewModel?.RingSlots[2]);
        AttachDriveClickHandler(RingItem3, () => viewModel?.RingSlots[3]);
    }

    private void AttachDriveClickHandler(Control control, Func<DriveItemViewModel?> itemResolver)
    {
        control.PointerPressed += (_, e) =>
        {
            if (!model.DoubleClickToOpen) return;
            var pt = e.GetCurrentPoint(this);
            if (!pt.Properties.IsLeftButtonPressed) return;

            var item = itemResolver();
            if (item == null || !item.HasDrive || string.IsNullOrWhiteSpace(item.RootPath))
                return;

            var now = Environment.TickCount64;
            var currentWinPos = (VisualRoot as Window)?.Position;
            bool windowDidNotMove = lastClickWinPos == null || currentWinPos == null ||
                (Math.Abs(currentWinPos.Value.X - lastClickWinPos.Value.X) < 6 &&
                 Math.Abs(currentWinPos.Value.Y - lastClickWinPos.Value.Y) < 6);

            bool isDoubleClick = e.ClickCount >= 2 ||
                (now - lastClickTick <= 450 &&
                 windowDidNotMove &&
                 string.Equals(lastClickedDriveRoot, item.RootPath, StringComparison.OrdinalIgnoreCase));

            if (isDoubleClick)
            {
                lastClickTick = 0;
                lastClickedDriveRoot = string.Empty;
                e.Handled = true;
                StorageService.OpenDriveInExplorer(item.RootPath);
            }
            else
            {
                lastClickTick = now;
                lastClickedDriveRoot = item.RootPath;
                lastClickWinPos = currentWinPos;
            }
        };
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        SizeChanged += OnSizeChanged;

        if (viewModel == null)
        {
            viewModel = new StorageViewModel(model);
            viewModel.DrivesUpdated += OnDrivesUpdated;
            DataContext = viewModel;
            BindRingItems(viewModel);
        }
        ApplyLayout();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        if (viewModel != null)
        {
            viewModel.DrivesUpdated -= OnDrivesUpdated;
            viewModel.Dispose();
            viewModel = null;
        }
    }

    private void OnDrivesUpdated() => ApplyLayout();

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        if (viewModel == null) return;

        var (cols, rows) = ResolveSpan();
        bool isSingleCell = cols == 1 && rows == 1;

        if (model.DisplayMode == StorageDisplayMode.RingDashboard || isSingleCell)
        {
            ListScaleHost.IsVisible = false;
            RingScaleHost.IsVisible = true;
            ApplyRingLayout(cols, rows);
        }
        else
        {
            RingScaleHost.IsVisible = false;
            ListScaleHost.IsVisible = true;
            ApplyListLayout(cols, rows);
        }
    }

    private void ApplyRingLayout(int spanCols, int spanRows)
    {
        var items = new[] { RingItem0, RingItem1, RingItem2, RingItem3 };
        var valueContainers = new[] { RingValueContainer0, RingValueContainer1, RingValueContainer2, RingValueContainer3 };

        if (spanCols == 1 && spanRows == 1)
        {
            RingBoard.ColumnDefinitions = new ColumnDefinitions("*");
            RingBoard.RowDefinitions = new RowDefinitions("*");
            RingBoard.Width = 104;
            RingBoard.Height = 104;

            RingItem0.IsVisible = true;
            Grid.SetColumn(RingItem0, 0);
            Grid.SetRow(RingItem0, 0);
            RingValueContainer0.IsVisible = false;

            for (int i = 1; i < items.Length; i++)
                items[i].IsVisible = false;
            return;
        }

        for (int i = 0; i < items.Length; i++)
            items[i].IsVisible = true;

        var (columns, rows, showBottomText) = ResolveRingGrid(spanCols, spanRows);

        RingBoard.ColumnDefinitions = new ColumnDefinitions(
            columns == 1 ? "*" : columns == 4 ? "*,*,*,*" : "*,*");
        RingBoard.RowDefinitions = new RowDefinitions(
            rows == 1 ? "*" : rows == 4 ? "*,*,*,*" : "*,*");

        (RingBoard.Width, RingBoard.Height) = columns == 4
            ? showBottomText ? (360d, 150d) : (360d, 90d)
            : rows == 4
                ? showBottomText ? (120d, 360d) : (90d, 360d)
                : showBottomText ? (220d, 220d) : (200d, 200d);

        var index = 0;
        for (var row = 0; row < rows && index < items.Length; row++)
        {
            for (var col = 0; col < columns && index < items.Length; col++)
            {
                var item = items[index];
                Grid.SetColumn(item, col);
                Grid.SetRow(item, row);
                valueContainers[index].IsVisible = showBottomText;
                index++;
            }
        }
    }

    /// <summary>
    /// Identical to MultiDashboard.ResolveLayout:
    /// 2×2 → 2×2 rings (NO bottom text);
    /// 4×2 → 4×1 rings + bottom text;
    /// 4×4 → 2×2 rings + bottom text.
    /// </summary>
    private static (int Columns, int Rows, bool ShowBottomText) ResolveRingGrid(int sCols, int sRows)
    {
        if (sCols >= 3 && sRows >= 3) return (2, 2, true);
        if (sCols >= 3 && sRows == 2) return (4, 1, true);
        if (sCols >= 3 && sRows == 1) return (4, 1, false);
        if (sCols == 1 && sRows >= 3) return (1, 4, true);
        if (sCols == 1 && sRows == 2) return (2, 2, false);
        if (sCols == 2 && sRows == 1) return (4, 1, false);
        return (2, 2, false);
    }

    private void ApplyListLayout(int spanCols, int spanRows)
    {
        if (viewModel == null) return;
        ListBoard.Children.Clear();
        ListBoard.RowDefinitions.Clear();
        ListBoard.ColumnDefinitions.Clear();

        var drives = viewModel.Drives;

        // 1. Wide Strip (e.g. 2x1, 3x1, 4x1)
        if (spanRows == 1 && spanCols >= 2)
        {
            int maxDrives = Math.Min(2, Math.Max(1, drives.Count));
            ListBoard.Width = spanCols >= 3 ? 260 : 172;
            ListBoard.Height = 74;

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = maxDrives == 1 ? 0 : 8,
                Margin = new Thickness(2, 0)
            };

            foreach (var d in drives.Take(maxDrives))
            {
                stack.Children.Add(BuildCompactDriveRow(
                    d,
                    barMaxWidth: ListBoard.Width - 6,
                    barHeight: 8.5,
                    showSubLine: maxDrives == 1,
                    largeFont: false));
            }

            ListBoard.Children.Add(stack);
            return;
        }

        // 2. Medium Wide 4x2 (Columns >= 3, Rows == 2)
        if (spanCols >= 3 && spanRows == 2)
        {
            ListBoard.Width = 348;
            ListBoard.Height = 162;
            ListBoard.RowDefinitions = new RowDefinitions(model.ShowHeader ? "Auto,*" : "*");

            int contentRow = 0;
            if (model.ShowHeader)
            {
                var header = BuildHeaderBar(viewModel.HeaderTitle, viewModel.WideSummaryText, fontSize: 13.5);
                header.Margin = new Thickness(2, 0, 2, 6);
                Grid.SetRow(header, 0);
                ListBoard.Children.Add(header);
                contentRow = 1;
            }

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,8,*"),
                RowDefinitions = new RowDefinitions("*,6,*")
            };

            var truncated = drives.Take(4).ToList();
            for (int i = 0; i < truncated.Count; i++)
            {
                var card = BuildDriveSubCard(truncated[i], cardInnerWidth: 148, showBadge: false);
                Grid.SetColumn(card, (i % 2) * 2);
                Grid.SetRow(card, (i / 2) * 2);
                grid.Children.Add(card);
            }

            Grid.SetRow(grid, contentRow);
            ListBoard.Children.Add(grid);
            return;
        }

        // 3. Large Dashboard 4x4 (Columns >= 3, Rows >= 3)
        if (spanCols >= 3 && spanRows >= 3)
        {
            ListBoard.Width = 348;
            ListBoard.Height = 344;
            ListBoard.RowDefinitions = new RowDefinitions(model.ShowHeader ? "Auto,*" : "*");

            int contentRow = 0;
            if (model.ShowHeader)
            {
                var overviewBanner = BuildLargeOverviewBanner(viewModel, bannerWidth: 324);
                overviewBanner.Margin = new Thickness(0, 0, 0, 8);
                Grid.SetRow(overviewBanner, 0);
                ListBoard.Children.Add(overviewBanner);
                contentRow = 1;
            }

            int maxSlots = model.ShowHeader ? 6 : 8;
            int rowsCount = maxSlots / 2;
            var rowDefs = string.Join(",6,", Enumerable.Repeat("*", rowsCount));

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,8,*"),
                RowDefinitions = new RowDefinitions(rowDefs)
            };

            var truncated = drives.Take(maxSlots).ToList();
            for (int i = 0; i < truncated.Count; i++)
            {
                var card = BuildDriveSubCard(truncated[i], cardInnerWidth: 148, showBadge: true);
                Grid.SetColumn(card, (i % 2) * 2);
                Grid.SetRow(card, (i / 2) * 2);
                grid.Children.Add(card);
            }

            Grid.SetRow(grid, contentRow);
            ListBoard.Children.Add(grid);
            return;
        }

        // 4. Default Standard 2x2 (or 2x3, 1x2, etc.)
        // Use a compact 168x162 virtual canvas matching actual 2x2 DIP size so text is never scaled down tiny!
        bool isTall = spanRows >= 3;
        ListBoard.Width = 168;
        ListBoard.Height = isTall ? 260 : 162;
        ListBoard.RowDefinitions = new RowDefinitions(model.ShowHeader ? "Auto,*" : "*");

        int listRow = 0;
        if (model.ShowHeader)
        {
            var header = BuildHeaderBar(viewModel.HeaderTitle, viewModel.HeaderSummaryText, fontSize: 14);
            header.Margin = new Thickness(1, 0, 1, 4);
            Grid.SetRow(header, 0);
            ListBoard.Children.Add(header);
            listRow = 1;
        }

        int maxItems = isTall
            ? (model.ShowHeader ? 5 : 6)
            : (model.ShowHeader ? 3 : 4);

        var visibleDrives = drives.Take(maxItems).ToList();
        var listContainer = new Grid();
        if (visibleDrives.Count > 0)
        {
            var rDefs = string.Join(",", Enumerable.Repeat("*", visibleDrives.Count));
            listContainer.RowDefinitions = new RowDefinitions(rDefs);

            bool twoOrFewer = visibleDrives.Count <= 2 && !isTall;
            for (int i = 0; i < visibleDrives.Count; i++)
            {
                var rowCtrl = BuildCompactDriveRow(
                    visibleDrives[i],
                    barMaxWidth: 164,
                    barHeight: twoOrFewer ? 9.5 : 8,
                    showSubLine: twoOrFewer,
                    largeFont: twoOrFewer);
                rowCtrl.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(rowCtrl, i);
                listContainer.Children.Add(rowCtrl);
            }
        }

        Grid.SetRow(listContainer, listRow);
        ListBoard.Children.Add(listContainer);
    }

    private Control BuildHeaderBar(string title, string rightSummary, double fontSize)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(1, 0)
        };

        var icon = new PathIcon
        {
            Data = StreamGeometry.Parse("M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zm0 2v8h16V6H4zm0 10v2h16v-2H4zm13 0.5a1 1 0 1 1 0 2 1 1 0 0 1 0-2zm-3 0a1 1 0 1 1 0 2 1 1 0 0 1 0-2z"),
            Width = 15,
            Height = 15,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.92
        };
        icon.Classes.Add("elem-shadow");
        icon.Bind(PathIcon.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundAccentBrush"));
        Grid.SetColumn(icon, 0);

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = fontSize,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        Grid.SetColumn(titleBlock, 1);

        var summaryBlock = new TextBlock
        {
            Text = rightSummary,
            FontSize = fontSize - 1,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.82,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        summaryBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        Grid.SetColumn(summaryBlock, 2);

        grid.Children.Add(icon);
        grid.Children.Add(titleBlock);
        grid.Children.Add(summaryBlock);
        return grid;
    }

    private Control BuildCompactDriveRow(
        DriveItemViewModel item,
        double barMaxWidth,
        double barHeight = 8,
        bool showSubLine = false,
        bool largeFont = false)
    {
        var container = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(1, 1),
            Cursor = model.DoubleClickToOpen ? new Cursor(StandardCursorType.Hand) : Cursor.Default
        };
        ToolTip.SetTip(container, item.TooltipText);
        AttachDriveClickHandler(container, () => item);

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = showSubLine ? 4 : 3
        };

        var topRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };

        var iconSize = largeFont ? 14.5 : 13;
        var icon = new PathIcon
        {
            Data = StreamGeometry.Parse(item.IconData),
            Width = iconSize,
            Height = iconSize,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.9
        };
        icon.Classes.Add("elem-shadow");
        icon.Bind(PathIcon.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        Grid.SetColumn(icon, 0);

        var nameText = new TextBlock
        {
            Text = item.DisplayTitle,
            FontSize = largeFont ? 14 : 13,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        nameText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        Grid.SetColumn(nameText, 1);

        var statusText = new TextBlock
        {
            Text = item.RightStatusText,
            FontSize = largeFont ? 13 : 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Opacity = item.UsagePercent >= 75 && model.UseWarningColors ? 0.98 : 0.86
        };
        if (item.UsagePercent >= 75 && model.UseWarningColors)
            statusText.Foreground = item.StatusBrush;
        else
            statusText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        Grid.SetColumn(statusText, 2);

        topRow.Children.Add(icon);
        topRow.Children.Add(nameText);
        topRow.Children.Add(statusText);

        var barTrack = BuildProgressBar(item.UsageFraction, item.StatusBrush, barMaxWidth, barHeight);

        stack.Children.Add(topRow);
        stack.Children.Add(barTrack);

        if (showSubLine)
        {
            var subRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };
            var leftSub = new TextBlock
            {
                Text = item.UsedOverTotalText,
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                Opacity = 0.78
            };
            leftSub.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
            Grid.SetColumn(leftSub, 0);

            var rightSub = new TextBlock
            {
                Text = $"{item.UsagePercent}%",
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Opacity = 0.88
            };
            rightSub.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
            Grid.SetColumn(rightSub, 1);

            subRow.Children.Add(leftSub);
            subRow.Children.Add(rightSub);
            stack.Children.Add(subRow);
        }

        container.Child = stack;
        return container;
    }

    private Control BuildDriveSubCard(DriveItemViewModel item, double cardInnerWidth, bool showBadge)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.Parse("#14808080")),
            BorderBrush = new SolidColorBrush(Color.Parse("#22808080")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6),
            Cursor = model.DoubleClickToOpen ? new Cursor(StandardCursorType.Hand) : Cursor.Default
        };
        ToolTip.SetTip(card, item.TooltipText);
        AttachDriveClickHandler(card, () => item);

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var topRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto")
        };

        var icon = new PathIcon
        {
            Data = StreamGeometry.Parse(item.IconData),
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.9
        };
        icon.Classes.Add("elem-shadow");
        icon.Bind(PathIcon.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        Grid.SetColumn(icon, 0);

        var titleText = new TextBlock
        {
            Text = item.DisplayTitle,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        Grid.SetColumn(titleText, 1);

        if (showBadge)
        {
            var fsBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.Parse("#1F808080")),
                Padding = new Thickness(4, 1),
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = item.DriveFormat,
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Opacity = 0.8
                }
            };
            ((TextBlock)fsBadge.Child!).Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
            Grid.SetColumn(fsBadge, 2);
            topRow.Children.Add(fsBadge);
        }

        var pctText = new TextBlock
        {
            Text = item.PercentText,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = item.StatusBrush,
            Margin = new Thickness(4, 0, 0, 0)
        };
        Grid.SetColumn(pctText, 3);

        topRow.Children.Add(icon);
        topRow.Children.Add(titleText);
        topRow.Children.Add(pctText);
        Grid.SetRow(topRow, 0);

        var bar = BuildProgressBar(item.UsageFraction, item.StatusBrush, cardInnerWidth, barHeight: 8);
        bar.VerticalAlignment = VerticalAlignment.Center;
        bar.Margin = new Thickness(0, 4);
        Grid.SetRow(bar, 1);

        var bottomRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var usedText = new TextBlock
        {
            Text = item.UsedOverTotalText,
            FontSize = 11.5,
            FontWeight = FontWeight.Medium,
            Opacity = 0.78,
            VerticalAlignment = VerticalAlignment.Center
        };
        usedText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        Grid.SetColumn(usedText, 0);

        var freeText = new TextBlock
        {
            Text = item.FreeSpaceText,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.86,
            VerticalAlignment = VerticalAlignment.Center
        };
        freeText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        Grid.SetColumn(freeText, 1);

        bottomRow.Children.Add(usedText);
        bottomRow.Children.Add(freeText);
        Grid.SetRow(bottomRow, 2);

        grid.Children.Add(topRow);
        grid.Children.Add(bar);
        grid.Children.Add(bottomRow);
        card.Child = grid;
        return card;
    }

    private Control BuildLargeOverviewBanner(StorageViewModel vm, double bannerWidth)
    {
        var banner = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.Parse("#16808080")),
            BorderBrush = new SolidColorBrush(Color.Parse("#24808080")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8)
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 5
        };

        var header = BuildHeaderBar($"{vm.HeaderTitle} ({vm.Drives.Count} 个分区)", vm.WideSummaryText, fontSize: 13.5);
        var primaryBrush = vm.Drives.FirstOrDefault()?.StatusBrush ?? new SolidColorBrush(Color.Parse("#007AFF"));
        var bar = BuildProgressBar(vm.TotalUsageFraction, primaryBrush, bannerWidth, barHeight: 8.5);

        stack.Children.Add(header);
        stack.Children.Add(bar);
        banner.Child = stack;
        return banner;
    }

    private static Border BuildProgressBar(double fraction, IBrush fillBrush, double trackWidth, double barHeight)
    {
        var clamped = Math.Clamp(fraction, 0.0, 1.0);
        var radius = new CornerRadius(barHeight / 2.0);
        var fillWidth = clamped <= 0.0 ? 0.0 : Math.Max(barHeight, trackWidth * clamped);

        var track = new Border
        {
            Width = trackWidth,
            Height = barHeight,
            CornerRadius = radius,
            Background = new SolidColorBrush(Color.Parse("#26808080")),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ClipToBounds = true
        };

        var fill = new Border
        {
            Width = fillWidth,
            Height = barHeight,
            CornerRadius = radius,
            Background = fillBrush,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        fill.Classes.Add("elem-shadow");

        track.Child = fill;
        return track;
    }

    private (int Columns, int Rows) ResolveSpan()
    {
        for (var node = this.GetVisualParent(); node != null; node = node.GetVisualParent())
        {
            if (node is DeskCanvas.Views.Widget widget)
                return widget.CurrentSpan;
        }

        // Gallery preview or standalone fallback
        var size = Bounds.Size;
        if (size.Width > 0 && size.Height > 0)
        {
            if (size.Width <= 110 && size.Height <= 110)
                return (1, 1);
            if (size.Width > size.Height * 1.6 && size.Height <= 120)
                return (2, 1);
            if (size.Width > size.Height * 1.4 && size.Height > 120)
                return (4, 2);
            if (size.Width >= 320 && size.Height >= 320)
                return (4, 4);
        }
        return (2, 2);
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings || settings.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var updated = settings.Deserialize<StorageModel>();
            if (updated != null)
            {
                model = updated;
                viewModel?.UpdateModel(updated);
                ApplyLayout();
            }
        }
        catch { }
    }

    public void Suspend() => viewModel?.Suspend();

    public void Resume() => viewModel?.Resume();
}
