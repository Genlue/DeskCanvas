using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DeskCanvas.Views;
using DeskCanvas.Views.Controls;
using Hitokoto.Views;
using Reminders.Views;
using Timetable.Views;

namespace UiFoundationChecks;

internal static partial class Program
{
    /// <summary>Hitokoto / Timetable / Reminders 弹窗迁移(W8)</summary>
    private static void CheckPopupMigrationInfo()
    {
        Console.WriteLine("--- Hitokoto / Timetable / Reminders 弹窗迁移(W8) ---");

        CheckHitokotoMigration();
        CheckTimetableMigration();
        CheckRemindersMigration();
    }

    // ---- Hitokoto (次序 4): 收藏/筛选、历史内容及动态标题 ----------------------------------

    private static void CheckHitokotoMigration()
    {
        Console.WriteLine("--- Hitokoto: 一言弹窗迁移 ---");

        CheckShellMigration(
            () => new HitokotoPopupWindow(),
            "Hitokoto",
            businessControls:
            [
                ("SearchBox", "搜索框"),
                ("TabFavorites", "收藏页签"),
                ("TabHistory", "历史页签"),
                ("ClearHistoryBtn", "清空历史按钮"),
                ("ItemsStack", "历史/收藏列表"),
                ("SpotlightFavBtn", "收藏动作按钮"),
                ("SpotlightLikeBtn", "点赞动作按钮"),
                ("ToastBanner", "Toast 提示"),
            ],
            headerInteractive: [],
            extra: (popup, shell) =>
            {
                // 动态标题（标题 + 分类徽标）必须活在 Shell 的 HeaderContent 槽位里。
                var badge = popup.FindControl<TextBlock>("CurrentCategoryBadge");
                Check(badge != null && LivesInSlot(badge, shell.HeaderContent),
                    "Hitokoto: 动态标题/分类徽标位于 Shell 的 HeaderContent 槽位");
                Check(!string.IsNullOrWhiteSpace(popup.FindControl<TextBlock>("SpotlightQuoteText")?.Text),
                    "Hitokoto: 焦点句已填充（业务路径未破坏）");
                Check(popup.FindControl<Button>("TabFavorites")?.Content != null,
                    "Hitokoto: 收藏页签计数已填充（业务路径未破坏）");
            });
    }

    // ---- Timetable (次序 5): 周标识、注释、课程表 -------------------------------------------

    private static void CheckTimetableMigration()
    {
        Console.WriteLine("--- Timetable: 课程表面板迁移 ---");

        CheckShellMigration(
            () => new TimetablePanelWindow(),
            "Timetable",
            businessControls:
            [
                ("PanelGridHost", "课程表网格宿主"),
                ("NotesCard", "注释卡片"),
                ("NotesPanel", "注释列表"),
                ("NotesHeaderText", "注释标题"),
            ],
            headerInteractive: [],
            extra: (popup, shell) =>
            {
                // 动态标题（课表名 + 周徽标）必须活在 Shell 的 HeaderContent 槽位里。
                var badge = popup.FindControl<TextBlock>("WeekBadgeText");
                Check(badge != null && LivesInSlot(badge, shell.HeaderContent),
                    "Timetable: 当前周标识位于 Shell 的 HeaderContent 槽位");
                Check(!string.IsNullOrWhiteSpace(badge?.Text),
                    "Timetable: 周徽标文本已填充（业务路径未破坏）");
                var gridHost = popup.FindControl<Grid>("PanelGridHost");
                Check(gridHost != null && gridHost.Children.Count > 0,
                    "Timetable: 周网格已构建（业务路径未破坏）");
            });
    }

    // ---- Reminders (次序 6): 输入/勾选、清单数据和滚动 --------------------------------------

    private static void CheckRemindersMigration()
    {
        Console.WriteLine("--- Reminders: 待办弹窗迁移 ---");

        CheckShellMigration(
            () => new RemindersPopupWindow(),
            "Reminders",
            businessControls:
            [
                ("TitleBox", "清单标题输入框"),
                ("SubtitleText", "统计副标题"),
                ("ItemsList", "清单列表"),
                ("EmptyPanel", "空态提示"),
            ],
            headerInteractive:
            [
                ("TitleBox", "清单标题输入框"),
                ("ClearCompletedButton", "清除已完成按钮"),
            ],
            extra: (popup, shell) =>
            {
                // 动态标题（可编辑清单名 + 计数）在 HeaderContent，业务动作在 HeaderActions。
                var titleBox = popup.FindControl<TextBox>("TitleBox");
                Check(titleBox != null && LivesInSlot(titleBox, shell.HeaderContent),
                    "Reminders: 可编辑清单标题位于 Shell 的 HeaderContent 槽位");
                var clearCompleted = popup.FindControl<Button>("ClearCompletedButton");
                Check(clearCompleted != null && ReferenceEquals(shell.HeaderActions, clearCompleted),
                    "Reminders: 清除已完成按钮位于 Shell 的 HeaderActions 槽位");
                var input = popup.FindControl<TextBox>("NewItemTextBox");
                Check(input != null && LivesInSlot(input, shell.FooterContent),
                    "Reminders: 新建输入框位于 Shell 的 FooterContent 槽位");
                Check(popup.FindControl<Button>("AddButton") != null,
                    "Reminders: 添加按钮仍在");
            });
    }

    // ---- 每个弹窗的公共迁移契约 ---------------------------------------------------------------

    private static void CheckShellMigration<TWindow>(
        Func<TWindow> create,
        string label,
        (string name, string what)[] businessControls,
        (string name, string what)[] headerInteractive,
        Action<TWindow, SecondaryPanelShell>? extra = null)
        where TWindow : SecondaryPanelWindow
    {
        // 1) headless 实例化 + 恰好一个 Shell + 无本地外壳残留 + PanelCard 回退。
        var popup = create();
        popup.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var shell = popup.Content as SecondaryPanelShell;
        Check(shell != null, $"{label}: 窗口根节点就是共享 SecondaryPanelShell");
        Check(popup.GetLogicalDescendants().OfType<SecondaryPanelShell>().Count() == 1,
            $"{label}: 树中恰好一个 Shell（无壳中壳、无包旧卡片）");

        // 旧外壳节点名必须彻底消失；树中唯一的玻璃表面就是 Shell 端口槽位持有的那一个。
        var names = popup.GetLogicalDescendants().OfType<Control>()
            .Select(c => c.Name).Where(n => n is not null).ToHashSet();
        Check(!names.Contains("CardBorder"), $"{label}: 无本地 CardBorder");
        Check(!names.Contains("LiquidGlassSurfaceControl"), $"{label}: 无本地 LiquidGlassSurfaceControl");
        Check(!names.Contains("CloseButton"), $"{label}: 无本地 CloseButton");
        var glassInTree = popup.GetVisualDescendants().OfType<LiquidGlassSurface>().ToList();
        Check(glassInTree.Count == 1 && ReferenceEquals(glassInTree[0], shell!.GlassSurface),
            $"{label}: Shell 端口槽位持有树中唯一玻璃表面（业务不得另声明）");

        // Styles 不得再声明外壳/玻璃/关闭按钮相关选择器。
        var selectors = popup.Styles.OfType<Style>()
            .Select(s => s.Selector?.ToString() ?? "").ToList();
        Check(selectors.All(s => !s.Contains("LiquidGlass") && !s.Contains("CloseButton")
                                 && !s.Contains("SecondaryPanelShell") && !s.Contains("CardBorder")),
            $"{label}: Styles 无外壳/玻璃/关闭按钮选择器");

        // 不再 override PanelCard，运行时解析到 Shell 的卡片。
        var getter = typeof(TWindow).GetProperty("PanelCard", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetGetMethod(true)!;
        Check(getter.DeclaringType == typeof(SecondaryPanelWindow),
            $"{label}: 不再 override PanelCard（走基类 Shell 回退）");
        var panelCard = typeof(SecondaryPanelWindow)
            .GetProperty("PanelCard", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(popup);
        var connectedCard = ReadField<SecondaryPanelShell?>(popup, "connectedShell")?.CardBorder;
        Check(ReferenceEquals(panelCard, connectedCard) && connectedCard is not null,
            $"{label}: 运行时 PanelCard 解析到已连接的 Shell 卡片");

        // 关键业务控件存在。
        foreach (var (name, what) in businessControls)
            Check(popup.FindControl<Control>(name) != null, $"{label}: {what}仍在 ({name})");

        // 标题拖动面挂 IsDragHandle；标题区里的业务交互控件（以及共享关闭按钮）必须被排除。
        Check(shell!.HeaderDragArea is not null && PanelDragBehavior.GetIsDragHandle(shell.HeaderDragArea!),
            $"{label}: 标题拖动面挂 PanelDragBehavior.IsDragHandle");
        foreach (var (name, what) in headerInteractive)
        {
            var control = popup.FindControl<Control>(name);
            Check(control != null && PanelDragBehavior.IsDragExcluded(control, shell.HeaderDragArea!),
                $"{label}: {what}被拖动排除 ({name})");
        }
        Check(shell.CloseButton is not null && PanelDragBehavior.IsDragExcluded(shell.CloseButton!, shell.HeaderDragArea!),
            $"{label}: 共享关闭按钮被拖动排除");

        extra?.Invoke(popup, shell);

        // 2) 关闭单路径（按钮）：走 Button 的真实 OnClick 路径（同 PanelShellChecks 的做法）。
        //    不用合成 MouseDown：headless 的 RunJobs/渲染 tick 不推进动画时钟，开场缩放
        //    （320ms）进行中卡片仍收在 60%，合成点击点会落在卡外命中背板（假阳性）或被
        //    输入管线整单吞掉（假失败）。真实滚轮/拖拽/动画完成度属于人工验收范围。
        var requests = 0;
        shell.AddHandler(SecondaryPanelShell.CloseRequestedEvent, (_, _) => requests++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        var onClick = typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, null, Array.Empty<Type>(), null)
                      ?? typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(RoutedEventArgs)], null)!;
        onClick.Invoke(shell.CloseButton, onClick.GetParameters().Length == 0
            ? Array.Empty<object?>()
            : [new RoutedEventArgs(Button.ClickEvent)]);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(requests == 1, $"{label}: 关闭按钮点击恰好一次 CloseRequested ({requests})");
        Check(ReadField<bool>(popup, "closeAnimationRunning"),
            $"{label}: 基类接管并进入退出动画态");
        FrameworkClose(popup);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 3) PART_Backdrop 按压（卡片圆角外的窗口角落）恰好一次 CloseRequested。
        var backdropPanel = create();
        backdropPanel.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // 渲染器命中测试器需要先跑一帧渲染（同 W7 检查）。
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var backdropShell = (SecondaryPanelShell)backdropPanel.Content!;
        var backdropRequests = 0;
        backdropShell.AddHandler(SecondaryPanelShell.CloseRequestedEvent, (_, _) => backdropRequests++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        backdropPanel.MouseDown(new Point(2, 2), MouseButton.Left);
        backdropPanel.MouseUp(new Point(2, 2), MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(backdropRequests == 1, $"{label}: PART_Backdrop 按压恰好一次 CloseRequested ({backdropRequests})");
        Check(ReadField<bool>(backdropPanel, "closeAnimationRunning"),
            $"{label}: 背板按压同样进入基类退出动画态");
        FrameworkClose(backdropPanel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 4) 卡内按压零 CloseRequested。
        var insidePanel = create();
        insidePanel.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var insideShell = (SecondaryPanelShell)insidePanel.Content!;
        var insideRequests = 0;
        insideShell.AddHandler(SecondaryPanelShell.CloseRequestedEvent, (_, _) => insideRequests++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        var contentHost = insideShell.ContentHost!;
        var insideSpot = contentHost.TranslatePoint(
            new Point(contentHost.Bounds.Width / 2, contentHost.Bounds.Height / 2), insidePanel)!.Value;
        insidePanel.MouseDown(insideSpot, MouseButton.Left);
        insidePanel.MouseUp(insideSpot, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(insideRequests == 0, $"{label}: 卡内按压零 CloseRequested ({insideRequests})");
        Check(!ReadField<bool>(insidePanel, "closeAnimationRunning"), $"{label}: 卡内按压后面板保持打开");
        FrameworkClose(insidePanel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 5) 模板重应用：detach/reattach 后 cardScale 订阅数恒 1。
        var reapplyPanel = create();
        reapplyPanel.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var reapplyShell = (SecondaryPanelShell)reapplyPanel.Content!;
        var firstScale = ReadField<ScaleTransform?>(reapplyPanel, "cardScale");
        Check(firstScale is not null && PropertyChangedHandlerCount(firstScale!) == 1,
            $"{label}: 初始连接恰好一个 cardScale 订阅");
        reapplyPanel.Content = null;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        reapplyPanel.Content = reapplyShell;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var reconnectedScale = ReadField<ScaleTransform?>(reapplyPanel, "cardScale");
        Check(reapplyShell.IsTemplateApplied && reconnectedScale is not null
              && PropertyChangedHandlerCount(reconnectedScale!) == 1,
            $"{label}: 模板重应用后 cardScale 订阅数仍恒 1（替换而非堆叠）");
        Check(reapplyShell.CardBorder!.Opacity == 1.0,
            $"{label}: 重应用后活动面板卡片立即显现（不卡在 Opacity=0）");
        FrameworkClose(reapplyPanel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 6) Shell 关闭按钮无障碍名在 zh-Hans/en 两文化下非空。
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { new CultureInfo("zh-Hans"), new CultureInfo("en") })
            {
                CultureInfo.CurrentUICulture = culture;
                var localized = create();
                localized.Show();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var localizedShell = (SecondaryPanelShell)localized.Content!;
                var accessibleName = localizedShell.CloseButton is null
                    ? null
                    : AutomationProperties.GetName(localizedShell.CloseButton);
                Check(!string.IsNullOrWhiteSpace(accessibleName),
                    $"{label} {culture.Name}: Shell 关闭按钮无障碍名非空 (\"{accessibleName}\")");
                FrameworkClose(localized);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    /// <summary>命名控件是否活在 Shell 的某个业务槽位（HeaderContent/FooterContent 等）子树内。</summary>
    private static bool LivesInSlot(Control? control, object? slot)
        => control != null && slot is Control slotRoot
           && (ReferenceEquals(control, slotRoot)
               || control.GetLogicalAncestors().Contains(slotRoot));
}
