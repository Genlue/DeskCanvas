using System.Reflection;
using DeskCanvas.Core.Models.Attributes;
using Wheel.Locales;
using Wheel.Models;
using Wheel.Views;
using Wheel.Views.Settings;

[assembly: AssemblyCompany("DeskCanvas")]
[assembly: AssemblyVersion("1.0.0")]

[assembly: WidgetInfo(typeof(WheelView), typeof(WheelModel), typeof(WheelSettings), "Wheel_Title", "Wheel_Subtitle", defaultColumns: 2, defaultRows: 2, presetSpans: "1x1,2x2,4x2")]
[assembly: Locale(typeof(Locale), "Wheel", "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8zm1-13h-2v6l5.25 3.15.75-1.23-4.5-2.67z")]
