import { redirect } from "next/navigation";

/** The wallet decides: signed in → home, otherwise → login. */
export default function Index() {
  redirect("/wallet");
}
