# Resolution and DPI matrix

Run from the project directory:

```powershell
.\tests\Test-ResolutionMatrix.ps1
# Optional separate output directory:
.\tests\Test-ResolutionMatrix.ps1 -OutputDirectory tests/artifacts/resolution-rerun
```

The runner compiles an isolated diagnostic executable with the .NET Framework compiler and the current product source. It does not rebuild `dist`, launch hardware providers, run SSD/GPU benchmarks, modify Windows display settings, or change product source. Its C# file is outside the top-level `tests/*.cs` discovery used by `test.ps1`.

The matrix covers 14 display resolutions: 1024×768, 1280×720, 1280×800, 1366×768, 1440×900, 1600×900, 1920×1080, 1920×1200, 2560×1440, 3840×2160, 2560×1080, 3440×1440, 1080×1920 and 1440×2560. Each is tested at 100%, 125%, 150% and 200%, with startup and maximized estimates, using populated and unavailable-data fixtures: **224 cases**.

Each case invokes `DashboardLayoutTests.VerifyLayout` through reflection at the requested DPI. Both offscreen hosts inherit the actual window's layout rounding, pixel snapping, flow direction, complete font settings and text formatting/rendering/hinting modes. `FormattedText` uses the descendant's current DPI and formatting mode. Card/control bounds, reference port scrolling, measured text width/height, existing font-size minimums and proportional scaling remain strict. Every visible text drawing is checked against ancestor clips using its actual WPF glyph bounds: whitespace-only font leading is distinguished from lost visible letters.

The same fixture is measured offscreen with `VisualTreeHelper.SetRootDpi`; root and descendant text DPI must agree. A single visual is resized through eight width/height/DPI combinations three times. `SetRootDpi` alone updates DPI metadata without invalidating already-measured offscreen text; the runner therefore explicitly invalidates the visual tree before measuring it again. This is a documented offscreen harness requirement, not evidence of native window DPI-event behavior. `resize-probe.txt` in the earlier `resolution-2.4.4` output records the stale-width diagnosis. Native HWND monitor transitions require their separate test.

The runner also executes the existing viewport, layout/selection, temperature-presentation and history-presentation groups, plus the dialog regression. The legacy layout cases include exactly **960×680 DIP at the host's actual 150% DPI**, which exposed a ports scrolling issue missed by the estimated monitor sizes. The dialog regression covers five work areas down to 320×240 DIP, reachable bottom actions through scrolling, and invalid work-area input.

Usable content is estimated by subtracting **48 DIP for the taskbar, 16 DIP for the horizontal frame and 39 DIP for the title/frame height**. These explicit assumptions make results reproducible; actual Windows themes, taskbar placement and maximized frames can differ. Startup fitting also tests a simulated monitor at a negative horizontal origin. The matrix does not physically switch monitors, change OS scaling or verify per-monitor transitions through a real HWND.

`matrix.json` records the actual runtime, OS system DPI and actual work area separately from simulated cases. `matrix.csv` includes logical content size, Viewbox scale, applied/inherited DPI and effective text sizes. A nominal 14-DIP body font becomes `14 × Viewbox scale` DIP and then that value times the simulated DPI factor in physical pixels. The smallest visible data text excludes the branding footer; the footer size is reported separately. The 12-effective-DIP threshold is a readability diagnostic, not a geometry assertion or a universal visual-acuity claim. A case can pass all geometry checks yet be classified **fits-but-small**.

## Recorded run: ADAMO GT 2.4.3, 13 October 2026

**Historical baseline, superseded for visual correctness.** This first offscreen harness copied only the font family and missed native Display formatting/layout-rounding inheritance. Later native tests found clipped glyphs despite its geometry pass. The strengthened 2.4.4 checks above correct that gap; do not cite this old pass as proof that 2.4.3 had no clipping.

- 224/224 geometry cases passed; 24 repeated resize steps passed; existing viewport regressions passed.
- 132 cases met the 12-effective-DIP data-text threshold; 92 fit but reduced text below it.
- At 100% scaling, 56/56 cases met the threshold; at 125%, 36/56; at 150%, 28/56; at 200%, 12/56.
- 1366×768 at 125% gives approximately 1076.8×527.4 content DIP: body text becomes 10.86 DIP. 1920×1080 at 150% gives 1264×633: body text is 13.03 DIP, with the smallest data text 12.10 DIP. At 3840×2160 and 200%, body text remains 14 DIP.
- 1024×768 at 200% still fits, but body text is only 6.11 DIP; this is not a usable reading size despite passing geometric bounds.
- The actual OS remained at 144 DPI / 150%, with a 1280×752 DIP work area. .NET Framework WPF exposed public `SetRootDpi`; offscreen descendant text reported all four requested DPI scales correctly.

Evidence: `tests/artifacts/resolution-2.4.3/results.txt`, `matrix.csv`, `matrix.json` and `compile.txt`. Hardware probes and benchmarks were not run.

## Final strengthened run: ADAMO GT 2.4.4, 13 October 2026

The final XAML, including the ports spacing correction, passed **224/224 matrix cases**, **24 repeated resize/DPI steps**, and the existing viewport regression. The legacy layout/selection, temperature-presentation and history-presentation groups also passed. The dialog regression passed all five work areas, scrolling reachability and invalid-input checks. Exit code: **0**.

The readability result remains **132 cases at or above the 12-effective-DIP data threshold and 92 fits-but-small cases**; preserving visible glyphs does not enlarge the text on a small/high-scaling screen. Actual OS DPI remained 144 / 150% throughout this offscreen test.

Final evidence: `tests/artifacts/resolution-2.4.4-release/matrix.json`, `matrix.csv`, `results.txt` and `compile.txt`. Earlier outputs are preserved: `resolution-2.4.3-native-inheritance-before` records the pre-fix native-presentation failures; `resolution-2.4.4` records the offscreen DPI invalidation diagnosis; `ui-2.4.4` records the exact 960×680 DIP ports scrolling regression before its fix. The default runner output is `tests/artifacts/resolution-2.4.4`; choose a new `-OutputDirectory` when preserving an earlier run.
