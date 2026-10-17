using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Presenters;
using DeskCanvas.Locales;
using DeskCanvas.UI.Samples;
using SharedSlider = DeskCanvas.UI.Controls.LabeledSlider;
using LegacySlider = DeskCanvas.Views.Controls.LabeledSlider;
using SharedSetting = DeskCanvas.UI.Controls.Setting;
using LegacySetting = DeskCanvas.Views.Controls.Setting;
using SharedColorInput = DeskCanvas.UI.Controls.ColorInput;
using LegacyColorInput = DeskCanvas.Views.Controls.ColorInput;
using SharedLayoutPanel = DeskCanvas.UI.Controls.SettingLayoutPanel;
using SharedLayoutSlot = DeskCanvas.UI.Controls.SettingLayoutSlot;
using SharedLayoutMode = DeskCanvas.UI.Controls.SettingLayoutMode;
using SharedEffectiveLayout = DeskCanvas.UI.Controls.SettingEffectiveLayout;
using SharedColorFormat = DeskCanvas.UI.Controls.ColorInputFormat;
using SharedColorPolicy = DeskCanvas.UI.Controls.ColorInputAlphaPolicy;

AppBuilder.Configure<ProbeApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
var failures = new List<string>();
var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {message}");
    if (!condition) failures.Add(message);
}
void Flush() => Dispatcher.UIThread.RunJobs();
T Part<T>(Control slider, string name) where T : Control => slider.FindControl<T>(name)
    ?? throw new InvalidOperationException($"Missing {slider.GetType().FullName}.{name}");
void Key(Control target, RoutedEvent<KeyEventArgs> routedEvent, Key key)
{
    target.RaiseEvent(new KeyEventArgs { RoutedEvent = routedEvent, Key = key });
    Flush();
}

Check(typeof(LegacySlider).BaseType == typeof(SharedSlider), "host facade executes shared implementation");
Check(ReferenceEquals(LegacySlider.ValueProperty, SharedSlider.ValueProperty), "old/new styled properties have one identity");
foreach (var control in new SharedSlider[] { new SharedSlider(), new LegacySlider() })
{
    var name = control.GetType().FullName!;
    control.Minimum = 5;
    control.Maximum = 95;
    control.Step = 5;
    control.Unit = "%";
    control.ShowEditableValue = true;
    var window = new Window { Width = 480, Height = 120, Content = control };
    window.Show();
    Flush();
    var model = new BoundValue { Level = 27 };
    var commits = new List<double>();
    control.ValueCommitted += (_, e) => commits.Add(e.Value);
    var legacyCommits = new List<double>();
    if (control is LegacySlider legacy)
        legacy.ValueCommitted += (_, e) => legacyCommits.Add(e.Value);
    using var binding = control.Bind(SharedSlider.ValueProperty,
        new Binding(nameof(BoundValue.Level)) { Source = model, Mode = BindingMode.TwoWay });
    Flush();
    var slider = Part<Slider>(control, "SliderPart");
    var numeric = Part<NumericUpDown>(control, "NumericPart");
    var text = Part<TextBlock>(control, "ValueTextPart");
    var numericText = numeric.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");
    Check(numericText.IsVisible, $"{name}: numeric template resolves under Fluent");
    Check(control.Value == 27 && numeric.Value == 27 && text.Text == "27%", $"{name}: external off-grid load is preserved");
    model.Level = 42;
    Flush();
    Key(numeric, InputElement.KeyDownEvent, Avalonia.Input.Key.Enter);
    numeric.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
    Check(commits.Count == 0 && model.Level == 42, $"{name}: external binding loads/completion cause no persistence commit");
    numericText.Text = "63";
    Flush();
    Check(control.Value == 65 && model.Level == 65 && commits.Count == 0, $"{name}: numeric preview snaps and retains binding");
    Key(numericText, InputElement.KeyDownEvent, Avalonia.Input.Key.Enter);
    Check(commits.SequenceEqual(new[] { 65d }), $"{name}: numeric Enter immediately commits typed input");
    numeric.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
    Key(numericText, InputElement.KeyDownEvent, Avalonia.Input.Key.Enter);
    Check(commits.SequenceEqual(new[] { 65d }), $"{name}: numeric Enter plus blur commits exactly once");
    Key(slider, InputElement.KeyDownEvent, Avalonia.Input.Key.Right);
    Check(control.Value == 70 && commits.Count == 1, $"{name}: arrow key previews one configured step");
    Key(slider, InputElement.KeyUpEvent, Avalonia.Input.Key.Right);
    Key(slider, InputElement.KeyUpEvent, Avalonia.Input.Key.Right);
    Check(commits.SequenceEqual(new[] { 65d, 70d }), $"{name}: keyboard completion commits exactly once");
    slider.Value = 85;
    slider.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
    slider.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
    Check(commits.SequenceEqual(new[] { 65d, 70d, 85d }), $"{name}: drag completion commits exactly once");
    model.Level = 33;
    Flush();
    Key(slider, InputElement.KeyUpEvent, Avalonia.Input.Key.Right);
    Check(control.Value == 33 && commits.Count == 3, $"{name}: binding survives user edits and resets baseline on reload");
    AutomationProperties.SetName(control, "Opacity");
    Check(AutomationProperties.GetName(slider) == "Opacity" && AutomationProperties.GetName(numeric) == "Opacity",
        $"{name}: accessible name reaches both editors");
    if (control is LegacySlider)
        Check(legacyCommits.SequenceEqual(commits), "host event bridge preserves payload and exactly-once semantics");
    control.Value = double.NaN;
    Check(text.Text == "5%", $"{name}: non-finite load displays safe range start");
    Key(numeric, InputElement.KeyDownEvent, Avalonia.Input.Key.Enter);
    Check(commits.Count == 3, $"{name}: unchanged non-finite external load never commits");
    numeric.Value = 50;
    Key(numeric, InputElement.KeyDownEvent, Avalonia.Input.Key.Enter);
    Check(control.Value == 50 && commits.Count == 4, $"{name}: numeric editor recovers after non-finite external load");
    window.Close();
}

// ---- Setting rows: shared template + host facade (W3 contract) ---------------------------
Check(typeof(LegacySetting).BaseType == typeof(SharedSetting), "Setting host facade executes shared implementation");
Check(ReferenceEquals(LegacySetting.TitleProperty, SharedSetting.TitleProperty), "Setting old/new styled properties have one identity");
foreach (var setting in new SharedSetting[]
{
    new SharedSetting { Title = "Row", Content = new TextBox() },
    new LegacySetting { Title = "Row", AdminRequired = true, RestartRequired = true, Content = new TextBox() },
})
{
    var name = setting.GetType().FullName!;
    var rowWindow = new Window { Width = 520, Height = 170, Content = setting };
    rowWindow.Show();
    Flush();
    var layoutPanel = setting.GetVisualDescendants().OfType<SharedLayoutPanel>().SingleOrDefault();
    Check(layoutPanel is not null, $"{name}: template builds exactly one shared SettingLayoutPanel");
    var editor = setting.GetVisualDescendants().OfType<ContentPresenter>()
        .FirstOrDefault(p => SharedLayoutPanel.GetSlot(p) == SharedLayoutSlot.Editor);
    var label = setting.GetVisualDescendants().OfType<StackPanel>()
        .FirstOrDefault(s => SharedLayoutPanel.GetSlot(s) == SharedLayoutSlot.Label);
    Check(editor is not null && label is not null, $"{name}: editor presenter and label group carry the shared slots");
    Check(!string.IsNullOrEmpty(setting.AdminRequiredText) && !string.IsNullOrEmpty(setting.RestartRequiredText),
        $"{name}: requirement hint texts are present");
    setting.LayoutMode = SharedLayoutMode.Stacked;
    Flush();
    Check(setting.EffectiveLayout == SharedEffectiveLayout.Stacked,
        $"{name}: Stacked request resolves through the shared panel");
    rowWindow.Close();
}
var legacyRow = new LegacySetting { Title = "Hints", AdminRequired = true, RestartRequired = true };
var hintWindow = new Window { Width = 520, Height = 170, Content = legacyRow };
hintWindow.Show();
Flush();
var hintTexts = legacyRow.GetVisualDescendants().OfType<TextBlock>()
    .Where(t => t.IsVisible).Select(t => t.Text).ToList();
Check(hintTexts.Contains(Locale.Settings_AdminRequired) && hintTexts.Contains(Locale.Settings_RestartRequired),
    "host Setting renders the localized requirement hints inside the shared template");
hintWindow.Close();

// ---- ColorInput: shared draft/commit engine + host facade (W6 contract) ------------------
Check(typeof(LegacyColorInput).BaseType == typeof(SharedColorInput), "ColorInput host facade executes shared implementation");
Check(ReferenceEquals(LegacyColorInput.ValueProperty, SharedColorInput.ValueProperty), "ColorInput old/new styled properties have one identity");
var localeColorInput = new LegacyColorInput();
Check(localeColorInput.InvalidFormatText == Locale.Settings_ColorInput_InvalidFormat
      && !string.IsNullOrEmpty(localeColorInput.InvalidFormatText),
    "host ColorInput feeds the Locale invalid-format copy");
foreach (var control in new SharedColorInput[] { new SharedColorInput(), new LegacyColorInput() })
{
    var name = control.GetType().FullName!;
    var colorWindow = new Window { Width = 460, Height = 130, Content = control };
    colorWindow.Show();
    Flush();
    var commits = new List<string?>();
    control.ValueCommitted += (_, e) => commits.Add(e.Value);
    var legacyCommits = new List<string?>();
    if (control is LegacyColorInput legacyInput)
        legacyInput.ValueCommitted += (_, e) => legacyCommits.Add(e.Value);
    var model = new BoundText { Colour = "#FF34C759" };
    using var binding = control.Bind(SharedColorInput.ValueProperty,
        new Binding(nameof(BoundText.Colour)) { Source = model, Mode = BindingMode.TwoWay });
    Flush();
    var picker = Part<ColorPicker>(control, "PickerPart");
    var hex = Part<TextBox>(control, "HexBoxPart");
    Check(control.Value == "#FF34C759" && hex.Text == "#FF34C759", $"{name}: external ARGB load mirrors picker and hex");
    Check(commits.Count == 0 && model.Colour == "#FF34C759", $"{name}: external load never commits");
    hex.Text = "#00AAFF";
    Key(hex, InputElement.KeyDownEvent, Avalonia.Input.Key.Enter);
    Check(commits.SequenceEqual(new[] { "#FF00AAFF" }) && model.Colour == "#FF00AAFF",
        $"{name}: hex Enter commits the composed ARGB once");
    Key(hex, InputElement.KeyDownEvent, Avalonia.Input.Key.Enter);
    Check(commits.Count == 1, $"{name}: repeat Enter without edits stays silent");
    hex.Text = "not-a-color";
    Key(hex, InputElement.KeyDownEvent, Avalonia.Input.Key.Enter);
    Check(control.HasValidationError && !string.IsNullOrEmpty(control.ValidationMessage)
          && Part<TextBlock>(control, "ErrorTextPart").IsVisible,
        $"{name}: invalid draft shows an inline error");
    Check(model.Colour == "#FF00AAFF" && commits.Count == 1, $"{name}: invalid draft never touches the model");
    picker.Color = Avalonia.Media.Color.FromRgb(0x10, 0x20, 0x30);
    Flush();
    Check(commits.SequenceEqual(new[] { "#FF00AAFF", "#FF102030" }) && control.Value == "#FF102030",
        $"{name}: picker change goes through the same commit path and re-renders hex");
    if (control is LegacyColorInput)
        Check(legacyCommits.SequenceEqual(commits), "host event bridge preserves ColorInput payloads");
    control.IsReadOnly = true;
    Check(hex.IsReadOnly && !picker.IsEnabled, $"{name}: read-only keeps the value visible but not editable");
    colorWindow.Close();
}

// Preserve keeps the previous alpha; ForceOpaque and AllowEmpty are explicit contracts.
var preserveWindow = new Window { Width = 460, Height = 130, Content = new SharedColorInput() };
preserveWindow.Show();
Flush();
var preserve = (SharedColorInput)preserveWindow.Content!;
preserve.Value = "#80FF0000";
string? preserveResult = null;
preserve.ValueCommitted += (_, e) => preserveResult = e.Value;
Part<TextBox>(preserve, "HexBoxPart").Text = "#00FF00";
Key(Part<TextBox>(preserve, "HexBoxPart"), InputElement.KeyDownEvent, Avalonia.Input.Key.Enter);
Check(preserveResult == "#8000FF00", "Preserve keeps the previous alpha on an RGB edit");
preserveWindow.Close();

var opaque = new SharedColorInput { AlphaPolicy = SharedColorPolicy.ForceOpaque, Value = "#803494FF" };
var opaqueWindow = new Window { Width = 460, Height = 130, Content = opaque };
opaqueWindow.Show();
Flush();
string? opaqueResult = null;
opaque.ValueCommitted += (_, e) => opaqueResult = e.Value;
Part<ColorPicker>(opaque, "PickerPart").Color = Avalonia.Media.Color.FromRgb(0x11, 0x22, 0x33);
Flush();
Check(opaqueResult == "#FF112233", "ForceOpaque writes FF regardless of the previous alpha");
opaqueWindow.Close();

var clearable = new SharedColorInput { AllowEmpty = true };
clearable.Value = "#FF102030";
var clearWindow = new Window { Width = 460, Height = 130, Content = clearable };
clearWindow.Show();
Flush();
string? clearedResult = null;
clearable.ValueCommitted += (_, e) => clearedResult = e.Value;
var clearHex = Part<TextBox>(clearable, "HexBoxPart");
clearHex.Text = string.Empty;
Key(clearHex, InputElement.KeyDownEvent, Avalonia.Input.Key.Enter);
Check(clearedResult == null && clearable.Value == null, "AllowEmpty clears to the null model state with one commit");
clearWindow.Close();

var oldUri = new Uri("avares://DeskCanvas/Styles/DesignTokens.axaml");
var newUri = new Uri("avares://DeskCanvas.UI/Styles/DesignTokens.axaml");
var oldTokens = new ResourceInclude(oldUri) { Source = oldUri };
var newTokens = new ResourceInclude(newUri) { Source = newUri };
// Compiled AXAML is resolved through its compiled loader, not the raw asset table.
Check(oldTokens.Loaded.MergedDictionaries.Count == 1 && newTokens.Loaded.Count > 0,
    "old/new design-token URIs load compiled resource dictionaries");
foreach (var key in newTokens.Loaded.Keys)
{
    var oldFound = oldTokens.TryGetResource(key, ThemeVariant.Default, out var oldValue);
    var newFound = newTokens.TryGetResource(key, ThemeVariant.Default, out var newValue);
    Check(oldFound && newFound && Equals(oldValue, newValue), $"resource bridge retains token {key}");
}
var oldControlUri = new Uri("avares://DeskCanvas/Views/Controls/LabeledSlider.axaml");
var loaded = (LegacySlider)AvaloniaXamlLoader.Load(oldControlUri);
loaded.Value = 39;
Check(loaded.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "ValueTextPart").Text == "39",
    "old control URI loads functional facade with shared visual tree");
var oldSettingUri = new Uri("avares://DeskCanvas/Views/Controls/Setting.axaml");
Check(AvaloniaXamlLoader.Load(oldSettingUri) is LegacySetting, "old Setting URI loads the host facade");
var oldColorUri = new Uri("avares://DeskCanvas/Views/Controls/ColorInput.axaml");
Check(AvaloniaXamlLoader.Load(oldColorUri) is LegacyColorInput, "old ColorInput URI loads the host facade");
var catalog = new SharedComponentCatalog();
Check(catalog.GetLogicalDescendants().OfType<SharedSlider>().Count() == 4,
    "catalog renders percentage, editable, disabled and stacked-row slider examples");
Check(catalog.GetLogicalDescendants().OfType<SharedSetting>().Count() == 2, "catalog renders shared setting rows");
Check(catalog.GetLogicalDescendants().OfType<SharedColorInput>().Count() == 2, "catalog renders shared colour inputs");

// ---- Unified Widget Controls & Tokens (P10 contract) -------------------------------------
Check(Application.Current!.TryGetResource("CardSubtleCornerRadius", ThemeVariant.Default, out var cscr) && Equals(cscr, new CornerRadius(11)), "CardSubtleCornerRadius token resolves 11");
Check(Application.Current!.TryGetResource("PillHeight", ThemeVariant.Default, out var ph) && Equals(ph, 26.0), "PillHeight token resolves 26.0");
Check(Application.Current!.TryGetResource("CardSubtleFill", ThemeVariant.Default, out var csf) && csf is Avalonia.Media.IBrush, "CardSubtleFill resolves as brush in Default theme");
Check(Application.Current!.TryGetResource("CardSubtleFill", ThemeVariant.Dark, out var csfd) && csfd is Avalonia.Media.IBrush, "CardSubtleFill resolves as brush in Dark theme");
Check(Application.Current!.TryGetResource("PillBackground", ThemeVariant.Default, out var pb) && pb is Avalonia.Media.IBrush, "PillBackground resolves as brush in Default theme");
Check(Application.Current!.TryGetResource("BadgeBackground", ThemeVariant.Default, out var bb) && bb is Avalonia.Media.IBrush, "BadgeBackground resolves as brush in Default theme");

var testWindow = new Window { Width = 400, Height = 400 };
var testPanel = new StackPanel();
testWindow.Content = testPanel;

var pillBtn = new Button { Classes = { "dc-pill" } };
var capsuleBtn = new Button { Classes = { "dc-pill", "capsule" } };
var toolbarBtn = new Button { Classes = { "dc-toolbar-button" } };
var iconBtn = new Button { Classes = { "dc-icon-button" } };
var cardBox = new Border { Classes = { "dc-card-box" } };
var badge = new Border { Classes = { "dc-badge" } };

testPanel.Children.Add(pillBtn);
testPanel.Children.Add(capsuleBtn);
testPanel.Children.Add(toolbarBtn);
testPanel.Children.Add(iconBtn);
testPanel.Children.Add(cardBox);
testPanel.Children.Add(badge);

testWindow.Show();
Flush();

Check(pillBtn.Height == 26 && pillBtn.CornerRadius == new CornerRadius(8), "dc-pill style applies height 26 and radius 8");
Check(capsuleBtn.CornerRadius == new CornerRadius(999), "dc-pill.capsule applies radius 999");
Check(toolbarBtn.CornerRadius == new CornerRadius(6), "dc-toolbar-button applies radius 6");
Check(iconBtn.Width == 28 && iconBtn.Height == 28 && iconBtn.CornerRadius == new CornerRadius(14), "dc-icon-button applies 28x28 circle");
Check(cardBox.CornerRadius == new CornerRadius(11) && cardBox.Padding == new Thickness(12, 10), "dc-card-box applies radius 11 and padding 12,10");
Check(badge.CornerRadius == new CornerRadius(6) && badge.Padding == new Thickness(7, 2), "dc-badge applies radius 6 and padding 7,2");

testWindow.Close();

// ---- 1280x800 display constraint: every widget popup window Height <= 560 DIP -----------
// The count is a canary for the glob itself (a moved folder or a renamed pattern would
// otherwise pass silently with zero files checked). It is deliberately a lower bound: the
// widget set changes with product decisions (Fortune/Wheel were removed), and a hard-coded
// total turns every such change into a red gate for no reason.
var repoRoot = FindRepoRoot();
var popupFiles = Directory.GetFiles(Path.Combine(repoRoot, "src", "Widgets"), "*Popup*.axaml", SearchOption.AllDirectories)
    .Concat(Directory.GetFiles(Path.Combine(repoRoot, "src", "Widgets"), "*PanelWindow*.axaml", SearchOption.AllDirectories))
    .ToList();

Check(popupFiles.Count >= 13, $"found the widget popup windows (count={popupFiles.Count}, expected >= 13)");
foreach (var popupPath in popupFiles)
{
    var pName = Path.GetFileName(popupPath);
    var pContent = File.ReadAllText(popupPath);
    var heightMatch = System.Text.RegularExpressions.Regex.Match(pContent, @"Height=""(\d+)""");
    Check(heightMatch.Success, $"{pName} specifies explicit Height in XAML");
    if (heightMatch.Success)
    {
        var height = int.Parse(heightMatch.Groups[1].Value);
        Check(height <= 560, $"{pName} height {height} <= 560 DIP (1280x800 display constraint)");
    }
}

Console.WriteLine(failures.Count == 0 ? $"UiSharedAvaloniaChecks {checks}/{checks} PASS" : $"FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;

static string FindRepoRoot()
{
    var current = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(current))
    {
        if (File.Exists(Path.Combine(current, "DeskCanvas.sln")) || Directory.Exists(Path.Combine(current, "src")))
            return current;
        current = Directory.GetParent(current)?.FullName;
    }
    return Directory.GetCurrentDirectory();
}

sealed class ProbeApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        // ColorInput embeds a ColorPicker; its control theme ships in the ColorPicker package.
        var colorPickerStyles = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml");
        Styles.Add(new StyleInclude(colorPickerStyles) { Source = colorPickerStyles });
        var controlsStyles = new Uri("avares://DeskCanvas.UI/Styles/Controls.axaml");
        Styles.Add(new StyleInclude(controlsStyles) { Source = controlsStyles });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://DeskCanvas.UI/"))
        { Source = new Uri("avares://DeskCanvas.UI/Styles/DesignTokens.axaml") });
    }
}
sealed class BoundText : INotifyPropertyChanged
{
    private string? colour;
    public string? Colour
    {
        get => colour;
        set { if (colour == value) return; colour = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Colour))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
sealed class BoundValue : INotifyPropertyChanged
{
    private double level;
    public double Level
    {
        get => level;
        set { if (level == value) return; level = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Level))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
