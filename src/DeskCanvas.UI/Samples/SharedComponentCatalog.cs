using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using DeskCanvas.UI.Controls;

namespace DeskCanvas.UI.Samples;

/// <summary>Embeddable component examples; no application services or persistence required.</summary>
public sealed class SharedComponentCatalog : UserControl
{
    public SharedComponentCatalog()
    {
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://DeskCanvas.UI/"))
        {
            Source = new Uri("avares://DeskCanvas.UI/Styles/DesignTokens.axaml")
        });
        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "LabeledSlider — percentage" },
                new LabeledSlider { Minimum = 0, Maximum = 100, Step = 1, Value = 65, Unit = "%" },
                new TextBlock { Text = "LabeledSlider — editable decimal" },
                new LabeledSlider { Minimum = 0, Maximum = 3, Step = .01, Value = 1.25,
                    DisplayFormat = "0.00", ShowEditableValue = true },
                new TextBlock { Text = "LabeledSlider — disabled" },
                new LabeledSlider { Value = 50, IsEnabled = false },
                new TextBlock { Text = "Setting row — icon, subtitle and editor presenter" },
                new Setting
                {
                    Title = "Accent colour",
                    Subtitle = "Applies to menus and highlights",
                    Content = new TextBox { MinWidth = 140 }
                },
                new Setting
                {
                    Title = "Advanced row",
                    LayoutMode = SettingLayoutMode.Stacked,
                    EditorHorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new LabeledSlider { Minimum = 0, Maximum = 100, Value = 30, ShowEditableValue = true }
                },
                new TextBlock { Text = "ColorInput — RGB commit, alpha forced opaque" },
                new ColorInput { ColorFormat = ColorInputFormat.Rgb, AlphaPolicy = ColorInputAlphaPolicy.ForceOpaque, Value = "#34C759" },
                new TextBlock { Text = "ColorInput — ARGB with alpha editing" },
                new ColorInput { IsAlphaVisible = true, Value = "#803494FF" }
            }
        };
    }
}
