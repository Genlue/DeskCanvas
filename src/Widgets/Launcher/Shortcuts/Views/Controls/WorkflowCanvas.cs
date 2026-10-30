using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Shortcuts.Models;
using Shortcuts.Services;

namespace Shortcuts.Views.Controls;

/// <summary>Identifies one port dot: which node, which port, and which side it lives on.</summary>
public sealed record PortRef(string NodeId, string PortId, bool IsOutput);

/// <summary>
/// Identifies a parameter row on a node card. Parameter rows own their presses — left opens the
/// value editor, right opens the binding menu — so the canvas must recognize them while the
/// press is still tunnelling down and must not turn the press into a node drag.
/// </summary>
public sealed record ParamRowRef(WorkflowNode Node, ParamSpec Spec, Control Anchor);

public enum NodeRunState
{
    Idle,
    Running,
    Failed,
}

/// <summary>
/// 节点编辑画布（v2，全面重写）。
/// <para>
/// 结构：画布自身是屏幕空间，子层依次为
/// ① <see cref="gridLayer"/> —— 屏幕空间点阵/线网（修复了旧版把网格画在变换空间里导致的
/// 视差 bug：网格间距被平方缩放、随平移漂移）；
/// ② <see cref="graphRoot"/> —— 持有平移/缩放变换的图空间，内含连线层与节点卡片；
/// ③ 框选橡皮筋（屏幕空间，永远在最上层）。
/// </para>
/// <para>
/// 与 v1 的关键差异：
/// - **连线可交互**：连线几何被采样后参与命中测试，可以点选、高亮、右键菜单、Delete 删除；
/// - **框选多选**：空白处左键拖拽 = 框选（旧版是平移），平移改为中键 / 空格 + 左键；
/// - **增量更新**：添加/删除/刷新节点只动一张卡片，编辑提交不再全量重建 —— 这是旧版
/// "删除节点崩溃（ContextMenu 锚点被销毁）"与卡顿的共同根源；
/// - **彩色语义**：执行连线继承源节点类别色、数据连线继承数据类型色；
/// - **零分配渲染**：画刷/画笔全局缓存，连线几何按端点缓存，变换对象复用。
/// </para>
/// </summary>
public sealed class WorkflowCanvas : Canvas
{
    private const double MinScale = 0.25;
    private const double MaxScale = 2.5;
    private const double PortHitRadius = 14;
    private const double LinkHitRadius = 8; // 屏幕像素，命中测试时按缩放换算

    private readonly Dictionary<string, WorkflowNodeCard> cards = new(StringComparer.Ordinal);
    private readonly HashSet<string> selectedNodeIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<WorkflowDiagnostic>> diagnosticsByNode =
        new(StringComparer.Ordinal);

    /// <summary>屏幕空间网格层（点阵），永远铺满视口、与图坐标对齐。</summary>
    private readonly GridLayer gridLayer = new() { IsHitTestVisible = false };

    /// <summary>
    /// 持有卡片与连线并承载平移/缩放变换。必须是子控件而不是画布自身：
    /// <see cref="Layoutable.ClipToBounds"/> 裁剪的是本地空间，变换被裁剪控件自己会把可视区域一起缩小。
    /// </summary>
    private readonly Canvas graphRoot = new() { ClipToBounds = false };

    private readonly LinkLayer linkLayer = new() { IsHitTestVisible = false };
    private readonly Border rubberBand = CreateRubberBand();

    private readonly ScaleTransform scaleTransform = new();
    private readonly TranslateTransform translateTransform = new();

    /// <summary>连线几何缓存：端点不变就复用同一份 StreamGeometry。</summary>
    private readonly Dictionary<string, (Point Start, Point End, StreamGeometry Geometry)> linkGeometry =
        new(StringComparer.Ordinal);

    private double scale = 1.0;
    private Point offset;

    // ---- 手势状态 ------------------------------------------------------------------
    private WorkflowNode? dragNode;
    private Dictionary<string, Point> dragStartPositions = new(StringComparer.Ordinal);
    private Point dragStartGraph;
    private bool dragMoved;

    private PortRef? linkSource;
    private Point linkCursor;

    private bool panning;
    private Point panStart;

    private bool banding;
    private Point bandStart;
    private bool spaceHeld;

    private string? hoveredLinkId;
    private Size lastFitSize;
    private bool pendingFit;
    private bool viewTouched;

    // ---- 事件 ----------------------------------------------------------------------

    /// <summary>Raised when the selected node changes (null = nothing or a link is selected).</summary>
    public event Action<WorkflowNode?>? SelectionChanged;

    /// <summary>Raised after a user gesture edits the graph (move, link, nudge) — commit + snapshot.</summary>
    public event Action? GraphEdited;

    /// <summary>A parameter chip was clicked: open the shared parameter editor.</summary>
    public event Action<WorkflowNode, ParamSpec, Control>? ParamEditRequested;

    /// <summary>A parameter chip was right-clicked: open the binding menu (source / detach).</summary>
    public event Action<WorkflowNode, ParamSpec, Control>? ParamMenuRequested;

    /// <summary>Double-click on a node: rename it.</summary>
    public event Action<WorkflowNode>? NodeActivated;

    /// <summary>A dropped link was rejected by the validator — the editor shows it as a toast.</summary>
    public event Action<WorkflowDiagnostic>? LinkRejected;

    /// <summary>Pan / zoom / extent changed — the minimap listens to this.</summary>
    public event Action? ViewChanged;

    /// <summary>A node card was (re)created — the editor attaches its context menu here.</summary>
    public event Action<WorkflowNodeCard>? NodeCardCreated;

    // Events can only be raised by the declaring type; the node cards ask through these.

    internal void RequestParamEdit(WorkflowNode node, ParamSpec spec, Control anchor) =>
        ParamEditRequested?.Invoke(node, spec, anchor);

    internal void RequestActivate(WorkflowNode node) => NodeActivated?.Invoke(node);

    internal void RequestParamMenu(WorkflowNode node, ParamSpec spec, Control anchor) =>
        ParamMenuRequested?.Invoke(node, spec, anchor);

    public WorkflowGraph? Graph { get; private set; }

    /// <summary>
    /// The shortcut that owns the graph. Needed for ports that depend on the document (a start
    /// node exposes one data port per declared input), so the card and the runner agree.
    /// </summary>
    public ShortcutDefinition? Owner { get; set; }

    /// <summary>主选节点（最后点击的一个；框选多个时它也在集合里）。</summary>
    public string? SelectedNodeId { get; private set; }

    /// <summary>当前选中的连线（与节点选择互斥）。</summary>
    public string? SelectedLinkId { get; private set; }

    /// <summary>框选产生的多选集合。</summary>
    public IReadOnlyCollection<string> SelectedNodeIds => selectedNodeIds;

    /// <summary>有手势进行中（拖拽 / 连线 / 平移 / 框选）——窗口用它挡住撤销等全量替换。</summary>
    public bool IsGestureActive => dragNode != null || linkSource != null || panning || banding;

    /// <summary>The card visual for a node (null when the node is not on this canvas).</summary>
    public WorkflowNodeCard? CardOf(string nodeId) => cards.GetValueOrDefault(nodeId);

    /// <summary>Display title of a node, used to label `{{nodeId}}` references on the cards.</summary>
    internal string? ResolveNodeTitle(string nodeId)
    {
        var node = Graph?.Find(nodeId);
        if (node == null) return null;

        if (!string.IsNullOrWhiteSpace(node.Title)) return node.Title;
        var definition = ActionCatalog.Get(node.Type);
        return definition == null ? node.Type : Loc.Get(definition.TitleKey);
    }

    public WorkflowNode? SelectedNode =>
        SelectedNodeId == null ? null : Graph?.Find(SelectedNodeId);

    public WorkflowCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Background = Brushes.Transparent;
        // Pan/zoom math assumes the transform is anchored at the top-left; Avalonia's default is
        // the centre, which silently adds a half-size offset to every coordinate.
        graphRoot.RenderTransformOrigin = RelativePoint.TopLeft;
        graphRoot.RenderTransform = new TransformGroup
        {
            Children = { scaleTransform, translateTransform },
        };

        Children.Add(gridLayer);
        Children.Add(graphRoot);
        Children.Add(rubberBand);

        AddHandler(PointerPressedEvent, OnCanvasPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnCanvasPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnCanvasPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnCanvasWheel, RoutingStrategies.Tunnel, handledEventsToo: true);

        linkLayer.Draw = DrawScene;
        gridLayer.Draw = DrawGrid;
        ContextRequested += OnCanvasContextRequested;
        KeyDown += OnCanvasKeyDown;
        KeyUp += OnCanvasKeyUp;
    }

    // ---- 加载与增量更新 ------------------------------------------------------------

    /// <summary>True while <see cref="Load"/> is rebuilding; a nested request is queued instead.</summary>
    private bool loading;

    /// <summary>A rebuild requested from inside a rebuild (card created / selection changed).</summary>
    private (WorkflowGraph Graph, bool ResetView)? pendingLoad;

    /// <summary>
    /// 全量重建（仅用于打开 / 切换 / 撤销 / 导入；日常编辑走增量 API）。
    /// <para>
    /// 重建会触发 <see cref="NodeCardCreated"/> 与 <see cref="SelectionChanged"/>，编辑器的处理器
    /// 完全可能在这些回调里再请求一次重建（撤销、切换卡片、删除后刷新）。嵌套执行会让
    /// <see cref="LoadCore"/> 把共享的 <see cref="linkLayer"/> 加到一个已经持有它的画布上，
    /// 抛 "already has a visual parent Canvas" —— 旧版删除节点崩溃的同一类根因。所以这里把
    /// 后续请求排队，当前这次重建结束后再顺序执行，画布永远只有一个连线层。
    /// </para>
    /// </summary>
    public void Load(WorkflowGraph graph, bool resetView = false)
    {
        if (loading)
        {
            // 排队期间任何一次请求要求复位视图，最终那次就复位（更宽的请求赢）。
            pendingLoad = (graph, resetView || (pendingLoad?.ResetView ?? false));
            return;
        }

        loading = true;
        try
        {
            // 处理器每次重建都再请求一次时不允许无限自旋：最多连做 4 轮，余下的请求丢弃，
            // 画布停在最近一次的图上——界面不会卡死，也不会留下半重建状态。
            for (var pass = 0; pass < 4; pass++)
            {
                pendingLoad = null;
                LoadCore(graph, resetView);
                if (pendingLoad is not { } queued) return;
                (graph, resetView) = queued;
            }
        }
        finally
        {
            loading = false;
        }
    }

    private void LoadCore(WorkflowGraph graph, bool resetView)
    {
        Graph = graph;
        graph.Invalidate();

        // 有弹窗锚在旧卡片上时由窗口负责先关闭；这里只管重建。
        graphRoot.Children.Clear();
        cards.Clear();
        selectedNodeIds.Clear();
        SelectedNodeId = null;
        SelectedLinkId = null;
        hoveredLinkId = null;
        linkGeometry.Clear();
        diagnosticsByNode.Clear();
        AttachLinkLayer();

        foreach (var node in graph.Nodes)
            AddCard(node);

        UpdateGraphExtent();

        if (resetView) ResetView();
        ApplyTransform();
        ApplySelectionVisuals();
        linkLayer.InvalidateVisual();
    }

    /// <summary>
    /// 把长期复用的连线层挂回图根：先确保它已从任何旧父级摘除。<c>Children.Clear()</c> 本身会
    /// 解除父子关系，但这一层是唯一被跨重建复用的实例——任何一条没清理干净的路径（嵌套重建、
    /// 上一次重建中途抛异常）都会让它带着旧父级进入下一次 <c>Add</c>。这里做一次幂等挂载，
    /// 把这类崩溃从"必现"降级为"不可能"。
    /// </summary>
    private void AttachLinkLayer()
    {
        if (linkLayer.Parent is Panel staleOwner) staleOwner.Children.Remove(linkLayer);
        graphRoot.Children.Add(linkLayer);
    }

    private void AddCard(WorkflowNode node)
    {
        var card = new WorkflowNodeCard(node, this, Owner);
        cards[node.Id] = card;
        SetLeft(card, node.X);
        SetTop(card, node.Y);
        graphRoot.Children.Add(card);
        NodeCardCreated?.Invoke(card);
        RestoreDiagnostics(node.Id, card);
    }

    /// <summary>重建单张卡片（参数 / 标题 / case 变了）。位置、选中态、运行态、错误徽标全部保留。</summary>
    public void RefreshNode(string nodeId)
    {
        if (Graph?.Find(nodeId) is not { } node) return;

        var index = -1;
        if (cards.TryGetValue(nodeId, out var old))
        {
            index = graphRoot.Children.IndexOf(old);
            graphRoot.Children.Remove(old);
        }

        linkGeometry.Clear();

        var card = new WorkflowNodeCard(node, this, Owner);
        cards[nodeId] = card;
        SetLeft(card, node.X);
        SetTop(card, node.Y);
        if (index >= 0) graphRoot.Children.Insert(index, card);
        else graphRoot.Children.Add(card);

        card.SetSelected(selectedNodeIds.Contains(nodeId));
        card.SetRunState(old?.CurrentRunState ?? NodeRunState.Idle);
        NodeCardCreated?.Invoke(card);
        RestoreDiagnostics(nodeId, card);
        UpdateGraphExtent();
        linkLayer.InvalidateVisual();
    }

    /// <summary>Re-read node positions after an external edit (auto-layout) without a full reload.</summary>
    public void SyncPositions()
    {
        if (Graph == null) return;
        foreach (var node in Graph.Nodes)
        {
            if (!cards.TryGetValue(node.Id, out var card)) continue;
            SetLeft(card, node.X);
            SetTop(card, node.Y);
        }
        linkGeometry.Clear();
        linkLayer.InvalidateVisual();
        UpdateGraphExtent();
    }

    // ---- 选择 ----------------------------------------------------------------------

    public void Select(string? nodeId)
    {
        SelectedLinkId = null;
        hoveredLinkId = null;
        selectedNodeIds.Clear();
        SelectedNodeId = nodeId != null && cards.ContainsKey(nodeId) ? nodeId : null;
        if (SelectedNodeId != null) selectedNodeIds.Add(SelectedNodeId);
        ApplySelectionVisuals();
        linkLayer.InvalidateVisual();
        SelectionChanged?.Invoke(SelectedNode);
    }

    /// <summary>选中一条连线（清空节点选择）。</summary>
    public void SelectLink(string? linkId)
    {
        selectedNodeIds.Clear();
        SelectedNodeId = null;
        SelectedLinkId = Graph?.Links.Any(l => l.Id == linkId) == true ? linkId : null;
        ApplySelectionVisuals();
        linkLayer.InvalidateVisual();
        SelectionChanged?.Invoke(null);
    }

    /// <summary>全选（start 节点不可选）。</summary>
    public void SelectAll()
    {
        if (Graph == null) return;
        SelectedLinkId = null;
        selectedNodeIds.Clear();
        foreach (var node in Graph.Nodes)
        {
            if (node.Type == WorkflowNodeTypes.Start) continue;
            selectedNodeIds.Add(node.Id);
        }

        SelectedNodeId = selectedNodeIds.FirstOrDefault();
        ApplySelectionVisuals();
        SelectionChanged?.Invoke(SelectedNode);
    }

    public void ClearSelection() => Select(null);

    private void ApplySelectionVisuals()
    {
        foreach (var (id, card) in cards)
            card.SetSelected(selectedNodeIds.Contains(id));
    }

    // ---- 校验诊断 ------------------------------------------------------------------

    /// <summary>把校验结果映射到节点徽标（红色数字 + tooltip 消息列表）。</summary>
    public void SetDiagnostics(IEnumerable<WorkflowDiagnostic> diagnostics)
    {
        diagnosticsByNode.Clear();
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.NodeId == null) continue;
            if (!diagnosticsByNode.TryGetValue(diagnostic.NodeId, out var list))
                diagnosticsByNode[diagnostic.NodeId] = list = [];
            list.Add(diagnostic);
        }

        foreach (var (nodeId, card) in cards)
            RestoreDiagnostics(nodeId, card);
    }

    private void RestoreDiagnostics(string nodeId, WorkflowNodeCard card)
    {
        if (!diagnosticsByNode.TryGetValue(nodeId, out var list) || list.Count == 0)
        {
            card.SetErrorBadge(0, null);
            return;
        }

        var tooltip = string.Join("\n", list.Select(d => $"[{d.Code}] {d.Text}"));
        card.SetErrorBadge(list.Count, tooltip);
    }

    // ---- 运行态 --------------------------------------------------------------------

    public void SetRunState(string? nodeId, NodeRunState state)
    {
        if (nodeId == null || Graph == null) return;
        if (!cards.TryGetValue(nodeId, out var card)) return;
        card.SetRunState(state);
    }

    public void ClearRunStates()
    {
        foreach (var card in cards.Values) card.SetRunState(NodeRunState.Idle);
    }

    // ---- 视图 ----------------------------------------------------------------------

    /// <summary>Graph-space point at the centre of the visible area (used to drop new nodes).</summary>
    public Point ViewCenter
    {
        get
        {
            var width = Bounds.Width > 0 ? Bounds.Width : 640;
            var height = Bounds.Height > 0 ? Bounds.Height : 480;
            return new Point((width / 2 - offset.X) / scale, (height / 2 - offset.Y) / scale);
        }
    }

    public double Scale => scale;
    public Point Offset => offset;
    public Size ViewportSize => new(Bounds.Width, Bounds.Height);

    public Point ScreenToGraph(Point screen) => new((screen.X - offset.X) / scale, (screen.Y - offset.Y) / scale);

    public Point GraphToScreen(Point graph) => new(graph.X * scale + offset.X, graph.Y * scale + offset.Y);

    /// <summary>全部内容的图空间包围盒（小地图用）。空图返回 null。</summary>
    public Rect? ContentBounds()
    {
        if (cards.Count == 0) return null;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (nodeId, card) in cards)
        {
            var width = card.Bounds.Width > 0 ? card.Bounds.Width : NodeMetrics.Width;
            var height = card.Bounds.Height > 0 ? card.Bounds.Height
                : (Graph?.Find(nodeId) is { } node ? NodeMetrics.ComputeHeight(node, Owner) : 120);
            minX = Math.Min(minX, GetLeft(card));
            minY = Math.Min(minY, GetTop(card));
            maxX = Math.Max(maxX, GetLeft(card) + width);
            maxY = Math.Max(maxY, GetTop(card) + height);
        }

        return minX > maxX ? null : new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>当前视口在图空间中的矩形。</summary>
    public Rect ViewportGraphRect()
    {
        var size = ViewportSize;
        return new Rect(ScreenToGraph(default), ScreenToGraph(new Point(size.Width, size.Height)));
    }

    public void ResetView()
    {
        scale = 1.0;
        offset = default;
        ApplyTransform();
        CenterOnContent();
    }

    /// <summary>Pan so one node is in the middle of the viewport (used by "jump to source").</summary>
    public void CenterOnNode(string nodeId)
    {
        if (!cards.TryGetValue(nodeId, out var card)) return;
        var width = Bounds.Width > 0 ? Bounds.Width : 640;
        var height = Bounds.Height > 0 ? Bounds.Height : 480;
        var cardHeight = card.Bounds.Height > 0 ? card.Bounds.Height : 120;

        offset = new Point(
            width / 2 - (GetLeft(card) + NodeMetrics.Width / 2) * scale,
            height / 2 - (GetTop(card) + cardHeight / 2) * scale);
        viewTouched = true;
        ApplyTransform();
    }

    /// <summary>把图空间中的一点移到视口中心（小地图拖动用）。</summary>
    public void CenterOnGraphPoint(Point graphPoint)
    {
        var width = Bounds.Width > 0 ? Bounds.Width : 640;
        var height = Bounds.Height > 0 ? Bounds.Height : 480;
        offset = new Point(width / 2 - graphPoint.X * scale, height / 2 - graphPoint.Y * scale);
        viewTouched = true;
        ApplyTransform();
    }

    /// <summary>Pan so the whole graph is visible (and centred) without changing zoom.</summary>
    public void CenterOnContent()
    {
        var content = ContentBounds();
        if (content == null || Bounds.Width <= 0) return;

        offset = new Point(
            (Bounds.Width - content.Value.Width * scale) / 2 - content.Value.X * scale,
            (Bounds.Height - content.Value.Height * scale) / 2 - content.Value.Y * scale);
        ApplyTransform();
    }

    /// <summary>
    /// Scale + centre so the whole graph fits. Imported and migrated graphs can be far wider
    /// than the window; opening them at 100% would hide half the workflow off-screen.
    /// </summary>
    public void ZoomToFit() => FitTo(Bounds.Width > 0 ? Bounds.Size : lastFitSize);

    /// <summary>Fit again on the next layout pass (the window may not have its final size yet).</summary>
    public void RequestFit()
    {
        pendingFit = true;
        viewTouched = false;
    }

    private void FitTo(Size viewport)
    {
        var content = ContentBounds();
        if (content == null) return;
        if (!double.IsFinite(viewport.Width) || viewport.Width <= 16 || viewport.Height <= 16) return;

        const double padding = 40;
        var contentWidth = Math.Max(1, content.Value.Width);
        var contentHeight = Math.Max(1, content.Value.Height);
        var fit = Math.Min(
            (viewport.Width - padding * 2) / contentWidth,
            (viewport.Height - padding * 2) / contentHeight);

        scale = Math.Clamp(Math.Min(fit, 1.0), MinScale, 1.0);
        offset = new Point(
            (viewport.Width - contentWidth * scale) / 2 - content.Value.X * scale,
            (viewport.Height - contentHeight * scale) / 2 - content.Value.Y * scale);

        lastFitSize = viewport;
        pendingFit = false;
        ApplyTransform();
    }

    public void ZoomBy(double factor)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        ApplyZoom(factor, center);
    }

    /// <summary>Refresh link geometry after a card changed size (e.g. a chip got edited).</summary>
    public void InvalidateLinks()
    {
        linkGeometry.Clear();
        UpdateGraphExtent();
        linkLayer.InvalidateVisual();
    }

    /// <summary>
    /// Grow the drawing surface to cover every card. The link layer and the graph root are
    /// unclipped, but a zero-sized visual can be culled, so both get the real extent.
    /// </summary>
    private void UpdateGraphExtent()
    {
        double maxX = 1200, maxY = 800;
        foreach (var (nodeId, card) in cards)
        {
            var width = card.Bounds.Width > 0 ? card.Bounds.Width : NodeMetrics.Width;
            var height = card.Bounds.Height > 0 ? card.Bounds.Height
                : (Graph?.Find(nodeId) is { } node ? NodeMetrics.ComputeHeight(node, Owner) : 140);
            maxX = Math.Max(maxX, GetLeft(card) + width);
            maxY = Math.Max(maxY, GetTop(card) + height);
        }

        graphRoot.Width = maxX + 400;
        graphRoot.Height = maxY + 400;
        linkLayer.Width = graphRoot.Width;
        linkLayer.Height = graphRoot.Height;
        SetLeft(linkLayer, 0);
        SetTop(linkLayer, 0);
        ViewChanged?.Invoke();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);

        // The window's first real size arrives after Load(); fitting there (and again on resize)
        // keeps the graph centred instead of anchored to a stale viewport.
        if (!viewTouched && (pendingFit || finalSize != lastFitSize))
        {
            FitTo(finalSize);
        }

        return size;
    }

    // ---- 变换 ----------------------------------------------------------------------

    private void ApplyTransform()
    {
        scaleTransform.ScaleX = scale;
        scaleTransform.ScaleY = scale;
        translateTransform.X = offset.X;
        translateTransform.Y = offset.Y;
        InvalidateVisual();
        gridLayer.InvalidateVisual();
        ViewChanged?.Invoke();
    }

    private void ApplyZoom(double factor, Point parentPoint)
    {
        var graphPoint = new Point(
            (parentPoint.X - offset.X) / scale,
            (parentPoint.Y - offset.Y) / scale);

        var next = Math.Clamp(scale * factor, MinScale, MaxScale);
        if (Math.Abs(next - scale) < 0.0001) return;
        scale = next;
        viewTouched = true;

        offset = new Point(
            parentPoint.X - graphPoint.X * scale,
            parentPoint.Y - graphPoint.Y * scale);

        ApplyTransform();
    }

    private Visual? TransformParent => this.GetVisualParent();

    private Point ParentPoint(PointerEventArgs e) =>
        TransformParent is Visual parent ? e.GetPosition(parent) : e.GetPosition(this);

    // ---- 输入：按键 ----------------------------------------------------------------

    private void OnCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && !e.Handled)
        {
            spaceHeld = true;
            e.Handled = true;
        }
    }

    private void OnCanvasKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) spaceHeld = false;
    }

    // ---- 输入：指针 ----------------------------------------------------------------

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // This handler is registered for both tunnel and bubble with handledEventsToo, so every
        // press reaches it twice; act only on the first (tunnel) invocation.
        if (e.Handled) return;

        var properties = e.GetCurrentPoint(this).Properties;

        // A press on a parameter row belongs to the row, not to the drag gesture: left opens the
        // value editor, right opens the binding menu.
        if (FindParamRow(e.Source as Visual) is { } row)
        {
            if (properties.IsLeftButtonPressed)
            {
                RequestParamEdit(row.Node, row.Spec, row.Anchor);
                e.Handled = true;
                return;
            }

            if (properties.IsRightButtonPressed)
            {
                RequestParamMenu(row.Node, row.Spec, row.Anchor);
                e.Handled = true;
                return;
            }
        }

        // 中键：任意位置平移（节点上也是平移，不选中）。
        if (properties.IsMiddleButtonPressed)
        {
            StartPan(e);
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed) return; // 右键交给 ContextRequested

        Focus();

        var graphPoint = e.GetPosition(graphRoot);

        // 端口命中：从输出口或输入口都可以发起连线（反向拖动在释放时归一化）。
        if (HitTestPort(graphPoint) is { } port)
        {
            linkSource = port;
            linkCursor = graphPoint;
            e.Pointer.Capture(this);
            e.Handled = true;
            linkLayer.InvalidateVisual();
            return;
        }

        // 节点命中：选择 + 开始拖拽（双击 = 重命名）。
        if (ResolveNode(e.Source as Visual) is { } node)
        {
            if (e.ClickCount >= 2)
            {
                NodeActivated?.Invoke(node);
                e.Handled = true;
                return;
            }

            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (ctrl)
            {
                if (selectedNodeIds.Contains(node.Id) && selectedNodeIds.Count > 1)
                {
                    selectedNodeIds.Remove(node.Id);
                    SelectedNodeId = selectedNodeIds.FirstOrDefault();
                    ApplySelectionVisuals();
                    SelectionChanged?.Invoke(SelectedNode);
                    e.Handled = true;
                    return;
                }

                selectedNodeIds.Add(node.Id);
                SelectedNodeId = node.Id;
            }
            else if (!selectedNodeIds.Contains(node.Id))
            {
                selectedNodeIds.Clear();
                selectedNodeIds.Add(node.Id);
                SelectedNodeId = node.Id;
            }

            SelectedLinkId = null;
            ApplySelectionVisuals();
            SelectionChanged?.Invoke(SelectedNode);

            dragNode = node;
            dragStartPositions = selectedNodeIds.ToDictionary(
                id => id,
                id => Graph?.Find(id) is { } n ? new Point(n.X, n.Y) : default,
                StringComparer.Ordinal);
            dragStartGraph = graphPoint;
            dragMoved = false;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // 空白处：空格按住 = 平移，否则 = 框选。
        var ctrlEmpty = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (spaceHeld)
        {
            if (!ctrlEmpty) Select(null);
            StartPan(e);
            e.Handled = true;
            return;
        }

        if (!ctrlEmpty) Select(null);
        banding = true;
        bandStart = ParentPoint(e);
        UpdateBand(bandStart);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void StartPan(PointerEventArgs e)
    {
        panning = true;
        panStart = ParentPoint(e);
        e.Pointer.Capture(this);
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (linkSource != null)
        {
            linkCursor = e.GetPosition(graphRoot);
            linkLayer.InvalidateVisual();
            return;
        }

        if (dragNode != null)
        {
            var current = e.GetPosition(graphRoot);
            var dx = current.X - dragStartGraph.X;
            var dy = current.Y - dragStartGraph.Y;
            if (!dragMoved && Math.Abs(dx) + Math.Abs(dy) < 3) return;

            dragMoved = true;
            foreach (var (nodeId, startPosition) in dragStartPositions)
            {
                if (Graph?.Find(nodeId) is not { } node) continue;
                node.X = startPosition.X + dx;
                node.Y = startPosition.Y + dy;

                if (cards.TryGetValue(nodeId, out var card))
                {
                    SetLeft(card, node.X);
                    SetTop(card, node.Y);
                }
            }

            UpdateGraphExtent();
            linkLayer.InvalidateVisual();
            return;
        }

        if (panning)
        {
            var current = ParentPoint(e);
            viewTouched = true;
            offset = new Point(offset.X + (current.X - panStart.X), offset.Y + (current.Y - panStart.Y));
            panStart = current;
            ApplyTransform();
            return;
        }

        if (banding)
        {
            UpdateBand(ParentPoint(e));
            return;
        }

        // 无手势悬停：连线高亮 + 光标反馈。
        if (IsPointerOver && Graph != null && !IsGestureActive)
        {
            var hover = HitTestLink(e.GetPosition(graphRoot));
            if (!string.Equals(hover, hoveredLinkId, StringComparison.Ordinal))
            {
                hoveredLinkId = hover;
                linkLayer.InvalidateVisual();
                Cursor = hover != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            }
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (linkSource is { } source)
        {
            var position = e.GetPosition(graphRoot);
            var target = HitTestPort(position) ??
                         (source.IsOutput ? HitTestNodePort(position, source) : null);
            linkSource = null;
            e.Pointer.Capture(null);

            if (target != null)
            {
                // 反向拖拽（从输入口拖到输出口）归一化为 output→input。
                if (!source.IsOutput && target.IsOutput) (source, target) = (target, source);

                if (TryConnect(source, target))
                {
                    GraphEdited?.Invoke();
                }
                else if (LastRejection != null)
                {
                    LinkRejected?.Invoke(LastRejection);
                }
            }

            linkLayer.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (dragNode != null)
        {
            var moved = dragMoved;
            dragNode = null;
            dragMoved = false;
            dragStartPositions.Clear();
            e.Pointer.Capture(null);
            if (moved) GraphEdited?.Invoke();
            return;
        }

        if (panning)
        {
            panning = false;
            e.Pointer.Capture(null);
            return;
        }

        if (banding)
        {
            banding = false;
            HideBand();
            e.Pointer.Capture(null);

            var end = ParentPoint(e);
            var rect = BandGraphRect(bandStart, end);
            if (rect.Width > 4 || rect.Height > 4)
                SelectNodesInRect(rect, e.KeyModifiers.HasFlag(KeyModifiers.Control));
        }
    }

    private void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        // Wheel zooms (node-editor convention); the canvas has no scrollbars to fight with.
        ApplyZoom(e.Delta.Y > 0 ? 1.1 : 1 / 1.1, ParentPoint(e));
        e.Handled = true;
    }

    // ---- 右键菜单 ------------------------------------------------------------------

    private void OnCanvasContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!e.TryGetPosition(graphRoot, out var graphPoint)) return;

        // 连线上右键：连线菜单。
        var link = HitTestLink(graphPoint);
        if (link != null)
        {
            SelectLink(link);
            var flyout = new MenuFlyout { Placement = PlacementMode.Pointer };
            var delete = new MenuItem { Header = Loc.Get("Shortcuts_Canvas_DeleteLink") };
            delete.Click += (_, _) =>
            {
                var deleted = DeleteSelected();
                if (deleted > 0) GraphEdited?.Invoke();
            };
            flyout.Items.Add(delete);
            flyout.ShowAt(this);
            e.Handled = true;
            return;
        }

        // 空白处右键：画布菜单。
        var menu = new MenuFlyout { Placement = PlacementMode.Pointer };

        var autoLayout = new MenuItem { Header = Loc.Get("Shortcuts_Canvas_AutoLayout") };
        autoLayout.Click += (_, _) => AutoLayout();
        menu.Items.Add(autoLayout);

        var selectAll = new MenuItem { Header = Loc.Get("Shortcuts_Canvas_SelectAll") };
        selectAll.Click += (_, _) => SelectAll();
        menu.Items.Add(selectAll);

        var fit = new MenuItem { Header = Loc.Get("Shortcuts_Canvas_Fit") };
        fit.Click += (_, _) => ZoomToFit();
        menu.Items.Add(fit);

        menu.ShowAt(this);
        e.Handled = true;
    }

    // ---- 命中测试 ------------------------------------------------------------------

    /// <summary>Graph-space port hit test across every card's cached anchors.</summary>
    private PortRef? HitTestPort(Point point)
    {
        PortRef? best = null;
        var bestDistance = PortHitRadius;

        foreach (var card in cards.Values)
        {
            foreach (var anchor in card.Anchors)
            {
                var position = new Point(anchor.Center.X + GetLeft(card), anchor.Center.Y + GetTop(card));
                var distance = Math.Abs(position.X - point.X) + Math.Abs(position.Y - point.Y);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = anchor.Port;
            }
        }

        return best;
    }

    /// <summary>卡片缓存锚点的绝对（图空间）位置。</summary>
    private bool TryAnchorOf(string nodeId, string portId, bool output, out Point anchor)
    {
        anchor = default;
        if (!cards.TryGetValue(nodeId, out var card)) return false;
        anchor = new Point(card.Anchor(portId, output, Owner).X + GetLeft(card),
            card.Anchor(portId, output, Owner).Y + GetTop(card));
        return true;
    }

    /// <summary>Dropping a link on a node body picks its natural port (exec-in, or the nearest parameter).</summary>
    private PortRef? HitTestNodePort(Point point, PortRef source)
    {
        foreach (var card in cards.Values)
        {
            var bounds = new Rect(GetLeft(card), GetTop(card), card.Bounds.Width, card.Bounds.Height);
            if (!bounds.Contains(point)) continue;
            if (card.Node.Id == source.NodeId) return null;

            var from = Graph?.Find(source.NodeId);
            var isData = from != null && WorkflowNodeTypes.Outputs(from, Owner)
                .Any(p => p.Id == source.PortId && p.IsData);

            if (isData)
            {
                // Pick the parameter row under the cursor instead of always the first one.
                var specs = WorkflowNodeTypes.VisibleParams(card.Node);
                if (specs.Count == 0) return null;

                var localY = point.Y - GetTop(card) - NodeMetrics.HeaderHeight;
                var index = (int)Math.Floor(localY / NodeMetrics.RowHeight);
                index = Math.Clamp(index, 0, specs.Count - 1);
                return new PortRef(card.Node.Id, specs[index].Key, false);
            }

            return WorkflowNodeTypes.HasInput(card.Node.Type)
                ? new PortRef(card.Node.Id, WorkflowNodeTypes.In, false)
                : null;
        }

        return null;
    }

    /// <summary>Display label of one output port (used by the card's binding rows).</summary>
    internal string PortLabelOf(string nodeId, string portId)
    {
        var node = Graph?.Find(nodeId);
        if (node == null) return portId;

        var port = WorkflowNodeTypes.Outputs(node, Owner).FirstOrDefault(p => p.Id == portId);
        if (port == null) return portId;

        return port.Display ?? Loc.Get(port.LabelKey);
    }

    private WorkflowNode? ResolveNode(Visual? source)
    {
        for (var node = source; node != null; node = node.GetVisualParent())
        {
            if (node is WorkflowNodeCard card) return card.Node;
            if (ReferenceEquals(node, this)) break;
        }
        return null;
    }

    /// <summary>
    /// The parameter row a press landed on, or null. The walk stops at the node card (and at the
    /// canvas itself) so a press on the header, ports or empty surface never picks up a tag from
    /// an unrelated control above the canvas.
    /// </summary>
    private ParamRowRef? FindParamRow(Visual? source)
    {
        for (var node = source; node != null && node is not WorkflowNodeCard; node = node.GetVisualParent())
        {
            if (ReferenceEquals(node, this)) return null;
            if (node is Control { Tag: ParamRowRef row }) return row;
        }
        return null;
    }

    /// <summary>连线命中：把贝塞尔曲线采样成折线后做点到线段距离测试（图空间，阈值随缩放换算）。</summary>
    private string? HitTestLink(Point graphPoint)
    {
        if (Graph == null) return null;

        var threshold = Math.Clamp(LinkHitRadius / scale, 3, 24);
        string? best = null;
        var bestDistance = threshold;

        foreach (var link in Graph.Links)
        {
            if (!TryAnchorOf(link.From, link.FromPort, true, out var start)) continue;
            if (!TryAnchorOf(link.To, link.ToPort, false, out var end)) continue;

            var distance = DistanceToCurve(graphPoint, start, end);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = link.Id;
            }
        }

        return best;
    }

    private static double DistanceToCurve(Point point, Point start, Point end)
    {
        const int samples = 24;
        Point previous = start;
        var best = double.MaxValue;

        for (var i = 1; i <= samples; i++)
        {
            var t = (double)i / samples;
            var p = BezierPoint(start, end, t);
            best = Math.Min(best, DistanceToSegment(point, previous, p));
            previous = p;
        }

        return best;
    }

    /// <summary>与 BuildCurve 同参数的三次贝塞尔取点。</summary>
    private static Point BezierPoint(Point start, Point end, double t)
    {
        var dx = Math.Max(40, Math.Abs(end.X - start.X) * 0.5);
        var c1 = new Point(start.X + dx, start.Y);
        var c2 = new Point(end.X - dx, end.Y);

        var mt = 1 - t;
        var a = mt * mt * mt;
        var b = 3 * mt * mt * t;
        var c = 3 * mt * t * t;
        var d = t * t * t;

        return new Point(
            a * start.X + b * c1.X + c * c2.X + d * end.X,
            a * start.Y + b * c1.Y + c * c2.Y + d * end.Y);
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var vx = b.X - a.X;
        var vy = b.Y - a.Y;
        var wx = p.X - a.X;
        var wy = p.Y - a.Y;

        var lengthSquared = vx * vx + vy * vy;
        if (lengthSquared <= 0) return Math.Sqrt(wx * wx + wy * wy);

        var t = Math.Clamp((wx * vx + wy * vy) / lengthSquared, 0, 1);
        var dx = p.X - (a.X + t * vx);
        var dy = p.Y - (a.Y + t * vy);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ---- 连线编辑 ------------------------------------------------------------------

    /// <summary>
    /// Validate and create one link. Exec links replace whatever the source port pointed at
    /// (one continuation per port); data links replace the previous link into that parameter.
    /// </summary>
    private bool TryConnect(PortRef source, PortRef target)
    {
        if (Graph == null) return false;
        if (source.IsOutput == target.IsOutput) return false;
        if (source.NodeId == target.NodeId) return false;

        var from = Graph.Find(source.NodeId);
        var to = Graph.Find(target.NodeId);
        if (from == null || to == null) return false;

        var isData = !WorkflowNodeTypes.Outputs(from, Owner)
            .Any(p => p.Id == source.PortId && !p.IsData);

        // One validator decides legality, so a link the canvas accepts is a link the importer
        // and the runner also accept.
        if (WorkflowValidator.CheckLink(Graph, Owner, from, source.PortId, to, target.PortId, isData) is { } problem)
        {
            LastRejection = problem;
            return false;
        }

        if (isData)
        {
            Graph.Links.RemoveAll(l => l.To == to.Id && l.Kind == LinkKinds.Data &&
                                       string.Equals(l.ToPort, target.PortId, StringComparison.Ordinal));
            Graph.Links.Add(WorkflowLink.Data(from.Id, source.PortId, to.Id, target.PortId));
        }
        else
        {
            Graph.Links.RemoveAll(l => l.From == from.Id &&
                                       string.Equals(l.FromPort, source.PortId, StringComparison.Ordinal));
            Graph.Links.Add(WorkflowLink.Exec(from.Id, source.PortId, to.Id));
        }

        LastRejection = null;
        linkGeometry.Clear();
        Graph.Invalidate();
        linkLayer.InvalidateVisual();
        return true;
    }

    /// <summary>Why the last drop was refused (also raised via <see cref="LinkRejected"/>).</summary>
    public WorkflowDiagnostic? LastRejection { get; private set; }

    /// <summary>
    /// 删除当前选中内容：连线优先，其次全部选中的非 start 节点（连带其所有连线）。
    /// 返回删除的数量。**不触发事件** —— 由窗口调用方决定何时提交。
    /// </summary>
    public int DeleteSelected()
    {
        if (Graph == null) return 0;

        if (SelectedLinkId is { } linkId)
        {
            var link = Graph.Links.FirstOrDefault(l => l.Id == linkId);
            if (link == null)
            {
                SelectedLinkId = null;
                return 0;
            }

            Graph.Links.Remove(link);
            Graph.Invalidate();
            SelectedLinkId = null;
            linkGeometry.Clear();
            linkLayer.InvalidateVisual();
            return 1;
        }

        if (selectedNodeIds.Count == 0) return 0;

        var targets = selectedNodeIds
            .Select(id => Graph.Find(id))
            .Where(n => n != null && n.Type != WorkflowNodeTypes.Start)
            .Cast<WorkflowNode>()
            .ToList();

        if (targets.Count == 0) return 0;

        var removed = new HashSet<string>(targets.Select(n => n.Id), StringComparer.Ordinal);
        foreach (var node in targets)
        {
            Graph.Nodes.Remove(node);
            if (cards.TryGetValue(node.Id, out var card))
            {
                graphRoot.Children.Remove(card);
                cards.Remove(node.Id);
            }

            selectedNodeIds.Remove(node.Id);
            diagnosticsByNode.Remove(node.Id);
        }

        Graph.Links.RemoveAll(l => removed.Contains(l.From) || removed.Contains(l.To));
        Graph.Invalidate();
        linkGeometry.Clear();

        SelectedNodeId = selectedNodeIds.FirstOrDefault();
        ApplySelectionVisuals();
        UpdateGraphExtent();
        linkLayer.InvalidateVisual();
        return targets.Count;
    }

    /// <summary>Duplicate every selected node slightly offset, without copying its links. Returns the copy count.</summary>
    public int DuplicateSelected()
    {
        if (Graph == null || selectedNodeIds.Count == 0) return 0;

        var copies = new List<WorkflowNode>();
        foreach (var id in selectedNodeIds)
        {
            if (Graph.Find(id) is not { } node || node.Type == WorkflowNodeTypes.Start) continue;

            var copy = new WorkflowNode
            {
                Type = node.Type,
                X = node.X + 28,
                Y = node.Y + 28,
                Params = node.Params.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal),
                Title = node.Title,
                Cases = node.Cases?.Select(c => new WorkflowCase { Id = c.Id, Value = c.Value }).ToList(),
                OnError = node.OnError,
            };
            Graph.Nodes.Add(copy);
            copies.Add(copy);
        }

        if (copies.Count == 0) return 0;

        Graph.Invalidate();
        foreach (var copy in copies) AddCard(copy);
        UpdateGraphExtent();
        linkLayer.InvalidateVisual();

        selectedNodeIds.Clear();
        foreach (var copy in copies) selectedNodeIds.Add(copy.Id);
        SelectedNodeId = copies[^1].Id;
        ApplySelectionVisuals();
        SelectionChanged?.Invoke(SelectedNode);
        return copies.Count;
    }

    /// <summary>方向键微移选中节点（shift = 10 步长）。</summary>
    public void Nudge(double dx, double dy)
    {
        if (Graph == null || selectedNodeIds.Count == 0) return;

        foreach (var id in selectedNodeIds)
        {
            if (Graph.Find(id) is not { } node) continue;
            node.X += dx;
            node.Y += dy;
            if (cards.TryGetValue(id, out var card))
            {
                SetLeft(card, node.X);
                SetTop(card, node.Y);
            }
        }

        linkGeometry.Clear();
        UpdateGraphExtent();
        linkLayer.InvalidateVisual();
        GraphEdited?.Invoke();
    }

    /// <summary>Re-run the layered layout (imported graphs arrive with rough coordinates).</summary>
    public void AutoLayout()
    {
        if (Graph == null) return;
        WorkflowMigration.AutoLayout(Graph);
        SyncPositions();
        CenterOnContent();
    }

    /// <summary>Add a node at the view centre, auto-connecting from the selected node if it fits.</summary>
    public WorkflowNode? AddNode(ActionDefinition definition)
    {
        if (Graph == null) return null;

        var center = ViewCenter;
        var node = WorkflowNode.Create(definition, center.X - NodeMetrics.Width / 2, center.Y - 60);
        Graph.Nodes.Add(node);

        if (SelectedNode is { } selected && WorkflowNodeTypes.HasInput(node.Type) &&
            Graph.FirstOutgoing(selected.Id, WorkflowNodeTypes.Out) == null &&
            WorkflowNodeTypes.Outputs(selected, Owner).Any(p => p.Id == WorkflowNodeTypes.Out && !p.IsData))
        {
            Graph.Links.Add(WorkflowLink.Exec(selected.Id, WorkflowNodeTypes.Out, node.Id));
        }

        Graph.Invalidate();
        AddCard(node);
        UpdateGraphExtent();
        linkLayer.InvalidateVisual();
        Select(node.Id);
        return node;
    }

    // ---- 框选 ----------------------------------------------------------------------

    private static Border CreateRubberBand()
    {
        var band = new Border
        {
            IsHitTestVisible = false,
            IsVisible = false,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#88534AB7")),
            Background = new SolidColorBrush(Color.Parse("#14534AB7")),
            ZIndex = 20,
        };
        return band;
    }

    private void UpdateBand(Point parentPoint)
    {
        var rect = NormalizeRect(bandStart, parentPoint);
        Canvas.SetLeft(rubberBand, rect.X);
        Canvas.SetTop(rubberBand, rect.Y);
        rubberBand.Width = rect.Width;
        rubberBand.Height = rect.Height;
        rubberBand.IsVisible = true;
    }

    private void HideBand()
    {
        rubberBand.IsVisible = false;
        rubberBand.Width = 0;
        rubberBand.Height = 0;
    }

    private static Rect NormalizeRect(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private Rect BandGraphRect(Point parentA, Point parentB)
    {
        var a = ScreenToGraph(new Point(parentA.X, parentA.Y));
        var b = ScreenToGraph(new Point(parentB.X, parentB.Y));
        return NormalizeRect(a, b);
    }

    private void SelectNodesInRect(Rect rect, bool additive)
    {
        if (Graph == null) return;
        if (!additive) selectedNodeIds.Clear();
        SelectedLinkId = null;

        foreach (var node in Graph.Nodes)
        {
            if (node.Type == WorkflowNodeTypes.Start) continue;
            if (!cards.TryGetValue(node.Id, out var card)) continue;

            var width = card.Bounds.Width > 0 ? card.Bounds.Width : NodeMetrics.Width;
            var height = card.Bounds.Height > 0 ? card.Bounds.Height : NodeMetrics.ComputeHeight(node, Owner);
            var bounds = new Rect(node.X, node.Y, width, height);
            if (bounds.Intersects(rect)) selectedNodeIds.Add(node.Id);
        }

        SelectedNodeId = selectedNodeIds.FirstOrDefault();
        ApplySelectionVisuals();
        SelectionChanged?.Invoke(SelectedNode);
    }

    // ---- 渲染 ----------------------------------------------------------------------

    private sealed class LinkLayer : Control
    {
        public Action<DrawingContext>? Draw { get; set; }

        public override void Render(DrawingContext context) => Draw?.Invoke(context);
    }

    private sealed class GridLayer : Control
    {
        public Action<DrawingContext>? Draw { get; set; }

        public override void Render(DrawingContext context) => Draw?.Invoke(context);
    }

    private void DrawScene(DrawingContext context)
    {
        // 连线层与卡片共享图空间坐标。
        DrawLinks(context);
        DrawPendingLink(context);
    }

    /// <summary>
    /// 屏幕空间网格：横竖线网（比逐点绘制便宜两个数量级，且与图坐标严格对齐 ——
    /// 修复旧版在变换空间里画网格导致的「间距被平方缩放 + 平移视差」bug）。
    /// </summary>
    private void DrawGrid(DrawingContext context)
    {
        var spacing = 32.0;
        if (scale < 0.55) spacing = 64;
        if (scale < 0.28) spacing = 128;

        var step = spacing * scale;
        if (step < 8) return;

        var brush = TryBrush("CanvasGridDotBrush") ??
                    (ActualThemeVariant == ThemeVariant.Dark
                        ? Visuals.Tint("#FFFFFF", 0x12)
                        : Visuals.Tint("#000000", 0x10));
        var pen = NodeVisuals.Pen(brush, 1.0);

        var width = Bounds.Width;
        var height = Bounds.Height;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            // 与图空间对齐：屏幕上第 k 条线 = offset + k * step，k 为整数。
            var firstX = Math.Floor(-offset.X / step);
            for (var k = firstX; offset.X + k * step <= width; k++)
            {
                var x = offset.X + k * step;
                ctx.BeginFigure(new Point(x, 0), false);
                ctx.LineTo(new Point(x, height));
                ctx.EndFigure(false);
            }

            var firstY = Math.Floor(-offset.Y / step);
            for (var k = firstY; offset.Y + k * step <= height; k++)
            {
                var y = offset.Y + k * step;
                ctx.BeginFigure(new Point(0, y), false);
                ctx.LineTo(new Point(width, y));
                ctx.EndFigure(false);
            }
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawLinks(DrawingContext context)
    {
        if (Graph == null) return;

        var accent = TryBrush("SystemControlHighlightAccentBrush") ?? Visuals.Brush("#534AB7");

        foreach (var link in Graph.Links)
        {
            if (!cards.TryGetValue(link.From, out var fromCard) || !cards.TryGetValue(link.To, out var toCard))
                continue;

            var start = new Point(fromCard.Anchor(link.FromPort, true, Owner).X + GetLeft(fromCard),
                fromCard.Anchor(link.FromPort, true, Owner).Y + GetTop(fromCard));
            var end = new Point(toCard.Anchor(link.ToPort, false, Owner).X + GetLeft(toCard),
                toCard.Anchor(link.ToPort, false, Owner).Y + GetTop(toCard));

            var isHover = string.Equals(link.Id, hoveredLinkId, StringComparison.Ordinal);
            var isSelected = string.Equals(link.Id, SelectedLinkId, StringComparison.Ordinal);

            if (isSelected)
            {
                // 选中：先画一圈半透明光晕，再画主题强调色实线。
                context.DrawGeometry(null, NodeVisuals.Pen(accent, 8 / scale), CurveFor(link, start, end));
            }
            else if (isHover)
            {
                context.DrawGeometry(null, NodeVisuals.Pen(accent, 5 / scale), CurveFor(link, start, end));
            }

            var brush = isSelected || isHover ? accent : LinkBrushFor(link, fromCard);
            var width = isSelected || isHover ? 2.8 : link.IsData ? 1.7 : 2.1;
            var pen = NodeVisuals.Pen(brush, width / scale, dashed: link.IsData);
            context.DrawGeometry(null, pen, CurveFor(link, start, end));

            // 执行方向箭头（尺寸随缩放保持屏幕恒定）。
            if (!link.IsData)
                DrawArrow(context, start, end, pen.Brush!, isSelected || isHover);
        }
    }

    /// <summary>连线颜色：数据线按源端口类型，执行线按源节点类别。</summary>
    private IBrush LinkBrushFor(WorkflowLink link, WorkflowNodeCard fromCard)
    {
        if (link.IsData)
        {
            var anchor = fromCard.Anchors.FirstOrDefault(a => a.Port.PortId == link.FromPort && a.Port.IsOutput);
            return NodeVisuals.DataLinkBrush(anchor?.DataType ?? DataType.Any);
        }

        return NodeVisuals.ExecLinkBrush(fromCard.Node);
    }

    private void DrawPendingLink(DrawingContext context)
    {
        if (linkSource is not { } source || Graph == null) return;
        if (!cards.TryGetValue(source.NodeId, out var card)) return;

        var anchor = card.Anchor(source.PortId, source.IsOutput, Owner);
        var start = new Point(anchor.X + GetLeft(card), anchor.Y + GetTop(card));

        var anchorInfo = card.Anchors.FirstOrDefault(a => a.Port.PortId == source.PortId && a.Port.IsOutput == source.IsOutput);
        var brush = source.IsOutput
            ? (anchorInfo is { IsData: true }
                ? NodeVisuals.DataLinkBrush(anchorInfo.DataType)
                : NodeVisuals.ExecLinkBrush(card.Node))
            : NodeVisuals.DataLinkBrush(anchorInfo?.DataType ?? DataType.Any);

        var geometry = source.IsOutput ? BuildCurve(start, linkCursor) : BuildCurve(linkCursor, start);
        context.DrawGeometry(null, NodeVisuals.Pen(brush, 2.0 / scale, dashed: true), geometry);
    }

    /// <summary>按端点缓存曲线几何：拖拽中只有端点变化的线会重建。</summary>
    private StreamGeometry CurveFor(WorkflowLink link, Point start, Point end)
    {
        if (linkGeometry.TryGetValue(link.Id, out var cached) &&
            cached.Start == start && cached.End == end)
        {
            return cached.Geometry;
        }

        var geometry = BuildCurve(start, end);
        if (linkGeometry.Count > 1024) linkGeometry.Clear();
        linkGeometry[link.Id] = (start, end, geometry);
        return geometry;
    }

    private static StreamGeometry BuildCurve(Point start, Point end)
    {
        // Horizontal S-curve: the same shape node editors use, so "left to right" reads as
        // "flows this way" even when the target sits above the source.
        var dx = Math.Max(40, Math.Abs(end.X - start.X) * 0.5);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false);
            ctx.CubicBezierTo(
                new Point(start.X + dx, start.Y),
                new Point(end.X - dx, end.Y),
                end);
            ctx.EndFigure(false);
        }
        return geometry;
    }

    private void DrawArrow(DrawingContext context, Point start, Point end, IBrush brush, bool emphasized)
    {
        if (Math.Abs(end.X - start.X) < 24) return;

        var size = 1 / scale;
        var tip = new Point(end.X - 2 * size, end.Y);
        var length = (emphasized ? 8 : 7) * size;
        var half = (emphasized ? 4.5 : 4) * size;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(tip, true);
            ctx.LineTo(new Point(tip.X - length, tip.Y - half));
            ctx.LineTo(new Point(tip.X - length, tip.Y + half));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }

    private IBrush? TryBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;
}
