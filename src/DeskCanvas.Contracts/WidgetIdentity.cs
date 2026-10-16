namespace DeskCanvas.Contracts;

/// <summary>Stable identity of one component instance. Geometry and type are not identity.</summary>
public readonly record struct WidgetInstanceId(Guid Value)
{
    public static WidgetInstanceId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

/// <summary>Version of the persisted layout document.</summary>
public readonly record struct LayoutRevision(long Value)
{
    public static LayoutRevision Initial => new(0);
    public LayoutRevision Next() => new(checked(Value + 1));
}

/// <summary>Persisted identity and generation for a layout entry.</summary>
public sealed record WidgetInstanceDescriptor(
    WidgetInstanceId InstanceId,
    string WidgetId,
    string? ScreenId,
    LayoutRevision Revision,
    long Generation)
{
    public WidgetInstanceDescriptor(string widgetId)
        : this(WidgetInstanceId.New(), widgetId, null, LayoutRevision.Initial, 0) { }
}
