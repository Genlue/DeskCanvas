using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shortcuts.Models;

namespace Shortcuts.Services.Ai;

/// <summary>Outcome of one AI workflow generation attempt.</summary>
/// <param name="Success">True when <paramref name="WorkflowJson"/> is usable.</param>
/// <param name="WorkflowJson">The extracted, shape-checked workflow JSON (null on failure).</param>
/// <param name="Error">Chinese, UI-displayable failure reason (null on success).</param>
/// <param name="Warnings">Non-fatal notes (reserved; currently empty).</param>
/// <param name="Elapsed">Total wall time of the request.</param>
public sealed record AiGenerateResult(
    bool Success,
    string? WorkflowJson,
    string? Error,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed)
{
    public static AiGenerateResult Fail(string error, TimeSpan elapsed) =>
        new(false, null, error, [], elapsed);
}

/// <summary>
/// Calls an OpenAI-compatible <c>/chat/completions</c> endpoint to turn a user request into a
/// workflow JSON, using <see cref="WorkflowSpecDocument.Build()"/> as the system prompt.
/// <para>
/// Pure service layer (no UI). The reply is never trusted: the JSON is extracted with
/// <see cref="WorkflowImport.ExtractJson"/> and shape-checked (a <c>nodes</c> array must exist)
/// before it is returned — the full semantic validation stays with
/// <see cref="WorkflowImport.Parse"/> at import time.
/// </para>
/// </summary>
public static class AiWorkflowService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(180),
    };

    /// <summary>
    /// Generate a (possibly modified) workflow JSON. <paramref name="currentGraph"/> is optional
    /// context: when present the model is told to return the complete modified workflow based on it.
    /// </summary>
    public static async Task<AiGenerateResult> GenerateAsync(
        AiSettings settings,
        string userPrompt,
        WorkflowGraph? currentGraph,
        ShortcutDefinition? currentOwner,
        CancellationToken ct = default)
    {
        var started = Stopwatch.StartNew();

        // ---- settings validation --------------------------------------------------
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return AiGenerateResult.Fail("请先在设置里填写 API Key。", started.Elapsed);
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            return AiGenerateResult.Fail("请先在设置里填写接口地址（Base URL）。", started.Elapsed);
        if (string.IsNullOrWhiteSpace(settings.Model))
            return AiGenerateResult.Fail("请先在设置里填写模型名称。", started.Elapsed);
        if (string.IsNullOrWhiteSpace(userPrompt))
            return AiGenerateResult.Fail("请先描述你想要的工作流。", started.Elapsed);

        // ---- messages -------------------------------------------------------------
        var messages = new List<object>
        {
            new { role = "system", content = BuildSystemPrompt() },
        };

        if (currentGraph is { Nodes.Count: > 0 } or { Links.Count: > 0 })
        {
            var graphJson = JsonSerializer.Serialize(currentGraph, DocumentJson.Options);
            var context = new StringBuilder();
            context.AppendLine("以下是当前画布上的工作流 JSON：");
            context.AppendLine(graphJson);
            if (currentOwner != null && !string.IsNullOrWhiteSpace(currentOwner.Name))
                context.AppendLine($"（它所属的快捷指令名称为：{currentOwner.Name}。）");
            context.Append(
                "请在这份工作流的基础上按我下一条消息的要求修改，返回**完整**修改后的工作流 JSON" +
                "（包含全部节点与连线，而不是只返回改动部分）。");
            messages.Add(new { role = "user", content = context.ToString() });
            messages.Add(new { role = "assistant", content = "已了解当前工作流。请告诉我你的修改要求，我会返回完整修改后的工作流 JSON。" });
        }

        messages.Add(new { role = "user", content = userPrompt });

        // ---- request --------------------------------------------------------------
        var url = settings.BaseUrl.TrimEnd('/') + "/chat/completions";
        var body = JsonSerializer.Serialize(new
        {
            model = settings.Model,
            temperature = settings.Temperature,
            stream = false,
            messages,
        });

        string content;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return AiGenerateResult.Fail(
                    $"HTTP {(int)response.StatusCode}：{Truncate(text, 300)}", started.Elapsed);
            }

            content = ExtractContent(text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AiGenerateResult.Fail("已取消。", started.Elapsed);
        }
        catch (TaskCanceledException)
        {
            return AiGenerateResult.Fail("请求超时（超过 180 秒），请检查网络或稍后重试。", started.Elapsed);
        }
        catch (Exception ex)
        {
            return AiGenerateResult.Fail($"网络请求失败：{ex.Message}", started.Elapsed);
        }

        // ---- reply parsing ----------------------------------------------------------
        if (string.IsNullOrWhiteSpace(content))
            return AiGenerateResult.Fail("模型返回了空内容。", started.Elapsed);

        var json = WorkflowImport.ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json) || !LooksLikeJson(json))
        {
            return AiGenerateResult.Fail(
                $"未能从回复中解析出 JSON。回复开头：{Truncate(content, 300)}", started.Elapsed);
        }

        if (!HasWorkflowShape(json, out var reason))
            return AiGenerateResult.Fail(reason, started.Elapsed);

        return new AiGenerateResult(true, json, null, [], started.Elapsed);
    }

    // ---- prompt ---------------------------------------------------------------------

    private static string BuildSystemPrompt() =>
        WorkflowSpecDocument.Build() +
        """
        
        
        ## 8. 本会话的输出纪律（最高优先级）

        - 严格按照上述规范输出**唯一一个** JSON 对象，不要输出任何解释、markdown 代码围栏（```）或多余文本。
        - 不要把 JSON 拆成多个片段；不要在 JSON 前后添加任何说明性文字或注释。
        - 不得输出本规范之外的动作类型或端口名；JSON 必须能被程序直接反序列化。
        """;

    // ---- reply helpers ----------------------------------------------------------------

    /// <summary>Reads <c>choices[0].message.content</c> from a chat-completions response.</summary>
    private static string ExtractContent(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var first = choices[0];
        if (first.ValueKind != JsonValueKind.Object ||
            !first.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return content.GetString() ?? string.Empty;
    }

    private static bool LooksLikeJson(string text) =>
        text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[');

    /// <summary>
    /// Cheap shape check before handing the JSON to the UI: an importable document must
    /// expose a <c>nodes</c> array (directly, nested under <c>workflow</c>, or per entry of
    /// a <c>shortcuts</c> array).
    /// </summary>
    private static bool HasWorkflowShape(string json, out string error)
    {
        error = "";
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            error = $"返回的 JSON 格式有误：{Truncate(ex.Message, 200)}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (HasNodesArray(root)) return true;

                if (root.TryGetProperty("workflow", out var nested) &&
                    nested.ValueKind == JsonValueKind.Object && HasNodesArray(nested))
                    return true;

                if (root.TryGetProperty("shortcuts", out var list) &&
                    list.ValueKind == JsonValueKind.Array &&
                    list.EnumerateArray().Any(item =>
                        item.ValueKind == JsonValueKind.Object && HasNodesArray(item)))
                    return true;
            }

            error = "返回的 JSON 缺少 nodes 数组，不是一个可导入的工作流。请重试或调整描述。";
            return false;
        }
    }

    private static bool HasNodesArray(JsonElement element) =>
        element.TryGetProperty("nodes", out var nodes) &&
        nodes.ValueKind == JsonValueKind.Array;

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var compact = text.Replace("\r", "").Replace('\n', ' ').Trim();
        return compact.Length <= maxLength ? compact : compact[..maxLength] + "…";
    }
}
