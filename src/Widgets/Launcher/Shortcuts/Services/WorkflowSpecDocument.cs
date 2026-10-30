using System.Globalization;
using System.Text;
using Shortcuts.Models;

namespace Shortcuts.Services;

/// <summary>
/// Builds the "AI 帮写" specification: a self-contained document the user can copy into any
/// chat model together with their request, so the model replies with a workflow JSON this app
/// can import verbatim.
/// <para>
/// The document is <b>generated from <see cref="ActionCatalog"/> and
/// <see cref="WorkflowNodeTypes"/></b>, never hand-maintained: a new action type or port shows
/// up in the prompt the moment it is registered, which is the only way the model's output can
/// stay in sync with what the runner actually accepts.
/// </para>
/// </summary>
public static class WorkflowSpecDocument
{
    /// <summary>The full specification text (markdown, Chinese prose + English type ids).</summary>
    public static string Build()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# DeskCanvas 快捷指令工作流 JSON 规范");
        sb.AppendLine();
        sb.AppendLine("你要为我生成一份**可直接导入 DeskCanvas「快捷指令」组件的工作流 JSON**。");
        sb.AppendLine("正常情况下只输出一个 JSON 代码块，不要解释、不要注释、不要多余文字。");
        sb.AppendLine("**唯一例外**：完成第 7 节的安全审查后若发现安全隐患，先在 JSON 前用几行「⚠️ 安全提示」逐条说明（每条一行：风险 + 建议），随后照常输出 JSON；没有隐患就不要附加任何文字。");
        sb.AppendLine();

        AppendTopLevel(sb);
        AppendNodeSection(sb);
        AppendLinkSection(sb);
        AppendCatalog(sb);
        AppendExamples(sb);
        AppendChecklist(sb);
        AppendSafetyReview(sb);

        return sb.ToString();
    }

    // ---- 1 ------------------------------------------------------------------------

    private static void AppendTopLevel(StringBuilder sb)
    {
        sb.AppendLine("## 1. 顶层结构");
        sb.AppendLine();
        sb.AppendLine("推荐直接带名称、图标、颜色和输入字段：");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"name\": \"我的快捷指令\",");
        sb.AppendLine("  \"glyph\": \"bolt\",");
        sb.AppendLine("  \"color\": \"#534AB7\",");
        sb.AppendLine("  \"confirmBeforeRun\": false,");
        sb.AppendLine("  \"inputs\": [");
        sb.AppendLine("    { \"key\": \"text\", \"name\": \"文本\", \"type\": \"text\", \"required\": true, \"default\": \"\" }");
        sb.AppendLine("  ],");
        sb.AppendLine("  \"nodes\": [ ... ],");
        sb.AppendLine("  \"links\": [ ... ]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("- `glyph` 可选值：" + string.Join("、", GlyphLibrary.PickerGlyphs.Take(40)) + "（也可省略，默认 `bolt`）");
        sb.AppendLine("- `color`：`#RRGGBB`，可省略。");
        sb.AppendLine("- `confirmBeforeRun`：关机、休眠、清空回收站等危险操作请设为 `true`。");
        sb.AppendLine("- `inputs`：快捷指令声明的输入字段。`key` 是稳定标识，`type` 取 `text`/`number`/`boolean`/`datetime`/`path`/`list`/`object`/`any`；`required` 为 true 时调用方必须提供。");
        sb.AppendLine("- `nodes` 必填，`links` 可为空数组。也可以把多个快捷指令放进 `{ \"shortcuts\": [ ... ] }` 数组。");
        sb.AppendLine();
        sb.AppendLine("声明了 `inputs` 后，`start` 节点会为每个字段暴露一个**同名数据输出口**（例如 `text`）。");
        sb.AppendLine("下游既可以用数据线从 `start.text` 取值，也可以在文本里写 `{{text}}`。");
        sb.AppendLine();
    }

    // ---- 2 ------------------------------------------------------------------------

    private static void AppendNodeSection(StringBuilder sb)
    {
        sb.AppendLine("## 2. 节点 node");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{ \"id\": \"n1\", \"type\": \"openApp\", \"x\": 0, \"y\": 0, \"title\": \"打开记事本\", \"params\": { \"target\": \"notepad.exe\" } }");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("| 字段 | 必填 | 说明 |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine("| `id` | 建议 | 唯一字符串。省略时自动生成；连线要用它引用节点。 |");
        sb.AppendLine("| `type` | 是 | 见第 4 节节点类型表，必须是表中存在的 id（区分大小写）。 |");
        sb.AppendLine("| `x` `y` | 否 | 画布坐标（数字）。全部省略/为 0 时会自动排版。 |");
        sb.AppendLine("| `params` | 否 | 参数对象，键见类型表。省略即用默认值。 |");
        sb.AppendLine("| `title` | 否 | 卡片上显示的自定义标题。 |");
        sb.AppendLine("| `cases` | 仅 `switch` | 分支行数组，每行 `{ \"id\": \"case0\", \"value\": \"匹配值\" }`；`id` 就是执行输出口名。 |");
        sb.AppendLine("| `onError` | 否 | `\"stop\"`（默认，失败即停止）或 `\"route\"`（失败走该节点的 `error` 出口）。 |");
        sb.AppendLine();
        sb.AppendLine("参数值有两种写法：");
        sb.AppendLine();
        sb.AppendLine("- 普通值：`\"target\": \"notepad.exe\"`（数字/布尔也直接写，如 `\"count\": 3`）");
        sb.AppendLine("- 引用其他节点的输出：`\"text\": { \"kind\": \"variable\", \"sourceActionId\": \"n1\", \"sourcePort\": \"text\" }`");
        sb.AppendLine();
        sb.AppendLine("`sourcePort` 省略时表示「该节点的默认数据输出口」。**连线得到的结果不会再展开一次模板**：");
        sb.AppendLine("如果来源值本身包含 `{{...}}`，它会被原样当作数据使用。");
        sb.AppendLine();
        sb.AppendLine("文本参数里可以使用模板占位符（只在字面量中展开）：");
        sb.AppendLine();
        sb.AppendLine("- `{{last}}`：最近一次数据结果；");
        sb.AppendLine("- `{{index}}`：当前循环序号（从 0 开始）；`{{item}}`：当前遍历项；`{{var}}`：`forEach.var` 指定的变量名；");
        sb.AppendLine("- `{{nodeId}}`：某个节点默认数据输出口的值；`{{变量名}}`：`setVar` 写入的命名变量；");
        sb.AppendLine("- `{{inputKey}}`：快捷指令声明的输入字段。");
        sb.AppendLine();
        sb.AppendLine("例：`\"message\": \"第 {{index}} 项是 {{item}}\"`。");
        sb.AppendLine();
    }

    // ---- 3 ------------------------------------------------------------------------

    private static void AppendLinkSection(StringBuilder sb)
    {
        sb.AppendLine("## 3. 连线 link");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{ \"from\": \"n1\", \"fromPort\": \"out\", \"to\": \"n2\", \"toPort\": \"in\" }");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("两种连线，建议显式写 `kind`：");
        sb.AppendLine();
        sb.AppendLine("1. **执行流（exec）**：`kind` 为 `\"exec\"`，`fromPort` 是执行输出口，`toPort` 固定为 `\"in\"`，决定「下一步做什么」。");
        sb.AppendLine("2. **数据流（data）**：`kind` 为 `\"data\"`，`fromPort` 是来源节点的**数据输出口名**，`toPort` 是目标节点的**参数名**。");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{ \"from\": \"clip\", \"fromPort\": \"text\", \"to\": \"note\", \"toPort\": \"message\", \"kind\": \"data\" }");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("- 数据线的 `fromPort` 可以省略，此时使用来源节点的默认数据输出口（类型表里标为默认的那个）。");
        sb.AppendLine("- 数据口必须**按名字**连接；旧的通用 `value` 口已经取消，只有表中列出的口才存在。");
        sb.AppendLine("- 表中参数默认可以用数据线绑定；例外是 `runShortcut.target`（快捷指令选择器，`Bindable:false`），请写名称或 id。");
        sb.AppendLine("- 每个执行输出口只能连一条线；连第二条会被校验器报错（`ExecFanOut`）。");
        sb.AppendLine("- 一个节点的 `in` 可以有多条入线 —— 那是「合并」，任何一条路径到达都会执行它。");
        sb.AppendLine("- 普通动作（流程控制节点除外）都有 `out` 和 `error` 两个执行出口；`error` 只在 `onError: \"route\"` 时被跟随。");
        sb.AppendLine("- 执行从 `start` 节点开始。`start` 没有 `in`；它有一个 `out`，以及每个声明输入的输出口。");
        sb.AppendLine("- 链尾不接线 = 结束。需要提前结束用 `end`，循环里提前跳出用 `break`，跳过本轮用 `continue`，返回结果用 `return`。");
        sb.AppendLine();
        sb.AppendLine("### switch 的动态分支");
        sb.AppendLine();
        sb.AppendLine("`switch` 的每个 case 都是一条执行输出口，端口名就是 case 的 `id`；另有 `default`。");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"id\": \"sw\", \"type\": \"switch\",");
        sb.AppendLine("  \"params\": { \"value\": \"{{clip}}\", \"ignoreCase\": \"true\" },");
        sb.AppendLine("  \"cases\": [");
        sb.AppendLine("    { \"id\": \"case0\", \"value\": \"yes\" },");
        sb.AppendLine("    { \"id\": \"case1\", \"value\": \"no\" }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("连线时 `fromPort` 写 `case0` / `case1` / `default`。case 的 `id` 必须唯一，且不要用 `default`。");
        sb.AppendLine();
        sb.AppendLine("### 错误出口");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{ \"id\": \"http\", \"type\": \"httpRequest\", \"onError\": \"route\", \"params\": { \"url\": \"https://example.com\", \"method\": \"GET\" } }");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("再补一条 `{ \"from\": \"http\", \"fromPort\": \"error\", \"to\": \"fallback\", \"toPort\": \"in\" }`。");
        sb.AppendLine("没有 `error` 连线时，即使 `onError: \"route\"` 也会按失败处理。");
        sb.AppendLine();
        sb.AppendLine("### 循环端口");
        sb.AppendLine();
        sb.AppendLine("- `body`：每轮执行的入口；");
        sb.AppendLine("- `done`：循环结束后的入口；");
        sb.AppendLine("- `collected`：循环体里 `collect` 收集到的列表，类型 `List`，在 `done` 之后读取。");
        sb.AppendLine();
        sb.AppendLine("循环体尾部不接线表示结束本轮，不要画回边。`body` 路径不要接进 `done` 的后继，否则后继会每轮执行一次。");
        sb.AppendLine();
        sb.AppendLine("### return 与子流程");
        sb.AppendLine();
        sb.AppendLine("`return` 把 `value` 作为本次快捷指令的返回值；`runShortcut` 的 `result` 输出就是子流程的 `return` 值。");
        sb.AppendLine("`runShortcut.target` 写子快捷指令的 id 或名称，`arguments` 写 JSON 对象字符串（可用模板，如 `\"{\\\"text\\\":\\\"{{text}}\\\"}\"`）。");
        sb.AppendLine();
    }

    // ---- 4 ------------------------------------------------------------------------

    private static void AppendCatalog(StringBuilder sb)
    {
        sb.AppendLine("## 4. 节点类型表");
        sb.AppendLine();
        sb.AppendLine("参数格式：`key(类型, 必填/可选, 默认值, 取值范围)`；输出口格式：`portId(类型)`，`exec` 表示执行口。");
        sb.AppendLine("所有普通动作都带 `out(exec)` 与 `error(exec)`；数据口只能从来源节点的输出口连到目标参数。");
        sb.AppendLine();

        foreach (var category in ActionCatalog.CategoryOrder)
        {
            var definitions = ActionCatalog.Visible.Where(d => d.Category == category).ToList();
            if (definitions.Count == 0) continue;

            sb.AppendLine($"### {CategoryName(category)}");
            sb.AppendLine();
            sb.AppendLine("| type | 名称 | 用途 | 参数 | 输出口 |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");

            foreach (var definition in definitions)
            {
                var purpose = string.IsNullOrEmpty(definition.SummaryKey)
                    ? string.Empty
                    : Loc.Get(definition.SummaryKey);

                sb.AppendLine(
                    $"| `{definition.Type}` | {Loc.Get(definition.TitleKey)} | {EscapeCell(purpose)} | {EscapeCell(ParamTable(definition))} | {PortText(definition.Type)} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("补充：`start` 是入口节点（不在上表中，但每个工作流都必须有且只有一个）。");
        sb.AppendLine("它有一个 `out`，并在快捷指令声明 `inputs` 时暴露同名数据口。");
        sb.AppendLine();
    }

    // ---- 5 ------------------------------------------------------------------------

    private static void AppendExamples(StringBuilder sb)
    {
        sb.AppendLine("## 5. 示例");
        sb.AppendLine();
        sb.AppendLine("### 5.1 剪贴板分支：空则提示，否则搜索");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("""
{
  "name": "示例：剪贴板分支",
  "nodes": [
    { "id": "s", "type": "start" },
    { "id": "clip", "type": "getClipboard" },
    {
      "id": "br",
      "type": "branch",
      "params": {
        "left": { "kind": "variable", "sourceActionId": "clip", "sourcePort": "text" },
        "op": "isEmpty"
      }
    },
    { "id": "empty", "type": "notify", "params": { "message": "剪贴板是空的" } },
    {
      "id": "search",
      "type": "webSearch",
      "params": {
        "query": { "kind": "variable", "sourceActionId": "clip", "sourcePort": "text" },
        "engine": "bing"
      }
    }
  ],
  "links": [
    { "from": "s", "fromPort": "out", "to": "clip", "toPort": "in" },
    { "from": "clip", "fromPort": "out", "to": "br", "toPort": "in" },
    { "from": "br", "fromPort": "true", "to": "empty", "toPort": "in" },
    { "from": "br", "fromPort": "false", "to": "search", "toPort": "in" }
  ]
}
""");
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("### 5.2 HTTP → JSON → 遍历 → 收集 → 显示");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("""
{
  "name": "示例：HTTP 列表",
  "nodes": [
    { "id": "s", "type": "start" },
    {
      "id": "http",
      "type": "httpRequest",
      "params": { "url": "https://example.com/items.json", "method": "GET", "timeout": 15 }
    },
    {
      "id": "parse",
      "type": "parseJson",
      "params": {
        "text": { "kind": "variable", "sourceActionId": "http", "sourcePort": "body" }
      }
    },
    {
      "id": "loop",
      "type": "forEach",
      "params": {
        "list": { "kind": "variable", "sourceActionId": "parse", "sourcePort": "value" },
        "separator": "newline",
        "var": "item"
      }
    },
    { "id": "collect", "type": "collect", "params": { "value": "{{item}}" } },
    {
      "id": "show",
      "type": "showResult",
      "params": {
        "value": { "kind": "variable", "sourceActionId": "loop", "sourcePort": "collected" },
        "title": "结果"
      }
    }
  ],
  "links": [
    { "from": "s", "fromPort": "out", "to": "http", "toPort": "in" },
    { "from": "http", "fromPort": "out", "to": "parse", "toPort": "in" },
    { "from": "parse", "fromPort": "out", "to": "loop", "toPort": "in" },
    { "from": "loop", "fromPort": "body", "to": "collect", "toPort": "in" },
    { "from": "loop", "fromPort": "done", "to": "show", "toPort": "in" },
    { "from": "http", "fromPort": "body", "to": "parse", "toPort": "text", "kind": "data" },
    { "from": "parse", "fromPort": "value", "to": "loop", "toPort": "list", "kind": "data" },
    { "from": "loop", "fromPort": "collected", "to": "show", "toPort": "value", "kind": "data" }
  ]
}
""");
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("### 5.3 父流程传参 → 子流程计算 → 返回");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("""
{
  "shortcuts": [
    {
      "name": "Child: echo",
      "inputs": [
        { "key": "text", "name": "文本", "type": "text", "required": true }
      ],
      "nodes": [
        { "id": "s", "type": "start" },
        { "id": "conv", "type": "toJson" },
        { "id": "ret", "type": "return" }
      ],
      "links": [
        { "from": "s", "fromPort": "out", "to": "conv", "toPort": "in" },
        { "from": "conv", "fromPort": "out", "to": "ret", "toPort": "in" },
        { "from": "s", "fromPort": "text", "to": "conv", "toPort": "value", "kind": "data" },
        { "from": "conv", "fromPort": "text", "to": "ret", "toPort": "value", "kind": "data" }
      ]
    },
    {
      "name": "Parent",
      "inputs": [
        { "key": "name", "name": "名称", "type": "text", "required": true, "default": "World" }
      ],
      "nodes": [
        { "id": "s", "type": "start" },
        {
          "id": "call",
          "type": "runShortcut",
          "params": {
            "target": "Child: echo",
            "arguments": "{\"text\":\"{{name}}\"}"
          }
        },
        { "id": "ret", "type": "return" }
      ],
      "links": [
        { "from": "s", "fromPort": "out", "to": "call", "toPort": "in" },
        { "from": "call", "fromPort": "out", "to": "ret", "toPort": "in" },
        { "from": "call", "fromPort": "result", "to": "ret", "toPort": "value", "kind": "data" }
      ]
    }
  ]
}
""");
        sb.AppendLine("```");
        sb.AppendLine();
    }

    // ---- 6 ------------------------------------------------------------------------

    private static void AppendChecklist(StringBuilder sb)
    {
        sb.AppendLine("## 6. 生成前自检");
        sb.AppendLine();
        sb.AppendLine("- 每个 `type` 都在第 4 节表里，拼写完全一致（区分大小写）。");
        sb.AppendLine("- 每个 `link` 的 `from`/`to` 都能在 `nodes` 里找到；`fromPort` 是该节点列出的输出口。");
        sb.AppendLine("- 执行线一律连 `\"in\"`；数据线的 `toPort` 必须是目标参数名，`fromPort` 必须是来源的数据口名。");
        sb.AppendLine("- 数据口按名字连接：不要写旧的通用 `value`（除非该节点表里确实有 `value` 口）。");
        sb.AppendLine("- `switch` 的 `cases` 每行都有唯一 `id`，连线使用同一个 `id`；没有匹配时走 `default`。");
        sb.AppendLine("- `onError: \"route\"` 必须配一条从 `error` 口出发的执行线。");
        sb.AppendLine("- 循环体从 `body` 口出去，循环结束后走 `done` 口；收集结果从 `collected` 口读，别把 `body` 和 `done` 接反。");
        sb.AppendLine("- 声明了 `inputs` 才使用 `start.<key>` 数据口；`return` 的值会成为 `runShortcut.result`。");
        sb.AppendLine("- 需要用户确认的危险操作（关机/休眠/清空回收站）请把 `confirmBeforeRun` 设为 `true`。");
        sb.AppendLine("- 输出纯 JSON，不要 Markdown 注释、不要尾随逗号。");
    }

    // ---- 7 ------------------------------------------------------------------------

    private static void AppendSafetyReview(StringBuilder sb)
    {
        sb.AppendLine("## 7. 安全审查（必做，输出前完成）");
        sb.AppendLine();
        sb.AppendLine("在输出 JSON 之前，对整份工作流做一次独立的安全审查：");
        sb.AppendLine();
        sb.AppendLine("- **优先派发子代理复审**：如果你所在的运行环境支持派发子代理/子任务，请派发一个**只做安全审查**的子代理，把本规范、用户需求和拟生成的 JSON 交给它复审，采纳其结论后再输出；");
        sb.AppendLine("- 环境不支持子代理时，你必须切换到独立的审查者视角，抛开自己刚才的思路，把需求与 JSON 重新通读一遍再输出。");
        sb.AppendLine();
        sb.AppendLine("审查清单（逐项核对）：");
        sb.AppendLine();
        sb.AppendLine("- **危险系统操作**：`sleep`、`lockScreen`、`emptyRecycleBin`、删除/覆盖用户文件的 `writeTextFile`，以及 `runCommand` 中的关机、重启、格式化、批量删除、清空目录、改注册表/系统设置、下载并执行等破坏性命令 —— 只要出现，`confirmBeforeRun` 必须为 `true`，且不得用参数模板把操作范围扩大到用户没有要求的路径；");
        sb.AppendLine("- **命令注入**：`runCommand.command` 若拼接了 `{{...}}` 模板（剪贴板、输入字段、文件内容等外部数据），必须提示「模板内容会被 shell 当作命令执行」的风险；");
        sb.AppendLine("- **隐私外传**：剪贴板内容、文件内容或输入字段经 `httpRequest` 发往第三方时，在提示中说明数据会离开本机；");
        sb.AppendLine("- **静默后台触发**：带热键或计划自动运行的工作流无法弹确认，应避免不可逆操作；");
        sb.AppendLine("- **权限越界**：动作访问的路径/网址明显超出用户需求范围时收窄。");
        sb.AppendLine();
        sb.AppendLine("审查发现任何隐患：在 JSON 前输出「⚠️ 安全提示」逐条告知我（每条一行：风险 + 建议）；");
        sb.AppendLine("没有发现隐患：只输出 JSON，不要附加审查过程说明。");
        sb.AppendLine();
    }

    // ---- helpers ------------------------------------------------------------------

    private static string ParamTable(ActionDefinition definition)
    {
        if (definition.Params.Count == 0) return "（无）";
        return string.Join("；", definition.Params.Select(ParamText));
    }

    private static string ParamText(ParamSpec spec)
    {
        var required = spec.Required ? "必填" : "可选";
        var defaultValue = string.IsNullOrEmpty(spec.DefaultValue) ? "—" : spec.DefaultValue;
        var range = spec.Kind == ParamKind.Number
            ? $"{spec.Min.ToString(CultureInfo.InvariantCulture)}~{spec.Max.ToString(CultureInfo.InvariantCulture)}"
            : spec.Options is { Count: > 0 }
                ? "{" + string.Join("|", spec.Options.Select(o => o.Value)) + "}"
                : "—";

        return $"`{spec.Key}`({TypeName(spec.DataType)}, {required}, 默认 {defaultValue}, 范围 {range})";
    }

    private static string PortText(string type)
    {
        // Switch arms are data, not a fixed vocabulary: the id comes from each case row.
        if (type == WorkflowNodeTypes.Switch)
            return "`<caseId>`(exec)、`default`(exec)";

        if (type == WorkflowNodeTypes.Note)
            return "（无，注释节点不参与执行）";

        var ports = WorkflowNodeTypes.Outputs(type);
        if (ports.Count == 0) return "（无，流程在此结束）";

        return string.Join("、", ports.Select(port =>
            $"`{port.Id}`({(port.IsData ? TypeName(port.DataType) : "exec")})"));
    }

    private static string TypeName(DataType type) => type switch
    {
        DataType.Any => "Any",
        DataType.Number => "Number",
        DataType.Boolean => "Boolean",
        DataType.DateTime => "DateTime",
        DataType.Path => "Path",
        DataType.List => "List",
        DataType.Object => "Object",
        DataType.Null => "Null",
        _ => "Text",
    };

    private static string EscapeCell(string text) =>
        text.Replace("|", "\\|").Replace("\r", string.Empty).Replace('\n', ' ');

    private static string CategoryName(ActionCategory category) => category switch
    {
        ActionCategory.Launch => "启动与打开",
        ActionCategory.Media => "媒体控制",
        ActionCategory.System => "系统与工具",
        ActionCategory.Text => "文本与数据",
        ActionCategory.Data => "数据节点",
        ActionCategory.Flow => "流程控制",
        _ => "DeskCanvas 集成",
    };
}
