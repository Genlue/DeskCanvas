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
        public static string Hitokoto_LikedToast => ResourceManager.GetString("Hitokoto_LikedToast", resourceCulture) ?? "Liked via Hitokoto API ♡";
        public static string Hitokoto_FavoritedToast => ResourceManager.GetString("Hitokoto_FavoritedToast", resourceCulture) ?? "Added to local favorites";
        public static string Hitokoto_UnfavoritedToast => ResourceManager.GetString("Hitokoto_UnfavoritedToast", resourceCulture) ?? "Removed from local favorites";
    }
}
