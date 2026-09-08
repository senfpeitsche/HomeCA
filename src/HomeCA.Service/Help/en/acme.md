# Configure ACME

HomeCA provides an RFC 8555-compatible directory for standard ACME clients.

```text
http://HOMECA:5080/acme/directory
```

Configure an allowed issuance zone before requesting certificates. Do not use `/api/v1/acme/` as the RFC 8555 directory URL.

## Setup checklist

1. Configure the issuing zone.
2. Decide whether the client is covered by the network allowlist or needs EAB.
3. Configure the ACME client with the RFC 8555 directory URL above.
4. Choose HTTP-01 for internal issuance, or configure a DNS connector for an external DNS-01 issuer.
5. Verify the resulting order and certificate in the HomeCA inventory.

## Client access

Allowlisted client networks can create accounts without External Account Binding (EAB). For every other client, create an individual EAB credential in HomeCA and copy the displayed Key ID and HMAC key immediately; the HMAC key is shown only once.

Use a direct client IP address or CIDR network in the allowlist. Do not allowlist a reverse-proxy address because that would grant every client behind it access.

## DNS-01 issuance

Associate the issuance zone with a configured DNS connector before using DNS-01. Test the connector and its TXT-record permission first. Keep ACME account and order IDs unchanged when diagnosing client issues.

Before production use, run the connector check and a TXT test from **Settings**. A successful connection alone is not enough: the configured token must be allowed to create and remove records in the selected zone.

## Internal HTTP-01 issuance

For HomeCA's internal RFC 8555 server, configure the client to answer HTTP-01 and expose the key authorization at `http://<DNS-name>/.well-known/acme-challenge/<token>`. HomeCA fetches this URL after the client responds to the ACME challenge; the authorization becomes valid only when the response matches. The client must be reachable from HomeCA, but it does not need public Internet exposure.

Use an external ACME issuer only when a publicly trusted certificate is required. Keep internal HomeCA issuance for services whose clients already trust the HomeCA Root CA.

Store EAB HMAC keys in the ACME client's protected secret store; do not place them in shell history or shared configuration files.

Request names only from zones that are explicitly enabled for the selected issuer or internal issuance policy.

After a successful order, verify the resulting certificate inventory entry and deploy it through the same controlled process as other TLS certificates.

When an order fails, inspect the challenge status and DNS connector result before creating a new account or credential.

Allow DNS propagation time before retrying a DNS-01 challenge; avoid leaving diagnostic TXT records behind after testing.

Record the ACME account, order, and connector involved in a failure so that investigation remains reproducible.
