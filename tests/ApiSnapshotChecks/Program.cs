// ApiSnapshotChecks: records the public API surface of the four SDK assemblies
// (Contracts, Runtime, Core, UI) as a stable line set and compares it against a
// committed baseline, so any added / removed / signature-changed public member
// becomes an explicit release-gate decision instead of a silent binary break.
//
// Modes:
//   Verify (default)  — compares the freshly built snapshot with Baseline.txt.
//                       Any difference is reported and the process exits 1.
//   Record            — set DESKCANVAS_API_SNAPSHOT_RECORD=1 (or delete the
//                       baseline) to rewrite Baseline.txt, print RECORDED and
//                       exit 0. Baseline regeneration is an integration decision.
//
// Only public members are captured. Signatures are normalized to
// assembly-qualified-free type names; compiler-generated members (EqualityContract,
// <Clone$>, backing fields, accessor pairs) are filtered out.

using System.Reflection;

var failures = new List<string>();
var passed = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(ok || detail == null ? "" : $" — {detail}")}");
    if (ok) passed++; else failures.Add(name);
}

// The four SDK assemblies, resolved via anchor types so the project references
// alone guarantee they are loaded (no path probing). Order matters: the snapshot
// is the per-assembly sorted output concatenated in this array's order, and the
// round-trip check asserts global ordinal sortedness — so the array must follow
// the assemblies' ordinal name order (Contracts < Core < Runtime < UI).
var contracts = typeof(DeskCanvas.Contracts.WidgetInstanceId).Assembly;
var core = typeof(DeskCanvas.Core.Const).Assembly;
var runtime = typeof(DeskCanvas.Runtime.WidgetSessionRegistry).Assembly;
var ui = typeof(DeskCanvas.UI.Controls.GlassSurfaceBase).Assembly;
var assemblies = new[] { contracts, core, runtime, ui };

// Build the full snapshot once; every assertion works on this line set.
var current = assemblies.SelectMany(ApiSnapshot.Snapshot).ToList();

Check("Contracts snapshot is non-empty", current.Any(l => l.StartsWith("type DeskCanvas.Contracts.")));
Check("Runtime snapshot is non-empty", current.Any(l => l.StartsWith("type DeskCanvas.Runtime.")));
Check("Core snapshot is non-empty", current.Any(l => l.StartsWith("type DeskCanvas.Core.")));
Check("UI snapshot is non-empty", current.Any(l => l.StartsWith("type DeskCanvas.UI.")));

// Key SDK contract types must remain publicly visible (P1/P2/P5/P7/P8 surface).
// fullName may be short (prefixed with the assembly name) or already assembly-qualified.
bool HasType(Assembly assembly, string fullName)
{
    var name = assembly.GetName().Name!;
    var line = fullName.StartsWith(name + ".", StringComparison.Ordinal)
        ? $"type {fullName}"
        : $"type {name}.{fullName}";
    return current.Contains(line);
}

Check("Contracts keeps WidgetInstanceId", HasType(contracts, "WidgetInstanceId"));
Check("Contracts keeps IWidgetSession", HasType(contracts, "IWidgetSession"));
Check("Contracts keeps ActivitySnapshot", HasType(contracts, "ActivitySnapshot"));
Check("Contracts keeps ResourcePressureEvaluator", HasType(contracts, "ResourcePressureEvaluator"));
Check("Contracts keeps IResourceLedger", HasType(contracts, "IResourceLedger"));
Check("Contracts keeps DraftSettingsStore<T>", HasType(contracts, "DraftSettingsStore`1"));
Check("Runtime keeps ResourceLedger", HasType(runtime, "ResourceLedger"));
Check("Runtime keeps SharedTimeZone", HasType(runtime, "SharedTimeZone"));
Check("Runtime keeps WidgetSession and WidgetSessionRegistry",
    HasType(runtime, "WidgetSession") && HasType(runtime, "WidgetSessionRegistry"));
Check("Runtime keeps SharedDataProvider<TKey,TValue> and PersistentSettingsSession<T>",
    HasType(runtime, "SharedDataProvider`2") && HasType(runtime, "PersistentSettingsSession`1"));
Check("Core keeps WidgetLayout and LayoutProvider",
    HasType(core, "DeskCanvas.Core.Models.WidgetLayout") && HasType(core, "DeskCanvas.Core.Services.LayoutProvider"));
Check("Core keeps AssemblyProvider and IAssemblyProvider",
    HasType(core, "DeskCanvas.Core.Services.AssemblyProvider") && HasType(core, "DeskCanvas.Core.Interfaces.IAssemblyProvider"));
Check("Core keeps WidgetInfoAttribute (component registration contract)",
    HasType(core, "DeskCanvas.Core.Models.Attributes.WidgetInfoAttribute"));
Check("Core keeps legacy widget interfaces (IWidgetSelfRefreshing/IWidgetSuspendable/IStackWidget)",
    HasType(core, "DeskCanvas.Core.Interfaces.IWidgetSelfRefreshing") &&
    HasType(core, "DeskCanvas.Core.Interfaces.IWidgetSuspendable") &&
    HasType(core, "DeskCanvas.Core.Interfaces.IStackWidget"));
Check("Core keeps ScreensLayout v1/v2/v3 migration model",
    HasType(core, "DeskCanvas.Core.Models.ScreensLayout") && HasType(core, "DeskCanvas.Core.Models.ScreenLayout"));
Check("UI keeps glass/panel facade types",
    HasType(ui, "DeskCanvas.UI.Controls.GlassSurfaceBase") &&
    HasType(ui, "DeskCanvas.UI.Controls.SecondaryPanelShell") &&
    HasType(ui, "DeskCanvas.UI.Controls.PanelDragBehavior"));
Check("UI keeps shared control surface (LabeledSlider/ColorInput/Setting)",
    HasType(ui, "DeskCanvas.UI.Controls.LabeledSlider") &&
    HasType(ui, "DeskCanvas.UI.Controls.ColorInput") &&
    HasType(ui, "DeskCanvas.UI.Controls.Setting"));

// The snapshot must be deterministic: rebuilding it in the same process yields
// the exact same line set, and the "type" header lines must appear in ascending
// ordinal order (the hierarchical emission invariant the baseline relies on).
var replay = assemblies.SelectMany(ApiSnapshot.Snapshot).ToList();
var typeLines = current.Where(l => l.StartsWith("type ", StringComparison.Ordinal)).ToList();
var orderedTypeLines = typeLines.OrderBy(l => l, StringComparer.Ordinal).ToList();
var badPair = typeLines.Zip(orderedTypeLines, (a, b) => (a, b)).FirstOrDefault(p => p.a != p.b);
var detail = !current.SequenceEqual(replay)
    ? $"recomputed snapshot differs from the first one (first diff: '{current.Zip(replay, (a, b) => (a, b)).FirstOrDefault(p => p.a != p.b).a}')"
    : badPair.a != badPair.b
        ? $"type header lines are not in ascending order (found '{badPair.a}' where '{badPair.b}' was expected)"
        : null;
Check("snapshot round-trip is stable in-process",
    detail == null,
    detail ?? "stable");

// Baseline location: the directory holding this csproj, found by walking up from
// the binary output (bin/.../net8.0 -> .../ApiSnapshotChecks).
var projectDir = ApiSnapshot.FindProjectDir();
Check("baseline directory located", projectDir != null);
if (projectDir == null)
{
    Console.WriteLine("\nApiSnapshotChecks FAILED: could not locate the ApiSnapshotChecks project directory.");
    return 1;
}
var baselinePath = Path.Combine(projectDir, "Baseline.txt");

var recordMode = Environment.GetEnvironmentVariable("DESKCANVAS_API_SNAPSHOT_RECORD") == "1";
if (recordMode || !File.Exists(baselinePath))
{
    File.WriteAllLines(baselinePath, current);
    Console.WriteLine(recordMode
        ? $"RECORDED baseline recorded: {current.Count} lines -> {baselinePath}"
        : $"RECORDED baseline was missing, recorded {current.Count} lines -> {baselinePath}");
    Console.WriteLine(recordMode
        ? "Integration step: review the baseline diff and commit it together with the change."
        : "Warning: the baseline was absent; a verify-only gate will start from this new baseline.");
    return failures.Count == 0 ? 0 : 1;
}

// Verify mode: line-level diff against the committed baseline.
var baseline = File.ReadAllLines(baselinePath)
    .Where(l => l.Length > 0)
    .ToList();
Check("baseline is non-empty", baseline.Count > 0);

var currentSet = current.ToHashSet();
var baselineSet = baseline.ToHashSet();
var added = current.Where(l => !baselineSet.Contains(l)).ToList();
var removed = baseline.Where(l => !currentSet.Contains(l)).ToList();

Check("no public API additions vs baseline", added.Count == 0,
    string.Join("; ", added.Take(10)) + (added.Count > 10 ? $" (+{added.Count - 10} more)" : ""));
Check("no public API removals/signature changes vs baseline", removed.Count == 0,
    string.Join("; ", removed.Take(10)) + (removed.Count > 10 ? $" (+{removed.Count - 10} more)" : ""));

Console.WriteLine(failures.Count == 0
    ? $"\nApiSnapshotChecks ALL PASS ({passed} 项, {current.Count} snapshot lines)"
    : $"\nApiSnapshotChecks FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;

/// <summary>Normalizes and serializes the public API surface of one assembly.</summary>
static class ApiSnapshot
{
    public static List<string> Snapshot(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        var lines = new List<string>();
        const BindingFlags Public = BindingFlags.Static | BindingFlags.Instance |
                                    BindingFlags.Public | BindingFlags.DeclaredOnly;
        foreach (var type in types
                     .Where(t => !IsCompilerGeneratedType(t))
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add($"type {type.FullName}");

            if (type.IsEnum)
            {
                foreach (var name in Enum.GetNames(type).OrderBy(n => n, StringComparer.Ordinal))
                    lines.Add($"enumvalue {type.FullName} {name}");
                continue;
            }

            foreach (var method in type.GetMethods(Public)
                         .Where(m => !IsCompilerGenerated(m.Name))
                         .Where(m => !m.IsSpecialName || m.Name.StartsWith("op_"))
                         .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var parameters = string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType)));
                lines.Add($"method {type.FullName} {method.Name}({parameters}) -> {TypeName(method.ReturnType)}");
            }

            foreach (var property in type.GetProperties(Public)
                         .Where(p => !IsCompilerGenerated(p.Name))
                         .OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                lines.Add(property.GetIndexParameters().Length > 0
                    ? $"indexer {type.FullName} [{string.Join(",", property.GetIndexParameters().Select(p => TypeName(p.ParameterType)))}] -> {TypeName(property.PropertyType)}"
                    : $"property {type.FullName} {property.Name} -> {TypeName(property.PropertyType)}");
            }

            foreach (var field in type.GetFields(Public)
                         .Where(f => !IsCompilerGenerated(f.Name))
                         .OrderBy(f => f.Name, StringComparer.Ordinal))
                lines.Add($"field {type.FullName} {field.Name} -> {TypeName(field.FieldType)}");

            foreach (var @event in type.GetEvents(Public)
                         .Where(e => !IsCompilerGenerated(e.Name))
                         .OrderBy(e => e.Name, StringComparer.Ordinal))
                lines.Add($"event {type.FullName} {@event.Name} -> {TypeName(@event.EventHandlerType!)}");
        }

        // Hierarchical emission order (types ordinal, members ordinal within their type)
        // is the deterministic canonical order. A flat line sort here would interleave
        // "type X" lines (t…) behind "enumvalue/field/method" lines (e/f/m…) and break
        // global segment ordering across assemblies.
        return lines;
    }

    /// <summary>Filters compiler-generated member names (get_/set_ accessors are
    /// represented by property lines; EqualityContract is protected and never public).</summary>
    private static bool IsCompilerGenerated(string name) =>
        name.Contains('<') || name.Contains('>') || name == "EqualityContract";

    /// <summary>Filters compiler-generated types: the Avalonia XAML compiler leaves
    /// public artifacts under CompiledAvaloniaXaml.* (e.g. !AvaloniaResources) that are
    /// build infrastructure, not part of this repository's API surface.</summary>
    private static bool IsCompilerGeneratedType(Type type) =>
        type.FullName != null &&
        (type.FullName.StartsWith("CompiledAvaloniaXaml", StringComparison.Ordinal) ||
         IsCompilerGenerated(type.Name));

    /// <summary>Assembly-qualified-free type name: namespace + stripped-arity name,
    /// recursing into generic arguments, arrays and by-ref wrappers.</summary>
    private static string TypeName(Type type)
    {
        if (type.IsByRef) return TypeName(type.GetElementType()!);
        if (type.IsPointer) return TypeName(type.GetElementType()!) + "*";
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";

        if (type.IsGenericParameter) return type.Name;

        var name = string.IsNullOrEmpty(type.Namespace) ? type.Name : $"{type.Namespace}.{type.Name}";
        if (type.IsGenericType)
        {
            var backtick = name.IndexOf('`');
            if (backtick >= 0) name = name[..backtick];
            return $"{name}<{string.Join(",", type.GetGenericArguments().Select(TypeName))}>";
        }

        return name;
    }

    /// <summary>Walks up from the binary output until the ApiSnapshotChecks project
    /// directory (the one containing this csproj) is found.</summary>
    public static string? FindProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.Name == "ApiSnapshotChecks" && File.Exists(Path.Combine(dir.FullName, "ApiSnapshotChecks.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        // Fallback for exotic layouts: relative to the current working directory.
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (dir.Name == "ApiSnapshotChecks" && File.Exists(Path.Combine(dir.FullName, "ApiSnapshotChecks.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
