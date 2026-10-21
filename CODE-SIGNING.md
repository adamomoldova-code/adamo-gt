# Code signing policy

Status as of 21 October 2026: the published ADAMO GT 2.7.1 application and single-file launcher remain unsigned. The owner authorized MIT licensing of the original code and bundled original artwork, and publication to [adamomoldova-code/adamo-gt](https://github.com/adamomoldova-code/adamo-gt). The license is recorded in [LICENSE](LICENSE). The original public source passed its [GitHub-hosted build](https://github.com/adamomoldova-code/adamo-gt/actions/runs/39620871067). The 2.7.2 candidate adds the embedded privacy policy and an explicit preference for background CRM availability checks. Foundation acceptance, MFA verification and signing-service configuration remain pending. No Foundation signing access has been assigned. Do not present this document as proof of a digital signature.

## Proposed scope after approval

Only ADAMO GT's own application executable and single-file launcher, built from the public repository by its reviewed GitHub Actions workflow, are intended for signing. Upstream DLLs, smartctl, DiskSpd and any sensor driver are not to be signed with this project's Foundation subscription. Their licenses and original identities remain intact.

The build first compiles and submits the inner ADAMO-GT.exe. After its signature is verified, it is included in the launcher's embedded payload and manifest. The newly built launcher is then submitted separately. Both signatures must be verified before archives and SHA-256 checksums are created. Approval of the custom embedded-payload arrangement must be confirmed with SignPath.

Repository maintainer and reviewer: [adamomoldova-code](https://github.com/adamomoldova-code), the authenticated account that owns this repository. The intended release approver is that maintainer account; SignPath membership and actual signing authorization are not configured yet. Every signing request must receive manual approval. Participants must use MFA for both repository and signing-service access, and the maintainer must verify that requirement before application submission.

## Foundation credit

After approval, the project home page and release pages must carry this attribution:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation.

That credit describes the proposed future service, not the current unsigned release. The certificate publisher would be SignPath Foundation. A signature does not guarantee that every Windows reputation check will immediately stop warning.

## Privacy

Online model lookup and the optional driver download are user-requested operations. In the 2.7.2 candidate, the first graphical launch presents the privacy policy; automatic CRM availability checks stay off until the operator explicitly enables and saves that preference. The fixed CRM service receives the connection IP, but no hardware report, during availability checks. The published 2.7.1 performs these checks automatically. Hardware specifications and the first-page screenshot are sent only when the operator explicitly submits the optional CRM form. See [ADAMO GT privacy and removal](PRIVACY.md), including stored preferences and removal instructions. Reports and manual device media are not automatically uploaded.

## Windows trust and reputation

The current unsigned download has no verified Authenticode publisher. SmartScreen evaluates the downloaded file's reputation and, when present, its signing identity. A valid signature identifies the publisher and protects file integrity; even OV or EV signing does not guarantee that a new release will immediately stop showing reputation warnings. A ZIP archive or a self-signed certificate does not establish public publisher trust. See [Microsoft's code signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) and [current SmartScreen behavior](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/distribution-feature-status#smartscreen-reputation-ev-certificates-no-longer-grant-instant-bypass).

[Microsoft KMS](https://learn.microsoft.com/en-us/windows-server/get-started/kms-activation-planning) activates volume-licensed Windows systems; it does not sign ADAMO GT or give its executable SmartScreen reputation. The signing workflow does not install untrusted certificates, alter Windows protection settings or reuse another publisher's private keys.

## References

- [Foundation conditions](https://signpath.org/terms.html)
- [Application form](https://signpath.org/apply.html)
- [GitHub integration and trusted build requirements](https://docs.signpath.io/trusted-build-systems/github)
