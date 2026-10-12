using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ControlCenter.Models;
using ControlCenter.Services;

namespace ControlCenter.Views.Controls;

public partial class ControlCapsuleView : UserControl
{
    public ControlItemType ItemType { get; private set; }
    public int ColSpan { get; private set; } = 1;
    public int RowSpan { get; private set; } = 1;
    public int WheelStep { get; set; } = 2;
    public bool IsStandaloneTile { get; set; }

    private SystemControlSnapshot snapshot = new();
    private bool isDraggingSlider;
    private double liveSliderPercent = 75;
    private string lastAudioListSignature = string.Empty;
    private bool suppressShadowOnContrastControls;

    public event Action? StateMutated;

    public ControlCapsuleView() : this(ControlItemType.Volume, 1, 1) { }

    public ControlCapsuleView(ControlItemType itemType, int colSpan, int rowSpan, int wheelStep = 2)
    {
        ItemType = itemType;
        ColSpan = Math.Max(1, colSpan);
        RowSpan = Math.Max(1, rowSpan);
        WheelStep = Math.Clamp(wheelStep, 1, 15);

        InitializeComponent();
        SetupContrastShadowGuard();

        SizeChanged += (_, _) =>
        {
            UpdateAdaptiveCornerRadius();
            UpdateSliderFillGeometry();
        };
        ResourcesChanged += (_, _) =>
        {
            UpdateAdaptiveCornerRadius();
            RenderSnapshot(snapshot);
        };
        Loaded += (_, _) =>
        {
            UpdateAdaptiveCornerRadius();
            RenderSnapshot(snapshot);
        };
        ActualThemeVariantChanged += (_, _) => RenderSnapshot(snapshot);
    }

    /// <summary>
    /// When the contrast foreground is dark/black (e.g. on a white slider fill bar),
    /// prevent the global WidgetTextShadow black DropShadowEffect from smudging dark glyphs.
    /// </summary>
    private void SetupContrastShadowGuard()
    {
        Visual[] contrastVisuals =
        [
            OverlayIcon1x1, OverlayLabel1x1,
            OverlayIcon2x1Slider, OverlayTitle2x1Slider, OverlayValue2x1Slider,
            OverlayIcon1x2Slider, OverlayTitle1x2Slider, OverlayValue1x2Slider,
            OverlayIcon2x2, OverlayTitle2x2, OverlaySubtitle2x2, OverlayValue2x2
        ];

        foreach (var v in contrastVisuals)
        {
            v.PropertyChanged += (s, e) =>
            {
                if (e.Property == Visual.EffectProperty && suppressShadowOnContrastControls && s is Visual vis && vis.Effect != null)
                {
                    vis.Effect = null;
                }
            };
        }
    }

    private void UpdateAdaptiveCornerRadius()
    {
        double outerCardRadius = 24.0;
        if (this.TryFindResource("WidgetCardCornerRadius", out var cardRes) && cardRes is CornerRadius cr)
            outerCardRadius = cr.TopLeft;
        else if (Application.Current?.TryFindResource("WidgetCardCornerRadius", out var appCardRes) == true && appCardRes is CornerRadius acr)
            outerCardRadius = acr.TopLeft;

        if (outerCardRadius <= 0)
        {
            var zero = new CornerRadius(0);
            CapsuleRoot.CornerRadius = zero;
            SliderFillBar.CornerRadius = zero;
            Badge2x1Toggle.CornerRadius = zero;
            Badge1x2Toggle.CornerRadius = zero;
            Badge2x2.CornerRadius = zero;
            OverlayBadge2x2.CornerRadius = zero;
            return;
        }

        // Concentric curvature:
        // SingleControlView has Margin=5, ControlCenterView has Margin=8+3.5.
        // Also keep a minimum ratio (0.72 for standalone, 0.66 for grid) so 1x1 cards on high-DPI
        // screens (where WidgetCardCornerRadius is already scaled by 0.55) keep a harmonious curve.
        double inset = IsStandaloneTile ? 4.5 : 8.0;
        double minRatio = IsStandaloneTile ? 0.74 : 0.66;
        double concentric = Math.Max(4.0, Math.Round(Math.Max(outerCardRadius - inset, outerCardRadius * minRatio)));

        var bounds = CapsuleRoot.Bounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            double minSide = Math.Min(bounds.Width, bounds.Height);
            concentric = Math.Min(concentric, Math.Round(minSide * 0.44));
        }

        var capsuleRadius = new CornerRadius(concentric);
        var fillRadius = new CornerRadius(Math.Max(0.0, concentric - 1.0));
        var badgeRadius = new CornerRadius(Math.Max(3.0, Math.Round(concentric * 0.62)));

        CapsuleRoot.CornerRadius = capsuleRadius;
        SliderFillBar.CornerRadius = fillRadius;
        Badge2x1Toggle.CornerRadius = badgeRadius;
        Badge1x2Toggle.CornerRadius = badgeRadius;
        Badge2x2.CornerRadius = badgeRadius;
        OverlayBadge2x2.CornerRadius = badgeRadius;
    }

    public void Configure(ControlItemType itemType, int colSpan, int rowSpan, int wheelStep)
    {
        ItemType = itemType;
        ColSpan = Math.Max(1, colSpan);
        RowSpan = Math.Max(1, rowSpan);
        WheelStep = Math.Clamp(wheelStep, 1, 15);
        lastAudioListSignature = string.Empty;
        RenderSnapshot(snapshot);
    }

    private bool IsSliderItem =>
        ItemType is ControlItemType.Brightness or ControlItemType.Volume
        || (ItemType == ControlItemType.Microphone && (ColSpan >= 2 || RowSpan >= 2));

    private bool IsVerticalSliderLayout =>
        IsSliderItem && ((ColSpan == 1 && RowSpan == 1) || (ColSpan == 1 && RowSpan >= 2));

    public void RenderSnapshot(SystemControlSnapshot nextSnapshot)
    {
        snapshot = nextSnapshot;

        if (!isDraggingSlider)
        {
            liveSliderPercent = ItemType switch
            {
                ControlItemType.Brightness => snapshot.Brightness,
                ControlItemType.Volume => snapshot.IsMuted ? 0 : snapshot.Volume,
                ControlItemType.Microphone => snapshot.IsMicMuted ? 0 : snapshot.MicVolume,
                _ => 0
            };
        }

        // Hide all base & overlay layouts first
        Layout1x1.IsVisible = false;
        Layout2x1Slider.IsVisible = false;
        Layout2x1Toggle.IsVisible = false;
        Layout1x2Slider.IsVisible = false;
        Layout1x2Toggle.IsVisible = false;
        Layout2x2Audio.IsVisible = false;
        Layout2x2General.IsVisible = false;

        FilledOverlayLayer.IsVisible = false;
        Overlay1x1.IsVisible = false;
        Overlay2x1Slider.IsVisible = false;
        Overlay1x2Slider.IsVisible = false;
        Overlay2x2General.IsVisible = false;
        BaseContentLayer.Clip = null;
        FilledOverlayLayer.Clip = null;

        var accentBrush = GetResourceBrush("SystemControlForegroundAccentBrush", Color.Parse("#007AFF"));
        var (contrastBrush, isDarkContrast) = ResolveReadableContrastBrush(accentBrush);
        suppressShadowOnContrastControls = isDarkContrast;
        if (isDarkContrast) ClearContrastShadows();

        var highFgBrush = GetResourceBrush("SystemControlForegroundBaseHighBrush", Colors.White);
        var inactiveBgBrush = new SolidColorBrush(Color.Parse("#1C808080"));
        var badgeInactiveBg = new SolidColorBrush(Color.Parse("#26808080"));

        bool isActive = IsItemActive(snapshot);
        bool isMuted = (ItemType == ControlItemType.Volume && snapshot.IsMuted)
                       || (ItemType == ControlItemType.Microphone && snapshot.IsMicMuted);

        var iconGeom = SafeParseGeometry(ControlCenterModel.GetItemIconPath(
            ItemType,
            ItemType is ControlItemType.Volume or ControlItemType.Microphone ? isMuted : isActive,
            liveSliderPercent));

        // 1. Determine active layout tier based on ColSpan x RowSpan
        if (ColSpan >= 2 && RowSpan >= 2)
        {
            if (ItemType == ControlItemType.AudioOutput)
            {
                CapsuleRoot.Background = inactiveBgBrush;
                Layout2x2Audio.IsVisible = true;
                RebuildAudioDeviceList(snapshot.AudioDevices);
                return;
            }

            Layout2x2General.IsVisible = true;
            CapsuleRoot.Background = inactiveBgBrush;

            Icon2x2.Data = iconGeom;
            Badge2x2.Background = isActive ? accentBrush : badgeInactiveBg;
            Icon2x2.Foreground = isActive ? contrastBrush : highFgBrush;

            Title2x2.Text = ControlCenterModel.GetItemDisplayName(ItemType);
            Title2x2.Foreground = highFgBrush;
            Subtitle2x2.Text = GetSubtitleText(snapshot);
            Subtitle2x2.Foreground = highFgBrush;
            Value2x2.Text = IsSliderItem ? GetSliderValueText(isMuted) : (isActive ? "开启" : "关闭");
            Value2x2.Foreground = highFgBrush;

            if (IsSliderItem)
            {
                FilledOverlayLayer.IsVisible = true;
                Overlay2x2General.IsVisible = true;
                OverlayIcon2x2.Data = iconGeom;
                OverlayBadge2x2.Background = new SolidColorBrush(isDarkContrast ? Color.Parse("#1F000000") : Color.Parse("#24FFFFFF"));
                OverlayIcon2x2.Foreground = contrastBrush;
                OverlayTitle2x2.Text = Title2x2.Text;
                OverlayTitle2x2.Foreground = contrastBrush;
                OverlaySubtitle2x2.Text = Subtitle2x2.Text;
                OverlaySubtitle2x2.Foreground = contrastBrush;
                OverlayValue2x2.Text = Value2x2.Text;
                OverlayValue2x2.Foreground = contrastBrush;
            }

            Configure2x2Presets(isActive, isMuted);
            UpdateSliderFillGeometry();
            return;
        }

        if (ColSpan >= 2 && RowSpan == 1)
        {
            if (IsSliderItem)
            {
                Layout2x1Slider.IsVisible = true;
                FilledOverlayLayer.IsVisible = true;
                Overlay2x1Slider.IsVisible = true;
                CapsuleRoot.Background = inactiveBgBrush;

                string shortTitle = ControlCenterModel.GetItemShortName(ItemType);
                string valText = GetSliderValueText(isMuted);

                // Base (Unfilled) Layer
                Icon2x1Slider.Data = iconGeom;
                Icon2x1Slider.Foreground = highFgBrush;
                Title2x1Slider.Text = shortTitle;
                Title2x1Slider.Foreground = highFgBrush;
                Value2x1Slider.Text = valText;
                Value2x1Slider.Foreground = highFgBrush;

                // Overlay (Filled) Layer — inverted contrast foreground
                OverlayIcon2x1Slider.Data = iconGeom;
                OverlayIcon2x1Slider.Foreground = contrastBrush;
                OverlayTitle2x1Slider.Text = shortTitle;
                OverlayTitle2x1Slider.Foreground = contrastBrush;
                OverlayValue2x1Slider.Text = valText;
                OverlayValue2x1Slider.Foreground = contrastBrush;

                UpdateSliderFillGeometry();
            }
            else
            {
                Layout2x1Toggle.IsVisible = true;
                CapsuleRoot.Background = inactiveBgBrush;

                Badge2x1Toggle.Background = isActive ? accentBrush : badgeInactiveBg;
                Icon2x1Toggle.Data = iconGeom;
                Icon2x1Toggle.Foreground = isActive ? contrastBrush : highFgBrush;
                if (isActive && isDarkContrast) Icon2x1Toggle.Effect = null;

                Title2x1Toggle.Text = ControlCenterModel.GetItemShortName(ItemType);
                Title2x1Toggle.Foreground = highFgBrush;
                Subtitle2x1Toggle.Text = GetSubtitleText(snapshot);
                Subtitle2x1Toggle.Foreground = highFgBrush;

                Action2x1Btn.IsVisible = ItemType is ControlItemType.WiFi or ControlItemType.Bluetooth or ControlItemType.AudioOutput;
            }
            return;
        }

        if (ColSpan == 1 && RowSpan >= 2)
        {
            if (IsSliderItem)
            {
                Layout1x2Slider.IsVisible = true;
                FilledOverlayLayer.IsVisible = true;
                Overlay1x2Slider.IsVisible = true;
                CapsuleRoot.Background = inactiveBgBrush;

                string shortTitle = ControlCenterModel.GetItemShortName(ItemType);
                string valText = GetSliderValueText(isMuted);

                // Base (Unfilled) Layer
                Icon1x2Slider.Data = iconGeom;
                Icon1x2Slider.Foreground = highFgBrush;
                Title1x2Slider.Text = shortTitle;
                Title1x2Slider.Foreground = highFgBrush;
                Value1x2Slider.Text = valText;
                Value1x2Slider.Foreground = highFgBrush;

                // Overlay (Filled) Layer — inverted contrast foreground
                OverlayIcon1x2Slider.Data = iconGeom;
                OverlayIcon1x2Slider.Foreground = contrastBrush;
                OverlayTitle1x2Slider.Text = shortTitle;
                OverlayTitle1x2Slider.Foreground = contrastBrush;
                OverlayValue1x2Slider.Text = valText;
                OverlayValue1x2Slider.Foreground = contrastBrush;

                UpdateSliderFillGeometry();
            }
            else
            {
                Layout1x2Toggle.IsVisible = true;
                CapsuleRoot.Background = inactiveBgBrush;

                Badge1x2Toggle.Background = isActive ? accentBrush : badgeInactiveBg;
                Icon1x2Toggle.Data = iconGeom;
                Icon1x2Toggle.Foreground = isActive ? contrastBrush : highFgBrush;
                if (isActive && isDarkContrast) Icon1x2Toggle.Effect = null;

                Title1x2Toggle.Text = ControlCenterModel.GetItemShortName(ItemType);
                Title1x2Toggle.Foreground = highFgBrush;
                Subtitle1x2Toggle.Text = GetSubtitleText(snapshot);
                Subtitle1x2Toggle.Foreground = highFgBrush;
            }
            return;
        }

        // Default: 1x1 Compact Tile
        Layout1x1.IsVisible = true;
        Icon1x1.Data = iconGeom;

        if (IsSliderItem)
        {
            FilledOverlayLayer.IsVisible = true;
            Overlay1x1.IsVisible = true;
            CapsuleRoot.Background = inactiveBgBrush;

            string valText = GetSliderValueText(isMuted);
            Icon1x1.Foreground = highFgBrush;
            Label1x1.Text = valText;
            Label1x1.Foreground = highFgBrush;

            OverlayIcon1x1.Data = iconGeom;
            OverlayIcon1x1.Foreground = contrastBrush;
            OverlayLabel1x1.Text = valText;
            OverlayLabel1x1.Foreground = contrastBrush;

            UpdateSliderFillGeometry();
        }
        else
        {
            CapsuleRoot.Background = isActive ? accentBrush : inactiveBgBrush;
            Icon1x1.Foreground = isActive ? contrastBrush : highFgBrush;
            Label1x1.Text = ItemType == ControlItemType.AudioOutput
                ? snapshot.CurrentAudioName
                : ControlCenterModel.GetItemShortName(ItemType);
            Label1x1.Foreground = isActive ? contrastBrush : highFgBrush;

            if (isActive && isDarkContrast)
            {
                Icon1x1.Effect = null;
                Label1x1.Effect = null;
            }
        }
    }

    private void ClearContrastShadows()
    {
        OverlayIcon1x1.Effect = null;
        OverlayLabel1x1.Effect = null;
        OverlayIcon2x1Slider.Effect = null;
        OverlayTitle2x1Slider.Effect = null;
        OverlayValue2x1Slider.Effect = null;
        OverlayIcon1x2Slider.Effect = null;
        OverlayTitle1x2Slider.Effect = null;
        OverlayValue1x2Slider.Effect = null;
        OverlayIcon2x2.Effect = null;
        OverlayTitle2x2.Effect = null;
        OverlaySubtitle2x2.Effect = null;
        OverlayValue2x2.Effect = null;
    }

    /// <summary>
    /// Updates the complementary clip regions for BaseContentLayer (unfilled) and
    /// FilledOverlayLayer (filled) so text/icons cut by the slider edge split-invert cleanly.
    /// </summary>
    private void UpdateSliderFillGeometry()
    {
        if (!FilledOverlayLayer.IsVisible)
        {
            BaseContentLayer.Clip = null;
            FilledOverlayLayer.Clip = null;
            return;
        }

        var bounds = ContentStage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            bounds = CapsuleRoot.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        double w = bounds.Width;
        double h = bounds.Height;
        double ratio = Math.Clamp(liveSliderPercent / 100.0, 0.0, 1.0);

        if (IsVerticalSliderLayout)
        {
            double fillH = Math.Round(h * ratio);
            double topUnfilledH = Math.Max(0, h - fillH);

            FilledOverlayLayer.Clip = new RectangleGeometry(new Rect(0, topUnfilledH, w, fillH));
            BaseContentLayer.Clip = new RectangleGeometry(new Rect(0, 0, w, topUnfilledH));
        }
        else
        {
            double fillW = Math.Round(w * ratio);
            double rightUnfilledW = Math.Max(0, w - fillW);

            FilledOverlayLayer.Clip = new RectangleGeometry(new Rect(0, 0, fillW, h));
            BaseContentLayer.Clip = new RectangleGeometry(new Rect(fillW, 0, rightUnfilledW, h));
        }

        if (suppressShadowOnContrastControls)
            ClearContrastShadows();
    }

    private void Configure2x2Presets(bool isActive, bool isMuted)
    {
        if (ItemType == ControlItemType.Brightness)
        {
            Presets2x2Grid.Columns = 4;
            SetPresetBtn(PresetBtn0, "25%", 25, Math.Abs(liveSliderPercent - 25) < 5);
            SetPresetBtn(PresetBtn1, "50%", 50, Math.Abs(liveSliderPercent - 50) < 5);
            SetPresetBtn(PresetBtn2, "75%", 75, Math.Abs(liveSliderPercent - 75) < 5);
            SetPresetBtn(PresetBtn3, "100%", 100, Math.Abs(liveSliderPercent - 100) < 5);
        }
        else if (ItemType is ControlItemType.Volume or ControlItemType.Microphone)
        {
            Presets2x2Grid.Columns = 4;
            SetPresetBtn(PresetBtn0, isMuted ? "取消静音" : "静音", -1, isMuted);
            SetPresetBtn(PresetBtn1, "25%", 25, !isMuted && Math.Abs(liveSliderPercent - 25) < 5);
            SetPresetBtn(PresetBtn2, "50%", 50, !isMuted && Math.Abs(liveSliderPercent - 50) < 5);
            SetPresetBtn(PresetBtn3, "100%", 100, !isMuted && Math.Abs(liveSliderPercent - 100) < 5);
        }
        else
        {
            Presets2x2Grid.Columns = 2;
            SetPresetBtn(PresetBtn0, isActive ? "关闭" : "开启", -10, isActive);
            SetPresetBtn(PresetBtn1, "系统设置 ↗", -20, false);
            PresetBtn2.IsVisible = false;
            PresetBtn3.IsVisible = false;
        }
    }

    private static void SetPresetBtn(Button btn, string label, int tagValue, bool active)
    {
        btn.IsVisible = true;
        btn.Content = label;
        btn.Tag = tagValue;
        btn.Classes.Set("active", active);
    }

    private void RebuildAudioDeviceList(IReadOnlyList<AudioDeviceInfo> devices)
    {
        var sig = string.Join("|", devices);
        if (sig == lastAudioListSignature) return;
        lastAudioListSignature = sig;

        AudioDeviceListPanel.Children.Clear();
        if (devices.Count == 0)
        {
            AudioDeviceListPanel.Children.Add(new TextBlock
            {
                Text = "未检测到活动音频输出设备",
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(2, 6, 2, 0)
            });
            return;
        }

        foreach (var dev in devices)
        {
            var btn = new Button
            {
                Classes = { "capsule-sub-btn" },
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 5),
                Tag = dev.Id
            };
            btn.Classes.Set("active", dev.IsDefault);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var dot = new TextBlock
            {
                Text = dev.IsDefault ? "●" : "○",
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            var nameBlock = new TextBlock
            {
                Text = dev.ShortName,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(nameBlock, 1);
            grid.Children.Add(dot);
            grid.Children.Add(nameBlock);
            btn.Content = grid;

            btn.Click += (_, e) =>
            {
                e.Handled = true;
                SystemControlService.Shared.SetDefaultAudioOutputDevice(dev.Id);
                StateMutated?.Invoke();
            };

            AudioDeviceListPanel.Children.Add(btn);
        }
    }

    private void OnPresetButtonClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: int code }) return;

        if (code == -1)
        {
            if (ItemType == ControlItemType.Volume) SystemControlService.Shared.ToggleMasterMute();
            else if (ItemType == ControlItemType.Microphone) SystemControlService.Shared.ToggleMicMute();
        }
        else if (code == -10)
        {
            ExecutePrimaryToggle();
            return;
        }
        else if (code == -20)
        {
            OpenCorrespondingSystemSettings();
            return;
        }
        else if (code >= 0)
        {
            ApplySliderPercent(code);
        }

        StateMutated?.Invoke();
    }

    private void OnSliderIconButtonClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemType == ControlItemType.Volume)
        {
            SystemControlService.Shared.ToggleMasterMute();
            StateMutated?.Invoke();
        }
        else if (ItemType == ControlItemType.Microphone)
        {
            SystemControlService.Shared.ToggleMicMute();
            StateMutated?.Invoke();
        }
        else if (ItemType == ControlItemType.Brightness)
        {
            double next = liveSliderPercent >= 95 ? 25 : liveSliderPercent >= 65 ? 100 : 75;
            ApplySliderPercent(next);
            StateMutated?.Invoke();
        }
    }

    private void OnSecondaryActionClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenCorrespondingSystemSettings();
    }

    private void OnCapsulePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        if (IsSliderItem)
        {
            isDraggingSlider = true;
            e.Pointer.Capture(CapsuleRoot);
            UpdateSliderFromPointer(e.GetPosition(CapsuleRoot));
            e.Handled = true;
        }
        else if (!(ColSpan >= 2 && RowSpan >= 2 && ItemType == ControlItemType.AudioOutput))
        {
            ExecutePrimaryToggle();
            e.Handled = true;
        }
    }

    private void OnCapsulePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isDraggingSlider) return;
        UpdateSliderFromPointer(e.GetPosition(CapsuleRoot));
        e.Handled = true;
    }

    private void OnCapsulePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!isDraggingSlider) return;
        isDraggingSlider = false;
        e.Pointer.Capture(null);
        UpdateSliderFromPointer(e.GetPosition(CapsuleRoot));
        StateMutated?.Invoke();
        e.Handled = true;
    }

    private void OnCapsulePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        isDraggingSlider = false;
    }

    private void OnCapsulePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!IsSliderItem && ItemType != ControlItemType.Microphone) return;

        double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (Math.Abs(delta) < 0.01) return;

        double step = Math.Sign(delta) * WheelStep;
        double current = ItemType switch
        {
            ControlItemType.Brightness => snapshot.Brightness,
            ControlItemType.Volume => snapshot.Volume,
            ControlItemType.Microphone => snapshot.MicVolume,
            _ => liveSliderPercent
        };

        ApplySliderPercent(Math.Clamp(current + step, 0, 100));
        StateMutated?.Invoke();
        e.Handled = true;
    }

    private void UpdateSliderFromPointer(Point pt)
    {
        var bounds = CapsuleRoot.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        double ratio = IsVerticalSliderLayout
            ? 1.0 - (pt.Y / bounds.Height)
            : pt.X / bounds.Width;

        double percent = Math.Round(Math.Clamp(ratio * 100.0, 0.0, 100.0));
        ApplySliderPercent(percent);
    }

    private void ApplySliderPercent(double percent)
    {
        liveSliderPercent = Math.Clamp(percent, 0, 100);
        UpdateSliderFillGeometry();

        string text = $"{(int)Math.Round(liveSliderPercent)}%";
        Label1x1.Text = text;
        OverlayLabel1x1.Text = text;
        Value2x1Slider.Text = text;
        OverlayValue2x1Slider.Text = text;
        Value1x2Slider.Text = text;
        OverlayValue1x2Slider.Text = text;
        Value2x2.Text = text;
        OverlayValue2x2.Text = text;

        var iconGeom = SafeParseGeometry(ControlCenterModel.GetItemIconPath(ItemType, liveSliderPercent <= 0.5, liveSliderPercent));
        Icon1x1.Data = iconGeom;
        OverlayIcon1x1.Data = iconGeom;
        Icon2x1Slider.Data = iconGeom;
        OverlayIcon2x1Slider.Data = iconGeom;
        Icon1x2Slider.Data = iconGeom;
        OverlayIcon1x2Slider.Data = iconGeom;
        Icon2x2.Data = iconGeom;
        OverlayIcon2x2.Data = iconGeom;

        switch (ItemType)
        {
            case ControlItemType.Brightness:
                SystemControlService.Shared.SetBrightness(liveSliderPercent);
                break;
            case ControlItemType.Volume:
                SystemControlService.Shared.SetMasterVolume(liveSliderPercent);
                break;
            case ControlItemType.Microphone:
                SystemControlService.Shared.SetMicVolume(liveSliderPercent);
                break;
        }
    }

    private async void ExecutePrimaryToggle()
    {
        switch (ItemType)
        {
            case ControlItemType.WiFi:
                await SystemControlService.Shared.ToggleWifiAsync();
                break;
            case ControlItemType.Bluetooth:
                await SystemControlService.Shared.ToggleBluetoothAsync();
                break;
            case ControlItemType.DarkMode:
                SystemControlService.Shared.ToggleSystemDarkMode();
                break;
            case ControlItemType.DesktopIcons:
                SystemControlService.Shared.ToggleDesktopIcons();
                break;
            case ControlItemType.Microphone:
                SystemControlService.Shared.ToggleMicMute();
                break;
            case ControlItemType.AudioOutput:
                SystemControlService.Shared.CycleNextAudioOutputDevice();
                break;
        }
        StateMutated?.Invoke();
    }

    private void OpenCorrespondingSystemSettings()
    {
        string uri = ItemType switch
        {
            ControlItemType.Brightness => "ms-settings:display",
            ControlItemType.Volume or ControlItemType.AudioOutput or ControlItemType.Microphone => "ms-settings:sound",
            ControlItemType.WiFi => "ms-settings:network-wifi",
            ControlItemType.Bluetooth => "ms-settings:bluetooth",
            ControlItemType.DarkMode => "ms-settings:colors",
            ControlItemType.DesktopIcons => "ms-settings:personalization",
            _ => "ms-settings:"
        };
        SystemControlService.OpenSystemSetting(uri);
    }

    private bool IsItemActive(SystemControlSnapshot s) => ItemType switch
    {
        ControlItemType.Brightness => s.Brightness > 5,
        ControlItemType.Volume => !s.IsMuted && s.Volume > 0,
        ControlItemType.AudioOutput => s.AudioDevices.Count > 0,
        ControlItemType.Microphone => s.HasMicrophone && !s.IsMicMuted,
        ControlItemType.WiFi => s.IsWifiEnabled,
        ControlItemType.Bluetooth => s.IsBluetoothEnabled,
        ControlItemType.DarkMode => s.IsSystemDarkMode,
        ControlItemType.DesktopIcons => s.AreDesktopIconsVisible,
        _ => false
    };

    private string GetSliderValueText(bool isMuted) =>
        isMuted ? "静音" : $"{(int)Math.Round(liveSliderPercent)}%";

    private string GetSubtitleText(SystemControlSnapshot s) => ItemType switch
    {
        ControlItemType.Brightness => $"{(int)Math.Round(s.Brightness)}%",
        ControlItemType.Volume => s.IsMuted ? "已静音" : $"{(int)Math.Round(s.Volume)}%",
        ControlItemType.AudioOutput => s.CurrentAudioName,
        ControlItemType.Microphone => !s.HasMicrophone ? "无麦克风" : (s.IsMicMuted ? "已静音" : $"开启 · {(int)Math.Round(s.MicVolume)}%"),
        ControlItemType.WiFi => s.WifiSubtitle,
        ControlItemType.Bluetooth => s.BluetoothSubtitle,
        ControlItemType.DarkMode => s.IsSystemDarkMode ? "系统深色" : "系统浅色",
        ControlItemType.DesktopIcons => s.AreDesktopIconsVisible ? "已显示" : "已隐藏",
        _ => string.Empty
    };

    /// <summary>
    /// Computes a guaranteed high-contrast foreground brush against <paramref name="accentBrush"/>
    /// based on perceived luminance (ITU-R BT.601), so white/light slider bars always render crisp
    /// dark text/icons and dark/colored slider bars render white text/icons.
    /// </summary>
    private (IBrush Brush, bool IsDarkForeground) ResolveReadableContrastBrush(IBrush accentBrush)
    {
        if (accentBrush is ISolidColorBrush solid)
        {
            var c = solid.Color;
            double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            if (luminance > 0.56)
                return (new SolidColorBrush(Color.Parse("#1C1C1E")), true);
            return (new SolidColorBrush(Colors.White), false);
        }

        var fallback = GetResourceBrush("AccentContrastForegroundBrush", Colors.White);
        if (fallback is ISolidColorBrush fbSolid)
        {
            var fc = fbSolid.Color;
            bool isDark = (0.299 * fc.R + 0.587 * fc.G + 0.114 * fc.B) / 255.0 < 0.5;
            return (fallback, isDark);
        }

        return (fallback, false);
    }

    private IBrush GetResourceBrush(string key, Color fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var res) && res is IBrush b)
            return b;
        if (Application.Current?.TryFindResource(key, ActualThemeVariant, out var appRes) == true && appRes is IBrush ab)
            return ab;
        return new SolidColorBrush(fallback);
    }

    private static StreamGeometry? SafeParseGeometry(string path)
    {
        try { return StreamGeometry.Parse(path); }
        catch { return null; }
    }
}
