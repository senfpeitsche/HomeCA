# HomeCA UX contract

The MudBlazor client is a local-admin tool. The session token exists only in the active server circuit and is cleared by **Abmelden**.

| Flow | Outcome | Feedback |
|---|---|---|
| Login | Open overview and load protected inventories | Inline credential error on failure |
| Certificate / CA read | Keep current view | Inline empty or error state |
| CA create, edit, deactivate or revoke | Keep CA view and reload inventory | Shared notification; revoked CAs cannot be reactivated |
| CA delete | Require a second explicit press; only inactive/revoked CAs without issued certificates or subordinate CAs may be removed | Inline/API reason plus shared notification |
| Issuance start | Open app-owned assistant beginning with the target profile and its policy-derived defaults | Status notice after the local step |
| Connector / backup test | Remain in settings | Show returned success or failure beside the operation |
| Certificate, SSH certificate, ACME order, revocation or CRL action | Remain in the relevant work area | Persistent result or inline error plus a shared notification |

The application uses German locale formatting. Native `select` controls are acceptable for the small fixed profile list. Destructive recovery actions are not exposed in the UI; restore remains a documented operator procedure.
, auch die andern .md file prüfen