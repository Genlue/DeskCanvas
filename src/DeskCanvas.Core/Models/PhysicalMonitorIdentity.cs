namespace DeskCanvas.Core.Models;

/// <summary>
/// Physical monitor identity discovered from Windows CCD (QueryDisplayConfig) and SetupAPI.
/// Represents the physical display device rather than transient desktop/GDI assignments.
/// </summary>
/// <param name="DeviceInstanceId">PnP device instance id (e.g. <c>"DISPLAY\SAC2463\5&amp;1551b5bc&amp;0&amp;UID4352"</c>).</param>
/// <param name="DeviceInterfacePath">Full device interface path (e.g. <c>"\\?\DISPLAY#SAC2463#5&amp;1551b5bc&amp;0&amp;UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"</c>).</param>
/// <param name="Manufacturer">EDID 3-letter manufacturer code (e.g. <c>"SAC"</c>, <c>"BOE"</c>).</param>
/// <param name="ProductCode">EDID 4-hex product code (e.g. <c>"2463"</c>, <c>"0C8E"</c>).</param>
/// <param name="HardwareId">Combined hardware id (<c>Manufacturer + ProductCode</c>, e.g. <c>"SAC2463"</c>).</param>
/// <param name="SerialNumber">Valid EDID text or numeric serial number if present; <c>null</c> if missing or placeholder.</param>
/// <param name="FriendlyName">EDID or WMI friendly name (e.g. <c>"G52 Max"</c>).</param>
/// <param name="IsInternal">True for built-in laptop panels (eDP / LVDS / Internal).</param>
/// <param name="OutputTechnology">Win32 DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY enum value.</param>
/// <param name="ConnectorInstance">Connector instance number reported by CCD.</param>
public record PhysicalMonitorIdentity(
    string DeviceInstanceId,
    string DeviceInterfacePath,
    string Manufacturer,
    string ProductCode,
    string HardwareId,
    string? SerialNumber,
    string FriendlyName,
    bool IsInternal,
    uint OutputTechnology = 0,
    uint ConnectorInstance = 0)
{
    /// <summary>
    /// Check whether a serial number string represents a valid, non-placeholder physical serial number.
    /// </summary>
    public static bool IsValidSerialNumber(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return false;
        var trimmed = serial.Trim();
        if (trimmed.Length == 0) return false;
        if (trimmed == "0") return false;
        if (trimmed.All(c => c == '0' || c == ' ' || c == '\0')) return false;

        var lower = trimmed.ToLowerInvariant();
        if (lower is "none" or "n/a" or "serial" or "serialnumber" or "00000000" or "12345678" or "0123456789")
            return false;

        return true;
    }

    /// <summary>
    /// Extract PnP Device Instance ID from a Windows monitor device interface path or instance ID.
    /// e.g. <c>\\?\DISPLAY#SAC2463#5&amp;1551B5BC&amp;0&amp;UID4352#{E6F07B5F-EE97-4A90-B076-33F57BF4EAA7}</c>
    ///   -> <c>DISPLAY\SAC2463\5&amp;1551B5BC&amp;0&amp;UID4352</c>
    /// </summary>
    public static string NormalizeDeviceInstanceId(string? pathOrId)
    {
        if (string.IsNullOrWhiteSpace(pathOrId)) return string.Empty;
        var s = pathOrId.Trim();
        if (s.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            s = s[4..];

        var lastHash = s.LastIndexOf('#');
        if (lastHash > 0 && s.IndexOf('{', lastHash) > 0)
        {
            s = s[..lastHash];
        }

        return s.Replace('#', '\\').ToUpperInvariant();
    }
}
