# ADAMO GT — privacy and removal

This document describes ADAMO GT 2.7.1 and its optional office CRM integration. Earlier versions may have different optional features.

## Local diagnostics

Hardware inventory, benchmarks and manual device tests run on this computer. ADAMO GT does not automatically upload reports, benchmark results, camera frames, microphone recordings or keyboard input to ADAMO.

Camera preview and microphone recordings of up to five seconds are used in memory. Saved device test results contain the user's verdict, device details and date, and a count of tested keys where relevant. They do not contain photographs, sound or an individual sequence of pressed keys.

## Optional network access

Normal graphical startup uses a local specification catalog and saved results. The refresh and search controls in the ports section display an explanation before online access. Choosing No leaves local information and tests available. Choosing Yes requests the lookup for that action; it does not enable future automatic lookups.

The lookup sends the manufacturer and model in a DuckDuckGo query and may retrieve matching manufacturer specification pages. It does not intentionally include the computer name, serial numbers, benchmark results or camera/microphone data. Remote services receive IP addresses and ordinary request information. The [DuckDuckGo privacy policy](https://duckduckgo.com/privacy) and the privacy policy linked from the manufacturer's source page apply. Allowed specification domains are hp.com, dell.com, lenovo.com, asus.com, acer.com, msi.com and microsoft.com. The Source button identifies the particular page. Browser links use the user's normal browser and its settings.

The command-line `--ports-probe <output>` option explicitly requests an online model lookup and writes its result locally; it does not show a graphical confirmation.

If the user chooses Install PawnIO in the sensor help window, ADAMO GT downloads the pinned official installer from GitHub, verifies its integrity and publisher, and opens the separate installer. The user decides whether to complete installation. [GitHub privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement); [PawnIO project](https://pawnio.eu/). Windows certificate checks and other operating-system services may make their own network requests.

## Stored information

### Optional CRM submission

Normal application startup and periodic/network-change checks make an anonymous HTTPS request to the fixed `https://crm.adamo.md` availability endpoint to decide whether the CRM button may be shown. This request contains no hardware report, password or token; CRM sees the connection IP. No positive decision is persisted or reused through the HTTP cache.

The form asks only for the integration password, quantity and condition. On explicit submission it sends an allowlisted hardware projection and the captured content of the real first page, including visible temperatures/status text, but excluding the desktop, other windows, password dialog and tooltips. The image is captured before the password dialog; camera/microphone/keyboard data and the full JSON report are not attached. The captured page and structured hardware are fixed together; subsequent sensor refreshes do not change a pending submission. The structured projection excludes Windows computer names, serial-derived hardware identifiers, local paths and raw diagnostic errors.

The v2 password travels only to the fixed CRM HTTPS session endpoint. The scoped token stays in memory. A random installation ID and a Windows-account-encrypted pending request are saved in `%LOCALAPPDATA%\ADAMO GT\crm-v2`; no credentials or tokens are saved. Exact retries retain the same operation ID. Existing v1 receipts are not removed or migrated automatically. The server chooses the warehouse and sticker printer.

Uploaded screenshots follow CRM's media visibility and retention rules and are not automatically made the product cover or published as a listing. The existing sticker workflow receives product and AD-code information. Do not remove an unresolved local receipt before checking the corresponding operation in CRM. Historical 2.7.0 receipts, if present, remain in `%LOCALAPPDATA%\ADAMO GT\crm` and are not deleted by this version.

### Local storage locations

- `%PROGRAMDATA%\ADAMO GT\History`: shared local benchmark and device-test history. Ordinary local Windows users can read it; administrators and SYSTEM can write it. It is not private to one Windows account. Hardware matching uses hashed identifiers.
- `%LOCALAPPDATA%\ADAMO GT\Runtime`: extracted files from the single-file launcher.
- `%LOCALAPPDATA%\PcPulse\specifications`: saved model specifications.
- `%LOCALAPPDATA%\PcPulse\last-error.log`: latest application error; exception details may include local paths.
- Reports are saved only at a user-selected location, normally under `%LOCALAPPDATA%\PcPulse\reports`. They can include the computer name, model/SKU, hardware and device identifiers, measurements, dates and manual verdicts. Review them before sharing. The application does not upload them.

SSD benchmarks create temporary test files on the selected drive and consume writes. Normal completion/cancellation removes the owned test file. A crash or power loss may leave temporary data. GPU benchmarks temporarily increase load and temperature.

Stress tests start only when selected and started manually. CPU/RAM/GPU loads operate locally. SSD stress uses its own temporary file of up to 256 MB with a limit of 8 GB written per session, then continues reading and verifying. It does not inspect user file contents. Stress results stay in memory for this application session and may be included in a user-exported report, independently of saved speed-test history. Cancellation and closing wait for pending operations to release their resources.

## Removal

1. Close ADAMO GT and allow running tests to stop. Delete the downloaded portable EXE/ZIP or extracted application directory when no longer needed.
2. To remove the launcher cache for this Windows account, delete `%LOCALAPPDATA%\ADAMO GT\Runtime` after all ADAMO GT processes have exited.
3. To erase shared test history for all users, an administrator can remove `%PROGRAMDATA%\ADAMO GT\History`. This removes the record that prevents first-run automatic tests; running the app again may start those tests again.
4. Delete the specification cache and error log listed above if unwanted. Remove saved reports separately, including any reports saved outside the default folder. Do not delete unrelated files merely because they are in a parent directory.
5. If PawnIO was installed, uninstall it separately using Windows Settings → Apps and its own uninstaller. Other hardware-monitoring programs may also use it. Deleting ADAMO GT does not uninstall that driver.

Project information and contact: [ADAMO.MD](https://adamo.md/).
