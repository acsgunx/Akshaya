using Akshaya.Modules.Identity.Application;
using Akshaya.Modules.Identity.Infrastructure;
using Akshaya.Modules.Identity.Infrastructure.Ef;
using Akshaya.Modules.Identity.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Akshaya.Modules.Identity;

/// <summary>Composition for the identity module.</summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the identity services and their EF-backed stores.
    ///
    /// The DbContext itself is NOT registered here — the composition root chooses the provider
    /// (Postgres in a deployment, SQLite or in-memory in tests), and a module that picked its
    /// own database provider could not be tested against anything else.
    /// </summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.TryAddSingleton<ICredentialCipher, AesGcmCredentialCipher>();

        services.TryAddScoped<IUserAccountStore, EfUserAccountStore>();
        services.TryAddScoped<ISavedCredentialStore, EfSavedCredentialStore>();

        services.AddScoped<UserAccountService>();
        services.AddScoped<BrokerCredentialVault>();

        return services;
    }
}
