using System.Text.Json;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Services;

var failures = new List<string>();
var passed = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
    if (ok) passed++; else failures.Add(name);
}

var root = Path.Combine(Path.GetTempPath(), "DeskCanvas-IdentityStorageChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", root);
try
{
    var legacy = new WidgetLayout("Clock", "Digital", 10, 20, 200, 120, null);
    File.WriteAllText(Path.Combine(root, "layout.json"), JsonSerializer.Serialize(new List<WidgetLayout> { legacy }));

    var provider = new LayoutProvider();
    var first = provider.Get().AllWidgets.Single();
    Check("legacy layout receives a stable InstanceId", first.HasStableIdentity);
    var persisted = JsonSerializer.Deserialize<ScreensLayout>(File.ReadAllText(Path.Combine(root, "layout.json")))!;
    var persistedId = persisted.AllWidgets.Single().InstanceId;
    Check("migration is persisted", persistedId == first.InstanceId);

    var secondProvider = new LayoutProvider();
    var second = secondProvider.Get().AllWidgets.Single();
    Check("reloading keeps the same InstanceId", second.InstanceId == first.InstanceId);
    Check("geometry changes still match by InstanceId", first.SameWidgetAs(first with { X = 999, Y = 777 }));

    var dataPath = Path.Combine(root, "failure.json");
    File.WriteAllText(dataPath, "\"old\"");
    var failing = new ThrowingParser(dataPath);
    _ = failing.Get();
    var changed = 0;
    failing.DataChanged += (_, _, _) => changed++;
    try { failing.Save("new"); } catch (InvalidOperationException) { }
    Check("failed serialization does not publish DataChanged", changed == 0);
    Check("failed serialization keeps the cached value", failing.Get() == "old");
    Check("failed serialization keeps the original file", File.ReadAllText(dataPath) == "\"old\"");
}
finally
{
    Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", null);
    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine(failures.Count == 0
    ? $"\nIdentityStorageChecks ALL PASS ({passed} 项)"
    : $"\nIdentityStorageChecks FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;

sealed class ThrowingParser(string path) : JsonParser<string>(path)
{
    protected override string SerializeForFile(string value) => throw new InvalidOperationException("injected save failure");
}
