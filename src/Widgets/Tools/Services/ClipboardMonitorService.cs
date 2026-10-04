using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DeskCanvas.Core;
using SkiaSharp;
using Tools.Models;

namespace Tools.Services;

public class ClipboardMonitorService
{
    private static ClipboardMonitorService? instance;
    public static ClipboardMonitorService Instance => instance ??= new ClipboardMonitorService();

    /// <summary>Width (px) each history thumbnail is decoded at. The card renders it ~100 px tall, so
    /// a 320 px wide decode is ~97 % smaller than the full-resolution capture while still looking sharp.</summary>
    private const int ThumbnailWidth = 320;

    private readonly DispatcherTimer timer;
    private uint lastSequence;
    private uint lastSelfSequence;
    private readonly string storageDir;
    private readonly string cacheDir;
    private readonly string historyFilePath;

    public ObservableCollection<ClipboardItem> History { get; } = [];
    public event Action? HistoryChanged;

    private ClipboardModel currentModel = new();

    public ClipboardMonitorService()
    {
        storageDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Const.AppName);
        cacheDir = Path.Combine(storageDir, "ClipboardCache");
        historyFilePath = Path.Combine(storageDir, "clipboard_history.json");

        try
        {
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }
        }
        catch { }

        LoadHistory();
        lastSequence = ClipboardNative.GetClipboardSequenceNumber();

        timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        timer.Tick += OnTimerTick;
        timer.Start();
    }

    public void UpdateSettings(ClipboardModel model)
    {
        currentModel = model;
        TrimHistory(model.MaxHistoryCount);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        uint currentSeq = ClipboardNative.GetClipboardSequenceNumber();
        if (currentSeq == lastSequence) return;

        lastSequence = currentSeq;
        if (currentSeq == lastSelfSequence) return;

        CheckAndCaptureClipboard();
    }

    private void CheckAndCaptureClipboard()
    {
        try
        {
            // 1. Files
            if (currentModel.CaptureFiles && ClipboardNative.IsClipboardFormatAvailable(ClipboardNative.CF_HDROP))
            {
                var files = ClipboardNative.ReadFiles();
                if (files != null && files.Length > 0)
                {
                    // Check if identical to current top item
                    var top = History.FirstOrDefault();
                    if (top?.Type == ClipboardType.Files && top.FilePaths != null && top.FilePaths.SequenceEqual(files))
                    {
                        return;
                    }

                    var item = new ClipboardItem
                    {
                        Type = ClipboardType.Files,
                        FilePaths = files,
                        Timestamp = DateTime.Now
                    };
                    AddItem(item);
                    return;
                }
            }

            // 2. Images
            if (currentModel.CaptureImages && (ClipboardNative.IsClipboardFormatAvailable(ClipboardNative.CF_DIB) || ClipboardNative.IsClipboardFormatAvailable(ClipboardNative.CF_DIBV5)))
            {
                using var skBitmap = ClipboardNative.ReadImage();
                if (skBitmap != null && skBitmap.Width > 0 && skBitmap.Height > 0)
                {
                    string fileName = $"{Guid.NewGuid():N}.png";
                    string filePath = Path.Combine(cacheDir, fileName);

                    using (var image = SKImage.FromBitmap(skBitmap))
                    using (var data = image.Encode(SKEncodedImageFormat.Png, 95))
                    using (var stream = File.OpenWrite(filePath))
                    {
                        data.SaveTo(stream);
                    }

                    var item = new ClipboardItem
                    {
                        Type = ClipboardType.Image,
                        ImagePath = filePath,
                        ImageWidth = skBitmap.Width,
                        ImageHeight = skBitmap.Height,
                        Timestamp = DateTime.Now
                    };

                    item.Thumbnail = CreateThumbnail(filePath);

                    AddItem(item);
                    return;
                }
            }

            // 3. Text
            if (currentModel.CaptureText && ClipboardNative.IsClipboardFormatAvailable(ClipboardNative.CF_UNICODETEXT))
            {
                var text = ClipboardNative.ReadText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var top = History.FirstOrDefault();
                    if (top?.Type == ClipboardType.Text && top.Text == text)
                    {
                        return;
                    }

                    var item = new ClipboardItem
                    {
                        Type = ClipboardType.Text,
                        Text = text,
                        Timestamp = DateTime.Now
                    };
                    AddItem(item);
                    return;
                }
            }
        }
        catch
        {
            // Ignore capture failure (e.g. locked clipboard)
        }
    }

    /// <summary>
    /// Decodes a history thumbnail scaled down to <see cref="ThumbnailWidth"/> px wide (aspect kept).
    /// A full-resolution decode would keep one unmanaged 8–33 MB pixel block per history item alive
    /// (20 items × 4K = 633 MB), and unmanaged bitmaps create no GC pressure, so nothing would ever
    /// reclaim them without an explicit Dispose.
    /// </summary>
    private static Bitmap? CreateThumbnail(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return Bitmap.DecodeToWidth(stream, ThumbnailWidth, BitmapInterpolationMode.LowQuality);
        }
        catch
        {
            // A thumbnail is cosmetic; a broken source image must not abort the capture.
            return null;
        }
    }

    /// <summary>
    /// Releases the unmanaged pixel block behind a history item's thumbnail before it leaves the
    /// history — otherwise the memory is only reclaimed by a finalizer/GC that the managed heap
    /// has no reason to trigger.
    /// </summary>
    private static void DisposeThumbnail(ClipboardItem item)
    {
        item.Thumbnail?.Dispose();
        item.Thumbnail = null;
    }

    /// <summary>
    /// Queues <see cref="DisposeThumbnail"/> for the values the history no longer holds. The delay is
    /// deliberate: <see cref="HistoryChanged"/> only *posts* the view's refresh, so disposing on the
    /// spot could destroy a bitmap that a visual still shows for one more render pass.
    /// </summary>
    private static void DisposeThumbnails(IEnumerable<ClipboardItem> removed)
    {
        var orphans = removed.ToList();
        if (orphans.Count == 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in orphans)
            {
                DisposeThumbnail(item);
            }
        }, DispatcherPriority.Background);
    }

    private void AddItem(ClipboardItem item)
    {
        History.Insert(0, item);
        TrimHistory(currentModel.MaxHistoryCount);
        SaveHistory();
        HistoryChanged?.Invoke();
    }

    public void RemoveItem(ClipboardItem item)
    {
        if (History.Remove(item))
        {
            DisposeThumbnails([item]);
            if (item.Type == ClipboardType.Image && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
            {
                try { File.Delete(item.ImagePath); } catch { }
            }
            SaveHistory();
            HistoryChanged?.Invoke();
        }
    }

    public void ClearAll()
    {
        DisposeThumbnails(History.ToList());
        History.Clear();
        try
        {
            if (Directory.Exists(cacheDir))
            {
                foreach (var file in Directory.GetFiles(cacheDir))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }

        SaveHistory();
        HistoryChanged?.Invoke();
    }

    public bool CopyToClipboard(ClipboardItem item)
    {
        bool success = false;
        try
        {
            switch (item.Type)
            {
                case ClipboardType.Text:
                    if (!string.IsNullOrEmpty(item.Text))
                    {
                        success = ClipboardNative.SetText(item.Text);
                    }
                    break;

                case ClipboardType.Files:
                    if (item.FilePaths != null && item.FilePaths.Length > 0)
                    {
                        success = ClipboardNative.SetFiles(item.FilePaths);
                    }
                    break;

                case ClipboardType.Image:
                    if (!string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
                    {
                        success = ClipboardNative.SetImageFromFile(item.ImagePath);
                    }
                    break;
            }

            if (success)
            {
                lastSelfSequence = ClipboardNative.GetClipboardSequenceNumber();
                lastSequence = lastSelfSequence;
            }
        }
        catch
        {
            success = false;
        }

        return success;
    }

    public void TogglePin(ClipboardItem item)
    {
        item.IsPinned = !item.IsPinned;
        var sorted = History.OrderByDescending(i => i.IsPinned).ThenByDescending(i => i.Timestamp).ToList();
        History.Clear();
        foreach (var it in sorted) History.Add(it);
        SaveHistory();
        HistoryChanged?.Invoke();
    }

    private void TrimHistory(int maxCount)
    {
        while (History.Count > maxCount)
        {
            int dropIndex = -1;
            for (int i = History.Count - 1; i >= 0; i--)
            {
                if (!History[i].IsPinned)
                {
                    dropIndex = i;
                    break;
                }
            }
            if (dropIndex < 0) break;

            var last = History[dropIndex];
            History.RemoveAt(dropIndex);
            DisposeThumbnails([last]);
            if (last.Type == ClipboardType.Image && !string.IsNullOrEmpty(last.ImagePath) && File.Exists(last.ImagePath))
            {
                try { File.Delete(last.ImagePath); } catch { }
            }
        }
    }

    private void LoadHistory()
    {
        if (!File.Exists(historyFilePath)) return;

        try
        {
            string json = File.ReadAllText(historyFilePath);
            var list = JsonSerializer.Deserialize<List<ClipboardItem>>(json);
            if (list == null) return;

            DisposeThumbnails(History.ToList());
            History.Clear();
            foreach (var item in list)
            {
                if (item.Type == ClipboardType.Image && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
                {
                    item.Thumbnail = CreateThumbnail(item.ImagePath);
                }
                History.Add(item);
            }
        }
        catch
        {
            // Ignore corrupted history file
        }
    }

    private void SaveHistory()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = false };
            string json = JsonSerializer.Serialize(History.Take(currentModel.MaxHistoryCount).ToList(), options);
            File.WriteAllText(historyFilePath, json);
        }
        catch
        {
            // Ignore write error
        }
    }
}
