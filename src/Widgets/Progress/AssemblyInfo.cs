using System.Reflection;
using Progress.Locales;
using Progress.Models;
using Progress.Views;
using Progress.Views.Settings;
using DeskCanvas.Core.Models.Attributes;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.0.1")]

[assembly: WidgetInfo(typeof(ProgressView), typeof(ProgressModel), typeof(ProgressSettings), "Progress_Title", "Progress_Subtitle", defaultColumns: 2, defaultRows: 2, presetSpans: "2x2,4x2,4x4")]
[assembly: Locale(typeof(Locale), "Progress", "M4 4h4v4H4V4zm6 0h4v4h-4V4zm6 0h4v4h-4V4zM4 10h4v4H4v-4zm6 0h4v4h-4v-4zm6 0h4v4h-4v-4zM4 16h4v4H4v-4zm6 0h4v4h-4v-4zm6 0h4v4h-4v-4z")]
