using System.Reflection;
using DeskCanvas.Core.Models.Attributes;
using Timetable.Locales;
using Timetable.Models;
using Timetable.Views;
using Timetable.Views.Settings;
using TimetableView = Timetable.Views.Timetable;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.1.0")]

[assembly: WidgetInfo(typeof(TimetableView), typeof(TimetableModel), typeof(TimetableSettings), "Timetable_Title", "Timetable_Subtitle", presetSpans: "1x1,2x2,4x2,4x4")]
[assembly: Locale(typeof(Locale), "Timetable", "M5,3 h14 a2,2 0 0 1 2,2 v14 a2,2 0 0 1 -2,2 h-14 a2,2 0 0 1 -2,-2 v-14 a2,2 0 0 1 2,-2 Z M19,5.2 h-14 v13.6 h14 Z M5,8.6 h14 v1.4 h-14 Z M10.4,8.6 h1.4 v10.2 h-1.4 Z M15.8,8.6 h1.4 v10.2 h-1.4 Z")]
