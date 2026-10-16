using System.Reflection;
using DeskCanvas.Core.Models.Attributes;
using Fortune.Locales;
using Fortune.Models;
using Fortune.Views;
using Fortune.Views.Settings;

[assembly: AssemblyCompany("DeskCanvas")]
[assembly: AssemblyVersion("1.0.0")]

[assembly: WidgetInfo(typeof(FortuneView), typeof(FortuneModel), typeof(FortuneSettings), "Fortune_Title", "Fortune_Subtitle", defaultColumns: 2, defaultRows: 2, presetSpans: "1x1,2x2,4x2")]
[assembly: Locale(typeof(Locale), "Fortune", "M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-7 3c1.66 0 3 1.34 3 3s-1.34 3-3 3-3-1.34-3-3 1.34-3 3-3zm6 12H6v-1.4c0-2 4-3.1 6-3.1s6 1.1 6 3.1V18z")]
