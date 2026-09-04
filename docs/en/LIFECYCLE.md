# Certificate Lifecycle

This short routine prevents certificates from becoming anonymous files without ownership.

## New service

1. Define DNS names, target system, and responsible person.
2. Select an appropriate target profile and decide whether TLS or mTLS is required.
3. Choose either an unrestricted issuance zone or an allowlist. Prefer an allowlist for individual administrative services.
4. Issue a certificate with the shortest practical validity period.
5. Download the deployment package only on a trusted administration system, install private keys with restrictive permissions, and reload the service.
6. From a client network, verify the chain, name, and validity. Record the target and owner in the inventory.

## Normal operation and renewal

- Use one renewal plan per service or the native ACME client; after renewal the target service must reload its files.
- Check expiry warnings daily in monitoring or email.
- Review the inventory monthly and investigate unknown, unused, or soon-to-expire certificates.
- Run a test renewal after changing a profile, DNS, or service.

## Service changed or retired

1. Add new names as SANs to the existing or a new certificate first.
2. After a successful migration, remove old names and revoke the old certificate if its key or system is no longer trustworthy.
3. Remove retired services from renewal plans, ACME configuration, DNS allowlists, and the inventory.
4. Securely delete old private keys and deployment packages.

## CA rotation

Replace an expiring issuing CA early with a new intermediate under the same root. Keep the old intermediate available until every certificate it issued has been renewed or has expired. See [Operations](OPERATIONS.md) for details.

Rotate the root CA only for expiry, credible compromise, or a planned change. This is a separate project: distribute the new root, replace every issuing CA and leaf certificate, then remove old trust anchors.
