using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using Fortune.Models;
using Fortune.Services;

namespace Fortune.ViewModels;

public class FortuneViewModel : INotifyPropertyChanged
{
    private bool hasDrawnToday;
    private bool isDrawing;
    private FortuneItem? currentFortune;
    private FortuneModel model;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasDrawnToday
    {
        get => hasDrawnToday;
        set => SetField(ref hasDrawnToday, value);
    }

    public bool IsDrawing
    {
        get => isDrawing;
        set => SetField(ref isDrawing, value);
    }

    public FortuneItem? CurrentFortune
    {
        get => currentFortune;
        set => SetField(ref currentFortune, value);
    }

    public FortuneModel Model
    {
        get => model;
        set => SetField(ref model, value);
    }

    public string DateDisplay => DateTime.Today.ToString("M月d日 · 今日运气");

    public ObservableCollection<DailyFortuneRecord> History { get; } = [];

    public FortuneViewModel(FortuneModel? initialModel = null)
    {
        model = initialModel ?? new FortuneModel();
        LoadCurrentState();
        FortuneStore.FortuneDrawn += OnFortuneDrawn;
    }

    public void LoadCurrentState()
    {
        var today = FortuneStore.GetTodayFortune();
        if (today != null)
        {
            HasDrawnToday = true;
            CurrentFortune = today.Fortune;
        }
        else
        {
            HasDrawnToday = false;
            CurrentFortune = null;
        }

        History.Clear();
        var data = FortuneStore.GetData();
        foreach (var r in data.History)
        {
            History.Add(r);
        }
    }

    public async Task DrawTodayAsync()
    {
        if (IsDrawing) return;
        IsDrawing = true;

        // 600ms ritual vibration / shaking time
        await Task.Delay(600);

        var record = FortuneStore.DrawToday(Model.PoolType);
        CurrentFortune = record.Fortune;
        HasDrawnToday = true;
        IsDrawing = false;
    }

    public async Task<FortuneItem> FreeDrawAsync()
    {
        if (IsDrawing) return CurrentFortune ?? FortuneRepository.GetRandomFortune(Model.PoolType);
        IsDrawing = true;

        await Task.Delay(500);

        var record = FortuneStore.FreeDraw(Model.PoolType);
        IsDrawing = false;
        return record.Fortune;
    }

    private void OnFortuneDrawn(DailyFortuneRecord record)
    {
        Dispatcher.UIThread.Post(() =>
        {
            History.Insert(0, record);
            if (History.Count > 30) History.RemoveAt(History.Count - 1);
        });
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
