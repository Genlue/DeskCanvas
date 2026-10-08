namespace DeskCanvas.Core.Models;

/// <summary>
/// Runtime snapshot of a Windows active display path (from QueryDisplayConfig).
/// Captures the connection from a GDI source view to an attached physical target.
/// </summary>
/// <param name="GdiDeviceName">GDI device name (e.g. <c>"\\.\DISPLAY1"</c>).</param>
/// <param name="AdapterId">Display adapter LUID string.</param>
/// <param name="SourceId">CCD source view id.</param>
/// <param name="TargetId">CCD target id.</param>
/// <param name="TargetAvailable">Whether the target is currently connected and available.</param>
/// <param name="OutputTechnology">Video output technology (e.g. Embedded DisplayPort, External DP, HDMI).</param>
/// <param name="PhysicalIdentity">Resolved physical monitor identity, if available.</param>
/// <param name="CloneGroupId">Optional clone group id when operating in duplicate mode.</param>
public record ActiveDisplayPath(
    string GdiDeviceName,
    string AdapterId,
    uint SourceId,
    uint TargetId,
    bool TargetAvailable,
    uint OutputTechnology,
    PhysicalMonitorIdentity? PhysicalIdentity = null,
    uint? CloneGroupId = null);
