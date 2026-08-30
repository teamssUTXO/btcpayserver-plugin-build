using Microsoft.AspNetCore.Mvc.ModelBinding;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.ModelBinders;

public class PluginSlugModelBinder : IModelBinder
{
    // Route key carrying the tenant key; must stay in sync with OwnPlugin authorization.
    public const string PluginSlugRouteKey = "pluginSlug";

    private readonly DBConnectionFactory _connectionFactory;

    public PluginSlugModelBinder(DBConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        // Keep the bound tenant key aligned with the route value checked by OwnPlugin authorization.
        if (!bindingContext.ActionContext.RouteData.Values.TryGetValue(PluginSlugRouteKey, out var value) ||
            value is not string v)
            return;

        if (PluginSelector.TryParse(v, out var s))
        {
            var pluginSlug = await _connectionFactory.ResolvePluginSlug(s);
            if (pluginSlug != null)
            {
                bindingContext.Result = ModelBindingResult.Success(pluginSlug);
            }
            else
            {
                bindingContext.Result = ModelBindingResult.Failed();
                bindingContext.ModelState.AddModelError(bindingContext.ModelName, "Unknown plugin identifier");
            }
        }
        else
        {
            bindingContext.Result = ModelBindingResult.Failed();
            bindingContext.ModelState.AddModelError(bindingContext.ModelName, "Invalid plugin selector");
        }
    }
}
