using System;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using Dice.Models;

namespace Dice.Views.Settings;

public partial class DiceSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private DiceModel currentModel;
    private bool isInitializing = true;

    public DiceSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        currentModel = LoadModel();

        InitializeComponent();
        InitializeControls();
        isInitializing = false;
    }

    private DiceModel LoadModel()
    {
        try
        {
            var layout = widgetLayoutProvider.Get();
            if (layout.Settings is { } settings && settings.ValueKind == JsonValueKind.Object)
            {
                var parsed = settings.Deserialize<DiceModel>();
                if (parsed != null) return parsed;
            }
        }
        catch { }

        return new DiceModel();
    }

    private void InitializeControls()
    {
        CountComboBox.SelectedIndex = Math.Clamp(currentModel.DiceCount - 1, 0, 5);

        int typeVal = (int)currentModel.Type;
        for (int i = 0; i < TypeComboBox.Items.Count; i++)
        {
            if (TypeComboBox.Items[i] is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int tag) && tag == typeVal)
            {
                TypeComboBox.SelectedIndex = i;
                break;
            }
        }

        FaceStyleComboBox.SelectedIndex = (int)currentModel.FaceStyle;
        ShowSumToggle.IsChecked = currentModel.ShowSum;
    }

    private void OnSettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        SaveModel();
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        SaveModel();
    }

    private void SaveModel()
    {
        int count = CountComboBox.SelectedIndex + 1;
        DiceType type = DiceType.D6;
        if (TypeComboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int tag))
        {
            type = (DiceType)tag;
        }

        DiceFaceStyle style = (DiceFaceStyle)FaceStyleComboBox.SelectedIndex;
        bool showSum = ShowSumToggle.IsChecked ?? true;

        currentModel = new DiceModel(count, type, style, showSum);
        try
        {
            var layout = widgetLayoutProvider.Get();
            layout = layout with { Settings = JsonSerializer.SerializeToElement(currentModel) };
            widgetLayoutProvider.Save(layout);
        }
        catch { }
    }
}
