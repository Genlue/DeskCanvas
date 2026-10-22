using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using DeskCanvas.Core.Models.Settings;
using Shortcuts.Models;
using Shortcuts.Services;

namespace Shortcuts.ViewModels;

public enum TileState
{
    Idle,
    Running,
    Done,
    Failed,
}

/// <summary>One tile on the card: a shortcut, or the "new shortcut" entry.</summary>
public sealed class ShortcutTileViewModel : INotifyPropertyChanged
{
    private TileState state = TileState.Idle;
    private string stateText = string.Empty;
    private double progress;
    private bool showSubtitle = true;
    private string colorHex = "#534AB7";
    private bool darkTheme;
    private IBrush nameBrush = Brushes.Black;
    private IBrush subBrush = Brushes.Black;
    private string name = string.Empty;
    private Geometry? glyphGeometry;
    private string actionCountText = string.Empty;
    private IBrush tintBrush = Brushes.Transparent;
    private IBrush iconBrush = Brushes.Gray;
    private IBrush glyphStrokeBrush = Brushes.White;
    private string hotkeyText = string.Empty;

    // Corner geometry: the outer tile follows the card radius, the icon block is concentric
    // inside the 6px vertical padding. Both are VM properties so compiled bindings can update
    // them when the card radius or the tile size changes (never computed only in the ctor).
    private double cardRadius = 16;
    private double tileWidth;
    private double tileHeight;
    private CornerRadius tileCornerRadius = new(10);
    private CornerRadius iconCornerRadius = new(4);

    public ShortcutTileViewModel(ShortcutDefinition shortcut)
    {
        ShortcutId = shortcut.Id;
        IsCreate = false;
        Apply(shortcut);
        RecomputeCornerRadius();
    }

    private ShortcutTileViewModel()
    {
        IsCreate = true;
        Name = Loc.Get("Shortcuts_Tile_New");
        GlyphGeometry = GlyphLibrary.GetGeometry("plus");
        ActionCountText = Loc.Get("Shortcuts_Tile_TapToCreate");
        TintBrush = Brushes.Transparent;
        IconBrush = new SolidColorBrush(Color.Parse("#B4B2A9"));
        HotkeyText = string.Empty;
        nameBrush = CreateNameLight;
        subBrush = CreateSubLight;
        RecomputeCornerRadius();
    }

    /// <summary>The trailing "new shortcut" tile.</summary>
    public static ShortcutTileViewModel CreateNew() => new();

    public bool IsCreate { get; }

    public string ShortcutId { get; } = string.Empty;

    public string Name
    {
        get => name;
        private set
        {
            if (name == value) return;
            name = value;
            OnPropertyChanged();
        }
    }

    public Geometry? GlyphGeometry
    {
        get => glyphGeometry;
        private set
        {
            if (ReferenceEquals(glyphGeometry, value)) return;
            glyphGeometry = value;
            OnPropertyChanged();
        }
    }

    public string ActionCountText
    {
        get => actionCountText;
        private set
        {
            if (actionCountText == value) return;
            actionCountText = value;
            OnPropertyChanged();
        }
    }

    public IBrush TintBrush
    {
        get => tintBrush;
        private set
        {
            if (ReferenceEquals(tintBrush, value)) return;
            tintBrush = value;
            OnPropertyChanged();
        }
    }

    public IBrush IconBrush
    {
        get => iconBrush;
        private set
        {
            if (ReferenceEquals(iconBrush, value)) return;
            iconBrush = value;
            OnPropertyChanged();
        }
    }

    public IBrush GlyphStrokeBrush
    {
        get => glyphStrokeBrush;
        private set
        {
            if (ReferenceEquals(glyphStrokeBrush, value)) return;
            glyphStrokeBrush = value;
            OnPropertyChanged();
        }
    }

    public IBrush NameBrush => nameBrush;

    public IBrush SubBrush => subBrush;

    /// <summary>Canonical hotkey text ("Ctrl+Alt+1"), or empty when unbound.</summary>
    public string HotkeyText
    {
        get => hotkeyText;
        private set
        {
            if (hotkeyText == value) return;
            hotkeyText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHotkeyBadge));
            OnPropertyChanged(nameof(ShowActionCountText));
        }
    }

    /// <summary>
    /// The badge needs ~52px beside the icon; below that the whole line is too narrow and the
    /// badge is omitted rather than covering the title. A 2x2 tile (~130px) still qualifies and
    /// drops the action-count text instead (see <see cref="ShowActionCountText"/>).
    /// </summary>
    public bool HasHotkeyBadge =>
        !IsCreate && !string.IsNullOrEmpty(hotkeyText) && ShowSubtitle && tileWidth >= 106;

    /// <summary>Hide the secondary count when the badge needs its row; never squeeze the title.</summary>
    public bool ShowActionCountText => !HasHotkeyBadge || tileWidth >= 200;

    /// <summary>Outer tile corner radius (follows the host card radius and shrinks with the tile).</summary>
    public CornerRadius TileCornerRadius => tileCornerRadius;

    /// <summary>Icon-block corner radius: outer − 6px vertical padding, floor 4.</summary>
    public CornerRadius IconCornerRadius => iconCornerRadius;

    // Create-tile neutrals, light/dark pairs (the tile sits on the card, not on a tint).
    private static readonly IBrush CreateNameLight = new SolidColorBrush(Color.Parse("#5F5E5A"));
    private static readonly IBrush CreateNameDark = new SolidColorBrush(Color.Parse("#C8C6BE"));
    private static readonly IBrush CreateSubLight = new SolidColorBrush(Color.Parse("#888780"));
    private static readonly IBrush CreateSubDark = new SolidColorBrush(Color.Parse("#9B9A92"));

    /// <summary>
    /// Refresh the tile from the shared document. Rebuild reuses instances, so name, glyph,
    /// color, action count and hotkey must move onto the existing tile instead of staying at
    /// the values captured when it was first created.
    /// </summary>
    public void Apply(ShortcutDefinition shortcut)
    {
        Name = shortcut.Name;
        GlyphGeometry = GlyphLibrary.GetGeometry(shortcut.Glyph);
        colorHex = shortcut.Color;

        var actionCount = shortcut.ActionCount;
        ActionCountText = actionCount == 1
            ? Loc.Get("Shortcuts_Actions_CountOne")
            : Loc.Format("Shortcuts_Actions_Count", actionCount);

        HotkeyText = shortcut.Hotkey ?? string.Empty;
        UpdateVisualBrushes();
    }

    /// <summary>Card radius (already scaled by the host) used to derive the tile radius.</summary>
    public void SetCardRadius(double radius)
    {
        if (Math.Abs(cardRadius - radius) < 0.001) return;
        cardRadius = radius;
        RecomputeCornerRadius();
    }

    /// <summary>Actual tile (button content) size; drives the "shrink when the tile is tiny" rule.</summary>
    public void SetTileSize(double width, double height)
    {
        if (Math.Abs(tileWidth - width) < 0.001 && Math.Abs(tileHeight - height) < 0.001) return;
        tileWidth = width;
        tileHeight = height;
        RecomputeCornerRadius();
        // The badge competes with the subtitle for the text column; hide it when the tile is
        // too narrow to keep the title readable (the 1x1 tier also hides the whole line).
        OnPropertyChanged(nameof(HasHotkeyBadge));
        OnPropertyChanged(nameof(ShowActionCountText));
    }

    private void RecomputeCornerRadius()
    {
        double outer;
        if (cardRadius <= 0.01)
        {
            // Frameless / native-frame host: keep the tile square like the card.
            outer = 0;
        }
        else
        {
            var baseRadius = Math.Clamp(cardRadius * 0.62, 8, 16);
            var minSide = tileWidth > 0 && tileHeight > 0 ? Math.Min(tileWidth, tileHeight) : 44;
            var shrinkLimit = Math.Max(6, minSide * 0.30);
            outer = Math.Clamp(Math.Round(Math.Min(baseRadius, shrinkLimit), 1), 6, 16);
        }

        var inner = outer <= 0 ? 0 : Math.Max(4, Math.Round(outer - 6, 1));
        SetCornerRadius(new CornerRadius(outer), new CornerRadius(inner));
    }

    private void SetCornerRadius(CornerRadius outer, CornerRadius inner)
    {
        var outerChanged = Math.Abs(tileCornerRadius.TopLeft - outer.TopLeft) > 0.01;
        var innerChanged = Math.Abs(iconCornerRadius.TopLeft - inner.TopLeft) > 0.01;
        if (!outerChanged && !innerChanged) return;

        tileCornerRadius = outer;
        iconCornerRadius = inner;
        OnPropertyChanged(nameof(TileCornerRadius));
        OnPropertyChanged(nameof(IconCornerRadius));
    }

    /// <summary>
    /// Swap the brushes for the active theme. In monochrome mode, adapts icon, glyph stroke,
    /// tint, and text colors to the configured monochrome variant.
    /// </summary>
    public void ApplyTheme(bool isDark)
    {
        darkTheme = isDark;
        UpdateVisualBrushes();
    }

    private void UpdateVisualBrushes()
    {
        IBrush nextName;
        IBrush nextSub;
        IBrush nextIcon;
        IBrush nextGlyphStroke;
        IBrush nextTint;

        if (IsCreate)
        {
            nextName = darkTheme ? CreateNameDark : CreateNameLight;
            nextSub = darkTheme ? CreateSubDark : CreateSubLight;
            nextIcon = new SolidColorBrush(Color.Parse("#B4B2A9"));
            nextGlyphStroke = Brushes.White;
            nextTint = Brushes.Transparent;
        }
        else
        {
            var theme = ShortcutsRuntime.AppSettings?.Get()?.Theme;
            var isMonochrome = theme != null && !theme.IsColorful && theme.Monochrome;

            if (isMonochrome)
            {
                var variant = darkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

                // Resolve IconBrush
                if (Application.Current != null &&
                    Application.Current.TryGetResource("SystemControlForegroundAccentBrush", variant, out var ab) &&
                    ab is IBrush accentB)
                {
                    nextIcon = accentB;
                }
                else if (theme != null && theme.EffectiveMonochromeVariant == MonochromeStyle.BlackWhite)
                {
                    nextIcon = darkTheme ? Brushes.White : Brushes.Black;
                }
                else if (theme != null && theme.EffectiveMonochromeVariant == MonochromeStyle.BackgroundColor)
                {
                    var hex = darkTheme ? theme.EffectiveSolidBackgroundLight : theme.EffectiveSolidBackgroundDark;
                    nextIcon = new SolidColorBrush(Color.TryParse(hex, out var bg) ? bg : (darkTheme ? Colors.White : Colors.Black));
                }
                else
                {
                    var accent = theme?.AccentColor;
                    if (!string.IsNullOrWhiteSpace(accent) && Color.TryParse(accent, out var parsedAccent))
                        nextIcon = new SolidColorBrush(parsedAccent);
                    else
                        nextIcon = darkTheme ? new SolidColorBrush(Color.Parse("#70A5FF")) : new SolidColorBrush(Color.Parse("#0078D4"));
                }

                // Resolve GlyphStrokeBrush (high contrast against IconBrush)
                if (Application.Current != null &&
                    Application.Current.TryGetResource("AccentContrastForegroundBrush", variant, out var cb) &&
                    cb is IBrush contrastB)
                {
                    nextGlyphStroke = contrastB;
                }
                else
                {
                    nextGlyphStroke = darkTheme ? Brushes.Black : Brushes.White;
                }

                // Resolve TintBrush (subtle card pill background)
                if (nextIcon is ISolidColorBrush scb)
                {
                    nextTint = new SolidColorBrush(Color.FromArgb(0x1C, scb.Color.R, scb.Color.G, scb.Color.B));
                }
                else
                {
                    nextTint = darkTheme
                        ? new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255))
                        : new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0));
                }

                // Resolve NameBrush & SubBrush
                if (Application.Current != null &&
                    Application.Current.TryGetResource("SystemControlForegroundBaseHighBrush", variant, out var bh) &&
                    bh is IBrush highBrush)
                {
                    nextName = highBrush;
                }
                else
                {
                    nextName = darkTheme ? Brushes.White : Brushes.Black;
                }

                if (Application.Current != null &&
                    Application.Current.TryGetResource("SystemControlForegroundBaseMediumBrush", variant, out var bm) &&
                    bm is IBrush medBrush)
                {
                    nextSub = medBrush;
                }
                else
                {
                    nextSub = darkTheme ? new SolidColorBrush(Color.Parse("#A0A0A0")) : new SolidColorBrush(Color.Parse("#606060"));
                }
            }
            else
            {
                nextIcon = Visuals.Brush(colorHex);
                nextGlyphStroke = Brushes.White;
                nextTint = Visuals.Tint(colorHex, 0x2E);
                nextName = darkTheme ? Visuals.Lighten(colorHex, 1.65) : Visuals.Shade(colorHex);
                nextSub = darkTheme ? Visuals.Lighten(colorHex, 1.3) : Visuals.Brush(colorHex);
            }
        }

        if (!ReferenceEquals(iconBrush, nextIcon))
        {
            iconBrush = nextIcon;
            OnPropertyChanged(nameof(IconBrush));
        }

        if (!ReferenceEquals(glyphStrokeBrush, nextGlyphStroke))
        {
            glyphStrokeBrush = nextGlyphStroke;
            OnPropertyChanged(nameof(GlyphStrokeBrush));
        }

        if (!ReferenceEquals(tintBrush, nextTint))
        {
            tintBrush = nextTint;
            OnPropertyChanged(nameof(TintBrush));
        }

        if (!ReferenceEquals(nameBrush, nextName))
        {
            nameBrush = nextName;
            OnPropertyChanged(nameof(NameBrush));
        }

        if (!ReferenceEquals(subBrush, nextSub))
        {
            subBrush = nextSub;
            OnPropertyChanged(nameof(SubBrush));
            OnPropertyChanged(nameof(StateBrush));
        }
    }

    public TileState State
    {
        get => state;
        private set
        {
            if (state == value) return;
            state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateVisible));
            OnPropertyChanged(nameof(SubtitleSlotVisible));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(RunGlyphGeometry));
            OnPropertyChanged(nameof(HasHotkeyBadge));
        }
    }

    /// <summary>Play/stop glyph of the row's run button (panel list).</summary>
    public Geometry? RunGlyphGeometry =>
        GlyphLibrary.GetGeometry(State == TileState.Running ? "stop" : "play");

    public string StateText
    {
        get => stateText;
        private set
        {
            if (stateText == value) return;
            stateText = value;
            OnPropertyChanged();
        }
    }

    public double Progress
    {
        get => progress;
        private set
        {
            if (Math.Abs(progress - value) < 0.001) return;
            progress = value;
            OnPropertyChanged();
        }
    }

    public bool IsRunning => State == TileState.Running;

    public bool StateVisible => State != TileState.Idle;

    /// <summary>The action-count line; hidden by the 1x1 layout where width is scarce.</summary>
    public bool ShowSubtitle
    {
        get => showSubtitle;
        set
        {
            if (showSubtitle == value) return;
            showSubtitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleSlotVisible));
            OnPropertyChanged(nameof(HasHotkeyBadge));
            OnPropertyChanged(nameof(ShowActionCountText));
        }
    }

    /// <summary>The count line only shows while the tile is idle and the layout has room.</summary>
    public bool SubtitleSlotVisible => ShowSubtitle && !StateVisible;

    // Cached brushes (Visuals returns shared, never-mutated instances): the getter is bound in
    // XAML and read on every state change, so it must not allocate per access. The Done/Failed
    // pairs carry a dark-theme variant: the dark greens/reds are unreadable on a dark card.
    private static readonly IBrush DoneBrush = Visuals.Brush("#3B6D11");
    private static readonly IBrush FailedBrush = Visuals.Brush("#A32D2D");
    private static readonly IBrush DoneBrushDark = Visuals.Lighten("#3B6D11", 1.55);
    private static readonly IBrush FailedBrushDark = Visuals.Lighten("#A32D2D", 1.6);

    public IBrush StateBrush => State switch
    {
        TileState.Done => darkTheme ? DoneBrushDark : DoneBrush,
        TileState.Failed => darkTheme ? FailedBrushDark : FailedBrush,
        _ => SubBrush,
    };

    /// <summary>Run state version, so a stale delayed reset cannot clobber a newer run.</summary>
    private int stateVersion;

    public int StateVersion => stateVersion;

    public void SetRunning(int stepIndex, int totalSteps)
    {
        stateVersion++;
        State = TileState.Running;
        Progress = totalSteps <= 0 ? 0 : Math.Clamp((double)stepIndex / totalSteps, 0, 1);
        StateText = Loc.Format("Shortcuts_Tile_Step",
            Math.Clamp(stepIndex, 1, Math.Max(totalSteps, 1)), Math.Max(totalSteps, 1));
    }

    public void SetFinished(bool success, string? detail)
    {
        stateVersion++;
        State = success ? TileState.Done : TileState.Failed;
        Progress = success ? 1 : Progress;
        StateText = success
            ? Loc.Get("Shortcuts_Tile_Done")
            : string.IsNullOrEmpty(detail) ? Loc.Get("Shortcuts_Tile_Failed") : detail;
    }

    public void SetCancelled()
    {
        stateVersion++;
        State = TileState.Idle;
        Progress = 0;
        StateText = string.Empty;
    }

    public void ResetIfVersion(int version)
    {
        if (version != stateVersion) return;
        State = TileState.Idle;
        Progress = 0;
        StateText = string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
