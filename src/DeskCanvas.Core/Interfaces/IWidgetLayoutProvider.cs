using DeskCanvas.Core.Models;

namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// Service for reading and writing layout settings of a single widget, stored in <c>layout.json</c>
/// inside the per-screen configuration the widget belongs to.
/// </summary>
public interface IWidgetLayoutProvider : IDataProvider<WidgetLayout>
{
    /// <summary>
    /// Remove the widget from the collection
    /// </summary>
    public void Remove();

    /// <summary>
    /// Id of the <see cref="ScreenLayout"/> this widget belongs to. Changes when the
    /// widget is dragged onto another screen (ownership transfer).
    /// </summary>
    public string ScreenId { get; set; }
}
