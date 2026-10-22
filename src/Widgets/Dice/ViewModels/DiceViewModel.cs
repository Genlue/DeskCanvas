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
        int maxVal = (int)model.Type;

        // 骰子类型切换（D6 → D20 等）不会改变数量，但旧点数可能超出新骰面的范围
        // （例如从 D20 切到 D4 后仍留着 17 点），骰面显示与总和都会失真，所以
        // 类型或数量任一变化都重掷一轮。
        bool outOfRange = CurrentDice.Any(v => v < 1 || v > maxVal);
        if (CurrentDice.Count != count || outOfRange)
        {
            CurrentDice.Clear();
            for (int i = 0; i < count; i++)
            {
                CurrentDice.Add(Rnd.Next(1, maxVal + 1));
            }
            PublishSum(CurrentDice.Sum());
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
            PublishSum(CurrentDice.Sum());
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
        PublishSum(result.Sum);
        IsCriticalMax = result.IsCriticalMax;

        IsRolling = false;
    }

    /// <summary>
    /// 先写总和，再保证骰面一定重绘。
    /// <para>
    /// 骰面值的集合实例从不被替换，而视图只在 Sum / CurrentDice / Model 变更时重绘；两次投掷
    /// 恰好同和（3+4 → 5+2）时 Sum 不变，骰面就会停在上一轮的点数上——用户看到的正是
    /// "点数与总和标签对不上"。同和时补发一次 CurrentDice 通知，不同和时由 Sum 的通知完成重绘，
    /// 每帧只重绘一次。
    /// </para>
    /// </summary>
    private void PublishSum(int newSum)
    {
        bool changed = Sum != newSum;
        Sum = newSum;
        if (!changed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentDice)));
        }
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
