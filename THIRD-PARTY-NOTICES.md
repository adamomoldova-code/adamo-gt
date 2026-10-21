# ADAMO GT — third-party components

The [MIT License](LICENSE) for the original ADAMO GT code and artwork does not replace the licenses below. Dependencies are included unchanged except for the documented DiskSpd source build. Signing is limited to ADAMO GT's own files.

| Component | Version | License | Source / included notice |
| --- | --- | --- | --- |
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | `vendor/LibreHardwareMonitor.LICENSE.txt`, `vendor/LibreHardwareMonitor.THIRD-PARTY-NOTICES.txt`; source pinned in `vendor/COMPONENTS.json` |
| BlackSharp.Core | 1.0.7 | MPL-2.0 | Source commit and attribution in `vendor/COMPONENTS.json` |
| DiskInfoToolkit | 1.1.2 | MPL-2.0 | Source commit and attribution in `vendor/COMPONENTS.json` |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 | Source commit in `vendor/COMPONENTS.json`; NDD omits default driver implementations |
| HidSharp | 2.6.4 | Apache-2.0 | `vendor/HidSharp.LICENSE.txt` |
| PawnIO.Modules embedded in LibreHardwareMonitor | 0.1.6 | LGPL-2.1 | `vendor/PawnIO.Modules-0.1.6-source.zip`, LHM third-party notices |
| Microsoft System.* / Microsoft.Bcl.* runtime libraries | Included LHM dependencies | MIT | `vendor/Microsoft.MIT.LICENSE.txt`, LHM third-party notices |
| smartctl and drivedb.h | 7.5 distribution | GPL-2.0-or-later | License, attribution and source tarball in `vendor/smartmontools-7.5/runtime` |
| Microsoft DiskSpd | 2.3 | MIT | `vendor/diskspd-2.3/runtime/LICENSE.txt`, `SOURCE.json`; source archive and build recipe under `vendor/diskspd-2.3` |

See the pinned source URLs and copyright names in `vendor/COMPONENTS.json` and the component-specific provenance files. Runtime packages carry these notices and the source required for the included smartmontools and embedded PawnIO modules.

PawnIO's Windows driver is optional and is not bundled. The application can download its official separately signed installer only after a user request. Its [upstream project](https://pawnio.eu/) controls its license and installer. The Foundation application must disclose this optional dependency and ask how its alternative upstream licensing affects eligibility.

No AIDA64, CrystalDiskInfo, CrystalDiskMark or Heaven proprietary executable is bundled in ADAMO GT.
