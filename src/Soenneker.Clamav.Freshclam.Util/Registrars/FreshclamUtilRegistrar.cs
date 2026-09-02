using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Clamav.Freshclam.Util.Abstract;
using Soenneker.Utils.Directory.Registrars;
using Soenneker.Utils.File.Registrars;
using Soenneker.Utils.Path.Registrars;
using Soenneker.Utils.Paths.Resources.Registrars;
using Soenneker.Utils.Process.Registrars;

namespace Soenneker.Clamav.Freshclam.Util.Registrars;

/// <summary>
/// A cross-platform .NET utility for updating ClamAV virus definitions with bundled FreshClam runtimes.
/// </summary>
public static class FreshclamUtilRegistrar
{
    /// <summary>
    /// Adds <see cref="IFreshclamUtil"/> as a singleton service. <para/>
    /// </summary>
    public static IServiceCollection AddFreshclamUtilAsSingleton(this IServiceCollection services)
    {
        services.AddDirectoryUtilAsSingleton()
                .AddFileUtilAsSingleton()
                .AddPathUtilAsSingleton()
                .AddResourcesPathUtilAsSingleton()
                .AddProcessUtilAsSingleton()
                .TryAddSingleton<IFreshclamUtil, FreshclamUtil>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="IFreshclamUtil"/> as a scoped service. <para/>
    /// </summary>
    public static IServiceCollection AddFreshclamUtilAsScoped(this IServiceCollection services)
    {
        services.AddDirectoryUtilAsScoped()
                .AddFileUtilAsScoped()
                .AddPathUtilAsScoped()
                .AddResourcesPathUtilAsScoped()
                .AddProcessUtilAsScoped()
                .TryAddScoped<IFreshclamUtil, FreshclamUtil>();

        return services;
    }
}
