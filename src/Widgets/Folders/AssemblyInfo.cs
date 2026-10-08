using System.Reflection;
using Folders.Locales;
using Folders.Models;
using Folders.Views;
using Folders.Views.Settings;
using DeskCanvas.Core.Models.Attributes;

[assembly: AssemblyCompany("creewick")]
[assembly: AssemblyVersion("0.2.3")]

[assembly: WidgetInfo(typeof(Folder), typeof(FolderModel), typeof(FolderSettings), "Folders_Title", "Folders_Subtitle", presetSpans: "2x2,4x2,4x4")]
[assembly: WidgetInfo(typeof(BigFolder), typeof(BigFolderModel), typeof(BigFolderSettings), "Folders_BigFolder_Title", "Folders_BigFolder_Subtitle", defaultColumns: 2, defaultRows: 2, presetSpans: "2x2,4x2,4x4")]
[assembly: WidgetInfo(typeof(SingleFile), typeof(SingleFileModel), typeof(SingleFileSettings), "Folders_SingleFile_Title", "Folders_SingleFile_Subtitle", defaultColumns: 1, defaultRows: 1, presetSpans: "1x1,2x2")]
[assembly: Locale(typeof(Locale), "Folders", "M2.5,5 C2.5,3.8954305 3.3954305,3 4.5,3 L8.256,3 C8.818,3 9.357,3.224 9.756,3.624 L11.133,5 L17.5,5 C18.6045695,5 19.5,5.8954305 19.5,7 L19.5,16.5 C19.5,17.6045695 18.6045695,18.5 17.5,18.5 L4.5,18.5 C3.3954305,18.5 2.5,17.6045695 2.5,16.5 L2.5,5 Z")]
