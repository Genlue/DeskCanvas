using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Views;

namespace DeskCanvas.Services;

/// <summary>
/// The single place that turns a stored <see cref="WidgetLayout"/> into live widget objects:
/// widget assembly loading, <see cref="WidgetInfoAttribute"/> lookup, content-control activation
/// (constructor injection of the layout provider and the deserialized model), and the per-widget
/// settings window.
/// <para>
/// Both the desktop widget host and the sidebar host go through this factory. Re-implementing
/// the widget-type dispatch anywhere else is exactly how 天气 / Notes / Stack / Folders would
/// start behaving differently on one surface than the other.
/// </para>
/// </summary>
public sealed class WidgetRuntimeFactory(IAssemblyProvider assemblyProvider)
{
    /// <summary>Load the widget assembly that owns <paramref name="typeName"/>.</summary>
    public Assembly LoadAssembly(string typeName) => assemblyProvider.LoadAssembly(typeName);

    /// <summary>
    /// Resolve the <see cref="WidgetInfoAttribute"/> describing the widget view named
    /// <paramref name="typeName"/> inside <paramref name="assembly"/>.
    /// </summary>
    /// <exception cref="ArgumentException">No matching attribute (or more than one) exists.</exception>
    public static WidgetInfoAttribute GetWidgetInfo(Assembly assembly, string typeName)
    {
        var widgetInfo = assembly
            .GetCustomAttributes<WidgetInfoAttribute>()
            .SingleOrDefault(attribute => attribute.ViewType.Name == typeName);

        if (widgetInfo == null)
            throw new ArgumentException($"No suitable WidgetInfoAttribute found for {typeName}");

        return widgetInfo;
    }

    /// <summary>Load the assembly and resolve the widget info in one step.</summary>
    public WidgetInfoAttribute GetWidgetInfo(string typeName, string subType) =>
        GetWidgetInfo(LoadAssembly(typeName), subType);

    /// <summary>
    /// Create the widget's content control. The layout provider is injected only when the widget's
    /// constructor actually declares an <see cref="IWidgetLayoutProvider"/> parameter; the model is
    /// injected as the next argument when present. This is why sidebar widgets can use a sidebar
    /// layout provider transparently.
    /// </summary>
    public UserControl CreateWidgetControl(Type type, IWidgetLayoutProvider? provider, object? model)
    {
        List<object> args = [];

        if (NeedsWidgetLayoutProvider(type) && provider != null)
            args.Add(provider);

        if (model != null)
            args.Add(model);

        return (assemblyProvider.Activate(type, args.ToArray()) as UserControl)!;
    }

    /// <summary>Build the widget's settings (编辑组件) window, wired to its layout provider.</summary>
    public EditWidget CreateEditWidgetWindow(IWidgetLayoutProvider provider, Type type)
    {
        var control = (UserControl) assemblyProvider.Activate(type, provider);
        return new EditWidget(provider, control);
    }

    /// <summary>Whether the widget's control type takes an <see cref="IWidgetLayoutProvider"/>.</summary>
    public static bool NeedsWidgetLayoutProvider(Type type) =>
        type.GetConstructors()
            .Any(constructor => constructor
                .GetParameters()
                .Any(param => param.ParameterType == typeof(IWidgetLayoutProvider)));

    /// <summary>
    /// Build the content control for a stored layout: assembly + widget info + model deserialization
    /// + constructor injection, all through the same dispatch the desktop host uses.
    /// </summary>
    public UserControl CreateContent(WidgetLayout layout, IWidgetLayoutProvider provider)
    {
        var info = GetWidgetInfo(layout.Type, layout.SubType);
        return CreateWidgetControl(info.ViewType, provider, layout.GetModel(info.ModelType));
    }

    /// <summary>
    /// A factory that re-creates the content control on demand (the host replaces the control when
    /// the widget's settings change and the view is not <see cref="IWidgetSelfRefreshing"/>).
    /// </summary>
    public Func<UserControl> ContentFactory(WidgetLayout layout, IWidgetLayoutProvider provider) =>
        () => CreateContent(layout, provider);

    /// <summary>
    /// The widget's settings-window factory, or <c>null</c> when the widget declares no edit view.
    /// </summary>
    public Func<EditWidget>? EditFactory(WidgetLayout layout, IWidgetLayoutProvider provider)
    {
        var info = GetWidgetInfo(layout.Type, layout.SubType);
        return info.EditModelViewType != null
            ? () => CreateEditWidgetWindow(provider, info.EditModelViewType)
            : null;
    }
}
