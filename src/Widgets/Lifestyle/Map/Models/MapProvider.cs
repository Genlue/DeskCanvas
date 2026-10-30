namespace Map.Models;

/// <summary>
/// Supported map tile providers and styles.
/// </summary>
public enum MapProvider
{
    /// <summary>高德地图 (AutoNavi / Amap) 标准路网，国内速度快，商圈细致</summary>
    Amap = 0,

    /// <summary>高德地图卫星影像</summary>
    AmapSatellite = 1,

    /// <summary>谷歌地图 (Google Maps) 街道图</summary>
    Google = 2,

    /// <summary>谷歌地图卫星图</summary>
    GoogleSatellite = 3,

    /// <summary>谷歌地图混合标注图</summary>
    GoogleHybrid = 4,

    /// <summary>苹果风格 / CartoDB Positron (最接近 iPad 浅色地图)</summary>
    AppleLight = 5,

    /// <summary>苹果风格 / CartoDB Dark Matter (iPad 深色夜间地图)</summary>
    AppleDark = 6,

    /// <summary>百度地图 (Baidu Maps)</summary>
    Baidu = 7,

    /// <summary>OpenStreetMap (标准开源路网)</summary>
    OpenStreetMap = 8
}
