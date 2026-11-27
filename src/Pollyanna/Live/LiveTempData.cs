using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Pollyanna.Live;

/// <summary>
/// One-shot notices (TempData: "Added Carol.", the gift coming back untied) are for the page the
/// person goes to next. A live refetch runs in the background, so it may read them but never
/// saves what it read: the notice is still there for the next real visit (spec §16 "A live
/// refetch isn't a visit").
/// </summary>
public sealed class LiveTempData(CookieTempDataProvider inner) : ITempDataProvider
{
    public IDictionary<string, object> LoadTempData(HttpContext context) => inner.LoadTempData(context);

    public void SaveTempData(HttpContext context, IDictionary<string, object> values)
    {
        if (!context.Request.IsLiveRefetch())
            inner.SaveTempData(context, values);
    }

    /// <summary>Puts this in front of the cookie provider Razor Pages registers.</summary>
    public static void AddTo(IServiceCollection services)
    {
        services.AddSingleton<CookieTempDataProvider>();
        services.AddSingleton<ITempDataProvider, LiveTempData>();
    }
}
