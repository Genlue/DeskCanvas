using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Picture.Locales;
using Picture.Models;
using Picture.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace Picture.Views.Settings;

public partial class PictureSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private PictureModel model;
    private bool isInitializing = true;
    private PictureItem? selectedItem;
    private DecodedPicture? selectedDecodedPicture;
    private Bitmap? selectedBitmap;

    private readonly ObservableCollection<PictureItem> items = [];

    public PictureSettings() : this(null!) { }

    public PictureSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider!;
        model = widgetLayoutProvider != null ? (ReadModel(widgetLayoutProvider.Get()) ?? new PictureModel()) : new PictureModel();

        InitializeComponent();

        CropPreview.WidgetAspect = GetWidgetAspect();
        CropPreview.CropChanged += OnCropPreviewChanged;

        InitCombos();
        LoadFromModel();
        isInitializing = false;

        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        CropPreview.PreviewBitmap = null;
        selectedDecodedPicture?.Dispose();
        selectedDecodedPicture = null;
        selectedBitmap = null;
        GC.Collect(1, GCCollectionMode.Optimized);
    }

    private void InitCombos()
    {
        IntervalCombo.ItemsSource = new List<string>
        {
            Locale.Picture_Interval_5s,
            Locale.Picture_Interval_10s,
            Locale.Picture_Interval_30s,
            Locale.Picture_Interval_1m,
            Locale.Picture_Interval_5m,
            Locale.Picture_Interval_15m,
            Locale.Picture_Interval_30m,
            Locale.Picture_Interval_1h,
            Locale.Picture_Interval_1d,
            Locale.Picture_Interval_Manual
        };

        OrderCombo.ItemsSource = new List<string>
        {
            Locale.Picture_Order_Seq,
            Locale.Picture_Order_Shuffle,
            Locale.Picture_Order_Fixed
        };

        FitModeCombo.ItemsSource = new List<string>
        {
            Locale.Picture_FitMode_CustomCrop,
            Locale.Picture_FitMode_Fill,
            Locale.Picture_FitMode_Fit
        };
    }

    private void LoadFromModel()
    {
        items.Clear();
        foreach (var item in model.GetItems())
        {
            items.Add(item.Copy());
        }

        PictureListBox.ItemsSource = items;

        IntervalCombo.SelectedIndex = (int)model.Interval;
        OrderCombo.SelectedIndex = (int)model.Order;
        FitModeCombo.SelectedIndex = (int)model.FitMode;

        FramelessSwitch.IsChecked = model.IsFrameless;
        CaptionSwitch.IsChecked = model.ShowCaption;
        ClickToNextSwitch.IsChecked = model.ClickToNext;
        DoubleClickToOpenSwitch.IsChecked = model.DoubleClickToOpen;

        if (items.Count > 0)
        {
            int idx = Math.Clamp(model.CurrentIndex, 0, items.Count - 1);
            PictureListBox.SelectedIndex = idx;
        }
        else
        {
            UpdateCropPanel(null);
        }
    }

    private void OnPictureSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (PictureListBox.SelectedItem is PictureItem item)
        {
            selectedItem = item;
            UpdateCropPanel(item);
        }
        else
        {
            selectedItem = null;
            UpdateCropPanel(null);
        }
    }

    private void UpdateCropPanel(PictureItem? item)
    {
        if (item == null)
        {
            CropPanel.IsVisible = false;
            selectedDecodedPicture?.Dispose();
            selectedDecodedPicture = null;
            selectedBitmap = null;
            CropPreview.PreviewBitmap = null;
            return;
        }

        CropPanel.IsVisible = true;

        if (File.Exists(item.Path))
        {
            // Clear the control's reference before disposing the old decode, otherwise the
            // preview would briefly render a freed bitmap.
            CropPreview.PreviewBitmap = null;
            try
            {
                selectedDecodedPicture?.Dispose();
                selectedDecodedPicture = PictureImageLoader.Load(item.Path, maxDimension: 800);
                selectedBitmap = selectedDecodedPicture?.PrimaryBitmap;
                CropPreview.PreviewBitmap = selectedBitmap;
            }
            catch
            {
                selectedDecodedPicture = null;
                selectedBitmap = null;
                CropPreview.PreviewBitmap = null;
            }
        }
        else
        {
            CropPreview.PreviewBitmap = null;
        }

        CropXSlider.Value = item.CropX * 100.0;
        CropYSlider.Value = item.CropY * 100.0;
        ZoomSlider.Value = item.Zoom * 100.0;

        CropXText.Text = $"{(int)CropXSlider.Value}%";
        CropYText.Text = $"{(int)CropYSlider.Value}%";
        ZoomText.Text = $"{(ZoomSlider.Value / 100.0):F2}x";

        CropPreview.WidgetAspect = GetWidgetAspect();
        CropPreview.CropX = item.CropX;
        CropPreview.CropY = item.CropY;
        CropPreview.Zoom = item.Zoom;
        CropPreview.FitMode = model.FitMode;
    }

    private double GetWidgetAspect()
    {
        if (widgetLayoutProvider != null)
        {
            var layout = widgetLayoutProvider.Get();
            if (layout is { Width: > 0, Height: > 0 })
            {
                return (double)layout.Width / layout.Height;
            }
        }
        return 1.0;
    }

    private void OnCropPreviewChanged(double cropX, double cropY, double zoom)
    {
        if (selectedItem == null) return;

        isInitializing = true;
        CropXSlider.Value = cropX * 100.0;
        CropYSlider.Value = cropY * 100.0;
        ZoomSlider.Value = zoom * 100.0;

        CropXText.Text = $"{(int)(cropX * 100.0)}%";
        CropYText.Text = $"{(int)(cropY * 100.0)}%";
        ZoomText.Text = $"{zoom:F2}x";
        isInitializing = false;

        selectedItem.CropX = cropX;
        selectedItem.CropY = cropY;
        selectedItem.Zoom = zoom;

        SaveItems();
    }

    private void OnCropSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || selectedItem == null) return;

        double cropX = CropXSlider.Value / 100.0;
        double cropY = CropYSlider.Value / 100.0;
        double zoom = ZoomSlider.Value / 100.0;

        selectedItem.CropX = cropX;
        selectedItem.CropY = cropY;
        selectedItem.Zoom = zoom;

        CropXText.Text = $"{(int)CropXSlider.Value}%";
        CropYText.Text = $"{(int)CropYSlider.Value}%";
        ZoomText.Text = $"{zoom:F2}x";

        CropPreview.CropX = cropX;
        CropPreview.CropY = cropY;
        CropPreview.Zoom = zoom;

        SaveItems();
    }

    private void OnAlignPresetClicked(object? sender, RoutedEventArgs e)
    {
        if (selectedItem == null || sender is not Button { Tag: string tag }) return;

        var parts = tag.Split(',');
        if (parts.Length == 2 && double.TryParse(parts[0], out var x) && double.TryParse(parts[1], out var y))
        {
            CropXSlider.Value = x * 100.0;
            CropYSlider.Value = y * 100.0;
        }
    }

    private async void OnAddFilesClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Locale.Picture_AddFiles,
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("图片与动态图 (*.jpg, *.png, *.webp, *.gif, *.bmp, *.ico, *.tiff)")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.gif", "*.ico", "*.tif", "*.tiff", "*.wbmp"]
                },
                new FilePickerFileType("动图 (*.gif, *.webp)")
                {
                    Patterns = ["*.gif", "*.webp"]
                },
                new FilePickerFileType("所有文件 (*.*)")
                {
                    Patterns = ["*.*"]
                }
            ]
        });

        if (files.Count > 0)
        {
            foreach (var f in files)
            {
                if (f.Path.LocalPath is { } path && File.Exists(path))
                {
                    items.Add(new PictureItem
                    {
                        Path = path,
                        Name = Path.GetFileNameWithoutExtension(path)
                    });
                }
            }

            if (PictureListBox.SelectedIndex < 0 && items.Count > 0)
            {
                PictureListBox.SelectedIndex = 0;
            }

            SaveItems();
        }
    }

    private async void OnAddFolderClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Locale.Picture_AddFolder,
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0].Path.LocalPath is { } dir && Directory.Exists(dir))
        {
            var validExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".wbmp"
            };

            var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => validExts.Contains(Path.GetExtension(f)))
                .OrderBy(f => f);

            foreach (var f in files)
            {
                items.Add(new PictureItem
                {
                    Path = f,
                    Name = Path.GetFileNameWithoutExtension(f)
                });
            }

            if (PictureListBox.SelectedIndex < 0 && items.Count > 0)
            {
                PictureListBox.SelectedIndex = 0;
            }

            SaveItems();
        }
    }

    private void OnDeleteItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PictureItem item })
        {
            int idx = items.IndexOf(item);
            items.Remove(item);
            if (selectedItem == item)
            {
                if (items.Count > 0)
                {
                    PictureListBox.SelectedIndex = Math.Clamp(idx, 0, items.Count - 1);
                }
                else
                {
                    selectedItem = null;
                    UpdateCropPanel(null);
                }
            }
            SaveItems();
        }
    }

    private void OnClearAllClicked(object? sender, RoutedEventArgs e)
    {
        items.Clear();
        selectedItem = null;
        UpdateCropPanel(null);
        SaveItems();
    }

    private void OnMoveUpClicked(object? sender, RoutedEventArgs e)
    {
        if (selectedItem == null) return;
        int idx = items.IndexOf(selectedItem);
        if (idx > 0)
        {
            items.Move(idx, idx - 1);
            PictureListBox.SelectedItem = selectedItem;
            SaveItems();
        }
    }

    private void OnMoveDownClicked(object? sender, RoutedEventArgs e)
    {
        if (selectedItem == null) return;
        int idx = items.IndexOf(selectedItem);
        if (idx < items.Count - 1 && idx >= 0)
        {
            items.Move(idx, idx + 1);
            PictureListBox.SelectedItem = selectedItem;
            SaveItems();
        }
    }

    private void OnSettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;

        model = model with
        {
            Interval = (SlideshowInterval)Math.Max(0, IntervalCombo.SelectedIndex),
            Order = (PlayOrder)Math.Max(0, OrderCombo.SelectedIndex),
            FitMode = (PictureFitMode)Math.Max(0, FitModeCombo.SelectedIndex)
        };

        CropPreview.FitMode = model.FitMode;
        Save();
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;

        model = model with
        {
            IsFrameless = FramelessSwitch.IsChecked ?? false,
            ShowCaption = CaptionSwitch.IsChecked ?? false,
            ClickToNext = ClickToNextSwitch.IsChecked ?? true,
            DoubleClickToOpen = DoubleClickToOpenSwitch.IsChecked ?? true
        };

        Save();
    }

    private void SaveItems()
    {
        model = model with
        {
            Items = items.Select(x => x.Copy()).ToList(),
            CurrentIndex = Math.Clamp(PictureListBox.SelectedIndex, 0, Math.Max(0, items.Count - 1))
        };
        Save();
    }

    private void Save()
    {
        if (widgetLayoutProvider == null) return;
        try
        {
            var layout = widgetLayoutProvider.Get();
            if (layout == null) return;
            widgetLayoutProvider.Save(layout with
            {
                Settings = JsonSerializer.SerializeToElement(model)
            });
        }
        catch
        {
            // Ignored
        }
    }

    private static PictureModel? ReadModel(WidgetLayout? layout)
    {
        if (layout?.Settings is not { } settings) return null;
        if (settings.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return settings.Deserialize<PictureModel>();
        }
        catch
        {
            return null;
        }
    }
}
