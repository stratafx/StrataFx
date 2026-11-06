using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StrataFx.Data;
using StrataFx.Hosting;

namespace StrataFx.EFCore;

public static class StrataFxEfCoreExtensions
{
    /// <summary>
    /// Uses EF Core for data access. Register the context with <c>services.AddDbContextFactory&lt;TContext&gt;(...)</c>.
    /// </summary>
    public static StrataFxBuilder UseEntityFrameworkCore<TContext>(this StrataFxBuilder fx) where TContext : DbContext
    {
        fx.Services.AddScoped<IDataStore, EfDataStore<TContext>>();
        return fx;
    }
}
