using DeskCanvas.Core.Services;

// UpdateAtomChecks — pins the releases.atom fallback parser (ReleaseAtomParser) against a
// frozen fixture in the real GitHub feed shape (v3.8.0 release: XML-escaped HTML content with
// zh text, /tag/ link, id tail) plus edge cases. Pure Core — no UI, runs headless anywhere.
// Exit code 0 = ALL PASS, 1 = failure (CI / 提交门槛按改动范围调用).

const string Fixture =
    """
    <?xml version="1.0" encoding="UTF-8"?>
    <feed xmlns="http://www.w3.org/2005/Atom" xmlns:media="http://search.yahoo.com/mrss/" xml:lang="en-US">
      <id>tag:github.com,2008:https://github.com/Genlue/DeskCanvas/releases</id>
      <link type="text/html" rel="alternate" href="https://github.com/Genlue/DeskCanvas/releases"/>
      <title>Release notes from DeskCanvas</title>
      <updated>2026-10-15T07:50:29Z</updated>
      <entry>
        <id>tag:github.com,2008:Repository/1342473727/v3.8.0</id>
        <updated>2026-10-15T07:50:29Z</updated>
        <link rel="alternate" type="text/html" href="https://github.com/Genlue/DeskCanvas/releases/tag/v3.8.0"/>
        <title>DeskCanvas v3.8.0</title>
        <content type="html">&lt;h1&gt;DeskCanvas v3.8.0 更新日志&lt;/h1&gt;
    &lt;p&gt;本次为大型功能版本：&lt;strong&gt;失焦自动切换主题&lt;/strong&gt;——未认证 GitHub API 只有 60 次/小时 配额，共享代理出口 IP 很容易被打光。&lt;/p&gt;
    &lt;p&gt;引号转义：判定&quot;窗口存在性&quot;而非&quot;点击归属&quot;。&lt;/p&gt;</content>
      </entry>
      <entry>
        <id>tag:github.com,2008:Repository/1342473727/v3.7.2</id>
        <updated>2026-10-14T09:09:13Z</updated>
        <link rel="alternate" type="text/html" href="https://github.com/Genlue/DeskCanvas/releases/tag/v3.7.2"/>
        <title>DeskCanvas v3.7.2</title>
        <content type="html">&lt;p&gt;Proxy persistence compatibility.&lt;/p&gt;</content>
      </entry>
    </feed>
    """;

var failures = new List<string>();
var passed = 0;

void Check(string name, bool condition, string detail = "")
{
    if (condition) { passed++; Console.WriteLine($"[PASS] {name}"); }
    else { failures.Add(name); Console.WriteLine($"[FAIL] {name}{(detail.Length > 0 ? $" — {detail}" : "")}"); }
}

// 1. URL 推导
Check("derive: 标准 API URL → atom feed",
    ReleaseAtomParser.DeriveAtomUrl("https://api.github.com/repos/Genlue/DeskCanvas/releases/latest")
        == "https://github.com/Genlue/DeskCanvas/releases.atom");
Check("derive: 带 query 的 API URL 同样推导",
    ReleaseAtomParser.DeriveAtomUrl("https://api.github.com/repos/Genlue/DeskCanvas/releases/latest?per_page=1")
        == "https://github.com/Genlue/DeskCanvas/releases.atom");
Check("derive: 非 GitHub API 的自定义 URL → null（不猜测 feed 位置）",
    ReleaseAtomParser.DeriveAtomUrl("https://example.com/updates/latest.json") == null);
Check("derive: null / 空串 → null",
    ReleaseAtomParser.DeriveAtomUrl(null) == null && ReleaseAtomParser.DeriveAtomUrl("") == null);

// 2. 真实形状解析
var latest = ReleaseAtomParser.TryParse(Fixture);
Check("parse: 样本可解析", latest != null);
if (latest != null)
{
    Check("parse: 首个 entry 即最新 (tag=v3.8.0)", latest.TagName == "v3.8.0", latest.TagName);
    Check("parse: 版本 3.8.0", latest.Version == new Version(3, 8, 0), latest.Version.ToString());
    Check("parse: 标题取自 entry (非 feed 级)", latest.Title == "DeskCanvas v3.8.0", latest.Title);
    Check("parse: 发布页链接", latest.PageUrl == "https://github.com/Genlue/DeskCanvas/releases/tag/v3.8.0", latest.PageUrl);
    Check("parse: changelog 保留中文正文",
        latest.ChangelogText.Contains("失焦自动切换", StringComparison.Ordinal)
        && latest.ChangelogText.Contains("60 次/小时", StringComparison.Ordinal),
        latest.ChangelogText[..Math.Min(60, latest.ChangelogText.Length)]);
    Check("parse: 标签已剥离", !latest.ChangelogText.Contains("<p>") && !latest.ChangelogText.Contains("<strong>"));
    Check("parse: 实体已解码", !latest.ChangelogText.Contains("&lt;") && !latest.ChangelogText.Contains("&quot;"));
    Check("parse: 版本比较 (3.8.0 > 3.7.2)", latest.Version > new Version(3, 7, 2));
}

// 3. link 缺失时 id 尾段兜底 + 预发布版本号提取
const string IdFallbackFixture =
    """
    <?xml version="1.0" encoding="UTF-8"?>
    <feed xmlns="http://www.w3.org/2005/Atom">
      <entry>
        <id>tag:github.com,2008:Repository/1342473727/v3.9.0-beta.1</id>
        <updated>2026-10-16T00:00:00Z</updated>
        <title>DeskCanvas v3.9.0-beta.1</title>
      </entry>
    </feed>
    """;
var idParsed = ReleaseAtomParser.TryParse(IdFallbackFixture);
Check("id 兜底: 无 /tag/ link 时从 id 尾段取 tag", idParsed?.TagName == "v3.9.0-beta.1", idParsed?.TagName ?? "(null)");
Check("id 兜底: 预发布标签提取主版本号 (3.9.0)", idParsed?.Version == new Version(3, 9, 0), idParsed?.Version.ToString() ?? "(null)");

// 4. 坏输入全部返回 null（回退失败时保留主源错误原因，不抛异常）
Check("坏输入: 非法 XML → null", ReleaseAtomParser.TryParse("<feed><entry>") == null);
Check("坏输入: 无 entry 的 feed → null", ReleaseAtomParser.TryParse("<feed xmlns=\"http://www.w3.org/2005/Atom\"><title>x</title></feed>") == null);
Check("坏输入: 空串 → null", ReleaseAtomParser.TryParse("") == null && ReleaseAtomParser.TryParse(null) == null);

Console.WriteLine(failures.Count == 0
    ? $"\nUpdateAtomChecks ALL PASS ({passed} 项)"
    : $"\nUpdateAtomChecks FAILED: {failures.Count} 项失败 — {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;
