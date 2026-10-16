using System.Reflection;
using Habits.Locales;
using Habits.Models;
using Habits.Views;
using Habits.Views.Settings;
using DeskCanvas.Core.Models.Attributes;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.0.0")]

[assembly: WidgetInfo(typeof(HabitsView), typeof(HabitsModel), typeof(HabitsSettings), "Habits_Title", "Habits_Subtitle", defaultColumns: 2, defaultRows: 2, presetSpans: "1x1,2x2,4x2,4x4")]
[assembly: Locale(typeof(Locale), "Habits", "M19,3H5C3.89,3 3,3.89 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5C21,3.89 20.1,3 19,3M19,5V19H5V5H19M10,17L5,12L6.41,10.59L10,14.17L17.59,6.58L19,8L10,17Z")]
