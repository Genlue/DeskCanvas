using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
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
    private readonly string colorHex = "#534AB7";
    private bool darkTheme;
    private IBrush nameBrush = Brushes.Black;
    private IBrush subBrush = Brushes.Black;

    public ShortcutTileViewModel(ShortcutDefinition shortcut)
    {
        ShortcutId = shortcut.Id;
        Name = shortcut.Name;
        GlyphGeometry = GlyphLibrary.GetGeometry(shortcut.Glyph);
        colorHex = shortcut.Color;
        var actionCount = shortcut.ActionCount;
        ActionCountText = actionCount == 1
            ? Loc.Get("Shortcuts_Actions_CountOne")
            : Loc.Format("Shortcuts_Actions_Count", actionCount);

        TintBrush = Visuals.Tint(shortcut.Color, 0x2E);
        IconBrush = Visuals.Brush(shortcut.Color);
        nameBrush = Visuals.Shade(shortcut.Color);
        subBrush = Visuals.Brush(shortcut.Color);
    }

    private ShortcutTileViewModel()
    {
        IsCreate = true;
        Name = Loc.Get("Shortcuts_Tile_New");
        GlyphGeometry = GlyphLibrary.GetGeometry("plus");
        ActionCountText = Loc.Get("Shortcuts_Tile_TapToCreate");
        TintBrush = Brushes.Transparent;
        IconBrush = new SolidColorBrush(Color.Parse("#B4B2A9"));
        nameBrush = CreateNameLight;
        subBrush = CreateSubLight;
    }

    /// <summary>The trailing "new shortcut" tile.</summary>
    public static ShortcutTileViewModel CreateNew() => new();

    public bool IsCreate { get; }

    public string ShortcutId { get; } = string.Empty;

    public string Name { get; }

    public Geometry? GlyphGeometry { get; }

    public string ActionCountText { get; }

    public IBrush TintBrush { get; }

    public IBrush IconBrush { get; }

    public IBrush NameBrush => nameBrush;

    public IBrush SubBrush => subBrush;

    // Create-tile neutrals, light/dark pairs (the tile sits on the card, not on a tint).
    private static readonly IBrush CreateNameLight = new SolidColorBrush(Color.Parse("#5F5E5A"));
    private static readonly IBrush CreateNameDark = new SolidColorBrush(Color.Parse("#C8C6BE"));
    private static readonly IBrush CreateSubLight = new SolidColorBrush(Color.Parse("#888780"));
    private static readonly IBrush CreateSubDark = new SolidColorBrush(Color.Parse("#9B9A92"));

    /// <summary>
    /// Swap the text brushes for the active theme. <see cref="Visuals.Shade"/> only darkens
    /// (text on a light tint); on a dark card the same darkened accent sits at ~1.2:1 against
    /// the tile and disappears — the dark variant lightens instead, so names and subtitles stay
    /// readable on both variants. Called by the card/panel views on construction and on every
    /// ActualThemeVariant change.
    /// </summary>
    public void ApplyTheme(bool isDark)
    {
        if (darkTheme == isDark) return;
        darkTheme = isDark;

        if (IsCreate)
        {
            nameBrush = isDark ? CreateNameDark : CreateNameLight;
            subBrush = isDark ? CreateSubDark : CreateSubLight;
        }
        else
        {
            nameBrush = isDark ? Visuals.Lighten(colorHex, 1.65) : Visuals.Shade(colorHex);
            subBrush = isDark ? Visuals.Lighten(colorHex, 1.3) : Visuals.Brush(colorHex);
        }

        OnPropertyChanged(nameof(NameBrush));
        OnPropertyChanged(nameof(SubBrush));
        OnPropertyChanged(nameof(StateBrush));
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
