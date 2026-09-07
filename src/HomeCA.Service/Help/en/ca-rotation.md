# Rotate an Intermediate CA

Create a replacement Intermediate CA under the same Root CA, make it the issuing CA, then deploy newly issued certificate chains.

> Revoke a CA only after a security incident. A routine rotation should deactivate the old Intermediate CA after its certificates have expired or been replaced.

## Rotation sequence

1. Create the replacement Intermediate CA below the existing Root CA.
2. Make the replacement the active issuing CA and issue or renew certificates for each target.
3. Deploy the new certificate chains with the target systems.
4. Keep the former Intermediate CA and its CRL reachable until all certificates it issued have expired or been replaced.

The Root CA does not change during a normal Intermediate rotation, so clients do not need a new trust-anchor installation. A CA revocation is reserved for a compromised CA key and requires replacement of every certificate it issued.

After rollout, inspect a renewed service certificate and confirm that it chains to the replacement Intermediate CA. Continue monitoring the old certificate inventory until the retirement conditions are met.

Plan the rotation early enough that newly issued certificates can receive their full intended validity before the current Intermediate CA expires.

Do not remove the former Intermediate CA's CRL endpoint while certificates issued by it are still in use.

Confirm the replacement is the active issuing CA before creating the first renewed certificate.

For a suspected CA-key compromise, revoke the affected CA, issue replacements, and prioritize deployment to exposed services.

Use the certificate inventory to identify every certificate issued by the retiring or revoked CA.

Notify service owners of the rotation window and the required chain update before changing the active issuer.
