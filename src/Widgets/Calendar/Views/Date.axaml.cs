using Avalonia.Controls;
using Avalonia.Interactivity;
using Calendar.Models;
using Calendar.ViewModels;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Runtime;

namespace Calendar.Views;

/// <summary>
/// 2×2 date card: "周二 2月" header, a large day number and the Chinese lunisolar date stacked
/// vertically beside it. The host's normal 12 DIP content inset is removed at the view root so
/// the card is full-bleed. Colours are intentionally not baked into the view: the face consumes
/// the Calendar surface/accent/text tokens so it follows the app theme just like the other
/// Calendar widgets.
/// <para>
/// The model's <see cref="DateModel.ShowLunar"/> switch hides the lunar column; the face then
/// centres the day number in the free space. Settings changes recreate the content, so the
/// view model can keep this value read-only for its whole lifetime.
/// </para>
/// </summary>
public partial class Date : UserControl, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    private readonly DateCalendarViewModel viewModel;

    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "CalendarCardBackground";

    /// <summary>
    /// <paramref name="model"/> supplies the persisted <see cref="DateModel.ShowLunar"/> switch;
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline. Null keeps the always-on preview/legacy behaviour, matching <see cref="DateOne"/>.
    /// </summary>
    public Date(DateModel? model = null, IWidgetSession? session = null)
    {
        viewModel = new DateCalendarViewModel(session, model?.ShowLunar ?? true);
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <inheritdoc />
    public void OnSessionActivity(ActivitySnapshot activity) => viewModel.OnSessionActivity(activity);

    private void OnLoaded(object? sender, RoutedEventArgs e) => viewModel.Start();

    private void OnUnloaded(object? sender, RoutedEventArgs e) => viewModel.Stop();
}