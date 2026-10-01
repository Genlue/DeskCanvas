using System.Reflection;
using Picture.Locales;
using Picture.Models;
using Picture.Views;
using Picture.Views.Settings;
using DeskCanvas.Core.Models.Attributes;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("1.0.0")]

[assembly: WidgetInfo(typeof(PictureView), typeof(PictureModel), typeof(PictureSettings), "Picture_Title", "Picture_Subtitle", defaultColumns: 2, defaultRows: 2)]
[assembly: Locale(typeof(Locale), "Picture", "M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z")]
