using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Progress.Models;
using Progress.ViewModels;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Runtime;

namespace Progress.Views;

/// <summary>
/// 通过 <see cref="IWidgetSurfaceHints"/> 向宿主声明多彩模式下的专属卡片画刷
/// （资源键按变体解析），宿主不再按视图类型名判断外观（W10 §9.1）。
/// </summary>
public partial class ProgressView : UserControl, IWidgetSelfRefreshing, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "ProgressCardBackground";

    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private readonly IAppSettingsProvider? appSettingsProvider;
    private readonly IWidgetSession? session;
    private ProgressModel model;
    private ProgressViewModel? viewModel;

    public ProgressView() : this(new ProgressModel()) { }

    public ProgressView(ProgressModel model) : this(model, null, null) { }

    public ProgressView(IWidgetLayoutProvider widgetLayoutProvider)
        : this(new ProgressModel(), widgetLayoutProvider, null) { }

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline (a stable instance identity): the view model then gates its five-second tick
    /// on the session's activity. Null keeps the legacy always-on behaviour (preview, legacy
    /// layout entries) — those hosts never push activity either.
    /// </summary>
    public ProgressView(ProgressModel model, IWidgetLayoutProvider? widgetLayoutProvider, IAppSettingsProvider? appSettingsProvider, IWidgetSession? session = null)
    {
        this.model = model;
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.appSettingsProvider = appSettingsProvider;
        this.session = session;
        viewModel = new ProgressViewModel(model, appSettingsProvider, session, themeAnchor: this);
        DataContext = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeVariantChanged += (_, _) => viewModel?.UpdateProgress();
    }

    /// <inheritdoc />
    public void OnSessionActivity(ActivitySnapshot activity) => viewModel?.OnSessionActivity(activity);

    /// <summary>
    /// The view can be detached and re-added later (the settings window caches pages and the
    /// Gallery keeps a live preview control), so the five-second tick and the app-settings
    /// subscription released by <see cref="OnUnloaded"/> are restarted here — on a *new* view
    /// model, because the released one was disposed.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var vm = viewModel;
        if (vm == null)
        {
            vm = new ProgressViewModel(model, appSettingsProvider, session, themeAnchor: this);
            viewModel = vm;
            DataContext = vm;
        }

        vm.Start();
    }

    /// <summary>
    /// Release the view model without detaching this handler: the control stays usable and is
    /// unloaded again on every later removal from the visual tree.
    /// </summary>
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        viewModel?.Dispose();
        viewModel = null;
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout?.Settings is { } settings && settings.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var newModel = settings.Deserialize<ProgressModel>();
                if (newModel != null)
                {
                    // Keep the model for a view model that is rebuilt on the next load.
                    model = newModel;
                    viewModel?.UpdateModel(newModel);
                }
            }
            catch
            {
                // Ignored
            }
        }
    }
}
