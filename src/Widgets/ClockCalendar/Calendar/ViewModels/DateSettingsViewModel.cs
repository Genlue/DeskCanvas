using System.Text.Json;
using Calendar.Models;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;

namespace Calendar.ViewModels;

public class DateSettingsViewModel(IWidgetLayoutProvider widgetLayoutProvider) : ReactiveObject
{
    public DateModel Model =>
        widgetLayoutProvider.Get().GetModel<DateModel>() ?? new DateModel();

    /// <summary>显示农历 — on by default; setting changes recreate the widget content.</summary>
    public bool ShowLunar
    {
        get => Model.ShowLunar;
        set
        {
            if (value == Model.ShowLunar) return;
            Save(Model with { ShowLunar = value });
            this.RaisePropertyChanged();
        }
    }

    private void Save(DateModel newModel)
    {
        var newSettings = widgetLayoutProvider.Get() with
        {
            Settings = JsonSerializer.SerializeToElement(newModel)
        };
        widgetLayoutProvider.Save(newSettings);
    }
}