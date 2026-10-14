using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Folders.Locales;
using Folders.Models;
using Folders.Services;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.Views;

namespace Folders.Views;

/// <summary>
/// The big folder widget's secondary panel: the grid of contained files with an inline
/// settings drawer. The shell (W7) plus <see cref="SecondaryPanelWindow"/> and
/// <see cref="PanelAppearanceResolver"/> own the card material, the chrome and the open/close
/// transitions. This window keeps its business behaviour and its three risk-carrying
/// specialisations:
/// <list type="bullet">
/// <item><see cref="TransformAnimationEnabled"/> stays conditional on the native-blur theme:
/// the native window region (SetWindowRgn) cannot follow a render transform, so the base
/// falls back to the opacity-only animation.</item>
/// <item><see cref="DeactivateCloseGraceMs"/> keeps the 150 ms focus-loss grace.</item>
/// <item><see cref="OnPanelEscape"/> closes the settings drawer first; the second Escape
/// closes the panel.</item>
/// </list>
/// File drag-drop onto the widget, icon loading and the file-grid interactions stay here too.
/// </summary>
public partial class BigFolderPopupWindow : SecondaryPanelWindow
{
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

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

    protected override Point? SpawnScreenCenter => spawnScreenCenter;
    protected override bool TransformAnimationEnabled => transformAnimationEnabled;
    protected override int DeactivateCloseGraceMs => 150;

    public BigFolderPopupWindow() : this(new BigFolderModel(), null, null) { }

    public BigFolderPopupWindow(List<string> items) : this(new BigFolderModel(items), null, null) { }

    public BigFolderPopupWindow(
        BigFolderModel model,
        Point? screenCenter = null,
        Action<BigFolderModel>? onModelChanged = null,
        double? cornerRadius = null)
    {
        currentModel = model;
        spawnScreenCenter = screenCenter;
        this.onModelChanged = onModelChanged;
        transformAnimationEnabled = !UsesNativeBlurTheme();

        InitializeComponent();
        InitializePanel(cornerRadius);

        SyncSettingsControls();
        ApplyContentTheme();
        PopulateItems();
    }

    private static bool UsesNativeBlurTheme()
    {
        // Historic semantics kept: any failure to read the settings (no provider, headless,
        // early start-up) leaves the transform animation ON — only a real native-blur theme
        // turns it off.
        try { return PanelAppearanceResolver.TryResolveSettingsProvider()!.Get().Theme.UsesNativeBlur; }
        catch { return false; }
    }

    /// <summary>Content-only tint: the header action button keeps the popup's historic
    /// secondary chrome colour. The shell's title and the shared close button are painted by
    /// the appearance binder; the card, the glass and the transparency level too.</summary>
    private void ApplyContentTheme()
    {
        var theme = PanelAppearanceResolver.ReadTheme(PanelAppearanceResolver.TryResolveSettingsProvider());
        bool isDark = PanelAppearanceResolver.IsDark(theme, ActualThemeVariant);

        SettingsButton.Foreground = isDark
            ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
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
        PanelShell!.Title = $"{titleBase} ({validPaths.Count})";

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

    public static void ShowPopup(
        BigFolderModel model,
        Point? screenCenter,
        Window? owner = null,
        Action<BigFolderModel>? onModelChanged = null,
        double? cornerRadius = null)
    {
        if (PanelCoolingDown<BigFolderPopupWindow>())
            return;

        if (TryCloseActivePanel<BigFolderPopupWindow>())
            return;

        var popup = new BigFolderPopupWindow(model, screenCenter, onModelChanged, cornerRadius);
        popup.ShowAsSecondaryPanel(owner);
    }

    private void ApplyWindowRegion()
    {
        var handle = TryGetPlatformHandle()?.Handle;
        if (handle == null) return;

        var theme = PanelAppearanceResolver.ReadTheme(PanelAppearanceResolver.TryResolveSettingsProvider());

        if (theme.UsesNativeBlur)
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            double scaling = screen?.Scaling ?? 1.0;
            int width = (int)Math.Round(ClientSize.Width * scaling);
            int height = (int)Math.Round(ClientSize.Height * scaling);
            // Match the card's own corner radius (propagated from the opening widget),
            // so the native blur region and the visual card share the same curvature.
            int radius = (int)Math.Round((SpawnCornerRadius > 0 ? SpawnCornerRadius : 18) * scaling);

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
