using System.Reflection;
using DeskCanvas.Core.Models.Attributes;
using Dice.Locales;
using Dice.Models;
using Dice.Views;
using Dice.Views.Settings;

[assembly: AssemblyCompany("DeskCanvas")]
[assembly: AssemblyVersion("1.1.2")]

[assembly: WidgetInfo(typeof(DiceView), typeof(DiceModel), typeof(DiceSettings), "Dice_Title", "Dice_Subtitle", defaultColumns: 2, defaultRows: 2, presetSpans: "1x1,2x2,4x2", category: "WidgetCategory_Utilities")]
[assembly: Locale(typeof(Locale), "Dice", "M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zM7.5 18c-.83 0-1.5-.67-1.5-1.5S6.67 15 7.5 15s1.5.67 1.5 1.5S8.33 18 7.5 18zm0-9C6.67 9 6 8.33 6 7.5S6.67 6 7.5 6s1.5.67 1.5 1.5S8.33 9 7.5 9zm4.5 4.5c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zm4.5 4.5c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zm0-9c-.83 0-1.5-.67-1.5-1.5S15.67 6 16.5 6s1.5.67 1.5 1.5-.67 1.5-1.5 1.5z")]
