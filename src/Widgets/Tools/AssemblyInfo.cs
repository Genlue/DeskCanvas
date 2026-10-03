using System.Reflection;
using Tools.Locales;
using Tools.Models;
using Tools.Views;
using Tools.Views.Settings;
using DeskCanvas.Core.Models.Attributes;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.0.2")]

[assembly: WidgetInfo(typeof(ClipboardView), typeof(ClipboardModel), typeof(ClipboardSettings), "Tools_Clipboard_Title", "Tools_Clipboard_Subtitle", defaultColumns: 2, defaultRows: 2)]
[assembly: WidgetInfo(typeof(TranslatorView), typeof(TranslatorModel), typeof(TranslatorSettings), "Tools_Translator_Title", "Tools_Translator_Subtitle", defaultColumns: 4, defaultRows: 2)]
[assembly: Locale(typeof(Locale), "Tools", "M22.7,19 L13.6,9.9 C14.5,7.6 14,4.9 12.1,3 C10.1,1 7.1,0.6 4.7,1.7 L9,6 L6,9 L1.7,4.7 C0.6,7.1 1,10.1 3,12.1 C4.9,14 7.6,14.5 9.9,13.6 L19,22.7 C19.4,23.1 20,23.1 20.4,22.7 L22.7,20.4 C23.1,20 23.1,19.4 22.7,19 Z")]
