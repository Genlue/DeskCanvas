using System.Reflection;
using Music.Locales;
using Music.Models;
using Music.Views;
using Music.Views.Settings;
using DeskCanvas.Core.Models.Attributes;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.0.1")]

[assembly: WidgetInfo(typeof(Music.Views.Music), typeof(MusicModel), typeof(MusicSettings), "Music_Title", "Music_Subtitle", presetSpans: "2x2,4x2,4x4")]
[assembly: Locale(typeof(Locale), "Music", "M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z")]
