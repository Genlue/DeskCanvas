using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DeskCanvas.Core.Services;

namespace Map.Services;

public record LocationResult(
    bool Success,
    double Latitude,
    double Longitude,
    string? City,
    string? FormattedAddress,
    string? ErrorMessage);

public static class GeoLocationService
{
    private static readonly HttpClient HttpClient = ProxySettings.CreateHttpClient();

    public static async Task<LocationResult> GetCurrentLocationAsync(CancellationToken cancellationToken = default)
    {
        // 1. Primary: http://ip-api.com/json/?lang=zh-CN
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var url = "http://ip-api.com/json/?fields=status,message,country,regionName,city,lat,lon&lang=zh-CN";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "DeskCanvas-Map/1.0");

            using var response = await HttpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var data = JsonSerializer.Deserialize<IpApiResponse>(json);
                if (data != null && string.Equals(data.Status, "success", StringComparison.OrdinalIgnoreCase)
                    && data.Lat.HasValue && data.Lon.HasValue)
                {
                    var city = !string.IsNullOrWhiteSpace(data.City) ? data.City : data.RegionName;
                    var fullAddr = $"{data.Country ?? ""} {data.RegionName ?? ""} {data.City ?? ""}".Trim();
                    return new LocationResult(true, data.Lat.Value, data.Lon.Value, city, fullAddr, null);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GeoLocation] Primary ip-api failed: {ex.Message}");
        }

        // 2. Fallback: https://ipwho.is/?lang=zh-CN
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var url = "https://ipwho.is/?lang=zh-CN";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "DeskCanvas-Map/1.0");

            using var response = await HttpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var data = JsonSerializer.Deserialize<IpWhoIsResponse>(json);
                if (data != null && data.Success == true && data.Latitude.HasValue && data.Longitude.HasValue)
                {
                    var city = !string.IsNullOrWhiteSpace(data.City) ? data.City : data.Region;
                    var fullAddr = $"{data.Country ?? ""} {data.Region ?? ""} {data.City ?? ""}".Trim();
                    return new LocationResult(true, data.Latitude.Value, data.Longitude.Value, city, fullAddr, null);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GeoLocation] Fallback ipwho.is failed: {ex.Message}");
        }

        return new LocationResult(false, 0, 0, null, null, "未能获取网络定位，请检查网络连接");
    }

    // ---------- 实时快速离线粗略地区定位引擎 (0ms 延迟) ----------

    private record GeoRegion(double Lat, double Lon, double RadiusKm, string Name, string? Detail = null);

    private static readonly GeoRegion[] KnownRegions =
    [
        // 中国直辖市与核心都会圈
        new(39.9042, 116.4074, 55, "北京市", "中国 · 首都"),
        new(31.2304, 121.4737, 60, "上海市", "中国 · 华东都会"),
        new(39.1256, 117.1902, 50, "天津市", "中国 · 华北沿海"),
        new(29.5630, 106.5516, 65, "重庆市", "中国 · 西南都会"),

        // 广东省与粤港澳大湾区
        new(23.1291, 113.2644, 45, "广州市", "广东省 · 省会"),
        new(22.5431, 114.0579, 35, "深圳市", "广东省 · 特区"),
        new(23.0207, 113.7518, 30, "东莞市", "广东省"),
        new(23.0215, 113.1214, 30, "佛山市", "广东省"),
        new(22.2707, 113.5767, 25, "珠海市", "广东省"),
        new(22.3193, 114.1694, 30, "中国香港", "特别行政区"),
        new(22.1987, 113.5439, 20, "中国澳门", "特别行政区"),

        // 长三角重点城市
        new(30.2741, 120.1551, 45, "杭州市", "浙江省 · 省会"),
        new(29.8683, 121.5440, 35, "宁波市", "浙江省"),
        new(28.0006, 120.6994, 35, "温州市", "浙江省"),
        new(30.7454, 120.7555, 30, "嘉兴市", "浙江省"),
        new(32.0603, 118.7969, 45, "南京市", "江苏省 · 省会"),
        new(31.2990, 120.5853, 40, "苏州市", "江苏省"),
        new(31.4912, 120.3119, 35, "无锡市", "江苏省"),
        new(31.8106, 119.9741, 35, "常州市", "江苏省"),
        new(34.2610, 117.1859, 40, "徐州市", "江苏省"),

        // 西南与中部枢纽
        new(30.5728, 104.0668, 55, "成都市", "四川省 · 省会"),
        new(31.4675, 104.6791, 35, "绵阳市", "四川省"),
        new(30.5928, 114.3055, 55, "武汉市", "湖北省 · 省会"),
        new(28.2282, 112.9388, 45, "长沙市", "湖南省 · 省会"),
        new(34.3416, 108.9398, 50, "西安市", "陕西省 · 省会"),
        new(34.7466, 113.6254, 50, "郑州市", "河南省 · 省会"),
        new(34.6185, 112.4540, 35, "洛阳市", "河南省"),

        // 华北与山东半岛
        new(36.6512, 117.1201, 45, "济南市", "山东省 · 省会"),
        new(36.0671, 120.3826, 45, "青岛市", "山东省"),
        new(37.4638, 121.4479, 35, "烟台市", "山东省"),
        new(38.0428, 114.5149, 45, "石家庄市", "河北省 · 省会"),
        new(39.1559, 115.6565, 35, "保定市", "河北省"),
        new(39.5380, 116.6838, 30, "廊坊市", "河北省"),
        new(39.6309, 118.1802, 40, "唐山市", "河北省"),
        new(37.8706, 112.5489, 45, "太原市", "山西省 · 省会"),

        // 东南与华南
        new(26.0745, 119.2965, 45, "福州市", "福建省 · 省会"),
        new(24.4798, 118.0894, 35, "厦门市", "福建省"),
        new(24.9089, 118.5894, 35, "泉州市", "福建省"),
        new(31.8206, 117.2272, 45, "合肥市", "安徽省 · 省会"),
        new(28.6820, 115.8579, 45, "南昌市", "江西省 · 省会"),
        new(22.8170, 108.3665, 45, "南宁市", "广西壮族自治区 · 首府"),
        new(25.2736, 110.2902, 35, "桂林市", "广西壮族自治区"),
        new(20.0440, 110.3283, 40, "海口市", "海南省 · 省会"),
        new(18.2528, 109.5119, 35, "三亚市", "海南省"),

        // 东北地区
        new(41.8057, 123.4315, 50, "沈阳市", "辽宁省 · 省会"),
        new(38.9140, 121.6147, 45, "大连市", "辽宁省"),
        new(43.8868, 125.3245, 50, "长春市", "吉林省 · 省会"),
        new(45.8038, 126.5350, 55, "哈尔滨市", "黑龙江省 · 省会"),

        // 西北与西南边陲
        new(25.0458, 102.7100, 45, "昆明市", "云南省 · 省会"),
        new(25.6065, 100.2676, 35, "大理州", "云南省"),
        new(26.8550, 100.2277, 35, "丽江市", "云南省"),
        new(26.6470, 106.6302, 45, "贵阳市", "贵州省 · 省会"),
        new(40.8426, 111.7500, 45, "呼和浩特市", "内蒙古自治区 · 首府"),
        new(36.0611, 103.8343, 45, "兰州市", "甘肃省 · 省会"),
        new(36.6171, 101.7782, 40, "西宁市", "青海省 · 省会"),
        new(38.4872, 106.2309, 40, "银川市", "宁夏回族自治区 · 首府"),
        new(43.8256, 87.6168, 55, "乌鲁木齐市", "新疆维吾尔自治区 · 首府"),
        new(29.6500, 91.1300, 45, "拉萨市", "西藏自治区 · 首府"),

        // 台湾省
        new(25.0330, 121.5654, 35, "台北市", "台湾"),
        new(22.6273, 120.3014, 35, "高雄市", "台湾"),
        new(24.1477, 120.6736, 35, "台中市", "台湾"),

        // 亚太重点国际城市
        new(35.6762, 139.6503, 60, "东京", "日本 · 首都"),
        new(34.6937, 135.5023, 45, "大阪", "日本 · 关西"),
        new(35.0116, 135.7681, 35, "京都", "日本 · 关西"),
        new(37.5665, 126.9780, 55, "首尔", "韩国 · 首都"),
        new(1.3521, 103.8198, 40, "新加坡", "城市国家"),
        new(13.7563, 100.5018, 50, "曼谷", "泰国 · 首都"),
        new(3.1390, 101.6869, 40, "吉隆坡", "马来西亚 · 首都"),
        new(21.0285, 105.8542, 45, "河内", "越南 · 首都"),
        new(10.8231, 106.6297, 45, "胡志明市", "越南"),
        new(14.5995, 120.9842, 45, "马尼拉", "菲律宾 · 首都"),
        new(-6.2088, 106.8456, 55, "雅加达", "印度尼西亚 · 首都"),

        // 欧洲大都会
        new(51.5074, -0.1278, 65, "伦敦", "英国 · 首都"),
        new(48.8566, 2.3522, 60, "巴黎", "法国 · 首都"),
        new(52.5200, 13.4050, 55, "柏林", "德国 · 首都"),
        new(50.1109, 8.6821, 40, "法兰克福", "德国"),
        new(48.1351, 11.5820, 40, "慕尼黑", "德国"),
        new(41.9028, 12.4964, 50, "罗马", "意大利 · 首都"),
        new(45.4642, 9.1900, 45, "米兰", "意大利"),
        new(40.4168, -3.7038, 55, "马德里", "西班牙 · 首都"),
        new(41.3879, 2.1699, 45, "巴塞罗那", "西班牙"),
        new(52.3676, 4.9041, 40, "阿姆斯特丹", "荷兰 · 首都"),
        new(50.8503, 4.3517, 40, "布鲁塞尔", "比利时 · 首都"),
        new(47.3769, 8.5417, 40, "苏黎世", "瑞士"),
        new(48.2082, 16.3738, 45, "维也纳", "奥地利 · 首都"),
        new(55.7558, 37.6173, 65, "莫斯科", "俄罗斯 · 首都"),
        new(59.9343, 30.3351, 55, "圣彼得堡", "俄罗斯"),

        // 美洲大都会
        new(40.7128, -74.0060, 60, "纽约", "美国 · 都会区"),
        new(34.0522, -118.2437, 70, "洛杉矶", "美国 · 加州"),
        new(37.7749, -122.4194, 45, "旧金山", "美国 · 湾区"),
        new(41.8781, -87.6298, 55, "芝加哥", "美国 · 伊利诺伊州"),
        new(47.6062, -122.3321, 45, "西雅图", "美国 · 华盛顿州"),
        new(38.9072, -77.0369, 45, "华盛顿特区", "美国 · 首都"),
        new(42.3601, -71.0589, 40, "波士顿", "美国 · 麻省"),
        new(25.7617, -80.1918, 45, "迈阿密", "美国 · 佛罗里达州"),
        new(29.7604, -95.3698, 50, "休斯敦", "美国 · 得州"),
        new(43.6532, -79.3832, 55, "多伦多", "加拿大 · 安大略省"),
        new(49.2827, -123.1207, 45, "温哥华", "加拿大 · BC省"),
        new(19.4326, -99.1332, 65, "墨西哥城", "墨西哥 · 首都"),
        new(-23.5505, -46.6333, 65, "圣保罗", "巴西"),
        new(-34.6037, -58.3816, 60, "布宜诺斯艾利斯", "阿根廷 · 首都"),

        // 大洋洲与中东非洲
        new(-33.8688, 151.2093, 60, "悉尼", "澳大利亚 · 新州"),
        new(-37.8136, 144.9631, 55, "墨尔本", "澳大利亚 · 维州"),
        new(-36.8485, 174.7633, 45, "奥克兰", "新西兰"),
        new(25.2048, 55.2708, 45, "迪拜", "阿联酋"),
        new(30.0444, 31.2357, 55, "开罗", "埃及 · 首都"),
        new(-33.9249, 18.4241, 45, "开普敦", "南非")
    ];

    /// <summary>
    /// 毫秒级零延迟本地粗略地区解析器（地图滑动时 60fps 实时触发）。
    /// </summary>
    public static (string Name, string? Description) ResolveCoarseRegion(double lat, double lon)
    {
        // 1. 优先在核心城市库中查找半径匹配
        GeoRegion? closest = null;
        var minDistance = double.MaxValue;

        for (var i = 0; i < KnownRegions.Length; i++)
        {
            var r = KnownRegions[i];
            var d = FastDistanceKm(lat, lon, r.Lat, r.Lon);
            if (d <= r.RadiusKm)
            {
                return (r.Name, r.Detail ?? "中国");
            }
            if (d < minDistance)
            {
                minDistance = d;
                closest = r;
            }
        }

        // 2. 若在 200km 范围内，显示“城市周边”
        if (closest != null && minDistance < 180.0)
        {
            return ($"{closest.Name}周边", closest.Detail ?? "周边地区");
        }

        // 3. 全球大区与洋流区域判别
        if (lat > 66.5) return ("北极圈", "极地地区");
        if (lat < -60.0) return ("南极洲", "极地大陆");

        // 中国大陆通用疆界 (大致 18°N~54°N, 73°E~135°E)
        if (lat is >= 18.0 and <= 53.5 && lon is >= 73.5 and <= 135.0)
        {
            return (closest != null ? $"{closest.Name}近郊" : "中国", "中国");
        }

        // 各大洲判别
        if (lat is >= 35.0 and <= 71.0 && lon is >= -11.0 and <= 45.0) return ("欧洲", "大洲");
        if (lat is >= 15.0 and <= 72.0 && lon is >= -168.0 and <= -52.0) return ("北美洲", "大洲");
        if (lat is >= -56.0 and <= 13.0 && lon is >= -82.0 and <= -34.0) return ("南美洲", "大洲");
        if (lat is >= -35.0 and <= 37.0 && lon is >= -18.0 and <= 52.0) return ("非洲", "大洲");
        if (lat is >= -47.0 and <= -10.0 && lon is >= 112.0 and <= 180.0) return ("大洋洲", "大洲");
        if (lat is >= -10.0 and <= 75.0 && lon is >= 45.0 and <= 180.0) return ("亚洲", "大洲");

        // 大洋
        if (lon is >= -180.0 and <= -70.0 or >= 120.0 and <= 180.0) return ("太平洋", "大洋");
        if (lon is >= -70.0 and <= 20.0) return ("大西洋", "大洋");
        if (lon is >= 20.0 and <= 120.0 && lat <= 30.0) return ("印度洋", "大洋");

        return ("当前位置", $"{Math.Abs(lat):F2}°, {Math.Abs(lon):F2}°");
    }

    private static double FastDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = (lat2 - lat1) * 111.0;
        var meanLatRad = (lat1 + lat2) * 0.5 * (Math.PI / 180.0);
        var dLon = (lon2 - lon1) * 111.0 * Math.Cos(meanLatRad);
        return Math.Sqrt(dLat * dLat + dLon * dLon);
    }

    // ---------- 在线精细逆地理编码（防抖异步补充精确地址） ----------

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Name, string Description)>
        GeocodeCache = new();

    public static async Task<(string? Name, string? Description)> ReverseGeocodeAsync(double lat, double lon, CancellationToken ct = default)
    {
        var key = $"{Math.Round(lat, 2):F2}_{Math.Round(lon, 2):F2}";
        if (GeocodeCache.TryGetValue(key, out var cached))
            return cached;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            // BigDataCloud Client Reverse Geocode API (免费、无 Key、全球可用、中文地名丰富)
            var url = $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={lat:F5}&longitude={lon:F5}&localityLanguage=zh";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "DeskCanvas-Map/1.0");

            using var response = await HttpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var city = root.TryGetProperty("city", out var c) ? c.GetString() : null;
                var locality = root.TryGetProperty("locality", out var l) ? l.GetString() : null;
                var subdivision = root.TryGetProperty("principalSubdivision", out var s) ? s.GetString() : null;
                var country = root.TryGetProperty("countryName", out var cn) ? cn.GetString() : null;

                string name;
                if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(locality) && !string.Equals(city, locality, StringComparison.OrdinalIgnoreCase))
                {
                    name = $"{city} · {locality}";
                }
                else if (!string.IsNullOrWhiteSpace(city))
                {
                    name = city;
                }
                else if (!string.IsNullOrWhiteSpace(subdivision))
                {
                    name = subdivision;
                }
                else
                {
                    (name, _) = ResolveCoarseRegion(lat, lon);
                }

                var descParts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(country)) descParts.Add(country);
                if (!string.IsNullOrWhiteSpace(subdivision) && subdivision != city) descParts.Add(subdivision);
                if (!string.IsNullOrWhiteSpace(city)) descParts.Add(city);
                if (!string.IsNullOrWhiteSpace(locality) && locality != city) descParts.Add(locality);

                var desc = descParts.Count > 0 ? string.Join(" ", descParts) : $"{Math.Abs(lat):F4}°{(lat >= 0 ? "N" : "S")}, {Math.Abs(lon):F4}°{(lon >= 0 ? "E" : "W")}";

                var result = (name, desc);
                GeocodeCache[key] = result;
                return result;
            }
        }
        catch
        {
            // Fallback to coarse resolver if offline or network error
        }

        var (coarseName, coarseDesc) = ResolveCoarseRegion(lat, lon);
        return (coarseName, coarseDesc ?? $"{Math.Abs(lat):F4}°{(lat >= 0 ? "N" : "S")}, {Math.Abs(lon):F4}°{(lon >= 0 ? "E" : "W")}");
    }

    private class IpApiResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("regionName")] public string? RegionName { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("lat")] public double? Lat { get; set; }
        [JsonPropertyName("lon")] public double? Lon { get; set; }
    }

    private class IpWhoIsResponse
    {
        [JsonPropertyName("success")] public bool? Success { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("region")] public string? Region { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("latitude")] public double? Latitude { get; set; }
        [JsonPropertyName("longitude")] public double? Longitude { get; set; }
    }
}
