using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Shortcuts.Models;
using Shortcuts.Services;
using Shortcuts.Services.Ai;
using Shortcuts.Views.Controls;

namespace Shortcuts.Views;

/// <summary>
/// Canvas host, node selection, run highlighting, the shared parameter editor, the diagnostics
/// panel, JSON export and the AI authoring drawer.
/// <para>
/// v2 关键变化：编辑提交**不再全量重建画布**（v1 在 CommitChange 里 RebuildCanvas，导致
/// ContextMenu/Flyout 锚点被销毁 —— 删除节点崩溃与各类"按钮点了没反应"的共同根源）。
/// 画布自身做增量更新；需要刷新某张卡片时调用 <see cref="WorkflowCanvas.RefreshNode"/>。
/// </para>
/// <para>
/// Every editor control here is driven by the catalog contract: a parameter's
/// <see cref="ParamSpec.Kind"/> picks the control, its <see cref="ParamSpec.DataType"/> decides
/// what the runner will accept, and its <see cref="ParamSpec.VisibleWhen"/> decides whether the
/// row is shown at all. Nothing is hard-coded per action type.
/// </para>
/// </summary>
public partial class ShortcutsEditorWindow
{
    private WorkflowCanvas? canvas;
    private bool runDetailsSubscribed;
    private Action<RunTrace>? traceHandler;

    /// <summary>Nodes highlighted after a run, so the post-run cleanup can clear them all.</summary>
    private readonly Dictionary<string, NodeRunState> nodeStates = new(StringComparer.Ordinal);

    /// <summary>当前打开的卡片锚定 Flyout（撤销/重做前先关闭，避免锚点悬挂）。</summary>
    private Flyout? openFlyout;
    private WorkflowNode? openParamNode;
    private bool openFlyoutRefreshing;

    private bool diagnosticsExpanded = true;
    private int validationVersion;
    private int toastVersion;
    private bool aiBusy;

    // ---- canvas -------------------------------------------------------------------

    private void RebuildCanvas()
    {
        var graph = shortcut.EffectiveGraph;

        if (canvas == null)
        {
            canvas = new WorkflowCanvas();
            canvas.SelectionChanged += _ => UpdateSelectionUi();
            canvas.GraphEdited += OnCanvasGraphEdited;
            canvas.ParamEditRequested += OpenParamEditor;
            canvas.ParamMenuRequested += OpenBindingMenu;
            canvas.NodeActivated += RenameNode;
            canvas.LinkRejected += rejection => ShowToast(rejection.Text, isError: true);
            canvas.NodeCardCreated += AttachNodeMenu;

            if (CanvasHost.Child is Panel host)
            {
                host.Children.Insert(0, canvas);
            }
            else
            {
                CanvasHost.Child = canvas;
            }

            Minimap.Source = canvas;
            SubscribeRunDetails();
        }

        // The canvas needs the document for ports that depend on it (start-node inputs).
        canvas.Owner = shortcut;
        RunDetailsTitle.Text = Loc.Get("Shortcuts_RunDetails_Title");
        ToolTip.SetTip(RunDetailsButton, Loc.Get("Shortcuts_RunDetails_Title"));
        canvas.Load(graph, resetView: false);
        EmptyHint.IsVisible = graph.Nodes.Count <= 1;
        UpdateSelectionUi();
        ScheduleValidation();
        RefreshRunDetails();
    }

    /// <summary>撤销/导入/切换后重载画布（全量重建，无锚点弹窗时才安全）。</summary>
    private void ReloadCanvasFromShortcut()
    {
        if (canvas == null)
        {
            RebuildCanvas();
            return;
        }

        canvas.Owner = shortcut;
        canvas.Load(shortcut.EffectiveGraph, resetView: false);
        EmptyHint.IsVisible = (canvas.Graph?.Nodes.Count ?? 0) <= 1;
        ScheduleValidation();
    }

    /// <summary>Fit the graph into view the first time a shortcut is opened.</summary>
    private void FitCanvasOnOpen() => canvas?.RequestFit();

    private void OnCanvasGraphEdited()
    {
        // The canvas already mutated the model; commit snapshots + persists. No rebuild.
        CommitChange();
    }

    private void UpdateSelectionUi()
    {
        // The empty-state hint is a canvas-level message: one node means "only the start node".
        EmptyHint.IsVisible = (canvas?.Graph?.Nodes.Count ?? 0) <= 1;
    }

    /// <summary>提交一次"改了某个节点内容"的编辑：只重建那张卡片，然后走通用提交。</summary>
    private void CommitNodeChange(WorkflowNode node)
    {
        canvas?.RefreshNode(node.Id);
        CommitChange();
    }

    /// <summary>给单张卡片挂右键菜单（画布在卡片创建/重建时回调）。</summary>
    private void AttachNodeMenu(WorkflowNodeCard card)
    {
        var node = card.Node;
        var menu = new ContextMenu();

        var edit = new MenuItem { Header = Loc.Get("Shortcuts_Node_Edit") };
        edit.Click += (_, _) => RenameNode(node);
        menu.Items.Add(edit);

        if (node.Type == WorkflowNodeTypes.Switch)
        {
            var cases = new MenuItem { Header = Loc.Get("Shortcuts_Node_EditCases") };
            cases.Click += (_, _) => EditSwitchCases(node);
            menu.Items.Add(cases);
        }

        if (ActionCatalog.Get(node.Type) is { Outputs.Count: > 0 })
        {
            var policy = new MenuItem
            {
                Header = Loc.Get("Shortcuts_Node_ErrorPolicy"),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = node.ErrorPolicy == ErrorPolicy.Route,
            };
            policy.Click += (_, _) =>
            {
                node.OnError = node.ErrorPolicy == ErrorPolicy.Route ? ErrorPolicy.Stop : ErrorPolicy.Route;
                CommitNodeChange(node);
            };
            menu.Items.Add(policy);
        }

        var duplicate = new MenuItem { Header = Loc.Get("Shortcuts_Menu_Duplicate") };
        duplicate.Click += (_, _) =>
        {
            card.ContextMenu?.Close();
            canvas?.Select(node.Id);
            DuplicateSelected();
        };
        menu.Items.Add(duplicate);

        if (node.Type != WorkflowNodeTypes.Start)
        {
            menu.Items.Add(new Separator());
            var delete = new MenuItem { Header = Loc.Get("Shortcuts_Menu_Delete") };
            delete.Click += (_, _) =>
            {
                // 先关菜单再删除：菜单锚在即将被移除的卡片上，先关闭可以彻底避免
                // 旧版"删除节点时 ContextMenu 锚点被销毁"的崩溃路径。
                card.ContextMenu?.Close();
                canvas?.Select(node.Id);
                DeleteSelected();
            };
            menu.Items.Add(delete);
        }

        card.ContextMenu = menu;
    }

    private void RenameNode(WorkflowNode node)
    {
        var box = new TextBox
        {
            Text = node.Title ?? string.Empty,
            PlaceholderText = Loc.Get(ActionCatalog.Get(node.Type)?.TitleKey ?? "Shortcuts_Action_note"),
            Width = 260,
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("Shortcuts_Node_Edit"), FontSize = 12, Opacity = 0.7 });
        panel.Children.Add(box);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        openFlyout = flyout;

        void Commit()
        {
            var text = (box.Text ?? string.Empty).Trim();
            var newTitle = text.Length == 0 ? null : text[..Math.Min(text.Length, 40)];
            if (!string.Equals(node.Title, newTitle, StringComparison.Ordinal))
                node.Title = newTitle;
            flyout.Hide();
            CommitChange();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };
        flyout.Closed += (_, _) =>
        {
            // Flyout 关闭后再刷新卡片：绝不在弹窗还开着时销毁它的锚点。
            Commit();
            openFlyout = null;
            canvas?.RefreshNode(node.Id);
        };

        if (canvas?.CardOf(node.Id) is { } card) flyout.ShowAt(card);
        flyout.Opened += (_, _) => box.Focus();
    }

    /// <summary>
    /// Switch arms: add / remove / rename rows. Each row keeps a stable port id, so editing the
    /// list never silently re-points an existing link.
    /// </summary>
    private void EditSwitchCases(WorkflowNode node)
    {
        var rows = new StackPanel { Spacing = 6, Width = 300 };
        var flyout = new Flyout { Content = rows, Placement = PlacementMode.Bottom };
        openFlyout = flyout;

        void Rebuild()
        {
            rows.Children.Clear();
            rows.Children.Add(new TextBlock
            {
                Text = Loc.Get("Shortcuts_Node_EditCases"),
                FontSize = 12,
                Opacity = 0.7,
            });

            foreach (var item in node.EffectiveCases.ToList())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var box = new TextBox { Text = item.Value, Width = 210 };
                box.LostFocus += (_, _) =>
                {
                    item.Value = box.Text ?? string.Empty;
                    CommitChange();
                };

                var remove = new Button { Content = "×", Width = 28, Focusable = false };
                remove.Click += (_, _) =>
                {
                    node.Cases?.Remove(item);
                    var graph = canvas?.Graph;
                    if (graph != null)
                        graph.Links.RemoveAll(l => l.From == node.Id && l.FromPort == item.Id);
                    CommitChange();
                    Rebuild();
                };

                row.Children.Add(box);
                row.Children.Add(remove);
                rows.Children.Add(row);
            }

            var add = new Button { Content = Loc.Get("Shortcuts_Node_AddCase"), Focusable = false };
            add.Click += (_, _) =>
            {
                node.EffectiveCases.Add(new WorkflowCase
                {
                    Id = "case_" + ShortcutDefinition.NewId()[..6],
                    Value = string.Empty,
                });
                CommitChange();
                Rebuild();
            };
            rows.Children.Add(add);
        }

        flyout.Closed += (_, _) =>
        {
            openFlyout = null;
            canvas?.RefreshNode(node.Id);
        };

        Rebuild();
        if (canvas?.CardOf(node.Id) is { } card) flyout.ShowAt(card);
    }

    private void OnZoomInClicked(object? sender, RoutedEventArgs e) => canvas?.ZoomBy(1.15);

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) => canvas?.ZoomBy(1 / 1.15);

    private void OnZoomFitClicked(object? sender, RoutedEventArgs e) => canvas?.ZoomToFit();

    private void OnAutoLayoutClicked(object? sender, RoutedEventArgs e)
    {
        if (canvas == null) return;
        canvas.AutoLayout();
        CommitChange();
        ShowToast(Loc.Get("Shortcuts_Canvas_AutoLayout"), isError: false);
    }

    private void DeleteSelected()
    {
        if (canvas == null) return;
        var deleted = canvas.DeleteSelected();
        if (deleted == 0) return;

        CommitChange();
        UpdateSelectionUi();
        ShowToast(deleted == 1
            ? Loc.Get("Shortcuts_Canvas_DeletedOne")
            : Loc.Format("Shortcuts_Canvas_DeletedCount", deleted), isError: false);
    }

    private void DuplicateSelected()
    {
        if (canvas == null) return;
        if (canvas.DuplicateSelected() == 0) return;
        CommitChange();
        UpdateSelectionUi();
    }

    // ---- toast ---------------------------------------------------------------------

    /// <summary>顶部浮动 toast：替代旧版"借用标题栏文本"的临时提示（会互相覆盖、且连线被拒永不显示）。</summary>
    private void ShowToast(string message, bool isError)
    {
        ToastText.Text = message;
        ToastBorder.Background = new SolidColorBrush(Color.Parse(isError ? "#E6A32D2D" : "#E64A4A4A"));
        ToastBorder.IsVisible = true;

        var version = ++toastVersion;
        DispatcherTimer.RunOnce(() =>
        {
            if (version == toastVersion) ToastBorder.IsVisible = false;
        }, TimeSpan.FromSeconds(isError ? 6 : 3.5));
    }

    // ---- validation ----------------------------------------------------------------

    /// <summary>防抖校验：编辑后 300ms 重算一次，结果映射到节点徽标 + 诊断条。</summary>
    private void ScheduleValidation()
    {
        var version = ++validationVersion;
        DispatcherTimer.RunOnce(() =>
        {
            if (version == validationVersion) ValidateNow();
        }, TimeSpan.FromMilliseconds(300));
    }

    private void ValidateNow()
    {
        if (canvas?.Graph == null) return;

        var report = WorkflowValidator.Validate(canvas.Graph, shortcut, forRun: false);
        canvas.SetDiagnostics(report.Diagnostics);
        UpdateDiagnosticsPanel(report);
    }

    private void UpdateDiagnosticsPanel(ValidationReport report)
    {
        var errors = report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var warnings = report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();

        if (errors.Count == 0 && warnings.Count == 0)
        {
            DiagnosticsBar.IsVisible = false;
            return;
        }

        DiagnosticsBar.IsVisible = true;
        DiagnosticsHeaderText.Text = errors.Count > 0
            ? Loc.Format("Shortcuts_Diagnostics_HeaderErrors", errors.Count, warnings.Count)
            : Loc.Format("Shortcuts_Diagnostics_HeaderWarnings", warnings.Count);
        DiagnosticsHeaderText.Foreground = Visuals.Brush(errors.Count > 0 ? "#A32D2D" : "#B3922E");

        DiagnosticsScroll.IsVisible = diagnosticsExpanded;
        DiagnosticsCollapseGlyph.Text = diagnosticsExpanded ? "▾" : "▸";

        DiagnosticsContent.Children.Clear();
        foreach (var diagnostic in report.Diagnostics.Take(50))
        {
            var nodeTitle = diagnostic.NodeId != null ? canvas?.ResolveNodeTitle(diagnostic.NodeId) : null;
            var row = new Button
            {
                Classes = { "library-item" },
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(new TextBlock
            {
                Text = diagnostic.Severity == DiagnosticSeverity.Error ? "●" : "▲",
                FontSize = 9,
                Foreground = Visuals.Brush(diagnostic.Severity == DiagnosticSeverity.Error ? "#A32D2D" : "#B3922E"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = diagnostic.Text + (nodeTitle != null ? $"  ·  {nodeTitle}" : string.Empty),
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Content = panel;
            ToolTip.SetTip(row, $"[{diagnostic.Code}] {diagnostic.Text}");

            var targetNode = diagnostic.NodeId;
            row.Click += (_, _) =>
            {
                if (targetNode == null || canvas == null) return;
                canvas.Select(targetNode);
                canvas.CenterOnNode(targetNode);
            };
            DiagnosticsContent.Children.Add(row);
        }
    }

    private void OnDiagnosticsCollapseClicked(object? sender, RoutedEventArgs e)
    {
        diagnosticsExpanded = !diagnosticsExpanded;
        DiagnosticsScroll.IsVisible = diagnosticsExpanded;
        DiagnosticsCollapseGlyph.Text = diagnosticsExpanded ? "▾" : "▸";
    }

    private void OnDiagnosticsCloseClicked(object? sender, RoutedEventArgs e) =>
        DiagnosticsBar.IsVisible = false;

    // ---- run feedback -------------------------------------------------------------

    private void SetNodeState(string nodeId, NodeRunState state)
    {
        nodeStates[nodeId] = state;
        canvas?.SetRunState(nodeId, state);
    }

    private void SetNodeOutput(string nodeId, string? text, bool failed)
    {
        if (canvas?.CardOf(nodeId) is not { } card) return;
        if (string.IsNullOrEmpty(text)) return;
        ToolTip.SetTip(card, text.Length > 400 ? text[..400] + "…" : text);
    }

    private void ClearRunHighlights(bool keepFailures)
    {
        foreach (var (nodeId, state) in nodeStates.ToList())
        {
            if (keepFailures && state == NodeRunState.Failed) continue;
            nodeStates[nodeId] = NodeRunState.Idle;
            canvas?.SetRunState(nodeId, NodeRunState.Idle);
        }
    }

    // ---- run details ---------------------------------------------------------------

    private void SubscribeRunDetails()
    {
        if (runDetailsSubscribed) return;
        runDetailsSubscribed = true;
        // 持有处理器引用，窗口关闭时退订 —— 旧版匿名 lambda 永不退订，静态事件把已关闭的
        // 窗口钉在内存里（泄漏），还会在窗口关闭后继续触发刷新。
        traceHandler = trace =>
        {
            if (trace.ShortcutId != shortcut.Id) return;
            if (Dispatcher.UIThread.CheckAccess()) RefreshRunDetails();
            else Dispatcher.UIThread.Post(RefreshRunDetails);
        };
        RunHistory.TraceUpdated += traceHandler;
    }

    private void OnRunDetailsClicked(object? sender, RoutedEventArgs e)
    {
        RunDetailsHost.IsVisible = !RunDetailsHost.IsVisible;
        if (RunDetailsHost.IsVisible) RefreshRunDetails();
    }

    private void OnRunDetailsCloseClicked(object? sender, RoutedEventArgs e) =>
        RunDetailsHost.IsVisible = false;

    /// <summary>
    /// Render the latest run: execution order, loop iteration, resolved inputs, typed outputs,
    /// duration and the exact node that failed.
    /// </summary>
    private void RefreshRunDetails()
    {
        RunDetailsContent.Children.Clear();

        var trace = RunHistory.Latest(shortcut.Id);
        if (trace == null)
        {
            RunDetailsContent.Children.Add(Muted(Loc.Get("Shortcuts_RunDetails_Empty")));
            return;
        }

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock
        {
            Text = $"{StatusText(trace.Status)} · {trace.Duration?.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture) ?? "—"} s",
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
        });
        header.Children.Add(Muted($"{Loc.Get("Shortcuts_RunDetails_RunId")}: {trace.RunId}"));
        if (trace.Error != null) header.Children.Add(Error(trace.Error));
        RunDetailsContent.Children.Add(header);

        foreach (var step in trace.Steps)
        {
            var card = new Border
            {
                Background = Visuals.Brush("#0A000000"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
            };

            var panel = new StackPanel { Spacing = 2 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            title.Children.Add(new TextBlock
            {
                Text = $"{step.Index}. {step.Title}",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
            });
            if (step.Iteration >= 0)
            {
                title.Children.Add(Muted(Loc.Format("Shortcuts_RunDetails_Iteration", step.Iteration)));
            }

            title.Children.Add(Muted($"{step.DurationMs} ms"));
            panel.Children.Add(title);

            foreach (var (label, value) in step.Inputs)
                panel.Children.Add(Muted($"← {label}: {value}"));

            foreach (var (port, value) in step.Outputs)
                panel.Children.Add(Muted($"→ {port}: {value}"));

            if (step.Error != null) panel.Children.Add(Error(step.Error));

            card.Child = panel;
            RunDetailsContent.Children.Add(card);
        }

        static TextBlock Muted(string text) => new()
        {
            Text = text,
            FontSize = 10.5,
            Opacity = 0.62,
            TextWrapping = TextWrapping.Wrap,
        };

        static TextBlock Error(string text) => new()
        {
            Text = text,
            FontSize = 10.5,
            Foreground = Visuals.Brush("#A32D2D"),
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static string StatusText(string status) => status switch
    {
        "success" => Loc.Get("Shortcuts_RunDetails_Success"),
        "failed" => Loc.Get("Shortcuts_RunDetails_Failed"),
        "cancelled" => Loc.Get("Shortcuts_RunDetails_Cancelled"),
        _ => Loc.Get("Shortcuts_RunDetails_Running"),
    };

    // ---- parameter editor ---------------------------------------------------------

    private void OpenParamEditor(WorkflowNode node, ParamSpec spec, Control anchor)
    {
        var panel = new StackPanel { Spacing = 8, Width = 320, MinWidth = 240 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get(spec.LabelKey),
            FontSize = 12,
            Opacity = 0.7,
        });

        if (spec.HelpKey != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Get(spec.HelpKey),
                FontSize = 10.5,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        Control? initialFocus = null;
        openFlyout = flyout;
        openParamNode = node;

        void Commit(ShortcutValue newValue, bool close)
        {
            node.Params[spec.Key] = newValue;
            CommitChange();
            if (close) flyout.Hide();
            // 卡片刷新统一延迟到 flyout.Closed：弹窗还开着时重建卡片会销毁自己的锚点。
        }

        // A bound parameter is read-only here: the editor must not offer a text box whose
        // contents would silently be ignored by the runner.
        if (canvas?.Graph?.IncomingData(node.Id, spec.Key) is { } existing)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Format("Shortcuts_Param_BoundFrom",
                    $"{canvas.ResolveNodeTitle(existing.From) ?? existing.From}.{canvas.PortLabelOf(existing.From, existing.FromPort)}"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

            var detach = new Button { Content = Loc.Get("Shortcuts_Param_Detach"), Focusable = false };
            detach.Click += (_, _) =>
            {
                DetachBinding(node, spec);
                flyout.Hide();
            };
            panel.Children.Add(detach);
            flyout.ShowAt(anchor);
            flyout.Opened += (_, _) => FocusEditor(initialFocus);
            return;
        }

        switch (spec.Kind)
        {
            case ParamKind.Choice:
            {
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                var current = node.Params.GetValueOrDefault(spec.Key);
                var selectedIndex = 0;
                var options = spec.Options ?? [];
                for (var i = 0; i < options.Count; i++)
                {
                    combo.Items.Add(new ComboBoxItem { Content = Loc.Get(options[i].LabelKey), Tag = options[i].Value });
                    if (current != null && !current.IsVariable && current.Text == options[i].Value)
                        selectedIndex = i;
                }

                combo.SelectedIndex = selectedIndex;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: string option })
                        Commit(ShortcutValue.Literal(option), close: true);
                };
                panel.Children.Add(combo);
                initialFocus = combo;
                break;
            }

            case ParamKind.Number:
            {
                var current = node.Params.GetValueOrDefault(spec.Key);
                var box = new NumericUpDown
                {
                    Minimum = (decimal)spec.Min,
                    Maximum = (decimal)spec.Max,
                    Increment = spec.Max - spec.Min > 10 ? 1 : 0.1m,
                    FormatString = "0.###",
                    Value = current is { IsVariable: false } && double.TryParse(current.Text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var parsed) ? (decimal)parsed : (decimal)((spec.Min + spec.Max) / 2),
                };

                // Commit once on close instead of per tick.
                var userTouched = false;
                var variableChosen = false;
                box.ValueChanged += (_, _) => userTouched = true;
                flyout.Closed += (_, _) =>
                {
                    if (!userTouched || variableChosen) return;
                    if (box.Value is { } value)
                        Commit(ShortcutValue.Literal(value.ToString(CultureInfo.InvariantCulture)), close: false);
                };
                panel.Children.Add(box);
                if (spec.Bindable)
                    panel.Children.Add(BuildBindingRow(node, spec, (newValue, close) =>
                    {
                        variableChosen = true;
                        Commit(newValue, close);
                    }));
                initialFocus = box;
                break;
            }

            case ParamKind.Hotkey:
            {
                var current = node.Params.GetValueOrDefault(spec.Key);
                var original = current is { IsVariable: false } ? current.Text?.Trim() ?? string.Empty : string.Empty;
                var capture = new HotkeyCaptureBox
                {
                    Value = original,
                    RequireModifier = false,
                    IsReadOnly = false,
                    ShowStatus = true,
                };

                // Do not rebuild the canvas while the flyout is open.
                var pending = original;
                var changed = false;
                capture.ValueChanged += (_, e) =>
                {
                    pending = e.Value ?? string.Empty;
                    changed = true;
                };
                panel.Children.Add(capture);

                flyout.Closed += (_, _) =>
                {
                    if (!changed) return;
                    if (pending.Length == 0) node.Params.Remove(spec.Key);
                    else node.Params[spec.Key] = ShortcutValue.Literal(pending);
                    CommitChange();
                };

                initialFocus = capture;
                break;
            }

            case ParamKind.Shortcut:
            {
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                var current = node.Params.GetValueOrDefault(spec.Key)?.Text;
                var selected = 0;
                var items = ShortcutsStore.Get().Shortcuts.Where(s => s.Id != shortcut.Id).ToList();
                for (var i = 0; i < items.Count; i++)
                {
                    combo.Items.Add(new ComboBoxItem { Content = items[i].Name, Tag = items[i].Id });
                    if (items[i].Id == current) selected = i;
                }

                if (items.Count == 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = Loc.Get("Shortcuts_Param_NoShortcuts"),
                        FontSize = 11,
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                    });
                    break;
                }

                combo.SelectedIndex = selected;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: string id })
                        Commit(ShortcutValue.Literal(id), close: true);
                };
                panel.Children.Add(combo);
                initialFocus = combo;
                break;
            }

            case ParamKind.MultilineText:
            case ParamKind.Json:
            case ParamKind.List:
            {
                var current = node.Params.GetValueOrDefault(spec.Key);
                var box = new TextBox
                {
                    Text = current is { IsVariable: false } ? current.Text ?? string.Empty : string.Empty,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = spec.Kind == ParamKind.List ? 90 : 120,
                    MaxHeight = 240,
                };

                if (spec.Kind is ParamKind.Json or ParamKind.List)
                {
                    box.FontFamily = new FontFamily("Consolas,Menlo,monospace");
                    box.FontSize = 11;
                }

                if (spec.Kind == ParamKind.List)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = Loc.Get("Shortcuts_Param_ListHint"),
                        FontSize = 10.5,
                        Opacity = 0.55,
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                var variableChosen = false;
                void CommitText(bool close)
                {
                    if (variableChosen) return;
                    var text = box.Text ?? string.Empty;
                    if (text.Length == 0 && current is null) return;
                    Commit(ShortcutValue.Literal(text), close);
                }

                box.LostFocus += (_, _) => CommitText(false);
                box.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
                    CommitText(true);
                    e.Handled = true;
                };

                panel.Children.Add(box);
                if (spec.Bindable)
                    panel.Children.Add(BuildBindingRow(node, spec, (newValue, close) =>
                    {
                        variableChosen = true;
                        Commit(newValue, close);
                    }));
                initialFocus = box;
                break;
            }

            default:
            {
                var current = node.Params.GetValueOrDefault(spec.Key);
                var box = new TextBox { Text = current is { IsVariable: false } ? current.Text ?? string.Empty : string.Empty };

                var variableChosen = false;
                var browsing = false;
                void CommitText(bool close)
                {
                    if (variableChosen || browsing) return;

                    var text = box.Text ?? string.Empty;
                    if (text.Length == 0 && (current is null || current.IsVariable)) return;

                    Commit(ShortcutValue.Literal(text), close);
                }

                box.LostFocus += (_, _) => CommitText(false);
                box.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    CommitText(true);
                    e.Handled = true;
                };

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                box.Width = 200;
                row.Children.Add(box);

                if (spec.Kind is ParamKind.FilePath or ParamKind.FolderPath)
                    row.Children.Add(BuildBrowseButton(spec, box, Commit, value => browsing = value));

                panel.Children.Add(row);
                if (spec.Bindable)
                    panel.Children.Add(BuildBindingRow(node, spec, (newValue, close) =>
                    {
                        variableChosen = true;
                        Commit(newValue, close);
                    }));
                initialFocus = box;
                break;
            }
        }

        // Focus once the popup is actually open (see v1 notes: a synchronous Focus() right after
        // ShowAt is dropped because the flyout's content is not attached yet).
        flyout.Opened += (_, _) =>
        {
            FocusEditor(initialFocus);
            Dispatcher.UIThread.Post(() => FocusEditor(initialFocus), DispatcherPriority.Input);
        };
        flyout.Closed += (_, _) =>
        {
            openFlyout = null;
            openParamNode = null;
            if (!openFlyoutRefreshing) canvas?.RefreshNode(node.Id);
            openFlyoutRefreshing = false;
        };
        flyout.ShowAt(anchor);
    }

    /// <summary>Right-click on a parameter row: jump to the source, detach, or pick a variable.</summary>
    private void OpenBindingMenu(WorkflowNode node, ParamSpec spec, Control anchor)
    {
        var menu = new MenuFlyout();
        var graph = canvas?.Graph;

        if (graph?.IncomingData(node.Id, spec.Key) is { } link)
        {
            var jump = new MenuItem { Header = Loc.Get("Shortcuts_Param_JumpToSource") };
            jump.Click += (_, _) =>
            {
                canvas?.Select(link.From);
                canvas?.CenterOnNode(link.From);
            };
            menu.Items.Add(jump);

            var detach = new MenuItem { Header = Loc.Get("Shortcuts_Param_Detach") };
            detach.Click += (_, _) => DetachBinding(node, spec);
            menu.Items.Add(detach);
            menu.Items.Add(new Separator());
        }

        var manual = new MenuItem { Header = Loc.Get("Shortcuts_Param_EditManual") };
        manual.Click += (_, _) => OpenParamEditor(node, spec, anchor);
        menu.Items.Add(manual);

        if (spec.Bindable)
        {
            var variable = new MenuItem { Header = Loc.Get("Shortcuts_Param_UseVariable") };
            variable.Click += (_, _) => OpenReferenceMenu(node, spec, anchor);
            menu.Items.Add(variable);
        }

        menu.ShowAt(anchor);
    }

    /// <summary>Remove whatever feeds a parameter and keep its current value as a literal.</summary>
    private void DetachBinding(WorkflowNode node, ParamSpec spec)
    {
        // Bind the surface itself, not just its graph: the two writes below (links + card
        // refresh) are canvas calls, and a null canvas means there is nothing to detach from.
        if (canvas is not { Graph: { } graph }) return;

        var literal = node.Params.GetValueOrDefault(spec.Key) is { IsVariable: false } existing
            ? existing.Text ?? string.Empty
            : string.Empty;

        graph.Links.RemoveAll(l => l.To == node.Id && l.Kind == LinkKinds.Data &&
                                   string.Equals(l.ToPort, spec.Key, StringComparison.Ordinal));
        node.Params[spec.Key] = ShortcutValue.Literal(literal);
        graph.Invalidate();
        canvas.InvalidateLinks();
        CommitChange();

        // 参数编辑器还开在这个节点上时不能立刻重建卡片（会销毁它的锚点），
        // 由编辑器自己的 flyout.Closed 统一刷新。
        if (!ReferenceEquals(openParamNode, node))
            canvas.RefreshNode(node.Id);
    }

    private void OpenReferenceMenu(WorkflowNode node, ParamSpec spec, Control anchor)
    {
        var menu = new MenuFlyout();

        var last = new MenuItem { Header = Loc.Get("Shortcuts_Param_LastResult") };
        last.Click += (_, _) => CommitReference(ShortcutValue.Variable(ValueKinds.LastResult));
        menu.Items.Add(last);
        menu.Items.Add(new Separator());

        var any = false;
        foreach (var candidate in VariableCandidates(node))
        {
            var definition = ActionCatalog.Get(candidate.Type);
            var title = definition != null ? Loc.Get(definition.TitleKey) : candidate.Type;
            if (!string.IsNullOrWhiteSpace(candidate.Title)) title = candidate.Title!;

            var ports = WorkflowNodeTypes.Outputs(candidate, shortcut)
                .Where(p => p.IsData)
                .ToList();

            var compatible = ports
                .Where(p => WorkflowNodeTypes.IsTypeCompatible(p.DataType, spec))
                .ToList();

            if (compatible.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = Loc.Format("Shortcuts_Param_Incompatible", title),
                    IsEnabled = false,
                });
                any = true;
                continue;
            }

            any = true;
            if (compatible.Count == 1)
            {
                var only = compatible[0];
                var item = new MenuItem
                {
                    Header = $"{title} · {only.Display ?? Loc.Get(only.LabelKey)}",
                };
                item.Click += (_, _) => CommitReference(ShortcutValue.Variable(candidate.Id, only.Id));
                menu.Items.Add(item);
                continue;
            }

            var group = new MenuItem { Header = title };
            foreach (var port in compatible)
            {
                var item = new MenuItem { Header = port.Display ?? Loc.Get(port.LabelKey) };
                var portId = port.Id;
                var nodeId = candidate.Id;
                item.Click += (_, _) => CommitReference(ShortcutValue.Variable(nodeId, portId));
                group.Items.Add(item);
            }

            menu.Items.Add(group);
        }

        if (!any)
            menu.Items.Add(new MenuItem { Header = Loc.Get("Shortcuts_Param_NoCandidates"), IsEnabled = false });

        menu.ShowAt(anchor);

        void CommitReference(ShortcutValue value)
        {
            canvas?.Graph?.Links.RemoveAll(l => l.To == node.Id && l.Kind == LinkKinds.Data &&
                                                string.Equals(l.ToPort, spec.Key, StringComparison.Ordinal));
            node.Params[spec.Key] = value;
            CommitChange();

            if (ReferenceEquals(openParamNode, node))
            {
                // 从参数编辑器里打开的变量菜单：编辑器还开着，先收起它，由其 Closed 刷新。
                openFlyoutRefreshing = true;
                openFlyout?.Hide();
            }
            else
            {
                canvas?.RefreshNode(node.Id);
            }
        }
    }

    /// <summary>The hotkey capture needs focus inside its text box, not on the wrapper control.</summary>
    private static void FocusEditor(Control? editor)
    {
        if (editor is HotkeyCaptureBox capture) capture.FocusInput();
        else editor?.Focus();
    }

    private Button BuildBrowseButton(ParamSpec spec, TextBox box, Action<ShortcutValue, bool> commit,
        Action<bool>? onBrowsingChanged = null)
    {
        // Focusable=false is essential: pressing a focusable button would steal focus from the
        // text box, whose lose-focus commit would otherwise run mid-press.
        var button = new Button
        {
            Content = Loc.Get("Shortcuts_Param_Browse"),
            FontSize = 12,
            Focusable = false,
        };
        button.Click += (_, _) =>
        {
            onBrowsingChanged?.Invoke(true);
            try
            {
                var ownerHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                var titleKey = spec.Kind switch
                {
                    ParamKind.FolderPath => "Shortcuts_Param_PickFolder",
                    ParamKind.FilePath => "Shortcuts_Param_PickFile",
                    _ => "Shortcuts_Param_PickApp",
                };
                var title = Loc.Get(titleKey);
                var picked = spec.Kind == ParamKind.FolderPath
                    ? FileDialogs.PickFolder(ownerHandle, title)
                    : FileDialogs.PickFile(ownerHandle, title);

                if (!string.IsNullOrEmpty(picked))
                {
                    box.Text = picked;
                    commit(ShortcutValue.Literal(picked), false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Shortcuts] Browse failed: {ex.Message}");
            }
            finally
            {
                onBrowsingChanged?.Invoke(false);
            }
        };
        return button;
    }

    private StackPanel BuildBindingRow(WorkflowNode node, ParamSpec spec, Action<ShortcutValue, bool> commit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(BuildVariableButton(node, spec, commit));
        return row;
    }

    private Button BuildVariableButton(WorkflowNode node, ParamSpec spec, Action<ShortcutValue, bool> commit)
    {
        var button = new Button
        {
            Content = Loc.Get("Shortcuts_Param_UseVariable"),
            FontSize = 12,
            Focusable = false,
        };
        button.Click += (_, _) => OpenReferenceMenu(node, spec, button);
        return button;
    }

    // ---- JSON export ---------------------------------------------------------------

    /// <summary>把当前快捷指令导出为独立 JSON 文件（与 shortcuts.json 的条目同构，可直接再导入）。</summary>
    private void OnExportJsonClicked(object? sender, RoutedEventArgs e)
    {
        var safeName = string.IsNullOrWhiteSpace(shortcut.Name) ? "shortcut" : shortcut.Name!;
        foreach (var c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
        if (safeName.Length > 60) safeName = safeName[..60];

        var path = FileDialogs.PutFile(
            TryGetPlatformHandle()?.Handle ?? IntPtr.Zero,
            Loc.Get("Shortcuts_Canvas_Export"),
            safeName + ".json",
            "json");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(shortcut, DocumentJson.FileOptions));
            ShowToast(Loc.Get("Shortcuts_Canvas_Exported"), isError: false);
        }
        catch (Exception ex)
        {
            ShowToast(Loc.Get("Shortcuts_Canvas_ExportFailed") + " " + ex.Message, isError: true);
        }
    }

    // ---- AI authoring --------------------------------------------------------------

    private void OnAiToggleClicked(object? sender, RoutedEventArgs e)
    {
        AiPanel.IsVisible = !AiPanel.IsVisible;
        if (AiPanel.IsVisible)
        {
            LoadAiSettingsToUi();
            if (AiPromptBox.Text is not { Length: > 0 }) AiPromptBox.Focus();
        }
    }

    private AiSettings LoadAiSettingsFromUi() => new()
    {
        BaseUrl = string.IsNullOrWhiteSpace(AiBaseUrlBox.Text) ? "https://api.openai.com/v1" : AiBaseUrlBox.Text!.Trim(),
        ApiKey = AiApiKeyBox.Text?.Trim() ?? string.Empty,
        Model = string.IsNullOrWhiteSpace(AiModelBox.Text) ? "gpt-4o-mini" : AiModelBox.Text!.Trim(),
    };

    private void LoadAiSettingsToUi()
    {
        var settings = AiSettingsStore.Load();
        if (AiBaseUrlBox.Text != settings.BaseUrl) AiBaseUrlBox.Text = settings.BaseUrl;
        if (AiApiKeyBox.Text != settings.ApiKey) AiApiKeyBox.Text = settings.ApiKey;
        if (AiModelBox.Text != settings.Model) AiModelBox.Text = settings.Model;
    }

    private void OnAiSettingCommitted(object? sender, RoutedEventArgs e) =>
        AiSettingsStore.Save(LoadAiSettingsFromUi());

    private async void OnAiGenerateClicked(object? sender, RoutedEventArgs e)
    {
        if (aiBusy) return;

        var settings = LoadAiSettingsFromUi();
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            AiSettingsExpander.IsExpanded = true;
            AiStatusText.Text = Loc.Get("Shortcuts_Ai_NeedKey");
            AiStatusText.Foreground = Visuals.Brush("#A32D2D");
            return;
        }

        var prompt = AiPromptBox.Text?.Trim();
        if (string.IsNullOrEmpty(prompt)) return;

        AiSettingsStore.Save(settings);
        AiStatusText.Foreground = default;
        AiStatusText.Text = Loc.Get("Shortcuts_Ai_Working");
        AiProgress.IsVisible = true;
        AiGenerateButton.IsEnabled = false;
        AiGenerateText.Text = Loc.Get("Shortcuts_Ai_Working");
        aiBusy = true;

        try
        {
            var currentGraph = canvas?.Graph is { Nodes.Count: > 0 } graph ? graph : null;
            var result = await AiWorkflowService.GenerateAsync(settings, prompt, currentGraph, shortcut);

            if (!result.Success)
            {
                AiStatusText.Text = result.Error;
                AiStatusText.Foreground = Visuals.Brush("#A32D2D");
                ShowToast(result.Error ?? Loc.Get("Shortcuts_Ai_Failed"), isError: true);
                return;
            }

            ApplyAiWorkflow(result.WorkflowJson!);
            AiStatusText.Text = Loc.Get("Shortcuts_Ai_Done");
        }
        catch (Exception ex)
        {
            AiStatusText.Text = ex.Message;
            AiStatusText.Foreground = Visuals.Brush("#A32D2D");
        }
        finally
        {
            aiBusy = false;
            AiProgress.IsVisible = false;
            AiGenerateButton.IsEnabled = true;
            AiGenerateText.Text = Loc.Get("Shortcuts_Ai_Generate");
        }
    }

    /// <summary>
    /// 把 AI 返回的 JSON 走标准导入管线（解析 + 修复 + 校验 + 自动排版），
    /// 成功后整体替换当前画布内容（可撤销）。
    /// </summary>
    private void ApplyAiWorkflow(string json)
    {
        try
        {
            var result = WorkflowImport.Parse(json, relayout: true);
            if (!result.Success || result.Shortcuts.Count == 0)
            {
                var first = result.Errors.FirstOrDefault() ?? Loc.Get("Shortcuts_Ai_ParseFailed");
                AiStatusText.Text = first;
                AiStatusText.Foreground = Visuals.Brush("#A32D2D");
                ShowToast(first, isError: true);
                return;
            }

            var imported = result.Shortcuts[0];
            shortcut.Graph = imported.Graph;
            shortcut.InputFields = imported.InputFields;
            shortcut.Actions = [];

            openFlyout?.Hide();
            ReloadCanvasFromShortcut();
            FitCanvasOnOpen();
            CommitChange();
            UpdateToolbar();
            UpdateUndoRedo();
            ShowToast(Loc.Get("Shortcuts_Ai_Applied"), isError: false);
        }
        catch (Exception ex)
        {
            ShowToast(Loc.Get("Shortcuts_Ai_ParseFailed") + " " + ex.Message, isError: true);
        }
    }

    // ---- import (existing dialog) --------------------------------------------------

    /// <summary>
    /// Import an AI-authored workflow. Each parsed shortcut is persisted first and then opened,
    /// so a failure to open never loses the document the user just pasted.
    /// </summary>
    private async void OnImportJsonClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var result = await WorkflowImportWindow.PromptAsync(this);
            if (result is not { Success: true }) return;

            foreach (var imported in result.Shortcuts)
            {
                imported.UpdatedAt = DateTime.Now;
                ShortcutsStore.Upsert(imported, this);
            }

            var first = result.Shortcuts[0];
            if (first.Id == shortcut.Id)
            {
                shortcut = first.Clone();
                history.Clear();
                history.Add(Snapshot());
                historyIndex = 0;
                UpdateToolbar();
                ReloadCanvasFromShortcut();
                FitCanvasOnOpen();
                UpdateUndoRedo();
            }
            else
            {
                SelectShortcut(first.Id);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Shortcuts] Import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Show the JSON spec with a copy button — the same text the settings page offers, so the
    /// "copy → paste to the AI → import the reply" loop works from either entry point.
    /// </summary>
    private void OnShowSpecClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;

        var spec = WorkflowSpecDocument.Build();
        var box = new TextBox
        {
            Text = spec,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas,Menlo,monospace"),
            FontSize = 11,
            Width = 640,
            Height = 460,
        };

        var copyButton = new Button { Content = Loc.Get("Shortcuts_Spec_Copy"), Focusable = false };
        copyButton.Click += async (_, _) =>
        {
            await CopyTextAsync(spec);
            copyButton.Content = Loc.Get("Shortcuts_Spec_Copied");
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = Loc.Get("Shortcuts_Spec_Title"),
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(copyButton);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get("Shortcuts_Spec_Hint"),
            FontSize = 11,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
        });
        panel.Children.Add(box);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        flyout.ShowAt(anchor);
    }

    /// <summary>Copy to the system clipboard; silently no-ops on hosts without a clipboard.</summary>
    internal async Task CopyTextAsync(string text)
    {
        try
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Shortcuts] Clipboard failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Nodes whose output can be referenced from <paramref name="current"/>. On a canvas there is
    /// no "earlier in the list" — any other executable node is a candidate, which is what makes
    /// data reuse possible across branches.
    /// </summary>
    private IEnumerable<WorkflowNode> VariableCandidates(WorkflowNode current)
    {
        if (canvas?.Graph is not { } graph) yield break;

        foreach (var node in graph.Nodes)
        {
            if (node.Id == current.Id) continue;
            if (node.Type is WorkflowNodeTypes.Start or WorkflowNodeTypes.Note) continue;
            if (WorkflowNodeTypes.Outputs(node, shortcut).All(p => !p.IsData)) continue;
            yield return node;
        }
    }
}
