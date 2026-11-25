/** Where the browser reaches the API gateway (REST and the /hubs/wallet SignalR hub). */
export const publicApiUrl = trimSlash(process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000");

/** Developer tools (SMS inbox, test top-ups). The services must run in Development for them to answer. */
export const devToolsEnabled = process.env.NEXT_PUBLIC_DEV_TOOLS === "true";

/** Jaeger's UI, for "open trace" links in the operations portal. */
export const jaegerUrl = trimSlash(process.env.NEXT_PUBLIC_JAEGER_URL ?? "http://localhost:16686");

/** Grafana's UI, for the dashboards linked from the operations portal. */
export const grafanaUrl = trimSlash(process.env.NEXT_PUBLIC_GRAFANA_URL ?? "http://localhost:3001");

/** Where the Next.js server reaches the gateway (inside docker compose this is the gateway container). */
export function serverApiUrl(): string {
  return trimSlash(process.env.API_URL ?? process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000");
}

function trimSlash(url: string): string {
  return url.replace(/\/+$/, "");
}
