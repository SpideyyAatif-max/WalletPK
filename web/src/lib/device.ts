const installationKey = "walletpk.installation-id";

/**
 * A random ID created once per browser, like a phone app's installation ID. Identity trusts a device
 * after one OTP, so the next login from this browser needs only the MPIN.
 */
export function installationId(): string {
  let id = localStorage.getItem(installationKey);
  if (!id) {
    id = crypto.randomUUID();
    localStorage.setItem(installationKey, id);
  }
  return id;
}

/** "Chrome on Windows", shown in the user's device list. */
export function deviceName(userAgent: string = navigator.userAgent): string {
  const browser = /Edg\//.test(userAgent)
    ? "Edge"
    : /OPR\//.test(userAgent)
      ? "Opera"
      : /Firefox\//.test(userAgent)
        ? "Firefox"
        : /Chrome\//.test(userAgent)
          ? "Chrome"
          : /Safari\//.test(userAgent)
            ? "Safari"
            : "Browser";
  const os = /Windows/.test(userAgent)
    ? "Windows"
    : /Android/.test(userAgent)
      ? "Android"
      : /iPhone|iPad/.test(userAgent)
        ? "iOS"
        : /Mac OS X/.test(userAgent)
          ? "macOS"
          : /Linux/.test(userAgent)
            ? "Linux"
            : "unknown OS";
  return `${browser} on ${os}`;
}
