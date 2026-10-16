using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Countdown.Models;
using Countdown.Services;

namespace Countdown.ViewModels;

public class CountdownItemViewModel : INotifyPropertyChanged
{
    private CountdownItem model;
    private CountdownGranularity granularity;
    private CountdownComputedResult computed;
    private IBrush colorBrush;

    private static readonly string[] CachedPercentStrings = InitializePercentStrings();
    private static readonly System.Collections.Generic.Dictionary<string, IBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IBrush DefaultColorBrush = new SolidColorBrush(Color.Parse("#FF5A5F"));

    private static string[] InitializePercentStrings()
    {
        var arr = new string[101];
        for (int i = 0; i <= 100; i++)
        {
            arr[i] = $"{i}%";
        }
        return arr;
    }

    public CountdownItemViewModel(CountdownItem model, CountdownGranularity granularity, DateTime now)
    {
        this.model = model;
        this.granularity = granularity;
        computed = CountdownCalculationService.Compute(model, now, granularity);
        colorBrush = ParseColorBrush(model.ColorTag);
    }

    public CountdownItem Model => model;

    public string Id => model.Id;
    public string Title => model.Title;
    public string Category => model.Category;
    public bool IsPinned => model.IsPinned;
    public bool IsLunar => model.IsLunar;
    public string? ColorTag => model.ColorTag;

    public IBrush ColorBrush => colorBrush;

    public CountdownComputedResult Computed => computed;

    public string MainDisplayNumber => computed.MainDisplayNumber;
    public string UnitLabel => computed.UnitLabel;
    public string SubtitleLabel => computed.SubtitleLabel;
    public string FormattedDate => computed.FormattedDate;
    public double Progress => computed.Progress;
    public bool IsCountup => computed.IsCountup;

    public double SweepAngle => Math.Max(0.1, computed.Progress * 359.9);
    public string ProgressPercentText
    {
        get
        {
            int pct = Math.Clamp((int)(computed.Progress * 100), 0, 100);
            return CachedPercentStrings[pct];
        }
    }
    public double StrokeDashOffset => 43.085 * (1.0 - Math.Clamp(computed.Progress, 0.0, 1.0));

    public void Update(CountdownItem newModel, CountdownGranularity newGranularity, DateTime now)
    {
        bool modelChanged = model != newModel;
        model = newModel;
        granularity = newGranularity;

        if (modelChanged)
        {
            colorBrush = ParseColorBrush(model.ColorTag);
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Category));
            OnPropertyChanged(nameof(IsPinned));
            OnPropertyChanged(nameof(IsLunar));
            OnPropertyChanged(nameof(ColorBrush));
        }

        var newComputed = CountdownCalculationService.Compute(model, now, granularity, computed);
        if (ReferenceEquals(newComputed, computed))
        {
            return;
        }

        ApplyComputedChange(newComputed);
    }

    public void Tick(DateTime now)
    {
        var newComputed = CountdownCalculationService.Compute(model, now, granularity, computed);
        if (ReferenceEquals(newComputed, computed))
        {
            return;
        }

        ApplyComputedChange(newComputed);
    }

    private void ApplyComputedChange(CountdownComputedResult newComputed)
    {
        var oldComputed = computed;
        computed = newComputed;

        if (oldComputed.MainDisplayNumber != newComputed.MainDisplayNumber)
            OnPropertyChanged(nameof(MainDisplayNumber));

        if (oldComputed.UnitLabel != newComputed.UnitLabel)
            OnPropertyChanged(nameof(UnitLabel));

        if (oldComputed.SubtitleLabel != newComputed.SubtitleLabel)
            OnPropertyChanged(nameof(SubtitleLabel));

        if (oldComputed.FormattedDate != newComputed.FormattedDate)
            OnPropertyChanged(nameof(FormattedDate));

        if (oldComputed.IsCountup != newComputed.IsCountup)
            OnPropertyChanged(nameof(IsCountup));

        if (Math.Abs(oldComputed.Progress - newComputed.Progress) > 0.0005)
        {
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(SweepAngle));
            OnPropertyChanged(nameof(StrokeDashOffset));
        }

        int oldPct = (int)(oldComputed.Progress * 100);
        int newPct = (int)(newComputed.Progress * 100);
        if (oldPct != newPct)
        {
            OnPropertyChanged(nameof(ProgressPercentText));
        }

        OnPropertyChanged(nameof(Computed));
    }

    public static IBrush ParseColorBrush(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return DefaultColorBrush;
        lock (BrushCache)
        {
            if (BrushCache.TryGetValue(hex, out var cached)) return cached;
            if (Color.TryParse(hex, out var color))
            {
                var brush = new SolidColorBrush(color);
                BrushCache[hex] = brush;
                return brush;
            }
            return DefaultColorBrush;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
