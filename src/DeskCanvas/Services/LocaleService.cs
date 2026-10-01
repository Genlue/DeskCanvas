using System.Globalization;
using System.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Locales;

namespace DeskCanvas.Services;

public class LocaleService : ILocaleService
{
    public LocaleService(IAppSettingsProvider appSettingsProvider)
    {
        appSettingsProvider.DataChanging += (_, _, newSettings) => 
            SetCulture(newSettings.Region.Language);
    }
    
    public void SetCulture(string cultureName)
    {
        Thread.CurrentThread.CurrentUICulture = new CultureInfo(cultureName);
        Locale.Culture = Thread.CurrentThread.CurrentUICulture;
    }
}