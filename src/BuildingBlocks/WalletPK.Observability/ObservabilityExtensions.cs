using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace WalletPK.Observability;

/// <summary>
/// One-line observability for every WalletPK service:
/// <code>
/// builder.AddWalletPkObservability("ledger");
/// ...
/// app.UseWalletPkWeb();
/// app.UseWalletPkObservability();
/// </code>
/// </summary>
public static class ObservabilityExtensions
{
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>Adds Serilog (console + Seq), OpenTelemetry tracing (Jaeger) and metrics (Prometheus).</summary>
    public static TBuilder AddWalletPkObservability<TBuilder>(
        this TBuilder builder,
        string serviceName,
        Action<ObservabilityOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        // Code defaults -> appsettings/environment -> explicit configure callback.
        var options = new ObservabilityOptions { ServiceName = serviceName };
        builder.Configuration.GetSection(ObservabilityOptions.SectionName).Bind(options);
        configure?.Invoke(options);

        var environmentName = builder.Environment.EnvironmentName;

        AddLogging(builder.Services, builder.Configuration, options, environmentName);
        AddOpenTelemetry(builder.Services, options, environmentName);

        return builder;
    }

    /// <summary>
    /// Adds one summary log line per HTTP request (method, path, status, duration).
    /// Call after <c>UseWalletPkWeb()</c> so the line carries the correlation ID.
    /// </summary>
    public static WebApplication UseWalletPkObservability(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";
            options.GetLevel = (httpContext, _, exception) =>
                exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
                    ? LogEventLevel.Error
                    : IsHealthCheck(httpContext) ? LogEventLevel.Verbose : LogEventLevel.Information;
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("ClientIp", httpContext.Connection.RemoteIpAddress?.ToString());
                diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
            };
        });

        return app;
    }

    private static void AddLogging(
        IServiceCollection services,
        IConfiguration configuration,
        ObservabilityOptions options,
        string environmentName)
    {
        services.AddSerilog((serviceProvider, logger) =>
        {
            // Sensible defaults first; a "Serilog" section in appsettings can override them.
            logger
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                .ReadFrom.Configuration(configuration)
                .ReadFrom.Services(serviceProvider)
                .Enrich.FromLogContext()
                .Enrich.With<CorrelationIdEnricher>()
                .Enrich.WithProperty("Service", options.ServiceName)
                .Enrich.WithProperty("ServiceVersion", options.ServiceVersion)
                .Enrich.WithProperty("Environment", environmentName)
                .Enrich.WithProperty("MachineName", Environment.MachineName);

            if (options.WriteToConsole)
            {
                logger.WriteTo.Console(outputTemplate: ConsoleTemplate, formatProvider: CultureInfo.InvariantCulture);
            }

            if (options.WriteToSeq)
            {
                logger.WriteTo.Seq(options.SeqUrl.AbsoluteUri);
            }

            options.ConfigureLogging?.Invoke(logger);
        });
    }

    private static void AddOpenTelemetry(IServiceCollection services, ObservabilityOptions options, string environmentName)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: options.ServiceName,
                    serviceNamespace: WalletPkTelemetry.Prefix,
                    serviceVersion: options.ServiceVersion,
                    serviceInstanceId: Environment.MachineName)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] = environmentName,
                }))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(WalletPkTelemetry.Wildcard)
                    .AddAspNetCoreInstrumentation(aspNetCore => aspNetCore.Filter = httpContext => !IsHealthCheck(httpContext))
                    .AddHttpClientInstrumentation();

                if (options.ExportTraces)
                {
                    tracing.AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = options.OtlpTracesEndpoint;
                        otlp.Protocol = OtlpExportProtocol.Grpc;
                    });
                }

                options.ConfigureTracing?.Invoke(tracing);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(WalletPkTelemetry.Wildcard)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (options.ExportMetrics)
                {
                    metrics.AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = options.OtlpMetricsEndpoint;
                        otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    });
                }

                options.ConfigureMetrics?.Invoke(metrics);
            });
    }

    private static bool IsHealthCheck(HttpContext httpContext) =>
        httpContext.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
}
