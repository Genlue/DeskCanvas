using System.Reflection;
using DeskCanvas.Core.Models.Attributes;
using Hitokoto.Locales;
using Hitokoto.Models;
using Hitokoto.Views;
using Hitokoto.Views.Settings;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.0.0")]

[assembly: WidgetInfo(
    typeof(HitokotoView),
    typeof(HitokotoModel),
    typeof(HitokotoSettings),
    "Hitokoto_Title",
    "Hitokoto_Subtitle",
    defaultColumns: 4,
    defaultRows: 2,
    presetSpans: "2x1,2x2,4x2,4x4")]
[assembly: Locale(
    typeof(Locale),
    "Hitokoto",
    "M6,17 L9,17 L11,13 L11,7 L5,7 L5,13 L8,13 L6,17 Z M14,17 L17,17 L19,13 L19,7 L13,7 L13,13 L16,13 L14,17 Z")]
