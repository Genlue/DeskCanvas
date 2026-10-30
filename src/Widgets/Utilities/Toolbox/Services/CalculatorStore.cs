using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Threading;
using DeskCanvas.Core;
using Tools.Models;

namespace Tools.Services;

/// <summary>
/// Shared singleton store for calculator history, memory registers, and live expression/result sync
/// across desktop widget instances (2x2 / 4x2 / 4x4) and the secondary popup window.
/// </summary>
public sealed class CalculatorStore
{
    public static CalculatorStore Instance { get; } = new();

    private readonly string storageFilePath;
    private readonly object syncLock = new();
    private readonly List<CalculatorHistoryItem> history = [];
    private readonly List<double> memorySlots = [];

    private CalculatorModel currentSettings = new();

    public string CurrentExpression { get; private set; } = string.Empty;
    public string CurrentResult { get; private set; } = "0";
    public double LastAns { get; private set; }
    public bool IsDegrees { get; private set; } = true;

    public event Action? StateChanged;

    private CalculatorStore()
    {
        var dir = Path.Combine(Const.DataFolder, "Calculator");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch { }
        storageFilePath = Path.Combine(dir, "state.json");
        LoadFromDisk();
    }

    public CalculatorModel Settings => currentSettings;

    public void Configure(CalculatorModel model)
    {
        currentSettings = model;
        IsDegrees = !string.Equals(model.DefaultAngleMode, "RAD", StringComparison.OrdinalIgnoreCase);

        bool trimmed = false;
        lock (syncLock)
        {
            int max = Math.Clamp(model.MaxHistoryItems, 5, 200);
            while (history.Count > max)
            {
                history.RemoveAt(history.Count - 1);
                trimmed = true;
            }
        }
        if (trimmed) SaveToDisk();
        NotifyChanged();
    }

    public void SetAngleMode(bool degrees)
    {
        if (IsDegrees == degrees) return;
        IsDegrees = degrees;
        NotifyChanged();
    }

    public IReadOnlyList<CalculatorHistoryItem> GetHistory()
    {
        lock (syncLock)
        {
            return history.ToList();
        }
    }

    public IReadOnlyList<double> GetMemorySlots()
    {
        lock (syncLock)
        {
            return memorySlots.ToList();
        }
    }

    public double? TopMemory
    {
        get
        {
            lock (syncLock)
            {
                return memorySlots.Count > 0 ? memorySlots[0] : null;
            }
        }
    }

    public void UpdateLiveSession(string expression, string result, object? sender = null)
    {
        CurrentExpression = expression ?? string.Empty;
        CurrentResult = string.IsNullOrWhiteSpace(result) ? "0" : result;
        NotifyChanged();
    }

    public CalculatorHistoryItem CommitCalculation(string expression, double numericResult)
    {
        string formatted = CalculatorEngine.FormatNumber(
            numericResult,
            currentSettings.DecimalPlaces,
            currentSettings.UseThousandsSeparator);

        var item = new CalculatorHistoryItem(
            Expression: expression.Trim(),
            Result: formatted,
            NumericResult: numericResult,
            Timestamp: DateTime.Now);

        lock (syncLock)
        {
            // Avoid duplicate consecutive entries
            if (history.Count == 0 ||
                !string.Equals(history[0].Expression, item.Expression, StringComparison.Ordinal) ||
                Math.Abs(history[0].NumericResult - item.NumericResult) > 1e-12)
            {
                history.Insert(0, item);
                int max = Math.Clamp(currentSettings.MaxHistoryItems, 5, 200);
                while (history.Count > max)
                {
                    history.RemoveAt(history.Count - 1);
                }
            }
        }

        LastAns = numericResult;
        CurrentExpression = expression.Trim();
        CurrentResult = formatted;

        SaveToDisk();
        NotifyChanged();
        return item;
    }

    public void ClearHistory()
    {
        lock (syncLock)
        {
            history.Clear();
        }
        SaveToDisk();
        NotifyChanged();
    }

    public void DeleteHistoryItem(CalculatorHistoryItem item)
    {
        lock (syncLock)
        {
            history.Remove(item);
        }
        SaveToDisk();
        NotifyChanged();
    }

    // ---- Memory Operations (MC / MR / M+ / M- / MS) ----

    public void MemoryClear()
    {
        lock (syncLock)
        {
            memorySlots.Clear();
        }
        SaveToDisk();
        NotifyChanged();
    }

    public void MemoryStore(double value)
    {
        lock (syncLock)
        {
            memorySlots.Insert(0, value);
            if (memorySlots.Count > 20)
                memorySlots.RemoveAt(memorySlots.Count - 1);
        }
        SaveToDisk();
        NotifyChanged();
    }

    public void MemoryAdd(double delta)
    {
        lock (syncLock)
        {
            if (memorySlots.Count == 0)
                memorySlots.Add(delta);
            else
                memorySlots[0] += delta;
        }
        SaveToDisk();
        NotifyChanged();
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(storageFilePath)) return;
            var json = File.ReadAllText(storageFilePath);
            var state = JsonSerializer.Deserialize<CalculatorStateFile>(json);
            if (state == null) return;

            lock (syncLock)
            {
                history.Clear();
                if (state.History != null)
                    history.AddRange(state.History);

                memorySlots.Clear();
                if (state.MemorySlots != null)
                    memorySlots.AddRange(state.MemorySlots);
            }

            CurrentExpression = state.LastExpression ?? string.Empty;
            CurrentResult = string.IsNullOrWhiteSpace(state.LastResult) ? "0" : state.LastResult;
            LastAns = state.LastAns;
        }
        catch
        {
            // Ignore corrupted state file
        }
    }

    private void SaveToDisk()
    {
        try
        {
            CalculatorStateFile snapshot;
            lock (syncLock)
            {
                snapshot = new CalculatorStateFile(
                    History: history.ToList(),
                    MemorySlots: memorySlots.ToList(),
                    LastExpression: CurrentExpression,
                    LastResult: CurrentResult,
                    LastAns: LastAns);
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(storageFilePath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }

    private void NotifyChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            StateChanged?.Invoke();
        }
        else
        {
            Dispatcher.UIThread.Post(() => StateChanged?.Invoke());
        }
    }
}
