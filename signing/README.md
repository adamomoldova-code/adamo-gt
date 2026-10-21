# SignPath preparation

These files prepare ADAMO GT for a possible SignPath Foundation application. They do not create a certificate, make the repository public, submit an application, or claim Foundation acceptance. The owner must first approve an OSI-approved license and the public repository. See the root `CODE-SIGNING.md` for project status.

## Public source export

Run `powershell -NoProfile -File scripts/Export-PublicSource.ps1`. It writes a new directory under `build/public-source` from an explicit allowlist and records relative file names and SHA-256 hashes in `SOURCE-MANIFEST.json`. It never initializes or pushes Git. Existing export directories are rejected; filesystem links are rejected. The export contains build recipes, tests, approved public documentation, selected brand images, dependency libraries and their source/license materials.

It excludes local machine JSON reports, screenshots, attachments, build outputs, release binaries, test artifacts, internal preparation reports, and the private local path in `src/assets/brand-source.md`. A pending license does not prevent a local preparation export, but signing requires both a root license and a maintainer's explicit license-approval variable. Review the export before publishing; the allowlist is not a complete secret scanner.

## Repository and service configuration

After the owner approves the license, repository identity and public export, create the public repository and establish the unsigned build on GitHub-hosted runners before applying to the Foundation. The application needs those public sources and build evidence. Configure protected default-branch reviews and MFA for maintainers. Only after Foundation acceptance, install/link the official SignPath GitHub connector and configure the service. Create a GitHub environment named `signpath-release`, restrict it to the default branch, and require a release approver. Set the SignPath release-signing policy to require manual approval for every request, with origin restricted to this repository and workflow. A workflow condition is an additional guard and does not replace server-side signing policy.

Import `inner-exe.xml` as artifact configuration `adamo-gt-inner-v1`, and `wrapper-exe.xml` as `adamo-gt-wrapper-v1`. Have SignPath validate both configurations. They sign only our two executables. The unsigned upstream libraries, smartctl and DiskSpd remain unchanged inside the package; signing them using our Foundation certificate is not permitted. DiskSpd also has a pinned application integrity hash.

Configure these GitHub environment values only after approval:

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `SIGNPATH_API_TOKEN` | SignPath CI submitter token; never commit or print it |
| Variable | `SIGNPATH_ORGANIZATION_ID` | Organization ID assigned by SignPath |
| Variable | `SIGNPATH_PROJECT_SLUG` | Accepted project's slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | Manually approved release policy slug |
| Variable | `SIGNPATH_CERTIFICATE_SUBJECT` | Exact public certificate subject approved by SignPath |
| Variable | `SIGNPATH_LICENSE_APPROVED` | `true` only after owner-approved OSI license and eligibility review |

## Build and signing order

1. Manually run `Windows build and optional SignPath signing` with signing off first. It uses Windows PowerShell 5.1, .NET Framework and vendored libraries on `windows-2022`. It runs `test.ps1`, `test-single-file.ps1`, builds a wrapper, and prepares source/runtime artifacts. It does not activate real camera/audio devices or install PawnIO.
2. Once GitHub build and service configuration are verified, manually dispatch from the protected default branch with `sign_release=true`. The signing job fails before submission if configuration/license is missing.
3. Sign `dist/ADAMO-GT.exe`; require a valid Windows trust chain, timestamp, exact publisher subject and product/version metadata.
4. Insert that signed inner EXE into runtime. Build the single-file wrapper so the embedded manifest hashes the signed inner file. Sign and verify the wrapper separately. Each SignPath request requires a separate manual approval under the configured policies.
5. Package the signed EXE, portable ZIP, matching source ZIP and final SHA-256 checksums. The workflow uploads them as review artifacts; it does not publish a GitHub release or alter adamo.md. Publish under a new version after verification rather than replace an existing published version.

## Verification limits

`scripts/Test-SigningPreparation.ps1` checks PowerShell syntax, XML structure/scoping and basic workflow guards. These are local checks, not SignPath schema/server acceptance or a GitHub-hosted run. `Test-SignedBinary.ps1` verifies only real returned signatures; it does not generate or trust self-signed certificates. Full smoke tests include WPF fixture rendering; the first hosted run must establish runner compatibility. Real hardware tests remain a separate, explicit local activity.

The current legacy `csc.exe` build is traceable from source, not claimed byte-for-byte reproducible: compiler and ZIP timestamps and Authenticode timestamps can differ. Dependencies are vendored so no dependency download is needed during compilation. DiskSpd's optional source rebuild requires the separately documented pinned MSVC/ATL/SDK; the default application build copies the existing reviewed MIT binary unchanged.

Actions are pinned to the commits verified on 2026-10-13: checkout v7.0.1, upload-artifact v7.0.2, download-artifact v8.0.1, and SignPath action v3. The public SignPath v3 tag was resolved using `git ls-remote`; its Node 24 action definition and parameters match the official documentation.

Sources: [Foundation terms](https://signpath.org/terms.html), [GitHub integration](https://docs.signpath.io/trusted-build-systems/github), [artifact syntax](https://docs.signpath.io/artifact-configuration/syntax), [metadata restrictions](https://docs.signpath.io/artifact-configuration/reference#file-metadata-restrictions), [SignPath action v3](https://github.com/SignPath/github-action-submit-signing-request/tree/f6d04783b4569d051e0c80105fe66e82819d0092).
