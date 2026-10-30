namespace Music.Locales;

[global::System.CodeDom.Compiler.GeneratedCodeAttribute("System.Resources.Tools.StronglyTypedResourceBuilder", "4.0.0.0")]
[global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
[global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
public class Locale
{
    private static global::System.Resources.ResourceManager resourceMan;
    private static global::System.Globalization.CultureInfo resourceCulture;

    internal Locale() { }

    [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
    public static global::System.Resources.ResourceManager ResourceManager
    {
        get
        {
            if (object.ReferenceEquals(resourceMan, null))
            {
                global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Music.Locales.Locale", typeof(Locale).Assembly);
                resourceMan = temp;
            }
            return resourceMan;
        }
    }

    [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
    public static global::System.Globalization.CultureInfo Culture
    {
        get => resourceCulture;
        set => resourceCulture = value;
    }

    public static string Music => ResourceManager.GetString("Music", resourceCulture) ?? "Music";
    public static string Music_Title => ResourceManager.GetString("Music_Title", resourceCulture) ?? "Music Player";
    public static string Music_Subtitle => ResourceManager.GetString("Music_Subtitle", resourceCulture) ?? "macOS style now playing widget";
    public static string Music_Play => ResourceManager.GetString("Music_Play", resourceCulture) ?? "Play";
    public static string Music_Pause => ResourceManager.GetString("Music_Pause", resourceCulture) ?? "Pause";
    public static string Music_Next => ResourceManager.GetString("Music_Next", resourceCulture) ?? "Next";
    public static string Music_Previous => ResourceManager.GetString("Music_Previous", resourceCulture) ?? "Previous";
    public static string Music_NotPlaying => ResourceManager.GetString("Music_NotPlaying", resourceCulture) ?? "Not Playing";
    public static string Music_Prompt => ResourceManager.GetString("Music_Prompt", resourceCulture) ?? "Start playing in your music app";
    public static string Music_OpenPlayerHint => ResourceManager.GetString("Music_OpenPlayerHint", resourceCulture) ?? "Start playing in your music app";
    public static string Music_DefaultPlayer => ResourceManager.GetString("Music_DefaultPlayer", resourceCulture) ?? "Music";
    public static string Music_UnknownArtist => ResourceManager.GetString("Music_UnknownArtist", resourceCulture) ?? "Unknown Artist";
    public static string Music_Untitled => ResourceManager.GetString("Music_Untitled", resourceCulture) ?? "Untitled";
    public static string Music_QuickAdd => ResourceManager.GetString("Music_QuickAdd", resourceCulture) ?? "Quick Add";
    public static string Music_MoveUp => ResourceManager.GetString("Music_MoveUp", resourceCulture) ?? "Move Up Priority";
    public static string Music_MoveDown => ResourceManager.GetString("Music_MoveDown", resourceCulture) ?? "Move Down Priority";
    public static string Music_DeleteRule => ResourceManager.GetString("Music_DeleteRule", resourceCulture) ?? "Delete Rule";
    public static string Music_SectionGeneral => ResourceManager.GetString("Music_SectionGeneral", resourceCulture) ?? "General & Display";
    public static string Music_AmbientGlow => ResourceManager.GetString("Music_AmbientGlow", resourceCulture) ?? "Enable Album Cover Ambient Glow";
    public static string Music_ShowProgress => ResourceManager.GetString("Music_ShowProgress", resourceCulture) ?? "Show Timeline Progress Bar & Scrubber";
    public static string Music_SectionPlayers => ResourceManager.GetString("Music_SectionPlayers", resourceCulture) ?? "Music Players & Smart Detection";
    public static string Music_PlayersHint => ResourceManager.GetString("Music_PlayersHint", resourceCulture) ?? "When multiple apps are active, players higher in the list take priority.";
    public static string Music_AddFromRunning => ResourceManager.GetString("Music_AddFromRunning", resourceCulture) ?? "Add from Running";
    public static string Music_BrowseExe => ResourceManager.GetString("Music_BrowseExe", resourceCulture) ?? "Browse App (.exe)";
    public static string Music_AddManualRule => ResourceManager.GetString("Music_AddManualRule", resourceCulture) ?? "Add Custom Rule";
    public static string Music_RestoreDefaults => ResourceManager.GetString("Music_RestoreDefaults", resourceCulture) ?? "Restore Defaults";
    public static string Music_ManualRuleTitle => ResourceManager.GetString("Music_ManualRuleTitle", resourceCulture) ?? "Add Custom Player Rule";
    public static string Music_ManualNameLabel => ResourceManager.GetString("Music_ManualNameLabel", resourceCulture) ?? "App Name:";
    public static string Music_ManualNameWatermark => ResourceManager.GetString("Music_ManualNameWatermark", resourceCulture) ?? "e.g. YesPlayMusic";
    public static string Music_ManualPatternLabel => ResourceManager.GetString("Music_ManualPatternLabel", resourceCulture) ?? "Pattern:";
    public static string Music_ManualPatternWatermark => ResourceManager.GetString("Music_ManualPatternWatermark", resourceCulture) ?? "e.g. yesplaymusic (process or AUMID keyword)";
    public static string Music_Cancel => ResourceManager.GetString("Music_Cancel", resourceCulture) ?? "Cancel";
    public static string Music_ConfirmAdd => ResourceManager.GetString("Music_ConfirmAdd", resourceCulture) ?? "Add";
    public static string Music_RunningSessionsTitle => ResourceManager.GetString("Music_RunningSessionsTitle", resourceCulture) ?? "Active Media Sessions (Click to Add):";
    public static string Music_Close => ResourceManager.GetString("Music_Close", resourceCulture) ?? "Close";
    public static string Music_BrowseExeTitle => ResourceManager.GetString("Music_BrowseExeTitle", resourceCulture) ?? "Select Music Player Executable";
    public static string Music_BrowseExeFilter => ResourceManager.GetString("Music_BrowseExeFilter", resourceCulture) ?? "Applications";
    public static string Music_NoSessionsTitle => ResourceManager.GetString("Music_NoSessionsTitle", resourceCulture) ?? "No running media sessions detected";
    public static string Music_NoSessionsHint => ResourceManager.GetString("Music_NoSessionsHint", resourceCulture) ?? "Please start playing music in any audio player first";
    public static string Music_Player_Netease => ResourceManager.GetString("Music_Player_Netease", resourceCulture) ?? "NetEase Cloud Music";
    public static string Music_Player_QQMusic => ResourceManager.GetString("Music_Player_QQMusic", resourceCulture) ?? "QQ Music";
    public static string Music_Player_Soda => ResourceManager.GetString("Music_Player_Soda", resourceCulture) ?? "Soda Music";
    public static string Music_Player_Kugou => ResourceManager.GetString("Music_Player_Kugou", resourceCulture) ?? "KuGou Music";
    public static string Music_Player_Kuwo => ResourceManager.GetString("Music_Player_Kuwo", resourceCulture) ?? "Kuwo Music";
    public static string Music_Player_Wmp => ResourceManager.GetString("Music_Player_Wmp", resourceCulture) ?? "Windows Media Player";
    public static string Music_Player_Browser => ResourceManager.GetString("Music_Player_Browser", resourceCulture) ?? "Browsers (Edge/Chrome/Firefox)";
}
