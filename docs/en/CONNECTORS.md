# Add a custom DNS connector

DNS connectors let HomeCA create and remove DNS-01 TXT records for external
ACME issuers. The built-in connector types are Technitium and Hetzner; this
guide describes how to add another provider.

## Architecture

Connector implementations live behind the DNS connector interface and are
resolved through dependency injection. The service stores a connector instance
with its non-secret settings; secrets are supplied separately and must never be
returned to the UI or written to logs.

## Implementation

1. Read the existing connector interface and the Technitium and Hetzner
   implementations in the service project.
2. Create an implementation for the provider that validates its required
   endpoint, token, zone and record data.
3. Implement create, lookup/verify and removal of the TXT record. Keep the
   operations idempotent: a retry must not leave duplicate records behind.
4. Translate provider failures into actionable errors without exposing secrets.
5. Register the implementation in dependency injection and give it a stable
   type identifier.

## UI and testing

Add the type to the connector selection only when it is supported end to end.
Use a masked input for tokens, and offer the existing connector test before an
issuer can rely on it. Test successful create/remove, invalid credentials,
missing zones, retry behavior and cleanup after a failed ACME order.

## Checklist

- No secret is logged, returned, or persisted in browser storage.
- TXT creation and deletion are idempotent.
- The DNS provider's propagation and API error behavior is documented.
- Dependency injection and the UI type selection use the same identifier.
