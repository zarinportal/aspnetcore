// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Builder
{
    /// <summary>
    /// Constains extensions for configuring routing on an <see cref="IApplicationBuilder"/>.
    /// </summary>
    public static class EndpointRoutingApplicationBuilderExtensions
    {
        private const string EndpointRouteBuilder = "__EndpointRouteBuilder";

        /// <summary>
        /// Adds a <see cref="EndpointRoutingMiddleware"/> middleware to the specified <see cref="IApplicationBuilder"/>.
        /// </summary>
        /// <param name="builder">The <see cref="IApplicationBuilder"/> to add the middleware to.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        /// <remarks>
        /// <para>
        /// A call to <see cref="UseRouting(IApplicationBuilder)"/> must be followed by a call to
        /// <see cref="UseEndpoints(IApplicationBuilder, Action{IEndpointRouteBuilder})"/> for the same <see cref="IApplicationBuilder"/>
        /// instance.
        /// </para>
        /// <para>
        /// The <see cref="EndpointRoutingMiddleware"/> defines a point in the middleware pipeline where routing decisions are
        /// made, and an <see cref="Endpoint"/> is associated with the <see cref="HttpContext"/>. The <see cref="EndpointMiddleware"/>
        /// defines a point in the middleware pipeline where the current <see cref="Endpoint"/> is executed. Middleware between
        /// the <see cref="EndpointRoutingMiddleware"/> and <see cref="EndpointMiddleware"/> may observe or change the
        /// <see cref="Endpoint"/> associated with the <see cref="HttpContext"/>.
        /// </para>
        /// </remarks>
        public static IApplicationBuilder UseRouting(this IApplicationBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            VerifyRoutingServicesAreRegistered(builder);

            IEndpointRouteBuilder endpointRouteBuilder;
            if (builder.Properties.TryGetValue("__GlobalEndpointRouteBuilder", out var obj))
            {
                endpointRouteBuilder = (IEndpointRouteBuilder)obj!;
            }
            else
            {
                endpointRouteBuilder = new DefaultEndpointRouteBuilder(builder);
                builder.Properties[EndpointRouteBuilder] = endpointRouteBuilder;
            }

            return builder.UseMiddleware<EndpointRoutingMiddleware>(endpointRouteBuilder);
        }

        /// <summary>
        /// Adds a <see cref="EndpointMiddleware"/> middleware to the specified <see cref="IApplicationBuilder"/>
        /// with the <see cref="EndpointDataSource"/> instances built from configured <see cref="IEndpointRouteBuilder"/>.
        /// The <see cref="EndpointMiddleware"/> will execute the <see cref="Endpoint"/> associated with the current
        /// request.
        /// </summary>
        /// <param name="builder">The <see cref="IApplicationBuilder"/> to add the middleware to.</param>
        /// <param name="configure">An <see cref="Action{IEndpointRouteBuilder}"/> to configure the provided <see cref="IEndpointRouteBuilder"/>.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        /// <remarks>
        /// <para>
        /// A call to <see cref="UseEndpoints(IApplicationBuilder, Action{IEndpointRouteBuilder})"/> must be preceded by a call to
        /// <see cref="UseRouting(IApplicationBuilder)"/> for the same <see cref="IApplicationBuilder"/>
        /// instance.
        /// </para>
        /// <para>
        /// The <see cref="EndpointRoutingMiddleware"/> defines a point in the middleware pipeline where routing decisions are
        /// made, and an <see cref="Endpoint"/> is associated with the <see cref="HttpContext"/>. The <see cref="EndpointMiddleware"/>
        /// defines a point in the middleware pipeline where the current <see cref="Endpoint"/> is executed. Middleware between
        /// the <see cref="EndpointRoutingMiddleware"/> and <see cref="EndpointMiddleware"/> may observe or change the
        /// <see cref="Endpoint"/> associated with the <see cref="HttpContext"/>.
        /// </para>
        /// </remarks>
        public static IApplicationBuilder UseEndpoints(this IApplicationBuilder builder, Action<IEndpointRouteBuilder> configure)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            VerifyRoutingServicesAreRegistered(builder);

            VerifyEndpointRoutingMiddlewareIsRegistered(builder, out var endpointRouteBuilder);

            configure(endpointRouteBuilder);

            // Yes, this mutates an IOptions. We're registering data sources in a global collection which
            // can be used for discovery of endpoints or URL generation.
            //
            // Each middleware gets its own collection of data sources, and all of those data sources also
            // get added to a global collection.
            // In the global endpoint route case we only want to copy data sources once, so we wait for a specific key before copying data sources
            // like in the case of minimal hosting
            if (builder.Properties.TryGetValue("__GlobalEndpointBuilderShouldCopyRoutes", out _) ||
                !builder.Properties.TryGetValue("__GlobalEndpointRouteBuilder", out _))
            {
                var routeOptions = builder.ApplicationServices.GetRequiredService<IOptions<RouteOptions>>();
                foreach (var dataSource in endpointRouteBuilder.DataSources)
                {
                    routeOptions.Value.EndpointDataSources.Add(dataSource);
                }
            }

            // REVIEW: this 'if' could be removed, see comment in WebApplicationBuilder
            if (!builder.Properties.TryGetValue("__GlobalEndpointRouteBuilder", out _))
            {
                builder.Properties.Remove(EndpointRouteBuilder);
            }

            return builder.UseMiddleware<EndpointMiddleware>();
        }

        private static void VerifyRoutingServicesAreRegistered(IApplicationBuilder app)
        {
            // Verify if AddRouting was done before calling UseEndpointRouting/UseEndpoint
            // We use the RoutingMarkerService to make sure if all the services were added.
            if (app.ApplicationServices.GetService(typeof(RoutingMarkerService)) == null)
            {
                throw new InvalidOperationException(Resources.FormatUnableToFindServices(
                    nameof(IServiceCollection),
                    nameof(RoutingServiceCollectionExtensions.AddRouting),
                    "ConfigureServices(...)"));
            }
        }

        private static void VerifyEndpointRoutingMiddlewareIsRegistered(IApplicationBuilder app, out IEndpointRouteBuilder endpointRouteBuilder)
        {
            if (!app.Properties.TryGetValue(EndpointRouteBuilder, out var obj) && !app.Properties.TryGetValue("__GlobalEndpointRouteBuilder", out obj))
            {
                var message =
                    $"{nameof(EndpointRoutingMiddleware)} matches endpoints setup by {nameof(EndpointMiddleware)} and so must be added to the request " +
                    $"execution pipeline before {nameof(EndpointMiddleware)}. " +
                    $"Please add {nameof(EndpointRoutingMiddleware)} by calling '{nameof(IApplicationBuilder)}.{nameof(UseRouting)}' inside the call " +
                    $"to 'Configure(...)' in the application startup code.";
                throw new InvalidOperationException(message);
            }

            endpointRouteBuilder = (IEndpointRouteBuilder)obj!;

            // This check handles the case where Map or something else that forks the pipeline is called between the two
            // routing middleware.
            if (endpointRouteBuilder is DefaultEndpointRouteBuilder defaultRouteBuilder && !object.ReferenceEquals(app, defaultRouteBuilder.ApplicationBuilder))
            {
                var message =
                    $"The {nameof(EndpointRoutingMiddleware)} and {nameof(EndpointMiddleware)} must be added to the same {nameof(IApplicationBuilder)} instance. " +
                    $"To use Endpoint Routing with 'Map(...)', make sure to call '{nameof(IApplicationBuilder)}.{nameof(UseRouting)}' before " +
                    $"'{nameof(IApplicationBuilder)}.{nameof(UseEndpoints)}' for each branch of the middleware pipeline.";
                throw new InvalidOperationException(message);
            }
        }
    }
}
