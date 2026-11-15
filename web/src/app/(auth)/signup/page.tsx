import type { Metadata } from "next";
import { SignupFlow } from "./signup-flow";

export const metadata: Metadata = { title: "Create account" };

export default function SignupPage() {
  return <SignupFlow />;
}
