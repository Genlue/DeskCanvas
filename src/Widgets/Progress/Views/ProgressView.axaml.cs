using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Progress.Models;
using Progress.ViewModels;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace Progress.Views;

public partial class ProgressView : UserControl, IWidgetSelfRefreshing
{
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private readonly IAppSettingsProvider? appSettingsProvider;
    private ProgressModel model;
    private ProgressViewModel? viewModel;

    public ProgressView() : this(new ProgressModel()) { }

    public ProgressView(ProgressModel model) : this(model, null, null) { }

    public ProgressView(IWidgetLayoutProvider widgetLayoutProvider)
        : this(new ProgressModel(), widgetLayoutProvider, null) { }

    public ProgressView(ProgressModel model, IWidgetLayoutProvider? widgetLayoutProvider, IAppSettingsProvider? appSettingsProvider)
    {
        this.model = model;
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.appSettingsProvider = appSettingsProvider;
        viewModel = new ProgressViewModel(model, appSettingsProvider);
        DataContext = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeVariantChanged += (_, _) => viewModel?.UpdateProgress();
    }

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
            vm = new ProgressViewModel(model, appSettingsProvider);
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
