using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using Wheel.Models;
using Wheel.Services;

namespace Wheel.ViewModels;

public class WheelViewModel : INotifyPropertyChanged
{
    private static readonly Random Rnd = new();
    private WheelTemplate currentTemplate;
    private WheelModel model;
    private double angle;
    private bool isSpinning;
    private string? winningResult;

    public event PropertyChangedEventHandler? PropertyChanged;

    public WheelTemplate CurrentTemplate
    {
        get => currentTemplate;
        set => SetField(ref currentTemplate, value);
    }

    public WheelModel Model
    {
        get => model;
        set => SetField(ref model, value);
    }

    public double Angle
    {
        get => angle;
        set => SetField(ref angle, value);
    }

    public bool IsSpinning
    {
        get => isSpinning;
        set => SetField(ref isSpinning, value);
    }

    public string? WinningResult
    {
        get => winningResult;
        set => SetField(ref winningResult, value);
    }

    public ObservableCollection<WheelTemplate> AllTemplates { get; } = [];

    public WheelViewModel(WheelModel? initialModel = null)
    {
        model = initialModel ?? new WheelModel();
        currentTemplate = WheelStore.GetTemplate(model.CurrentTemplateId);
        LoadTemplates();
        WheelStore.TemplatesChanged += LoadTemplates;
    }

    public void LoadTemplates()
    {
        AllTemplates.Clear();
        var data = WheelStore.GetData();
        foreach (var t in data.Templates)
        {
            AllTemplates.Add(t);
        }

        currentTemplate = WheelStore.GetTemplate(model.CurrentTemplateId);
        OnPropertyChanged(nameof(CurrentTemplate));
    }

    public async Task SpinAsync()
    {
        if (IsSpinning) return;

        var activeOptions = CurrentTemplate.Options.Where(o => o.IsEnabled).ToList();
        if (activeOptions.Count == 0)
        {
            // Reset all if all were eliminated
            foreach (var opt in CurrentTemplate.Options) opt.IsEnabled = true;
            activeOptions = CurrentTemplate.Options.ToList();
        }
        if (activeOptions.Count == 0) return;

        IsSpinning = true;
        WinningResult = null;

        int totalWeight = activeOptions.Sum(o => Math.Max(1, o.Weight));
        int roll = Rnd.Next(totalWeight);
        int currentCumulative = 0;
        WheelOption chosenOption = activeOptions[0];
        int weightBeforeChosen = 0;

        foreach (var opt in activeOptions)
        {
            int w = Math.Max(1, opt.Weight);
            if (roll >= currentCumulative && roll < currentCumulative + w)
            {
                chosenOption = opt;
                weightBeforeChosen = currentCumulative;
                break;
            }
            currentCumulative += w;
        }

        // Calculate target angle to place chosen slice under 12 o'clock pointer
        double chosenMidNormalized = (weightBeforeChosen + Math.Max(1, chosenOption.Weight) / 2.0) / totalWeight;
        double targetSliceAngle = (1.0 - chosenMidNormalized) * 360.0;

        int fullRotations = 5 + Rnd.Next(3);
        double startAngle = Angle % 360.0;
        double deltaAngle = (fullRotations * 360.0) + (targetSliceAngle - startAngle);
        if (deltaAngle < fullRotations * 360.0) deltaAngle += 360.0;

        double targetAngle = Angle + deltaAngle;
        double durationMs = Math.Max(1500, model.SpinDurationSeconds * 1000.0);
        int steps = 50;
        double stepDelay = durationMs / steps;

        for (int i = 1; i <= steps; i++)
        {
            double progress = (double)i / steps;
            // Ease-out cubic curve: 1 - (1 - t)^3
            double eased = 1.0 - Math.Pow(1.0 - progress, 3);
            Angle = startAngle + deltaAngle * eased;

            await Task.Delay((int)stepDelay);
        }

        Angle = targetAngle;
        WinningResult = chosenOption.Text;

        // Elimination mode
        if (CurrentTemplate.EliminateOnSelected || model.EliminateOnSelected)
        {
            chosenOption.IsEnabled = false;
        }

        WheelStore.RecordDecision(CurrentTemplate.Title, chosenOption.Text);
        IsSpinning = false;
    }

    public void SwitchTemplate(WheelTemplate template)
    {
        CurrentTemplate = template;
        Model = Model with { CurrentTemplateId = template.Id };
        WinningResult = null;
        Angle = 0;
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
