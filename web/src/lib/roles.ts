// Roles in the access token's "role" claim, as the services' policies check them (WalletPkPolicies).

export const staffRoles = ["ops", "compliance", "finance", "admin"] as const;
export const complianceRoles = ["compliance", "admin"] as const;
export const financeRoles = ["finance", "admin"] as const;

type WithRoles = { roles: readonly string[] } | null | undefined;

/** Back-office staff: may open /ops (Saga explorer, dead letters, live dashboard). */
export const isStaff = (user: WithRoles): boolean => !!user && user.roles.some((role) => (staffRoles as readonly string[]).includes(role));

/** Compliance officers and admins: fraud alerts, cases, freezes and rules. */
export const isCompliance = (user: WithRoles): boolean => !!user && user.roles.some((role) => (complianceRoles as readonly string[]).includes(role));

/** Finance and admins: start reconciliation runs and work the breaks (the other staff can look). */
export const isFinance = (user: WithRoles): boolean => !!user && user.roles.some((role) => (financeRoles as readonly string[]).includes(role));

export const roleNames: Record<string, string> = {
  customer: "Customer",
  merchant: "Merchant",
  agent: "Agent",
  ops: "Operations",
  compliance: "Compliance",
  finance: "Finance",
  admin: "Admin",
};
