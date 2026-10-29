using System;
using System.Collections.Generic;
using DeskCanvas.Core.Models;

namespace DeskCanvas.WinUI.Settings;

/// <summary>
/// 创建一个设置页所需的全部上下文。页面实现只从这里拿依赖，不自己去 <c>new</c> 窗口或服务，
/// 这样页面工厂、缓存与生命周期都由设置窗口外壳统一管理（报告 §3 第二步）。
/// </summary>
public sealed class SettingsPageContext
{
    public SettingsPageContext(
        IServiceProvider services,
        string pageKey,
        string title,
        IReadOnlyList<AssemblyInfo> assemblies)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        PageKey = pageKey ?? throw new ArgumentNullException(nameof(pageKey));
        Title = title ?? string.Empty;
        Assemblies = assemblies ?? Array.Empty<AssemblyInfo>();
    }

    /// <summary>原宿主构建的 DI 容器（<see cref="Host.AppHost.Services"/>）。</summary>
    public IServiceProvider Services { get; }

    /// <summary>稳定页面键，见 <c>DeskCanvas.Services.SettingsPageKeys</c>；组件库页为 <c>Gallery:&lt;后缀&gt;</c>。</summary>
    public string PageKey { get; }

    /// <summary>导航项标题（已经过 Locale 资源解析，页面标题栏直接用这个）。</summary>
    public string Title { get; }

    /// <summary>
    /// 该页涉及的组件包。仅组件库/分类页非空；系统设置页为空。

    /// 页面不得假设它们已加载（真实预览由组件库页面自己按需创建）。

    /// </summary>
    public IReadOnlyList<AssemblyInfo> Assemblies { get; }

    /// <summary>按需取一个可选服务；未注册时返回 null（不抛异常，页面应按降级方式显示）。</summary>
    public T? GetService<T>()
        where T : class
        => Services.GetService(typeof(T)) as T;

    /// <summary>取一个必需服务；未注册时抛出带页面键的明确异常，避免空引用难以定位。</summary>
    public T GetRequiredService<T>()
        where T : class
        => GetService<T>() ?? throw new InvalidOperationException(
            $"Settings page '{PageKey}' requires service '{typeof(T).FullName}', but it is not registered.");
}

/// <summary>
/// 一个设置页面的壳约定（下一批页面执行者照这个契约写）。
/// <para>
/// 外壳（<see cref="SettingsWindow"/>）只在页面可见期间调用 <see cref="Attach"/>，
/// 隐藏/离页/窗口关闭时一定调用一次 <see cref="Detach"/>。
/// </para>
/// <para><b>为什么不用 Loaded/Unloaded 单独承担</b>：设置窗口关闭是「隐藏 + 卸下内容」，WinUI 的
/// 导航缓存与视觉树都不保证在隐藏时把页面还给你（报告 §3 第二步）。显式的 Attach/Detach 让订阅、
/// 计时器、异步请求的释放点确定且可测试；Loaded/Unloaded 可以作为页面内部实现细节，但不能替代它。</para>
/// <para><b>契约（实现方必须满足）</b></para>
/// <list type="number">
/// <item><description><b>构造</b>只允许建控件与解析依赖，不读写 Provider/Store，不发起网络/磁盘 IO；
/// 依赖统一通过 <see cref="SettingsPageContext.GetRequiredService{T}"/> 取（IAppSettingsProvider、
/// IAssemblyProvider、ProfileService、DisplayMonitorService、UpdateService、ILocaleService…）。</description></item>
/// <item><description><b>Attach</b> 是唯一的订阅点：事件、计时器、异步请求都在这里开始；
/// 初始化写入设置的动作不算用户提交（报告 §4.2）。</description></item>
/// <item><description><b>Detach</b> 必须幂等且自包含：退订全部事件、取消 CancellationToken、
/// 停掉计时器、释放只属于可见期的位图/预览。抛异常也要把状态收干净。</description></item>
/// <item><description><b>可重复创建</b>：外壳在隐藏时丢弃页面实例，重开时由工厂新建一个，
/// 因此页面不得依赖「同一实例被 Attach 两次」来保存状态；上次的选中项/草稿要能从 Provider 重新读回来。</description></item>
/// <item><description><b>只能用 WinUI 控件</b>：不得引用 Avalonia 命名空间/类型（CS0104 冲突，
/// 报告 §5 转换表）；需要图标时用路径字符串 + PathIcon。</description></item>
/// <item><description><b>自注册</b>：页面在自己的文件里用 <c>[ModuleInitializer]</c> 调
/// <see cref="SettingsPageRegistry.Register"/> 认领页面键，不要改别的执行者的文件。</description></item>
/// <item><description><b>文案</b>必须走 Locale 资源键，不写死中英文（守卫规则 7）。</description></item>
/// </list>
/// <para>不想写样板代码时：代码构造根的页面继承 <see cref="SettingsPageBase"/>；XAML（UserControl）
/// 页面把 Attach/Detach 转发给 <see cref="SettingsPageLifetime"/>（参考 <c>Pages/PagePlaceholder</c>）。</para>
/// </summary>
public interface ISettingsPage
{
    /// <summary>页面根元素（UserControl 直接返回 <c>this</c>）。外壳把它放进内容区；一个实例只能挂一次。</summary>
    Microsoft.UI.Xaml.UIElement View { get; }

    /// <summary>进入可见状态：订阅数据源、启动必要的异步加载。</summary>
    void Attach(SettingsPageContext context);

    /// <summary>离开可见状态：退订、取消异步请求、释放只属于可见期的资源。必须幂等。</summary>
    void Detach();
}
