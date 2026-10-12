using System.Reflection;
using ControlCenter.Locales;
using ControlCenter.Models;
using ControlCenter.Views;
using ControlCenter.Views.Settings;
using DeskCanvas.Core.Models.Attributes;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.0.0")]

[assembly: WidgetInfo(
    typeof(ControlCenterView),
    typeof(ControlCenterModel),
    typeof(ControlCenterSettings),
    "ControlCenter_Title",
    "ControlCenter_Subtitle",
    defaultColumns: 4,
    defaultRows: 2,
    presetSpans: "2x2,4x2,4x4")]

[assembly: WidgetInfo(
    typeof(SingleControlView),
    typeof(SingleControlModel),
    typeof(SingleControlSettings),
    "SingleControl_Title",
    "SingleControl_Subtitle",
    defaultColumns: 1,
    defaultRows: 1,
    presetSpans: "1x1,2x1,1x2,2x2")]

[assembly: Locale(
    typeof(Locale),
    "ControlCenter",
    "M3 17v2h6v-2H3zM3 5v2h10V5H3zm10 16v-2h8v-2h-8v-2h-2v6h2zM7 9v2H3v2h4v2h2V9H7zm14 4v-2H11v2h10zm-6-4h2V7h4V5h-4V3h-2v6z")]
