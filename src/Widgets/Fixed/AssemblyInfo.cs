using System.Reflection;
using FixedWidgets.Locales;
using FixedWidgets.Models;
using FixedWidgets.Views;
using FixedWidgets.Views.Settings;
using DeskCanvas.Core.Models.Attributes;

[assembly: AssemblyCompany("DeskCanvas")]
[assembly: AssemblyVersion("1.0.1")]

[assembly: WidgetInfo(typeof(AggregateView), typeof(AggregateModel), typeof(AggregateSettings), "Fixed_Aggregate_Title", "Fixed_Aggregate_Subtitle", defaultColumns: 4, defaultRows: 2)]
[assembly: Locale(typeof(Locale), "FixedWidgets", "M3 3h8v8H3V3zm10 0h8v8h-8V3zM3 13h8v8H3v-8zm10 0h8v8h-8v-8z")]
