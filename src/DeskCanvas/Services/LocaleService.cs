using System.Globalization;
using System.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Locales;

namespace DeskCanvas.Services;

public class LocaleService : ILocaleService
{
    public LocaleService(IAppSettingsProvider appSettingsProvider)
    {
        appSettingsProvider.DataChanged += (_, _, newSettings) =>
            SetCulture(newSettings.Region.Language);
    }
    
    public void SetCulture(string cultureName)
    {
        var culture = new CultureInfo(cultureName);
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        Locale.Culture = culture;
    }
}
