using DeskCanvas.Core.Models;

namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// Service for reading and writing the multi-screen widget layout, stored in <c>layout.json</c>.
/// <para>
/// Format v2 groups widgets per screen (<see cref="ScreensLayout"/>); format v1 (a plain
/// <see cref="WidgetLayout"/> array) is still readable and is wrapped as the legacy primary entry.
/// </para>
/// </summary>
public interface ILayoutProvider : IDataProvider<ScreensLayout>;