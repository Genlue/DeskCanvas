using System.Text.Json;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Reminders.Models;
using Reminders.Services;
using uWidgets.Core.Interfaces;

namespace Reminders.Views.Settings;

public partial class ListSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;

    public ListSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        InitializeComponent();
        AllowInlineEditToggle.IsChecked = RemindersStore.Get().AllowInlineEdit;
        DeleteOnCheckToggle.IsChecked = RemindersStore.Get().DeleteOnCheck;
    }

    private void AllowInlineEditChanged(object? sender, RoutedEventArgs e)
    {
        var model = RemindersStore.Get();
        model = model with { AllowInlineEdit = AllowInlineEditToggle.IsChecked == true };
        RemindersStore.Save(model, this);

        try
        {
            var layout = widgetLayoutProvider.Get();
            layout = layout with { Settings = JsonSerializer.SerializeToElement(model) };
            widgetLayoutProvider.Save(layout);
        }
        catch { }
    }

    private void DeleteOnCheckChanged(object? sender, RoutedEventArgs e)
    {
        var model = RemindersStore.Get();
        model = model with { DeleteOnCheck = DeleteOnCheckToggle.IsChecked == true };
        RemindersStore.Save(model, this);

        try
        {
            var layout = widgetLayoutProvider.Get();
            layout = layout with { Settings = JsonSerializer.SerializeToElement(model) };
            widgetLayoutProvider.Save(layout);
        }
        catch { }
    }

    private void DeleteCompleted(object? sender, RoutedEventArgs e)
    {
        var model = RemindersStore.Get();
        model = model with { Reminders = model.Reminders
            .Where(entry => !entry.Completed)
            .ToList() };
        RemindersStore.Save(model, this);

        try
        {
            var layout = widgetLayoutProvider.Get();
            layout = layout with { Settings = JsonSerializer.SerializeToElement(model) };
            widgetLayoutProvider.Save(layout);
        }
        catch { }
    }

    private void DeleteAll(object? sender, RoutedEventArgs e)
    {
        var model = RemindersStore.Get() with { Reminders = [] };
        RemindersStore.Save(model, this);

        try
        {
            var layout = widgetLayoutProvider.Get();
            layout = layout with { Settings = JsonSerializer.SerializeToElement(model) };
            widgetLayoutProvider.Save(layout);
        }
        catch { }
    }
}