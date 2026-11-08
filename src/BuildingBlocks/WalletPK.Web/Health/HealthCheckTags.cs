namespace WalletPK.Web.Health;

/// <summary>
/// Tags that decide which endpoint a health check belongs to.
/// <para><b>live</b>: the process is running (restart it if not).</para>
/// <para><b>ready</b>: its dependencies (Postgres, Redis, Kafka) are reachable (stop sending traffic if not).</para>
/// </summary>
public static class HealthCheckTags
{
    public const string Live = "live";
    public const string Ready = "ready";
}
