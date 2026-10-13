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
}
