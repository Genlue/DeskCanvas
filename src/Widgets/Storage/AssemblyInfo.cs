using System.Reflection;
using Storage.Locales;
using Storage.Models;
using Storage.Views;
using Storage.Views.Settings;
using DeskCanvas.Core.Models.Attributes;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.0.0")]

[assembly: WidgetInfo(typeof(StorageView), typeof(StorageModel), typeof(StorageSettings), "Storage_Title", "Storage_Subtitle", defaultColumns: 2, defaultRows: 2, presetSpans: "1x1,2x1,2x2,4x2,4x4")]
[assembly: Locale(typeof(Locale), "Storage", "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zm0 2v8h16V6H4zm0 10v2h16v-2H4zm13 0.5a1 1 0 1 1 0 2 1 1 0 0 1 0-2zm-3 0a1 1 0 1 1 0 2 1 1 0 0 1 0-2z")]
