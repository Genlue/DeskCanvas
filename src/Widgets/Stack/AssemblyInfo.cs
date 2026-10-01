using System.Reflection;
using StackWidgets.Locales;
using StackWidgets.Models;
using StackWidgets.Views;
using StackWidgets.Views.Settings;
using DeskCanvas.Core.Models.Attributes;

[assembly: AssemblyCompany("DeskCanvas")]
[assembly: AssemblyVersion("1.0.0")]

[assembly: WidgetInfo(typeof(WidgetStackView), typeof(WidgetStackModel), typeof(WidgetStackSettings), "Stack_Widget_Title", "Stack_Widget_Subtitle", defaultColumns: 2, defaultRows: 2)]
[assembly: Locale(typeof(Locale), "StackWidgets", "M4 6H2v14c0 1.1.9 2 2 2h14v-2H4V6zm16-4H8c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 14H8V4h12v12z")]
