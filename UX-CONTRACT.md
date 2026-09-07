# HomeCA UX contract

The MudBlazor client is a local-admin tool. The bearer token exists only in the active server circuit. A browser-session HttpOnly cookie holds an opaque, server-side reference solely to survive a locale-triggered reload; **Abmelden** clears that reference.

| Flow | Outcome | Feedback |
|---|---|---|
| Login | Open overview and load protected inventories | Inline credential error on failure |
| Certificate / CA read | Keep current view | Inline empty or error state |
| CA create, edit, deactivate or revoke | Keep CA view and reload inventory | Shared notification; revoked CAs cannot be reactivated |
| CA delete | Require a second explicit press; only inactive/revoked CAs without issued certificates or subordinate CAs may be removed | Inline/API reason plus shared notification |
| Certificate revoke | Require a second explicit press; retain the certificate record and its export snapshot for audit and forensic use | Shared notification confirms the CRL update and retained record |
| Issuance start | Open app-owned assistant beginning with the target profile and its policy-derived defaults | Status notice after the local step |
| Connector / backup test | Remain in settings | Show returned success or failure beside the operation |
| Certificate, SSH certificate, ACME order, revocation or CRL action | Remain in the relevant work area | Persistent result or inline error plus a shared notification |

The application defaults to English locale formatting. The shared language selector persists a browser-scoped choice using the ASP.NET Core request-culture cookie and takes effect after a reload; German is a production-supported alternative. Native `select` controls are acceptable for the small fixed profile list. Destructive recovery actions are not exposed in the UI; restore remains a documented operator procedure.
