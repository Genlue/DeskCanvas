using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using Dice.Models;
using Dice.Services;

namespace Dice.ViewModels;

public class DiceViewModel : INotifyPropertyChanged
{
    private static readonly Random Rnd = new();
    private bool isRolling;
    private int sum;
    private bool isCriticalMax;
    private DiceModel model;
    private ObservableCollection<int> currentDice = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRolling
    {
        get => isRolling;
        set => SetField(ref isRolling, value);
    }

    public int Sum
    {
        get => sum;
        set => SetField(ref sum, value);
    }

    public bool IsCriticalMax
    {
        get => isCriticalMax;
        set => SetField(ref isCriticalMax, value);
    }

    public ObservableCollection<int> CurrentDice
    {
        get => currentDice;
        set => SetField(ref currentDice, value);
    }

    public DiceModel Model
    {
        get => model;
        set
        {
            if (SetField(ref model, value))
            {
                EnsureDiceCount();
            }
        }
    }

    public ObservableCollection<DiceRollRecord> RecentHistory { get; } = [];

    public DiceViewModel(DiceModel? initialModel = null)
    {
        model = initialModel ?? new DiceModel();
        EnsureDiceCount();
        LoadRecentHistory();
        DiceStore.RollCompleted += OnRollCompleted;
        DiceStore.HistoryCleared += OnHistoryCleared;
    }

    private void EnsureDiceCount()
    {
        int count = Math.Clamp(model.DiceCount, 1, 6);
        if (CurrentDice.Count != count)
        {
            CurrentDice.Clear();
            int maxVal = (int)model.Type;
            for (int i = 0; i < count; i++)
            {
                CurrentDice.Add(Rnd.Next(1, maxVal + 1));
            }
            Sum = CurrentDice.Sum();
        }
    }

    public async Task RollAsync()
    {
        if (IsRolling) return;
        IsRolling = true;
        IsCriticalMax = false;

        int count = Math.Clamp(model.DiceCount, 1, 6);
        int maxVal = (int)model.Type;

        // 8 frames of rapid random rolls to simulate rolling inertia
        for (int frame = 0; frame < 8; frame++)
        {
            for (int i = 0; i < count; i++)
            {
                if (i < CurrentDice.Count)
                {
                    CurrentDice[i] = Rnd.Next(1, maxVal + 1);
                }
            }
            Sum = CurrentDice.Sum();
            await Task.Delay(40 + frame * 15);
        }

        var result = DiceStore.Roll(count, model.Type);
        for (int i = 0; i < result.Values.Count; i++)
        {
            if (i < CurrentDice.Count)
            {
                CurrentDice[i] = result.Values[i];
            }
        }
        Sum = result.Sum;
        IsCriticalMax = result.IsCriticalMax;

        IsRolling = false;
    }

    private void LoadRecentHistory()
    {
        RecentHistory.Clear();
        var data = DiceStore.GetData();
        foreach (var r in data.History.Take(10))
        {
            RecentHistory.Add(r);
        }
    }

    private void OnRollCompleted(DiceRollRecord record)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RecentHistory.Insert(0, record);
            if (RecentHistory.Count > 10) RecentHistory.RemoveAt(RecentHistory.Count - 1);
        });
    }

    private void OnHistoryCleared()
    {
        Dispatcher.UIThread.Post(() => RecentHistory.Clear());
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
