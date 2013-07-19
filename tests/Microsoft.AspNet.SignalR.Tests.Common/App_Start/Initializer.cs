using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web;
using System.Web.Cors;
using System.Web.Routing;
using Microsoft.AspNet.SignalR.Infrastructure;
using Microsoft.AspNet.SignalR.StressServer.Connections;
using Microsoft.AspNet.SignalR.Tests.Common;
using Microsoft.AspNet.SignalR.Tests.Common.Connections;
using Microsoft.AspNet.SignalR.Tests.Common.Handlers;
using Microsoft.Owin;
using Microsoft.Owin.Cors;
using Microsoft.Owin.Security.Infrastructure;
using Microsoft.Owin.Security.OAuth;
using Owin;

[assembly: PreApplicationStartMethod(typeof(Initializer), "Start")]
[assembly: OwinStartup(typeof(Initializer))]

namespace Microsoft.AspNet.SignalR.Tests.Common
{
    public static class Initializer
    {
        public static void Start()
        {
            RouteTable.Routes.Add("ping", new Route("ping", new PingHandler()));
            RouteTable.Routes.Add("gc", new Route("gc", new GCHandler()));

            string logFileName = Path.Combine(HttpRuntime.AppDomainAppPath, ConfigurationManager.AppSettings["logFileName"] + ".server.trace.log");
            Trace.Listeners.Add(new TextWriterTraceListener(logFileName));
            Trace.AutoFlush = true;

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Trace.TraceError("Unobserved task exception: " + e.Exception.GetBaseException());

                e.SetObserved();
            };
        }

        public static void Configuration(IAppBuilder app)
        {
            string keepAliveRaw = ConfigurationManager.AppSettings["keepAlive"];
            string connectionTimeoutRaw = ConfigurationManager.AppSettings["connectionTimeout"];
            string transportConnectTimeoutRaw = ConfigurationManager.AppSettings["transportConnectTimeout"];
            string disconnectTimeoutRaw = ConfigurationManager.AppSettings["disconnectTimeout"];

            int connectionTimeout;
            if (Int32.TryParse(connectionTimeoutRaw, out connectionTimeout))
            {
                GlobalHost.Configuration.ConnectionTimeout = TimeSpan.FromSeconds(connectionTimeout);
            }

            int disconnectTimeout;
            if (Int32.TryParse(disconnectTimeoutRaw, out disconnectTimeout))
            {
                GlobalHost.Configuration.DisconnectTimeout = TimeSpan.FromSeconds(disconnectTimeout);
            }

            int transportConnectTimeout;
            if (Int32.TryParse(transportConnectTimeoutRaw, out transportConnectTimeout))
            {
                GlobalHost.Configuration.TransportConnectTimeout = TimeSpan.FromSeconds(transportConnectTimeout);
            }

            int keepAlive;
            if (String.IsNullOrEmpty(keepAliveRaw))
            {
                GlobalHost.Configuration.KeepAlive = null;
            }
            // Set only if the keep-alive was changed from the default value.
            else if (Int32.TryParse(keepAliveRaw, out keepAlive) && keepAlive != -1)
            {
                GlobalHost.Configuration.KeepAlive = TimeSpan.FromSeconds(keepAlive);
            }

            ConfigureRoutes(app, GlobalHost.DependencyResolver);
        }

        public static void ConfigureRoutes(IAppBuilder app, IDependencyResolver resolver)
        {
            var hubConfig = new HubConfiguration
            {
                Resolver = resolver,
                EnableDetailedErrors = true
            };

            app.MapHubs(hubConfig);

            app.MapHubs("/signalr2/test", new HubConfiguration()
            {
                Resolver = resolver
            });

            var config = new ConnectionConfiguration
            {
                Resolver = resolver
            };

            var corsOptions = new CorsOptions
            {
                CorsPolicy = new CorsPolicy
                {
                    AllowAnyHeader = true,
                    AllowAnyMethod = true,
                    AllowAnyOrigin = true,
                    SupportsCredentials = true
                }
            };

            app.Map("/multisend", map =>
            {
                map.UseCors(corsOptions);
                map.UseConnection<MySendingConnection>(config);
            });

            app.Map("/autoencodedjson", map =>
            {
                map.UseCors(corsOptions);
                map.UseConnection<AutoEncodedJsonConnection>(config);
            });

            app.Map("/redirectionConnection", map =>
            {
                map.UseCors(corsOptions);
                map.UseConnection<RedirectionConnection>(config);
            });

            app.MapConnection<MyBadConnection>("/ErrorsAreFun", config);
            app.MapConnection<MyGroupEchoConnection>("/group-echo", config);
            app.MapConnection<MyReconnect>("/my-reconnect", config);
            app.MapConnection<ExamineHeadersConnection>("/examine-request", config);
            app.MapConnection<ExamineReconnectPath>("/examine-reconnect", config);
            app.MapConnection<MyGroupConnection>("/groups", config);
            app.MapConnection<MyRejoinGroupsConnection>("/rejoin-groups", config);
            app.MapConnection<FilteredConnection>("/filter", config);
            app.MapConnection<ConnectionThatUsesItems>("/items", config);
            app.MapConnection<SyncErrorConnection>("/sync-error", config);
            app.MapConnection<AddGroupOnConnectedConnection>("/add-group", config);
            app.MapConnection<UnusableProtectedConnection>("/protected", config);
            app.MapConnection<FallbackToLongPollingConnection>("/fall-back", config);
            app.MapConnection<FallbackToLongPollingConnectionThrows>("/fall-back-throws", config);
            app.MapConnection<PreserializedJsonConnection>("/preserialize", config);

            // This subpipeline is protected by basic auth
            app.Map("/basicauth", subApp =>
            {
                subApp.UseBasicAuthentication(new BasicAuthenticationProvider());

                var subConfig = new ConnectionConfiguration
                {
                    Resolver = resolver
                };

                subApp.MapConnection<AuthenticatedEchoConnection>("/echo", subConfig);

                var subHubsConfig = new HubConfiguration
                {
                    Resolver = resolver
                };

                subApp.MapHubs(subHubsConfig);
            });

            // This subpipeline is protected by basic auth and oauth
            app.Map("/oauth", subApp =>
            {
                var authorizationProvider = new OAuthAuthorizationServerProvider();
                authorizationProvider.OnAuthorizeEndpoint = context =>
                {
                    Console.WriteLine("OAuthAuthorizationServerProvider.OnAuthorizeEndpoint");                    
                    return Task.FromResult<object>(null);
                };
                authorizationProvider.OnLookupClient = context =>
                {
                    Console.WriteLine("OAuthAuthorizationServerProvider.OnLookupClient");                    
                    string clientId = "user";
                    string clientPassword = "password";
                    string redirectUri = null;

                    if (context.RequestDetails.ClientId == clientId && context.RequestDetails.ClientSecret == clientPassword)
                    {
                        context.ClientFound(clientPassword, redirectUri);
                    }
                    
                    return Task.FromResult<object>(null);
                };
                authorizationProvider.OnTokenEndpoint = context =>
                {
                    Console.WriteLine("OAuthAuthorizationServerProvider.OnTokenEndpoint");
                    return Task.FromResult<object>(null);
                };
                authorizationProvider.OnValidateClientCredentials = context =>
                {
                    Console.WriteLine("OAuthAuthorizationServerProvider.OnValidateClientCredentials");

                    var roleName = "userRole";

                    var claims = new List<Claim>
                    {
                        new Claim(ClaimTypes.Name, context.ClientId),
                        new Claim(ClaimTypes.Role, roleName),
                    };

                    context.Validated(new ClaimsIdentity(claims, "Bearer"), new Dictionary<string, string>());

                    return Task.FromResult<object>(null);
                };
                authorizationProvider.OnValidateResourceOwnerCredentials = context =>
                {
                    Console.WriteLine("OAuthAuthorizationServerProvider.OnValidateResourceOwnerCredentials");
                    return Task.FromResult<object>(null);
                };

                var accessTokenProvider = new AuthenticationTokenProvider();
                accessTokenProvider.OnCreate = context =>
                {
                    Console.WriteLine("AuthenticationTokenProvider.OnCreate");
                    context.SetToken(context.SerializeTicket());
                };
                accessTokenProvider.OnReceive = context =>
                {
                    Console.WriteLine("AuthenticationTokenProvider.OnReceive");               
                    context.DeserializeTicket(context.Token);
                };

                var authorizationOptions = new OAuthAuthorizationServerOptions();
                authorizationOptions.AuthorizeEndpointPath = "/authorize";
                authorizationOptions.TokenEndpointPath = "/token";
                authorizationOptions.Provider = authorizationProvider;
                authorizationOptions.AccessTokenExpireTimeSpan = TimeSpan.FromSeconds(10);
                authorizationOptions.AccessTokenProvider = accessTokenProvider;
                authorizationOptions.RefreshTokenProvider = authorizationOptions.AccessTokenProvider;

                var bearerProvider = new OAuthBearerAuthenticationProvider();
                bearerProvider.OnValidateIdentity = context =>
                {
                    Console.WriteLine("OAuthBearerAuthenticationProvider.OnValidateIdentity");
                    if (!context.Identity.IsAuthenticated)
                    {
                        throw new InvalidOperationException("user is not authenticated");
                    }
                    if (context.Identity.Name != "user")
                    {
                        throw new InvalidOperationException("unexpected user: " + context.Identity.Name);
                    }

                    return Task.FromResult<object>(null);
                };

                var bearerOptions = new OAuthBearerAuthenticationOptions();
                bearerOptions.AccessTokenProvider = authorizationOptions.AccessTokenProvider;
                bearerOptions.Provider = bearerProvider;

                subApp.UseOAuthAuthorizationServer(authorizationOptions);
                subApp.UseOAuthBearerAuthentication(bearerOptions);                

                var subConfig = new ConnectionConfiguration
                {
                    Resolver = resolver
                };

                subApp.MapConnection<AuthenticatedEchoConnection>("/echo", subConfig);

                var subHubsConfig = new HubConfiguration
                {
                    Resolver = resolver
                };

                subApp.MapHubs(subHubsConfig);
            });

            // Perf/stress test related
            var performanceConfig = new ConnectionConfiguration
            {
                Resolver = resolver
            };

            app.MapConnection<StressConnection>("/echo", performanceConfig);

            performanceConfig.Resolver.Register(typeof(IProtectedData), () => new EmptyProtectedData());
        }
    }
}
