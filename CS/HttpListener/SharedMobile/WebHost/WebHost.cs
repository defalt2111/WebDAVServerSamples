// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting.Views;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.StackTrace.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Internal;

namespace SharedMobile
{
    internal class WebHost : IWebHost
    {
        private static readonly string DeprecatedServerUrlsKey = "server.urls";

        private readonly IServiceCollection _applicationServiceCollection;
        private IStartup _startup;
        private ApplicationLifetime _applicationLifetime;
        private HostedServiceExecutor _hostedServiceExecutor;

        private readonly IServiceProvider _hostingServiceProvider;
        private readonly WebHostOptions _options;
        private readonly IConfiguration _config;
        private readonly AggregateException _hostingStartupErrors;

        private IServiceProvider _applicationServices;
        private RequestDelegate _application;
        private ILogger<WebHost> _logger;

        private bool _stopped;

        // Used for testing only
        internal WebHostOptions Options => _options;

        private IServer Server { get; set; }

        public WebHost(
            IServiceCollection appServices,
            IServiceProvider hostingServiceProvider,
            WebHostOptions options,
            IConfiguration config,
            AggregateException hostingStartupErrors)
        {
            if (appServices == null)
            {
                throw new ArgumentNullException(nameof(appServices));
            }

            if (hostingServiceProvider == null)
            {
                throw new ArgumentNullException(nameof(hostingServiceProvider));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _config = config;
            _hostingStartupErrors = hostingStartupErrors;
            _options = options;
            _applicationServiceCollection = appServices;
            _hostingServiceProvider = hostingServiceProvider;
            _applicationServiceCollection.AddSingleton<IApplicationLifetime, ApplicationLifetime>();
            _applicationServiceCollection.AddSingleton<HostedServiceExecutor>();
        }

        public IServiceProvider Services
        {
            get
            {
                EnsureApplicationServices();
                return _applicationServices;
            }
        }

        public IFeatureCollection ServerFeatures
        {
            get
            {
                return Server?.Features;
            }
        }

        public void Initialize()
        {
            if (_application == null)
            {
                _application = BuildApplication();
            }
        }

        public void Start()
        {
            StartAsync().GetAwaiter().GetResult();
        }

        public virtual async Task StartAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            HostingEventSource.Log.HostStart();
            _logger = _applicationServices.GetRequiredService<ILogger<WebHost>>();

            Initialize();

            _applicationLifetime = _applicationServices.GetRequiredService<IApplicationLifetime>() as ApplicationLifetime;
            _hostedServiceExecutor = _applicationServices.GetRequiredService<HostedServiceExecutor>();
            var diagnosticSource = _applicationServices.GetRequiredService<DiagnosticListener>();
            var httpContextFactory = _applicationServices.GetRequiredService<IHttpContextFactory>();
            var hostingApp = new HostingApplication(_application, _logger, diagnosticSource, httpContextFactory);
            await Server.StartAsync(hostingApp, cancellationToken).ConfigureAwait(false);

            // Fire IApplicationLifetime.Started
            _applicationLifetime?.NotifyStarted();

            // Fire IHostedService.Start
            await _hostedServiceExecutor.StartAsync(cancellationToken).ConfigureAwait(false);

            // Log the fact that we did load hosting startup assemblies.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                foreach (var assembly in _options.HostingStartupAssemblies)
                {
                    _logger.LogDebug("Loaded hosting startup assembly {assemblyName}", assembly);
                }
            }
        }

        private void EnsureApplicationServices()
        {
            if (_applicationServices == null)
            {
                EnsureStartup();
                _applicationServices = _startup.ConfigureServices(_applicationServiceCollection);
            }
        }

        private void EnsureStartup()
        {
            if (_startup != null)
            {
                return;
            }

            _startup = _hostingServiceProvider.GetService<IStartup>();

            if (_startup == null)
            {
                throw new InvalidOperationException($"No startup configured. Please specify startup via WebHostBuilder.UseStartup, WebHostBuilder.Configure, injecting {nameof(IStartup)} or specifying the startup assembly via {nameof(WebHostDefaults.StartupAssemblyKey)} in the web host configuration.");
            }
        }

        private RequestDelegate BuildApplication()
        {
            EnsureApplicationServices();
            EnsureServer();

            var builderFactory = _applicationServices.GetRequiredService<IApplicationBuilderFactory>();
            var builder = builderFactory.CreateBuilder(Server.Features);
            builder.ApplicationServices = _applicationServices;

            var startupFilters = _applicationServices.GetService<IEnumerable<IStartupFilter>>();
            Action<IApplicationBuilder> configure = _startup.Configure;
            foreach (var filter in startupFilters.Reverse())
            {
                configure = filter.Configure(configure);
            }

            configure(builder);

            return builder.Build();
        }

        private void EnsureServer()
        {
            if (Server == null)
            {
                Server = _applicationServices.GetRequiredService<IServer>();

                var serverAddressesFeature = Server.Features?.Get<IServerAddressesFeature>();
                var addresses = serverAddressesFeature?.Addresses;
                if (addresses != null && !addresses.IsReadOnly && addresses.Count == 0)
                {
                    var urls = _config[WebHostDefaults.ServerUrlsKey] ?? _config[DeprecatedServerUrlsKey];
                    if (!string.IsNullOrEmpty(urls))
                    {
                        serverAddressesFeature.PreferHostingUrls = WebHostUtilities.ParseBool(_config, WebHostDefaults.PreferHostingUrlsKey);

                        foreach (var value in urls.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            addresses.Add(value);
                        }
                    }
                }
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;

            var timeoutToken = new CancellationTokenSource(Options.ShutdownTimeout).Token;
            if (!cancellationToken.CanBeCanceled)
            {
                cancellationToken = timeoutToken;
            }
            else
            {
                cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken).Token;
            }

            // Fire IApplicationLifetime.Stopping
            _applicationLifetime?.StopApplication();

            if (Server != null)
            {
                await Server.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            // Fire the IHostedService.Stop
            if (_hostedServiceExecutor != null)
            {
                await _hostedServiceExecutor.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            // Fire IApplicationLifetime.Stopped
            _applicationLifetime?.NotifyStopped();

            HostingEventSource.Log.HostStop();
        }

        public void Dispose()
        {
            if (!_stopped)
            {
                StopAsync().GetAwaiter().GetResult();
            }

            (_applicationServices as IDisposable)?.Dispose();
            (_hostingServiceProvider as IDisposable)?.Dispose();
        }
    }
}
