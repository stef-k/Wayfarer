using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Wayfarer.Parsers;

/// <summary>Renders an MVC/Razor view to an HTML string.</summary>
public interface IRazorViewRenderer
{
    Task<string> RenderViewToStringAsync(string viewPath, object model);
}

public sealed class RazorViewRenderer : IRazorViewRenderer
{
    readonly ICompositeViewEngine _views;
    readonly IServiceProvider _provider;
    readonly ITempDataProvider _temp;

    public RazorViewRenderer(
        ICompositeViewEngine viewEngine,
        IServiceProvider provider,
        ITempDataProvider tempDataProvider)
    {
        _views = viewEngine;
        _provider = provider;
        _temp = tempDataProvider;
    }

    public async Task<string> RenderViewToStringAsync(string viewPath, object model)
    {
        var http      = new DefaultHttpContext { RequestServices = _provider };
        var routeData = new RouteData();
        routeData.Routers.Add(new RouteCollection());   // <-- gives UrlHelper.Router
        var actionCtx = new ActionContext(http, routeData, new ActionDescriptor());

        await using var sw = new StringWriter();

        var viewEngineRes = _views.GetView(executingFilePath: null, viewPath, isMainPage: true);
        if (!viewEngineRes.Success)
            throw new InvalidOperationException($"Unable to find view: {viewPath}");

        var view = viewEngineRes.View;
        var vData = new ViewDataDictionary(
                metadataProvider: new EmptyModelMetadataProvider(),
                modelState: new ModelStateDictionary())
            { Model = model };

        var temp = new TempDataDictionary(actionCtx.HttpContext, _temp);

        var viewCtx = new ViewContext(actionCtx, view, vData, temp, sw,
            new HtmlHelperOptions());

        await view.RenderAsync(viewCtx);
        return sw.ToString();
    }
}