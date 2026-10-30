using System.Reflection;
using DeskCanvas.Core.Models.Attributes;
using AudioVisualizer.Locales;
using AudioVisualizer.Models;
using AudioVisualizer.Views;
using AudioVisualizer.Views.Settings;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.0.0")]

[assembly: WidgetInfo(
    typeof(AudioVisualizerView),
    typeof(AudioVisualizerModel),
    typeof(AudioVisualizerSettings),
    "AudioVisualizer_Title",
    "AudioVisualizer_Subtitle",
    defaultColumns: 4,
    defaultRows: 2,
    presetSpans: "2x1,2x2,4x2,4x4,6x2,6x3,8x2",
    category: "WidgetCategory_Lifestyle")]
[assembly: Locale(
    typeof(Locale),
    "AudioVisualizer",
    "M3 10v4M6 7v10M9 4v16M12 2v20M15 5v14M18 7v10M21 10v4")]
