#nullable enable
namespace Hitokoto.Locales
{
    using System;

    public class Locale
    {
        private static global::System.Resources.ResourceManager? resourceMan;
        private static global::System.Globalization.CultureInfo? resourceCulture;

        internal Locale()
        {
        }

        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        public static global::System.Resources.ResourceManager ResourceManager
        {
            get
            {
                if (object.ReferenceEquals(resourceMan, null))
                {
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Hitokoto.Locales.Locale", typeof(Locale).Assembly);
                    resourceMan = temp;
                }
                return resourceMan;
            }
        }

        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        public static global::System.Globalization.CultureInfo? Culture
        {
            get => resourceCulture;
            set => resourceCulture = value;
        }

        public static string Hitokoto => ResourceManager.GetString("Hitokoto", resourceCulture) ?? "Quotes";
        public static string Hitokoto_Title => ResourceManager.GetString("Hitokoto_Title", resourceCulture) ?? "Hitokoto Quotes";
        public static string Hitokoto_Subtitle => ResourceManager.GetString("Hitokoto_Subtitle", resourceCulture) ?? "Express the charm of words with code; discover quotes that touch your soul";
        public static string Hitokoto_Next => ResourceManager.GetString("Hitokoto_Next", resourceCulture) ?? "Next quote (or double-click card)";
        public static string Hitokoto_Like => ResourceManager.GetString("Hitokoto_Like", resourceCulture) ?? "Like this quote (Hitokoto Core API)";
        public static string Hitokoto_Favorite => ResourceManager.GetString("Hitokoto_Favorite", resourceCulture) ?? "Save to local favorites";
        public static string Hitokoto_Copy => ResourceManager.GetString("Hitokoto_Copy", resourceCulture) ?? "Copy quote and source";
        public static string Hitokoto_OpenPanel => ResourceManager.GetString("Hitokoto_OpenPanel", resourceCulture) ?? "Open quote collection (Favorites & History)";
        public static string Hitokoto_Copied => ResourceManager.GetString("Hitokoto_Copied", resourceCulture) ?? "Copied to clipboard";
        public static string Hitokoto_LikedToast => ResourceManager.GetString("Hitokoto_LikedToast", resourceCulture) ?? "Liked via Hitokoto API";
        public static string Hitokoto_FavoritedToast => ResourceManager.GetString("Hitokoto_FavoritedToast", resourceCulture) ?? "Added to local favorites";
        public static string Hitokoto_UnfavoritedToast => ResourceManager.GetString("Hitokoto_UnfavoritedToast", resourceCulture) ?? "Removed from local favorites";
        public static string Hitokoto_SectionAppearance => ResourceManager.GetString("Hitokoto_SectionAppearance", resourceCulture) ?? "Appearance & Typography";
        public static string Hitokoto_StyleMode => ResourceManager.GetString("Hitokoto_StyleMode", resourceCulture) ?? "Layout Style";
        public static string Hitokoto_StyleMode_Subtitle => ResourceManager.GetString("Hitokoto_StyleMode_Subtitle", resourceCulture) ?? "Switch between Modern Quote, Poetic Center, or Minimal layout";
        public static string Hitokoto_Frameless => ResourceManager.GetString("Hitokoto_Frameless", resourceCulture) ?? "Frameless Desktop Watermark Mode";
        public static string Hitokoto_Frameless_Subtitle => ResourceManager.GetString("Hitokoto_Frameless_Subtitle", resourceCulture) ?? "Hide card background and border so text floats directly over wallpaper";
        public static string Hitokoto_AutoHideActions => ResourceManager.GetString("Hitokoto_AutoHideActions", resourceCulture) ?? "Auto-Hide Action Buttons";
        public static string Hitokoto_AutoHideActions_Subtitle => ResourceManager.GetString("Hitokoto_AutoHideActions_Subtitle", resourceCulture) ?? "Show like, bookmark, copy, and expand buttons only on mouse hover";
        public static string Hitokoto_ShowCategory => ResourceManager.GetString("Hitokoto_ShowCategory", resourceCulture) ?? "Show Category Badge";
        public static string Hitokoto_ShowCategory_Subtitle => ResourceManager.GetString("Hitokoto_ShowCategory_Subtitle", resourceCulture) ?? "Display category pill (Poetry, Literature, Anime, etc.) at top-left";
        public static string Hitokoto_ShowAttribution => ResourceManager.GetString("Hitokoto_ShowAttribution", resourceCulture) ?? "Show Attribution & Author";
        public static string Hitokoto_ShowAttribution_Subtitle => ResourceManager.GetString("Hitokoto_ShowAttribution_Subtitle", resourceCulture) ?? "Display source work and author attribution at the bottom of the card";
        public static string Hitokoto_ShowLikes => ResourceManager.GetString("Hitokoto_ShowLikes", resourceCulture) ?? "Show Community Likes (Core API)";
        public static string Hitokoto_ShowLikes_Subtitle => ResourceManager.GetString("Hitokoto_ShowLikes_Subtitle", resourceCulture) ?? "Fetch live like count from Hitokoto Core API and support one-click liking";
        public static string Hitokoto_FontFamily => ResourceManager.GetString("Hitokoto_FontFamily", resourceCulture) ?? "Quote Font";
        public static string Hitokoto_FontFamily_Subtitle => ResourceManager.GetString("Hitokoto_FontFamily_Subtitle", resourceCulture) ?? "Choose display font for quotes (defaults to layout style recommendation)";
        public static string Hitokoto_FontSize => ResourceManager.GetString("Hitokoto_FontSize", resourceCulture) ?? "Font Size (0 = Auto)";
        public static string Hitokoto_FontSize_Subtitle => ResourceManager.GetString("Hitokoto_FontSize_Subtitle", resourceCulture) ?? "Set to 0 for automatic responsive scaling, or fix between 12–28 px";
        public static string Hitokoto_SectionFilter => ResourceManager.GetString("Hitokoto_SectionFilter", resourceCulture) ?? "Auto Refresh & Filtering";
        public static string Hitokoto_Interval => ResourceManager.GetString("Hitokoto_Interval", resourceCulture) ?? "Auto Refresh Interval";
        public static string Hitokoto_Interval_Subtitle => ResourceManager.GetString("Hitokoto_Interval_Subtitle", resourceCulture) ?? "Besides scheduled refresh, double-click the card anytime for a new quote";
        public static string Hitokoto_Categories => ResourceManager.GetString("Hitokoto_Categories", resourceCulture) ?? "Category Filter";
        public static string Hitokoto_Categories_Subtitle => ResourceManager.GetString("Hitokoto_Categories_Subtitle", resourceCulture) ?? "Selecting all or none means all categories; or pick specific genres";
        public static string Hitokoto_ResetCategories => ResourceManager.GetString("Hitokoto_ResetCategories", resourceCulture) ?? "Reset to All";
        public static string Hitokoto_MinLength => ResourceManager.GetString("Hitokoto_MinLength", resourceCulture) ?? "Minimum Quote Length";
        public static string Hitokoto_MinLength_Subtitle => ResourceManager.GetString("Hitokoto_MinLength_Subtitle", resourceCulture) ?? "Filter out very short quotes (default: 0 chars)";
        public static string Hitokoto_MaxLength => ResourceManager.GetString("Hitokoto_MaxLength", resourceCulture) ?? "Maximum Quote Length";
        public static string Hitokoto_MaxLength_Subtitle => ResourceManager.GetString("Hitokoto_MaxLength_Subtitle", resourceCulture) ?? "Prevent overly long quotes from crowding compact cards (default: 45 chars)";
        public static string Hitokoto_CustomApi => ResourceManager.GetString("Hitokoto_CustomApi", resourceCulture) ?? "Custom API Endpoint (Optional)";
        public static string Hitokoto_CustomApi_Subtitle => ResourceManager.GetString("Hitokoto_CustomApi_Subtitle", resourceCulture) ?? "Leave blank to use official https://v1.hitokoto.cn/, or enter a self-hosted URL";
        public static string Hitokoto_FontDefault => ResourceManager.GetString("Hitokoto_FontDefault", resourceCulture) ?? "Default for layout style";
        public static string Hitokoto_Interval_Manual => ResourceManager.GetString("Hitokoto_Interval_Manual", resourceCulture) ?? "Manual only (double-click / button)";
        public static string Hitokoto_Interval_5m => ResourceManager.GetString("Hitokoto_Interval_5m", resourceCulture) ?? "Every 5 minutes";
        public static string Hitokoto_Interval_15m => ResourceManager.GetString("Hitokoto_Interval_15m", resourceCulture) ?? "Every 15 minutes";
        public static string Hitokoto_Interval_30m => ResourceManager.GetString("Hitokoto_Interval_30m", resourceCulture) ?? "Every 30 minutes (Recommended)";
        public static string Hitokoto_Interval_1h => ResourceManager.GetString("Hitokoto_Interval_1h", resourceCulture) ?? "Every 1 hour";
        public static string Hitokoto_Interval_2h => ResourceManager.GetString("Hitokoto_Interval_2h", resourceCulture) ?? "Every 2 hours";
        public static string Hitokoto_Interval_Daily => ResourceManager.GetString("Hitokoto_Interval_Daily", resourceCulture) ?? "Daily quote (24 hours)";
        public static string Hitokoto_Style_ModernQuote => ResourceManager.GetString("Hitokoto_Style_ModernQuote", resourceCulture) ?? "Modern Quote (Left-aligned + Watermark)";
        public static string Hitokoto_Style_PoeticCenter => ResourceManager.GetString("Hitokoto_Style_PoeticCenter", resourceCulture) ?? "Poetic Center (Centered Serif)";
        public static string Hitokoto_Style_MinimalCard => ResourceManager.GetString("Hitokoto_Style_MinimalCard", resourceCulture) ?? "Minimal Card (No Watermark)";
    }
}
