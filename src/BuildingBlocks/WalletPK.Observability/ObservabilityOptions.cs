using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

namespace WalletPK.Observability;

/// <summary>
/// Where logs, traces and metrics go. Defaults point at the local Docker stack (M0.2);
/// override them in appsettings under "Observability" or with environment variables
/// such as <c>Observability__SeqUrl=http://seq:5341</c> when running inside Docker.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>Service name shown in Seq, Jaeger and Prometheus, for example "ledger".</summary>
    public string ServiceName { get; set; } = "walletpk";

    public string ServiceVersion { get; set; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";

    /// <summary>Human-readable logs in the terminal.</summary>
    public bool WriteToConsole { get; set; } = true;

    /// <summary>Structured, searchable logs in Seq.</summary>
    public bool WriteToSeq { get; set; } = true;

    public Uri SeqUrl { get; set; } = new("http://localhost:5341");

    /// <summary>Distributed traces to Jaeger over OTLP gRPC.</summary>
    public bool ExportTraces { get; set; } = true;

    public Uri OtlpTracesEndpoint { get; set; } = new("http://localhost:4317");

    /// <summary>Metrics pushed to Prometheus' OTLP receiver.</summary>
    public bool ExportMetrics { get; set; } = true;

    public Uri OtlpMetricsEndpoint { get; set; } = new("http://localhost:9090/api/v1/otlp/v1/metrics");

    /// <summary>Extra Serilog configuration (tests add an in-memory sink here).</summary>
    public Action<LoggerConfiguration>? ConfigureLogging { get; set; }

    /// <summary>Extra tracing configuration (tests add an in-memory exporter here).</summary>
    public Action<TracerProviderBuilder>? ConfigureTracing { get; set; }

    /// <summary>Extra metrics configuration.</summary>
    public Action<MeterProviderBuilder>? ConfigureMetrics { get; set; }
}
