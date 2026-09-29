using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Folders.Locales;
using Folders.Models;
using Folders.Services;
using uWidgets.Core.Models.Settings;
using uWidgets.Core.Services;
using uWidgets.Services;
using uWidgets.Views;
using Grid = Avalonia.Controls.Grid;

namespace Folders.Views;

public partial class BigFolderPopupWindow : SecondaryPanelWindow
{
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCRBUTTONDOWN = 0x00A4;
    private const int WM_NCMBUTTONDOWN = 0x00A7;

    public static readonly StyledProperty<int> PopupGridColumnsProperty =
        AvaloniaProperty.Register<BigFolderPopupWindow, int>(nameof(PopupGridColumns), 4);

    public int PopupGridColumns
    {
        get => GetValue(PopupGridColumnsProperty);
        set => SetValue(PopupGridColumnsProperty, value);
    }

    private readonly Point? spawnScreenCenter;
    private BigFolderModel currentModel;
    private readonly Action<BigFolderModel>? onModelChanged;
    // The native-blur theme clips this window with SetWindowRgn; a window region cannot
    // follow a render transform, so the transition falls back to opacity only.
    private readonly bool transformAnimationEnabled;
    private bool suppressSettingsEvents = false;
    private bool isClosing = false;

    private LowLevelMouseProc? mouseHookProc;
    private IntPtr hookHandle = IntPtr.Zero;

    protected override Visual? PanelCard => CardBorder;
    protected override Point? SpawnScreenCenter => spawnScreenCenter;
    protected override bool TransformAnimationEnabled => transformAnimationEnabled;
    protected override int DeactivateCloseGraceMs => 150;

    public BigFolderPopupWindow() : this(new BigFolderModel(), null, null) { }

    public BigFolderPopupWindow(List<string> items) : this(new BigFolderModel(items), null, null) { }

    public BigFolderPopupWindow(
        BigFolderModel model,
        Point? screenCenter = null,
        Action<BigFolderModel>? onModelChanged = null)
    {
        currentModel = model;
        spawnScreenCenter = screenCenter;
        this.onModelChanged = onModelChanged;
        transformAnimationEnabled = !UsesNativeBlurTheme();

        InitializeComponent();
        InitializePanel();

        SyncSettingsControls();
        ApplyTheme();
        PopulateItems();

        LiquidGlassPreRenderService.PreRenderCompleted += OnPreRenderCompleted;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    private static bool UsesNativeBlurTheme()
    {
        try { return new AppSettingsProvider().Get().Theme.UsesNativeBlur; }
        catch { return false; }
    }

    private void OnPreRenderCompleted()
    {
        if (LiquidGlassWallpaper.LiveSamplingEnabled) return;
        if (LiquidGlassBgImage.IsVisible)
        {
            var screen = spawnScreenCenter.HasValue ? Screens.ScreenFromPoint(new PixelPoint((int)spawnScreenCenter.Value.X, (int)spawnScreenCenter.Value.Y)) : Screens.Primary;
            var bmp = LiquidGlassPreRenderService.GetCachedBitmapFor(spawnScreenCenter, Width, Height, screen, Screens.All);
            if (bmp != null)
            {
                LiquidGlassBgImage.Source = bmp;
                CardBorder.Background = Brushes.Transparent;
            }
        }
    }

    private void ApplyTheme()
    {
        Theme theme;
        try
        {
            theme = new AppSettingsProvider().Get().Theme;
        }
        catch
        {
            theme = new Theme(DarkMode: true, AccentColor: null, OpacityLevel: 0.8, Monochrome: false, UseNativeFrame: false, FontFamily: "Inter");
        }

        bool isDark = ActualThemeVariant == ThemeVariant.Dark || (theme.DarkMode ?? true);

        if (theme.UsesRenderedGlass)
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            if (LiquidGlassWallpaper.LiveSamplingEnabled)
            {
                LiquidGlassSurfaceControl.Material = theme;
                LiquidGlassSurfaceControl.CornerRadius = CardBorder.CornerRadius;
                LiquidGlassSurfaceControl.IsVisible = true;
                LiquidGlassBgImage.IsVisible = false;
                LiquidGlassOverlay.IsVisible = false;
                CardBorder.Background = Brushes.Transparent;
                CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
                LiquidGlassSurfaceControl.RequestRender(immediate: true);
            }
            else
            {
                LiquidGlassSurfaceControl.IsVisible = false;
                LiquidGlassBgImage.IsVisible = true;
                LiquidGlassOverlay.IsVisible = false;
                CardBorder.Background = Brushes.Transparent;
                CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));

                var screen = spawnScreenCenter.HasValue ? Screens.ScreenFromPoint(new PixelPoint((int)spawnScreenCenter.Value.X, (int)spawnScreenCenter.Value.Y)) : Screens.Primary;
                var bmp = LiquidGlassPreRenderService.GetCachedBitmapFor(spawnScreenCenter, Width, Height, screen, Screens.All);

                if (bmp != null)
                {
                    LiquidGlassBgImage.Source = bmp;
                }
                else
                {
                    CardBorder.Background = new SolidColorBrush(isDark ? Color.FromArgb(40, 28, 28, 32) : Color.FromArgb(40, 245, 245, 248));
                    _ = TriggerDirectLiquidGlassRender(theme, isDark, screen);
                }
            }
        }
        else if (theme.EffectiveSurface == SurfaceStyle.Solid)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            LiquidGlassBgImage.IsVisible = false;
            LiquidGlassOverlay.IsVisible = false;

            var hex = isDark ? theme.EffectiveSolidBackgroundDark : theme.EffectiveSolidBackgroundLight;
            var baseColor = Color.TryParse(hex, out var parsed) ? parsed : (isDark ? Color.FromRgb(46, 46, 46) : Colors.White);
            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 255), 40, 255);
            CardBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        }
        else // Acrylic (毛玻璃)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
            LiquidGlassBgImage.IsVisible = false;
            LiquidGlassOverlay.IsVisible = false;

            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 220), 40, 240);
            CardBorder.Background = new SolidColorBrush(isDark ? Color.FromArgb(alpha, 28, 28, 32) : Color.FromArgb(alpha, 245, 245, 248));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(35, 0, 0, 0));
        }

        IBrush textBrush = isDark ? Brushes.White : new SolidColorBrush(Color.FromRgb(30, 30, 30));
        IBrush subTextBrush = isDark ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        TitleText.Foreground = textBrush;
        CloseButton.Foreground = subTextBrush;
        SettingsButton.Foreground = subTextBrush;
    }

    private async Task TriggerDirectLiquidGlassRender(Theme theme, bool isDark, Screen? screen)
    {
        try
        {
            var bmp = await LiquidGlassPreRenderService.RenderDirectAsync(
                spawnScreenCenter,
                Width,
                Height,
                CardBorder.CornerRadius.TopLeft,
                theme,
                isDark,
                screen,
                Screens.All);

            if (bmp != null)
            {
                LiquidGlassBgImage.Source = bmp;
                CardBorder.Background = Brushes.Transparent;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BigFolderPopupWindow] TriggerDirectLiquidGlassRender failed: {ex.Message}");
        }
    }

    private void SyncSettingsControls()
    {
        suppressSettingsEvents = true;
        try
        {
            FolderNameBox.Text = currentModel.FolderName ?? "";
            ColumnsNum.Value = Math.Clamp(currentModel.PopupColumns, 2, 8);
            IconSizeNum.Value = Math.Clamp(currentModel.PopupIconSize, 24, 80);
            SpacingNum.Value = Math.Clamp(currentModel.PopupSpacing, 0, 32);
            PaddingNum.Value = Math.Clamp(currentModel.PopupPadding, 6, 36);
            ShowNamesCheck.IsChecked = currentModel.PopupShowNames;
            ShowExtensionsCheck.IsChecked = currentModel.PopupShowExtensions;
        }
        finally
        {
            suppressSettingsEvents = false;
        }
    }

    private void PopulateItems()
    {
        var validPaths = currentModel.SafeItems
            .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            .Distinct()
            .ToList();

        string titleBase = !string.IsNullOrWhiteSpace(currentModel.FolderName)
            ? currentModel.FolderName
            : Locale.Folders_BigFolder_AllFiles;
        TitleText.Text = $"{titleBase} ({validPaths.Count})";

        int cols = Math.Clamp(currentModel.PopupColumns, 2, 8);
        int iconSize = Math.Clamp(currentModel.PopupIconSize, 24, 80);
        int spacing = Math.Clamp(currentModel.PopupSpacing, 0, 32);
        int pad = Math.Clamp(currentModel.PopupPadding, 6, 36);
        bool showNames = currentModel.PopupShowNames;
        bool showExt = currentModel.PopupShowExtensions;

        ItemsScrollViewer.Padding = new Thickness(pad, 4, pad, pad);

        // Uniform grid columns
        PopupGridColumns = cols;
        PopupItemsList.Tag = cols;

        var halfSpacing = spacing / 2.0;
        var itemMargin = new Thickness(halfSpacing, halfSpacing);
        double textWidth = Math.Max(50, iconSize * 1.8);

        bool isDark = ActualThemeVariant == ThemeVariant.Dark;
        var textForeground = isDark ? new SolidColorBrush(Color.FromArgb(230, 255, 255, 255))
                                    : new SolidColorBrush(Color.FromArgb(230, 25, 25, 25));

        var items = new List<BigFolderPopupItem>(validPaths.Count);
        foreach (var p in validPaths)
        {
            string name;
            if (showExt)
            {
                name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            else
            {
                name = Path.GetFileNameWithoutExtension(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            if (string.IsNullOrEmpty(name)) name = p;

            Bitmap? bmp = null;
            try { bmp = FolderIconService.GetIcon(p); } catch { }

            items.Add(new BigFolderPopupItem(
                Path: p,
                DisplayName: name,
                Icon: bmp,
                IconSize: iconSize,
                ShowName: showNames,
                Margin: itemMargin,
                TextWidth: textWidth,
                Foreground: textForeground
            ));
        }

        PopupItemsList.ItemsSource = items;
    }

    private void OnToggleSettingsClicked(object? sender, RoutedEventArgs e)
    {
        SettingsDrawer.IsVisible = !SettingsDrawer.IsVisible;
        if (SettingsDrawer.IsVisible)
        {
            FolderNameBox.Focus();
        }
    }

    private void OnNumericSettingChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        HandleSettingChanged();
    }

    private void OnSettingChanged(object? sender, RoutedEventArgs e)
    {
        HandleSettingChanged();
    }

    private void HandleSettingChanged()
    {
        if (suppressSettingsEvents) return;

        var newFolderName = string.IsNullOrWhiteSpace(FolderNameBox.Text) ? null : FolderNameBox.Text.Trim();
        var newCols = (int)(ColumnsNum.Value ?? 4);
        var newIconSize = (int)(IconSizeNum.Value ?? 40);
        var newSpacing = (int)(SpacingNum.Value ?? 8);
        var newPadding = (int)(PaddingNum.Value ?? 14);
        var newShowNames = ShowNamesCheck.IsChecked ?? true;
        var newShowExt = ShowExtensionsCheck.IsChecked ?? false;

        currentModel = currentModel with
        {
            FolderName = newFolderName,
            PopupColumns = newCols,
            PopupIconSize = newIconSize,
            PopupSpacing = newSpacing,
            PopupPadding = newPadding,
            PopupShowNames = newShowNames,
            PopupShowExtensions = newShowExt
        };

        PopulateItems();
        onModelChanged?.Invoke(currentModel);
    }

    private void OnFolderNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSettingChanged(sender, e);
            SettingsDrawer.IsVisible = false;
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        isClosing = true;
        UninstallMouseHook();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        isClosing = true;
        LiquidGlassSurfaceControl.IsVisible = false;
        UninstallMouseHook();
        LiquidGlassPreRenderService.PreRenderCompleted -= OnPreRenderCompleted;
    }

    private void InstallMouseHook()
    {
        if (hookHandle != IntPtr.Zero) return;
        try
        {
            mouseHookProc = HookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            IntPtr hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
            hookHandle = SetWindowsHookEx(WH_MOUSE_LL, mouseHookProc, hMod, 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to install mouse hook: {ex.Message}");
        }
    }

    private void UninstallMouseHook()
    {
        if (hookHandle != IntPtr.Zero)
        {
            try
            {
                UnhookWindowsHookEx(hookHandle);
            }
            catch { }
            hookHandle = IntPtr.Zero;
        }
        mouseHookProc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !isClosing && (DateTime.UtcNow - LoadedAtUtc).TotalMilliseconds > 150)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN
                    or WM_NCLBUTTONDOWN or WM_NCRBUTTONDOWN or WM_NCMBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var handle = TryGetPlatformHandle()?.Handle;
                if (handle.HasValue && GetWindowRect(handle.Value, out var rect))
                {
                    bool isInside = hookStruct.pt.X >= rect.Left && hookStruct.pt.X <= rect.Right &&
                                    hookStruct.pt.Y >= rect.Top && hookStruct.pt.Y <= rect.Bottom;
                    if (!isInside)
                    {
                        isClosing = true;
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                Close();
                            }
                            catch { }
                        });
                    }
                }
            }
        }

        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    public static void ShowPopup(
        BigFolderModel model,
        Point? screenCenter,
        Window? owner = null,
        Action<BigFolderModel>? onModelChanged = null)
    {
        if (PanelCoolingDown<BigFolderPopupWindow>())
            return;

        if (TryCloseActivePanel<BigFolderPopupWindow>())
            return;

        var popup = new BigFolderPopupWindow(model, screenCenter, onModelChanged);
        popup.ShowAsSecondaryPanel(owner);
    }

    private void ApplyWindowRegion()
    {
        var handle = TryGetPlatformHandle()?.Handle;
        if (handle == null) return;

        Theme theme;
        try
        {
            theme = new AppSettingsProvider().Get().Theme;
        }
        catch
        {
            theme = new Theme(DarkMode: true, AccentColor: null, OpacityLevel: 0.8, Monochrome: false, UseNativeFrame: false, FontFamily: "Inter");
        }

        if (theme.UsesNativeBlur)
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            double scaling = screen?.Scaling ?? 1.0;
            int width = (int)Math.Round(ClientSize.Width * scaling);
            int height = (int)Math.Round(ClientSize.Height * scaling);
            int radius = (int)Math.Round(18 * scaling);

            width = Math.Max(1, width);
            height = Math.Max(1, height);
            radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2);

            var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
            if (region != IntPtr.Zero)
            {
                if (SetWindowRgn(handle.Value, region, true) == 0)
                    DeleteObject(region);
            }
        }
        else
        {
            SetWindowRgn(handle.Value, IntPtr.Zero, true);
        }
    }

    protected override void OnPanelLoaded()
    {
        ApplyWindowRegion();
        if (LiquidGlassSurfaceControl.IsVisible)
        {
            LiquidGlassSurfaceControl.RequestRender(immediate: true);
        }
        InstallMouseHook();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Panel or Grid)
        {
            Close();
        }
    }

    protected override bool OnPanelEscape()
    {
        if (SettingsDrawer.IsVisible)
        {
            SettingsDrawer.IsVisible = false;
            return true;
        }
        return false;
    }

    private void OnItemClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: BigFolderPopupItem item })
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to start {item.Path}: {ex.Message}");
            }
            Close();
        }
    }
}

public record BigFolderPopupItem(
    string Path,
    string DisplayName,
    Bitmap? Icon,
    double IconSize = 36,
    bool ShowName = true,
    Thickness Margin = default,
    double TextWidth = 74,
    IBrush? Foreground = null);
