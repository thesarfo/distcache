using DistCache.Core.Networking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DistCache.Admin.Extensions;

/// <summary>
/// Extension methods for registering and mapping the gRPC peer server in an ASP.NET Core host.
/// </summary>
public static class CachePeerServiceExtensions
{
    /// <summary>
    /// Registers gRPC services required to host <see cref="CachePeerServer"/>.
    /// Call this before <see cref="MapCachePeerServer"/>.
    /// </summary>
    public static IServiceCollection AddCachePeerServer(this IServiceCollection services)
    {
        services.AddGrpc();
        return services;
    }

    /// <summary>
    /// Maps the <see cref="CachePeerServer"/> gRPC service to the endpoint routing pipeline.
    /// </summary>
    public static GrpcServiceEndpointConventionBuilder MapCachePeerServer(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGrpcService<CachePeerServer>();
}
