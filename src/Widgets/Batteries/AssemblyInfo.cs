using System.Reflection;
using Batteries.Locales;
using Batteries.Models;
using Batteries.Views;
using Batteries.Views.Settings;
using DeskCanvas.Core.Models.Attributes;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.0.4")]

[assembly: WidgetInfo(typeof(BatteriesView), typeof(BatteriesModel), typeof(BatteriesSettings), "Batteries_Title", "Batteries_Subtitle", defaultColumns: 2, defaultRows: 2)]
[assembly: Locale(typeof(Locale), "Batteries", "M15.67 4H14V2h-4v2H8.33C7.6 4 7 4.6 7 5.33v15.33C7 21.4 7.6 22 8.33 22h7.33c.74 0 1.34-.6 1.34-1.33V5.33C17 4.6 16.4 4 15.67 4z")]
