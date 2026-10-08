using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DeskCanvas.Core.Models;

/// <summary>
/// Source of a display configuration binding.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BindingSource
{
    /// <summary>Automatically bound from hardware PnP instance identity.</summary>
    AutoDetected,
    /// <summary>Manually assigned by the user in settings.</summary>
    Manual,
    /// <summary>Migrated from legacy layout.json configurations.</summary>
    Migrated
}

/// <summary>
/// Match / binding lifecycle status of a display.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BindingStatus
{
    /// <summary>Confirmed unique match to a physical display instance.</summary>
    Bound,
    /// <summary>Physical identity known, but no existing configuration bound.</summary>
    Unconfigured,
    /// <summary>Hardware identity missing, partial, or transitional.</summary>
    Unresolved,
    /// <summary>Multiple configurations or multiple monitors conflict on this identity.</summary>
    Conflict
}

/// <summary>
/// Persistent binding descriptor stored in <see cref="ScreenLayout"/> (layout format v3).
/// Pins a screen layout to a physical monitor by PnP instance ID and valid serial number.
/// </summary>
/// <param name="DeviceInstanceId">Canonical PnP device instance id (e.g. <c>"DISPLAY\SAC2463\5&amp;1551b5bc&amp;0&amp;UID4352"</c>).</param>
/// <param name="DeviceInterfacePath">Optional device interface path.</param>
/// <param name="HardwareId">EDID hardware id (e.g. <c>"SAC2463"</c>).</param>
/// <param name="SerialNumber">Valid EDID serial number, if confirmed.</param>
/// <param name="InstanceAliases">Verified historic PnP instance ids for this same physical monitor (e.g. across GPU ports or docks).</param>
/// <param name="Source">How this binding was established.</param>
/// <param name="Status">Last known binding status.</param>
public record ScreenBinding(
    string DeviceInstanceId,
    string? DeviceInterfacePath = null,
    string? HardwareId = null,
    string? SerialNumber = null,
    List<string>? InstanceAliases = null,
    BindingSource Source = BindingSource.AutoDetected,
    BindingStatus Status = BindingStatus.Bound)
{
    /// <summary>
    /// Check whether a candidate PnP instance id or interface path matches this binding.
    /// </summary>
    public bool MatchesInstance(string? candidatePathOrId)
    {
        if (string.IsNullOrWhiteSpace(candidatePathOrId)) return false;
        var normalized = PhysicalMonitorIdentity.NormalizeDeviceInstanceId(candidatePathOrId);

        if (string.Equals(PhysicalMonitorIdentity.NormalizeDeviceInstanceId(DeviceInstanceId), normalized, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(DeviceInterfacePath) &&
            string.Equals(PhysicalMonitorIdentity.NormalizeDeviceInstanceId(DeviceInterfacePath), normalized, StringComparison.OrdinalIgnoreCase))
            return true;

        if (InstanceAliases != null)
        {
            foreach (var alias in InstanceAliases)
            {
                if (string.Equals(PhysicalMonitorIdentity.NormalizeDeviceInstanceId(alias), normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
