using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Publishing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace AppHost.Extensions;

public static class YarpResourceExtensions
{
    public static IResourceBuilder<YarpResource> AddYarp(this IDistributedApplicationBuilder builder, string name)
    {
        var yarp = builder.Resources.OfType<YarpResource>().SingleOrDefault();

        if (yarp is not null)
        {
            throw new InvalidOperationException("A YARP resource has already been added to this application");
        }
        
        builder.Services.TryAddLifecycleHook<YarpResourceLifecycleHook>();

        var resource = new YarpResource(name);

        return builder.AddResource(resource).ExcludeFromManifest();
    }

    public static IResourceBuilder<YarpResource> LoadFromConfiguration(this IResourceBuilder<YarpResource> builder, string sectionName)
    {
        builder.Resource.ConfigurationSectionName = sectionName;
        return builder;
    }

    public static IResourceBuilder<YarpResource> Route(this IResourceBuilder<YarpResource> builder, string routeId, IResourceBuilder<IResourceWithServiceDiscovery> target, string? path = null, string[]? hosts = null, bool preservePath = false)
    {
        builder.Resource.RouteConfigs[routeId] = new()
        {
            RouteId = routeId,
            ClusterId = target.Resource.Name,
            Match = new()
            {
                Path = path,
                Hosts = hosts
            },
            Transforms =
            [
                preservePath || path is null ? [] : new Dictionary<string, string> { ["PathRemovePrefix"] = path }
            ]
        };

        if (builder.Resource.ClusterConfigs.ContainsKey(target.Resource.Name))
        {
            return builder;
        }

        builder.Resource.ClusterConfigs[target.Resource.Name] = new()
        {
            ClusterId = target.Resource.Name,
            Destinations = new Dictionary<string, DestinationConfig>
            {
                [target.Resource.Name] = new() { Address = $"http://{target.Resource.Name}" }
            }
        };

        builder.WithReference(target);

        return builder;
    }
}

public class YarpResource(string name) : Resource(name), IResourceWithServiceDiscovery, IResourceWithEnvironment
{
    public Dictionary<string, RouteConfig> RouteConfigs { get; } = [];
    public Dictionary<string, ClusterConfig> ClusterConfigs { get; } = [];
    public List<EndpointAnnotation> Endpoints { get; } = [];
    public string? ConfigurationSectionName { get; set; }

    public const string CatchAll = "/{**catchall}";
}

internal class YarpResourceLifecycleHook(    
    DistributedApplicationExecutionContext executionContext,
    ResourceNotificationService resourceNotificationService,
    ResourceLoggerService resourceLoggerService) : IDistributedApplicationLifecycleHook, IAsyncDisposable
{
    private WebApplication? _app;
    
    public ValueTask DisposeAsync()
    {
        return _app?.DisposeAsync() ?? default;
    }

    public async Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
    {
        if (executionContext.IsPublishMode)
        {
            return;
        }
        
        var yarpResource = appModel.Resources.OfType<YarpResource>().SingleOrDefault();

        if (yarpResource is null)
        {
            return;
        }
        
        await resourceNotificationService.PublishUpdateAsync(yarpResource, s => s with
        {
            ResourceType = "Yarp",
            State = "Starting"
        });
        
        var bindings = yarpResource.Annotations.OfType<EndpointAnnotation>().ToList();

        foreach (var b in bindings)
        {
            yarpResource.Annotations.Remove(b);
            yarpResource.Endpoints.Add(b);
        }
    }

    public async Task AfterEndpointsAllocatedAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
    {
        if (executionContext.IsPublishMode)
        {
            return;
        }

        var yarpResource = appModel.Resources.OfType<YarpResource>().SingleOrDefault();

        if (yarpResource is null)
        {
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        
        builder.Logging.ClearProviders();

        builder.Logging.AddProvider(new ResourceLoggerProvider(resourceLoggerService.GetLogger(yarpResource.Name)));

        if (yarpResource.TryGetEnvironmentVariables(out var envAnnotations))
        {
            var context = new EnvironmentCallbackContext(executionContext, cancellationToken: cancellationToken);

            foreach (var cb in envAnnotations)
            {
                await cb.Callback(context);
            }

            var settings = new Dictionary<string, string?>();

            foreach (var (k, v) in context.EnvironmentVariables)
            {
                var val = v switch
                {
                    string s => s,
                    IValueProvider vp => await vp.GetValueAsync(context.CancellationToken),
                    _ => throw new NotSupportedException()
                };

                if (val is not null)
                {
                    settings[k.Replace("__", ":")] = val;
                }
            }

            builder.Configuration.AddInMemoryCollection(settings);
        }

        builder.Services.AddServiceDiscovery();

        var proxyBuilder = builder.Services.AddReverseProxy();

        if (yarpResource.RouteConfigs.Count > 0)
        {
            proxyBuilder.LoadFromMemory(yarpResource.RouteConfigs.Values.ToList(), yarpResource.ClusterConfigs.Values.ToList());
        }

        if (yarpResource.ConfigurationSectionName is not null)
        {
            proxyBuilder.LoadFromConfig(builder.Configuration.GetSection(yarpResource.ConfigurationSectionName));
        }

        proxyBuilder.AddServiceDiscoveryDestinationResolver();

        _app = builder.Build();

        if (yarpResource.Endpoints.Count == 0)
        {
            _app.Urls.Add("http://127.0.0.1:0");
        }
        else
        {
            foreach (var ep in yarpResource.Endpoints)
            {
                var scheme = ep.UriScheme ?? "http";

                if (ep.Port is null)
                {
                    _app.Urls.Add($"{scheme}://127.0.0.1:0");
                }
                else
                {
                    _app.Urls.Add($"{scheme}://127.0.0.1:{ep.Port}");
                }
            }
        }

        _app.MapReverseProxy();

        await _app.StartAsync(cancellationToken);

        var urls = _app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses;

        await resourceNotificationService.PublishUpdateAsync(yarpResource, s => s with
        {
            State = "Running",
            Urls = [.. urls.Select(u => new UrlSnapshot(u, u, IsInternal: false))]
        });
    }
    
    private class ResourceLoggerProvider(ILogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return new ResourceLogger(logger);
        }

        public void Dispose()
        {
        }

        private class ResourceLogger(ILogger logger) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return logger.BeginScope(state);
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logger.IsEnabled(logLevel);
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                logger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}