# ADAMO GT

A portable Windows application for checking laptop and desktop hardware before buying or selling a computer.

ADAMO GT displays CPU, RAM, GPU, storage, battery, display and model information in one window. It provides short SSD/GPU benchmarks, selectable CPU/RAM/SSD/GPU stress tests, and guided camera, microphone, keyboard and speaker tests. Missing or unsupported sensor values remain unavailable rather than being invented. Battery runtime is an estimate; these checks are not a guarantee of hardware condition.

Project and downloads: [ADAMO.MD](https://adamo.md/ro/blog/adamo-gt-verificare-laptop-pc).

## Version 2.7.1

This source builds ADAMO GT 2.7.1 with the simplified office CRM flow described below. Official download availability is listed on the project page linked above. The executable is unsigned; no SignPath Foundation approval or signed release exists yet.

This version retains selectable RAM stress allocation, established Windows/OEM temperature providers and the camera compatibility fixes. It does not install sensor drivers automatically or change Windows security settings. The camera waits for actual frames and uses a bounded in-memory RGB display path; no image or video is saved. iMac/Boot Camp compatibility still requires validation on the affected computer with its camera driver.

The original ADAMO GT code, documentation and original bundled artwork are available under the [MIT License](LICENSE), adopted with the owner's authorization on 21 October 2026. Third-party components retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md). ADAMO trademark rights are retained separately; the copyright license does not grant trademark rights or permission to imply endorsement. Source repository: [adamomoldova-code/adamo-gt](https://github.com/adamomoldova-code/adamo-gt). SignPath Foundation approval and a signed release are pending.

## CRM

The **CRM** button appears only when the server confirms office access. Click it, enter the integration password, choose quantity and condition, then click **Trimite și imprimă**. The server must have the office IP, credentials, warehouse and printer configured; without that confirmation the button stays hidden. Local diagnostics remain available.

The app automatically prepares hardware specifications and a real screenshot of its first page before the password dialog opens. It captures the application content, including visible status text, without the desktop, other windows or the password. Structured data omits computer names, serial identifiers and raw logs; unknown hardware values remain unknown. External or unclassified disks and external displays are omitted from the automatic product data. For quantity above one, the same configuration is applied to all units while the screenshot describes this computer.

CRM creates a draft product and AD inventory codes using its configured warehouse, attaches the specification screenshot and queues the existing stickers on its configured printer. The screenshot is not automatically selected as the cover. Missing product details can be completed later in CRM; this action does not publish the product to a website or marketplace.

A queued sticker job is not confirmation of paper output. An interrupted submission can be retried with the same primary button. The encrypted local receipt preserves the original request so retries do not create another product or print job. If photo attachment fails, the same operation retries that attachment.

The server independently enforces office access on protected requests. A changed public office IP requires updating the server allowlist. No warehouse, printer, price, product title, notes or component checklist is requested in the desktop form.

Uncertain requests are saved locally as an encrypted, account-specific operation and retried with the same idempotency key. Do not delete this receipt or create a separate intake merely because a response was lost. CRM credentials and access tokens are not stored with it.

## Requirements and use

- Windows x64 and .NET Framework 4.8. Sensor support varies by hardware, firmware, Windows policy and driver availability.
- The application requests administrator privileges for hardware access.
- First launch shows information before running local benchmarks sequentially. Later launches reuse stored results; buttons start new tests.
- Port information uses the local catalog/cache at startup. Online lookup requires clicking refresh/search and agreeing to the explanation. The explicit `--ports-probe` diagnostic command also requests online lookup.
- CPU temperatures can require the separately installed PawnIO driver. Its installation is optional and uses its own official installer.
- The application uses the established LibreHardwareMonitor/PawnIO and Windows/OEM sensor paths. The AdamoThermal prototype and its device transport are kept outside the application sources and release payload. The retired `--adamo-thermal` argument is ignored for compatibility with old shortcuts. Run the normal application as administrator when existing CPU sensors require it; unavailable readings remain unavailable.

## Stress tests

Click **Стресс**, select CPU, RAM, SSD and/or GPU, choose 1, 5, 10 or 15 minutes, and click **Начать**. Selected tests run together by default; **По очереди** runs them sequentially for the selected duration each. Stress tests never start automatically. **Остановить** remains visible while scrolling; closing the window also requests cancellation and waits for resource cleanup.

- CPU uses checked integer calculations on up to 64 logical processors.
- RAM volume is selectable: Auto (the default), or up to 2, 4, 8, 16, 32 or 64 GB. Version 2.6.5 removes the old 2 GB total limit. Auto uses up to 80% of initially available physical memory, leaving at least 1 GB for Windows. Fixed selections are upper bounds and are reduced to the available safe budget. Commit/address-space limits can reduce the allocation further. The actual target and allocated volume are shown during preparation.
- Each of three RAM patterns is written across the entire allocated pool before verification. One reported cycle means all three patterns checked every allocated block. The detail shows allocated memory, coverage by all three patterns, and verified block operations. A timeout before the first complete cycle is reported as incomplete, not successful; large volumes may need a longer duration.
- RAM rechecks available physical memory before each allocation and each write/read block, stopping if its reserved headroom is lost. Cancellation is checked inside the pattern loops. The JSON report retains requested, allocated and covered volumes, including incomplete runs. This is a Windows process memory test: caches and paging remain active, and it does not test memory occupied by Windows and other applications or replace an offline memory diagnostic.
- SSD writes and verifies its own temporary file of up to 256 MB on the selected drive. After 8 GB of writes in one session it continues reading and verifying that file. User files are not test data. Unsupported or ambiguous volumes are rejected.
- GPU uses a hardware D3D11 workload and validates timing/device responses. It does not check every rendered pixel or the whole VRAM.
- With fresh matching sensor readings, reaching CPU 95 °C, GPU 90 °C or SSD 70 °C stops the session. Missing or stale temperatures cannot provide temperature protection. A workload error also cancels the remaining loads.
- Results and peak available temperatures remain visible during this application session and are included in the JSON report. They do not replace saved speed benchmarks. A completed stress run is not a guarantee that hardware has no faults.

See [privacy and removal instructions](PRIVACY.md), [compatibility](COMPATIBILITY.md), [portable packaging](SINGLE-FILE.md), and [third-party notices](THIRD-PARTY-NOTICES.md).

## Build and check

Run these commands from a Windows PowerShell 5.1 prompt in a clean source export:

```powershell
.\build.ps1
.\test.ps1
.\test-single-file.ps1
.\build-single-file.ps1
```

Build inputs are the checked-in C# and XAML source, catalog data, artwork and pinned third-party files. The local build does not require an IDE. Do not overwrite an existing release with a rebuilt file under the same version.

## Code signing policy

See [Code signing policy](CODE-SIGNING.md). Only the project's own application and single-file launcher are candidates for Foundation signing. Third-party helpers and libraries keep their upstream identity and must not be re-signed as ADAMO GT.

Creat cu ❤️ în Moldova | ADAMO.MD
