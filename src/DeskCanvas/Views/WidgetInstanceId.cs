using DeskCanvas.Contracts;

namespace DeskCanvas.Views;

public partial class Widget
{
    /// <summary>Stable identity of this widget instance, read from the widget's stored layout
    /// entry. Empty for contexts without a persisted layout (gallery preview, checks).</summary>
    public WidgetInstanceId InstanceId => new(widgetLayoutProvider.Get().InstanceId);
}
