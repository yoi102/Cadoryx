using Antelcat.I18N.WPF;
using Cadoryx.ViewModels.Services.Platform.Settings;
using System;
using System.Collections.Generic;
using System.Text;

namespace Cadoryx.wpf.Services.Application;

internal sealed class ApplicationCultureService : IApplicationCultureService
{
    public void ChangeCulture(string language)
    {
        var culture = new System.Globalization.CultureInfo(language);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        I18NExtension.Culture = culture;
    }

    public void ChangeCulture(int lcid)
    {
        var culture = new System.Globalization.CultureInfo(lcid);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        I18NExtension.Culture = culture;
    }

    public int GetCurrentCultureLCID()
    {
        return Thread.CurrentThread.CurrentUICulture.LCID;
    }

}
