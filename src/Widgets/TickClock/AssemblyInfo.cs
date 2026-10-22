using System.Reflection;
using DeskCanvas.Core.Models.Attributes;
using TickClock.Locales;
using TickClock.Models;
using TickClock.Views;
using TickClock.Views.Settings;

[assembly: AssemblyCompany("DeskCanvas")]
[assembly: AssemblyVersion("1.0.0")]

[assembly: WidgetInfo(typeof(TickClockView), typeof(TickClockModel), typeof(TickClockSettings),
    "TickClock_Title", "TickClock_Subtitle",
    defaultColumns: 2, defaultRows: 2, presetSpans: "2x2,4x4",
    category: "WidgetCategory_ClockCalendar")]
[assembly: Locale(typeof(Locale), "TickClock",
    "M24,3.5 C35.32,3.5 44.5,12.68 44.5,24 C44.5,35.32 35.32,44.5 24,44.5 C12.68,44.5 3.5,35.32 3.5,24 C3.5,12.68 12.68,3.5 24,3.5 Z M24,7 C14.61,7 7,14.61 7,24 C7,33.39 14.61,41 24,41 C33.39,41 41,33.39 41,24 C41,14.61 33.39,7 24,7 Z M23,12 L25,12 L25,23 L32,23 L32,25 L23,25 Z")]
