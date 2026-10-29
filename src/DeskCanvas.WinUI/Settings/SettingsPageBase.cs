using System;
using Microsoft.UI.Xaml;

namespace DeskCanvas.WinUI.Settings;

/// <summary>
/// 设置页面的最小基类：把 <see cref="ISettingsPage"/> 的对称生命周期做成幂等状态机，
/// 派生类只实现 <see cref="OnAttach"/>/<see cref="OnDetach"/>（以及 <see cref="View"/>）。
/// <para>
/// 适用：正文由代码构造的页面（根元素在 <see cref="View"/> 里给出）。
/// XAML 页面（UserControl）不能同时继承基类，请在页面里直接实现 <see cref="ISettingsPage"/>，
/// 或把状态机委托给 <see cref="SettingsPageLifetime"/>——两种写法都在
/// <c>Settings/Pages/PagePlaceholder</c> 里演示。
/// </para>
/// </summary>
public abstract class SettingsPageBase : ISettingsPage
{
    private bool attached;

    /// <inheritdoc />
    public abstract UIElement View { get; }

    /// <summary>当前页面的上下文；仅在 Attach 与 Detach 之间非空。</summary>
    protected SettingsPageContext? Context { get; private set; }

    /// <summary>是否处于已附加（可见）状态；重复 Attach/Detach 是安全的。</summary>
    protected bool IsAttached => attached;

    /// <inheritdoc />
    public void Attach(SettingsPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (attached) return;

        attached = true;
        Context = context;
        OnAttach(context);
    }

    /// <inheritdoc />
    public void Detach()
    {
        if (!attached) return;
        attached = false;

        try
        {
            OnDetach();
        }
        finally
        {
            Context = null;
        }
    }

    /// <summary>进入可见状态：在这里订阅事件、启动计时器/异步加载。</summary>
    protected virtual void OnAttach(SettingsPageContext context)
    {
    }

    /// <summary>离开可见状态：在这里退订、取消请求、释放只属于可见期的资源。必须幂等。</summary>
    protected virtual void OnDetach()
    {
    }

    /// <summary>取一个必需服务；未注册时抛出带页面键的明确异常。</summary>
    protected T GetRequiredService<T>()
        where T : class
        => Context?.GetRequiredService<T>()
           ?? throw new InvalidOperationException("Settings page is not attached.");

    /// <summary>取一个可选服务；未注册时返回 null。</summary>
    protected T? GetService<T>()
        where T : class
        => Context?.GetService<T>();
}

/// <summary>
/// XAML 页面（<c>UserControl</c>）用的生命周期样板：把 <see cref="ISettingsPage.Attach"/>/
/// <see cref="Detach"/> 转发进来，保证「重复 Attach 只跑一次、Detach 幂等、Context 在 Detach 后清空」。
/// <example>
/// <code>
/// public sealed partial class GeneralPage : UserControl, ISettingsPage
/// {
///     private readonly SettingsPageLifetime lifetime = new();
///
///     public GeneralPage() => InitializeComponent();
///
///     public UIElement View => this;
///
///     public void Attach(SettingsPageContext context) => lifetime.Attach(context, OnAttachPage);
///
///     public void Detach() => lifetime.Detach(OnDetachPage);
/// }
/// </code>
/// </example>
/// </summary>
public sealed class SettingsPageLifetime
{
    private bool attached;

    /// <summary>当前上下文；Detach 后为 null。</summary>
    public SettingsPageContext? Context { get; private set; }

    public bool IsAttached => attached;

    /// <summary>
    /// 幂等进入：第一次调用时设置 <see cref="Context"/> 并执行 <paramref name="onAttach"/>；
    /// 重复调用直接返回，避免页面被重复订阅。

    /// </summary>
    public void Attach(SettingsPageContext context, Action<SettingsPageContext> onAttach)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(onAttach);
        if (attached) return;

        attached = true;
        Context = context;
        onAttach(context);
    }

    /// <summary>
    /// 幂等离开：执行 <paramref name="onDetach"/> 后清空 <see cref="Context"/>；
    /// <paramref name="onDetach"/> 抛异常也会清空上下文，避免页面停在半释放状态。

    /// </summary>
    public void Detach(Action onDetach)
    {
        if (!attached) return;
        attached = false;

        try
        {
            onDetach?.Invoke();
        }
        finally
        {
            Context = null;
        }
    }

    /// <summary>已附加时取必需服务。</summary>
    public T GetRequiredService<T>(string pageKey)
        where T : class
        => Context?.GetRequiredService<T>()
           ?? throw new InvalidOperationException($"Settings page '{pageKey}' is not attached.");
}
