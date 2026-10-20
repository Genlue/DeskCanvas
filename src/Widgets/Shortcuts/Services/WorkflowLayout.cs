using Shortcuts.Models;

namespace Shortcuts.Services;

/// <summary>
/// 工作流图的分层自动排版引擎（Sugiyama 简化版，Sugiyama-lite）。
/// <para>
/// 目标：对任意（可能带环、带孤立节点、带注释）的图给出一个<b>确定性</b>（相同输入 → 相同输出）、
/// <b>无重叠</b>、<b>依赖方向正确</b>（被依赖者在左、依赖者在右）的布局。算法分五步：
/// </para>
/// <list type="number">
/// <item>构图：exec 连线作为强依赖（权重 1），data 连线作为次级依赖（权重 0.4）；
///       忽略指向 start 的边与 note 节点参与的边。</item>
/// <item>分层（列号）：Kahn 拓扑排序（遇环时把环上的"回边"剔除），然后按最长路径法给每个节点
///       分配层号 layer = max(所有前驱 layer + 1, 0)。循环体的回边被丢弃后，
///       forLoop → body → … → 回到 loop 不再阻塞分层，循环体子图天然落在循环节点右侧的后续列。</item>
/// <item>行分配：按拓扑序处理节点，子树连续打包——父节点行下方连续空闲行留给它的直接后继，
///       同层冲突时向后扫描空闲行；随后做 1~2 轮"重心微调"，把节点向其邻居的平均行靠拢，
///       让分支父节点尽量垂直居中于子块。</item>
/// <item>坐标：X = layer × 列间距，Y = row × 行间距。同一层内的行号互不相同，
///       因此同层两节点的垂直间距 ≥ 行间距，保证不重叠。</item>
/// <item>收尾：note 节点排到主图下方一条独立行；无任何 exec/data 边的孤立节点排到
///       最大列右侧一列，按行游标自上而下。</item>
/// </list>
/// <para>
/// 魔数说明见各常量注释。整份布局只读连线拓扑与节点类型，不修改任何连线；
/// 结束时调用 <see cref="WorkflowGraph.Invalidate"/> 刷新节点索引。
/// </para>
/// </summary>
public static class WorkflowLayout
{
    /// <summary>列间距（DIP）。卡片宽度约 260~300，340 留出连线与呼吸空间。</summary>
    private const double ColumnStep = 340d;

    /// <summary>行间距（DIP）。规范要求同层两节点 Y 间距 ≥ 170；行号以 1 为步长，天然满足。</summary>
    private const double RowStep = 170d;

    /// <summary>note 独立行与主图最低点的垂直间隔。留出一整行余量，注释不与卡片贴脸。</summary>
    private const double NoteRowGap = 220d;

    /// <summary>
    /// data 连线在"重心微调"里的权重：exec 邻居权重 1，data 邻居权重 0.4。
    /// data 是值依赖（次级约束），只应轻轻拉动节点，不应和执行流向平起平坐。
    /// </summary>
    private const double DataEdgeWeight = 0.4d;

    /// <summary>重心微调轮数。1 轮已能居中常见分支；2 轮让多级嵌套收敛得更稳，再多收益趋零。</summary>
    private const int BarycenterRounds = 2;

    /// <summary>
    /// 对整张图重新排版。<paramref name="owner"/> 目前未参与计算（布局只依赖连线拓扑），
    /// 保留参数以便未来按快捷键输入口细化 start 节点的布局。
    /// </summary>
    public static void Arrange(WorkflowGraph graph, ShortcutDefinition? owner = null)
    {
        _ = owner;

        graph.Nodes ??= [];
        graph.Links ??= [];
        graph.Nodes.RemoveAll(n => n == null || string.IsNullOrEmpty(n.Id));
        graph.Links.RemoveAll(l => l == null);
        graph.Invalidate(); // 索引基于清理后的节点表重建，下面的 Find 才可靠。

        if (graph.Nodes.Count == 0)
        {
            graph.Invalidate();
            return;
        }

        // ---- 0. 节点分组：note（含遗留 comment）单独排版，其余进入主图 ----
        var annotations = new List<WorkflowNode>();
        var main = new List<WorkflowNode>();
        var mainIdx = new Dictionary<WorkflowNode, int>();
        foreach (var node in graph.Nodes)
        {
            if (node.Type is WorkflowNodeTypes.Note or "comment")
                annotations.Add(node);
            else
            {
                mainIdx[node] = main.Count;
                main.Add(node);
            }
        }

        var count = main.Count;
        var startIdx = graph.Start is { } start && mainIdx.TryGetValue(start, out var si) ? si : -1;

        // ---- 1. 构图：exec 边（权重 1）+ data 边（权重 0.4）----
        // 忽略：指向 start 的边（start 是唯一入口，不该有前驱）、note 参与的边（已不在 main 表）、
        // 自环与重复边。两表都以 (From, To) 去重，保证相同图得到相同的邻接表。
        var execEdges = new HashSet<(int From, int To)>();
        var dataEdges = new HashSet<(int From, int To)>();
        foreach (var link in graph.Links)
        {
            var from = graph.Find(link.From);
            var to = graph.Find(link.To);
            if (from == null || to == null) continue;
            if (!mainIdx.TryGetValue(from, out var f) || !mainIdx.TryGetValue(to, out var t)) continue;
            if (f == t) continue;
            if (main[t].Type == WorkflowNodeTypes.Start) continue;

            if (link.IsData) dataEdges.Add((f, t));
            else execEdges.Add((f, t));
        }

        // 活跃节点 = 至少有一条边的节点，外加 start（即使没有任何边也要留在主图网格里）。
        // 其余主图节点即"孤立节点"，最后统一停靠到最右侧一列。
        var isActive = new bool[count];
        foreach (var (f, t) in execEdges)
        {
            isActive[f] = true;
            isActive[t] = true;
        }

        foreach (var (f, t) in dataEdges)
        {
            isActive[f] = true;
            isActive[t] = true;
        }

        if (startIdx >= 0) isActive[startIdx] = true;
        var activeTotal = isActive.Count(b => b);

        // ---- 2. 拓扑排序（Kahn；遇环时按文档序强制发射一个节点破环）----
        // 合并两类边做排序约束；排序结束后只保留指向"拓扑序更晚"节点的边，
        // 环上的回边（以及破环时未满足的入边）会被这一过滤自然丢弃。
        // 发射顺序用文档序做优先级（ready 集合取最小者），保证确定性。
        var succAll = NewAdjacency();
        var remainingIn = new int[count];
        foreach (var (f, t) in OrderedEdges())
        {
            succAll[f].Add(t);
            remainingIn[t]++;
        }

        var topoPos = new int[count];
        var emitted = new bool[count];
        var ready = new SortedSet<int>();
        for (var i = 0; i < count; i++)
        {
            if (isActive[i] && remainingIn[i] == 0) ready.Add(i);
        }

        var order = new List<int>(activeTotal);
        while (order.Count < activeTotal)
        {
            int v;
            if (ready.Count > 0)
            {
                v = ready.Min;
                ready.Remove(v);
            }
            else
            {
                // 成环：剩余节点全是环成员。取文档序最靠前者强制出列——
                // 它当时还未满足的入边即回边，稍后会被拓扑序过滤丢弃。
                v = Enumerable.Range(0, count).First(i => isActive[i] && !emitted[i]);
            }

            emitted[v] = true;
            topoPos[v] = order.Count;
            order.Add(v);

            foreach (var w in succAll[v])
            {
                if (!emitted[w] && --remainingIn[w] == 0) ready.Add(w);
            }
        }

        // 只保留"前驱在拓扑序更早"的边；exec 与 data 分开建邻接表，供分层/打包/微调使用。
        var execSucc = NewAdjacency();
        var execPred = NewAdjacency();
        var dataSucc = NewAdjacency();
        var dataPred = NewAdjacency();
        foreach (var (f, t) in execEdges.OrderBy(e => e.From).ThenBy(e => e.To))
        {
            if (topoPos[f] >= topoPos[t]) continue; // 回边：环的产物，不参与布局。
            execSucc[f].Add(t);
            execPred[t].Add(f);
        }

        foreach (var (f, t) in dataEdges.OrderBy(e => e.From).ThenBy(e => e.To))
        {
            if (topoPos[f] >= topoPos[t]) continue;
            dataSucc[f].Add(t);
            dataPred[t].Add(f);
        }

        // ---- 3. 分层（列号）：最长路径法，exec 与 data 前驱同样 +1（依赖方向正确）----
        // 拓扑序遍历保证所有保留的前驱都已定层。
        var layer = new int[count];
        foreach (var v in order)
        {
            var lv = 0;
            foreach (var u in execPred[v]) lv = Math.Max(lv, layer[u] + 1);
            foreach (var u in dataPred[v]) lv = Math.Max(lv, layer[u] + 1);
            layer[v] = lv;
        }

        // start 强制第 0 列。它没有入边（指向 start 的边已被忽略），这里只是防御性兜底。
        if (startIdx >= 0) layer[startIdx] = 0;

        // ---- 4. 行分配：子树连续打包 + 重心微调 ----
        // 按拓扑序处理：根节点取本层最上面的空闲行；每个节点把它尚未放置的直接 exec 后继
        // 依次安排到自己当前行下方的连续空闲行（第 1 个后继与父同行，其后逐行向下）。
        // 同层冲突时 NextFree 向后扫描，绝不与已放置节点同格。
        var row = new int[count];
        var placedRow = new bool[count];
        var usedRows = new Dictionary<int, HashSet<int>>();

        HashSet<int> RowsOf(int l)
        {
            if (!usedRows.TryGetValue(l, out var set))
                usedRows[l] = set = [];
            return set;
        }

        int NextFree(int l, int fromRow)
        {
            var rows = RowsOf(l);
            var r = Math.Max(fromRow, 0);
            while (rows.Contains(r)) r++;
            return r;
        }

        void Place(int v, int desiredRow)
        {
            var r = NextFree(layer[v], desiredRow);
            RowsOf(layer[v]).Add(r);
            row[v] = r;
            placedRow[v] = true;
        }

        foreach (var v in order)
        {
            if (!placedRow[v]) Place(v, 0);
            var slot = 0;
            foreach (var w in execSucc[v])
            {
                if (placedRow[w]) continue;
                Place(w, row[v] + slot);
                slot++;
            }
        }

        // 重心微调：逐层（自上而下）、按当前行序处理，把节点向其全部邻居的加权平均行靠拢
        // （exec 邻居权重 1，data 邻居权重 0.4）。只允许移动到空闲行，层内行号唯一性不被破坏，
        // 因此"同层垂直间距 ≥ RowStep"的不变量始终保持。分支/汇合节点由此趋向垂直居中。
        for (var round = 0; round < BarycenterRounds; round++)
        {
            foreach (var group in order.Where(v => isActive[v])
                         .GroupBy(v => layer[v]).OrderBy(g => g.Key))
            {
                foreach (var v in group.OrderBy(u => row[u]).ToList())
                {
                    double sum = 0, weight = 0;
                    foreach (var u in execPred[v]) { sum += 1d * row[u]; weight += 1d; }
                    foreach (var u in execSucc[v]) { sum += 1d * row[u]; weight += 1d; }
                    foreach (var u in dataPred[v]) { sum += DataEdgeWeight * row[u]; weight += DataEdgeWeight; }
                    foreach (var u in dataSucc[v]) { sum += DataEdgeWeight * row[u]; weight += DataEdgeWeight; }
                    if (weight <= 0) continue;

                    var desired = (int)Math.Round(sum / weight, MidpointRounding.AwayFromZero);
                    if (desired == row[v]) continue;

                    var target = NextFree(layer[v], desired);
                    if (target == row[v]) continue;

                    RowsOf(layer[v]).Remove(row[v]);
                    RowsOf(layer[v]).Add(target);
                    row[v] = target;
                }
            }
        }

        // ---- 5. 坐标 ----
        // 不给 body/true/false/case 端口的后继额外 +1 列：分层本身就是执行顺序
        // （后继至少落在下一列），循环体子图因经过循环节点也天然在右。
        var maxMainY = double.NegativeInfinity;
        var maxLayer = -1;
        for (var i = 0; i < count; i++)
        {
            if (!isActive[i]) continue;
            main[i].X = layer[i] * ColumnStep;
            main[i].Y = row[i] * RowStep;
            maxMainY = Math.Max(maxMainY, main[i].Y);
            maxLayer = Math.Max(maxLayer, layer[i]);
        }

        // 孤立节点（无任何 exec/data 边，且不是 start/note）：停靠到最大列右侧一整列。
        var isolatedX = (maxLayer + 1) * ColumnStep;
        var isolatedRow = 0;
        for (var i = 0; i < count; i++)
        {
            if (isActive[i]) continue;
            main[i].X = isolatedX;
            main[i].Y = isolatedRow * RowStep;
            isolatedRow++;
        }

        // note：主图下方一条独立行，X 按文档顺序等间隔。
        var noteY = double.IsNegativeInfinity(maxMainY) ? 0 : maxMainY + NoteRowGap;
        var noteX = 0d;
        foreach (var note in annotations)
        {
            note.X = noteX;
            note.Y = noteY;
            noteX += ColumnStep;
        }

        graph.Invalidate();
        return;

        List<int>[] NewAdjacency() => Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();

        // 所有边合并后按 (From, To) 排序：去重交给 HashSet，这里保证邻接表构建顺序确定。
        IEnumerable<(int From, int To)> OrderedEdges() =>
            execEdges.Concat(dataEdges).OrderBy(e => e.From).ThenBy(e => e.To);
    }
}
