using System.Reflection;
using Pomodoro.Locales;
using Pomodoro.Models;
using Pomodoro.Views;
using Pomodoro.Views.Settings;
using DeskCanvas.Core.Models.Attributes;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.0.2")]

[assembly: WidgetInfo(typeof(PomodoroView), typeof(PomodoroModel), typeof(PomodoroSettings), "Pomodoro_Title", "Pomodoro_Subtitle", defaultColumns: 2, defaultRows: 2)]
[assembly: Locale(typeof(Locale), "Pomodoro", "M12 2C6.5 2 2 6.5 2 12s4.5 10 10 10 10-4.5 10-10S17.5 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8zm.5-13H11v6l5.2 3.2.8-1.3-4.5-2.7V7z")]
