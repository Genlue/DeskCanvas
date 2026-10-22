using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// A settings section whose rows fold away behind their own title — the 应用主题 › 液态玻璃 affordance,
/// extracted so every page that needs a "these knobs are optional" fold looks and behaves the same
/// instead of copying the button + two chevrons + visibility binding per section.
/// <para>
/// Collapsed by default: a fold that starts open is just a header with extra chrome, and the whole
/// point of the fold is that the page can be read top-to-bottom without the expert rows.
/// </para>
/// <para>
/// <see cref="IsExpanded"/> is view state, not a setting: it is bound to a plain property on the
/// page's view model (the same way <c>AppearanceViewModel.OpticsExpanded</c> works), because "I
/// opened this fold once" is about this visit, not about how the app should look next launch.
/// </para>
/// <para>
/// Rows go in <see cref="SectionContent"/> (not <c>Content</c>): this control's XAML <b>is</b> its
/// content, so a child element would replace the header instead of sitting under it. The state bits
/// are pushed from <see cref="Sync"/> rather than bound, because a UserControl cannot reliably
/// reach its own properties through a parent binding — and one writer is easier to follow than five.
/// </para>
/// </summary>
public partial class CollapsibleSection : UserControl
{
    /// <summary>The section title, drawn as the fold's clickable header.</summary>
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<CollapsibleSection, string>(nameof(Header), string.Empty);

    /// <summary>
    /// Whether the rows are shown. Two-way by default so a page can bind its own property and get
    /// the click written back without spelling out the mode at every use site.
    /// </summary>
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<CollapsibleSection, bool>(
            nameof(IsExpanded),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The rows the fold hides. Shown only while <see cref="IsExpanded"/>.</summary>
    public static readonly StyledProperty<Control?> SectionContentProperty =
        AvaloniaProperty.Register<CollapsibleSection, Control?>(nameof(SectionContent));

    private readonly Button headerButton;
    private readonly TextBlock headerText;
    private readonly PathIcon chevronClosed;
    private readonly PathIcon chevronOpen;
    private readonly Panel body;

    public CollapsibleSection()
    {
        InitializeComponent();

        headerButton = this.FindControl<Button>("HeaderButton")!;
        headerText = this.FindControl<TextBlock>("HeaderText")!;
        chevronClosed = this.FindControl<PathIcon>("ChevronClosed")!;
        chevronOpen = this.FindControl<PathIcon>("ChevronOpen")!;
        body = this.FindControl<Panel>("Body")!;

        Sync();
    }

    public string Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public Control? SectionContent
    {
        get => GetValue(SectionContentProperty);
        set => SetValue(SectionContentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HeaderProperty
            || change.Property == IsExpandedProperty
            || change.Property == SectionContentProperty)
        {
            Sync();
        }
    }

    private void OnHeaderClick(object? sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;

    /// <summary>
    /// Write the chrome from the three properties. The only place the header's text, the two
    /// chevrons and the body's visibility are set, so the fold cannot render a state it does not
    /// have.
    /// </summary>
    private void Sync()
    {
        // A property set from XAML can land while InitializeComponent is still running, i.e. before
        // the parts are resolved; the constructor's own call comes after them.
        if (headerText == null) return;

        headerText.Text = Header;
        chevronClosed.IsVisible = !IsExpanded;
        chevronOpen.IsVisible = IsExpanded;

        // One child, owned by this fold: adopt the rows the first time they are seen and follow a
        // replacement, without detaching them on every unrelated property change.
        if (SectionContent is { } content)
        {
            if (body.Children.Count == 0 || !ReferenceEquals(body.Children[0], content))
            {
                body.Children.Clear();
                body.Children.Add(content);
            }
        }
        else if (body.Children.Count > 0)
        {
            body.Children.Clear();
        }

        body.IsVisible = IsExpanded;

        // Screen readers name the button after the section, which is the only text it shows.
        AutomationProperties.SetName(headerButton, Header);
    }
}
