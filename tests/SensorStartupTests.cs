using System;
using System.Linq;
using System.IO;
using System.Reflection;
using PcPulse;

namespace PcPulseTests
{
    internal static class SensorStartupTests
    {
        internal static void Run()
        {
            Require(SensorStartup.ShouldElevate(new string[0], false), "Every normal startup requests administrator access, independently of installed drivers");
            Require(!SensorStartup.ShouldElevate(new string[0], true), "Administrator startup does not loop");
            Require(!SensorStartup.ShouldElevate(new[] { "--render", "a.png" }, false), "Automatic headless checks remain non-interactive");
            Require(SensorStartup.ShouldElevate(new[] { "--probe", "a.json", "--elevate" }, false), "Explicit probe elevation is supported");
            Require(!SensorStartup.ShouldElevate(new[] { "--no-elevation", "--elevate" }, false), "Suppression prevents a UAC loop");
            Require(SensorStartup.ApplicationArguments(new[] { "--probe", "C:\\cale cu spații\\măsurare.json", "--no-elevation" }).SequenceEqual(new[] { "--probe", "C:\\cale cu spații\\măsurare.json" }), "Control arguments are removed without changing the output path");
            string route = SensorStartup.LauncherArgumentPrefix + "C:\\cale cu spații\\ADAMO-GT.exe";
            Require(SensorStartup.LauncherArgument(new[] { route }) == "C:\\cale cu spații\\ADAMO-GT.exe", "Internal routing preserves the full launcher path");
            Require(SensorStartup.LauncherArgument(new[] { route, route }) == null, "Ambiguous internal routing is ignored");
            Require(SensorStartup.ApplicationArguments(new[] { "--probe", "result.json", route, "--no-elevation" }).SequenceEqual(new[] { "--probe", "result.json" }), "Internal routing never reaches application command parsing");
            Require(SensorStartup.ShouldElevate(new[] { route }, false), "An internal route cannot suppress normal startup elevation");
            Require(!SensorStartup.ShouldElevate(new[] { route, "--no-elevation" }, false), "A routed child never loops through UAC");
            const string retiredOption = "--adamo-thermal";
            Require(SensorStartup.ShouldElevate(new[] { retiredOption }, false), "A stale lab-only shortcut still requests normal GUI administrator access");
            Require(!SensorStartup.ShouldElevate(new[] { retiredOption }, true), "A stale shortcut cannot relaunch an administrator process");
            Require(SensorStartup.IsInteractiveStartup(new[] { route, retiredOption }), "The discarded lab option is not a headless command");
            Require(!SensorStartup.ShouldElevate(new[] { retiredOption, "--probe", "result.json" }, false), "A retired option cannot elevate an otherwise headless probe");
            Require(SensorStartup.ShouldElevate(new[] { retiredOption, "--probe", "result.json", "--elevate" }, false), "Explicit probe elevation still works alongside a retired option");
            Require(!SensorStartup.ShouldElevate(new[] { retiredOption, "--no-elevation" }, false), "A stale option cannot override child elevation suppression");
            Require(!SensorStartup.IsInteractiveStartup(new[] { retiredOption, "--probe", "result.json" }), "A headless probe still waits for its child result");
            string[] forwarded = SensorStartup.ApplicationArguments(new[] { route, retiredOption, "--probe", "result.json", "--no-elevation" });
            Require(forwarded.SequenceEqual(new[] { "--probe", "result.json" }), "Elevation discards the retired option before forwarding positional probe arguments");
            Require(SensorStartup.ApplicationArguments(new[] { retiredOption, retiredOption }).Length == 0, "Repeated retired options have no application effect");
            string[] trailing = { "--render", "C:\\cale cu spații\\--adamo-thermal.png", retiredOption, "1200", "760", retiredOption };
            Require(SensorStartup.ApplicationArguments(trailing).SequenceEqual(new[] { "--render", "C:\\cale cu spații\\--adamo-thermal.png", "1200", "760" }), "Trailing/repeated exact retired options do not disturb paths or dimensions");
            string[] notAnOption = { "--adamo-thermal=true", "--ADAMO-THERMAL" };
            Require(SensorStartup.ApplicationArguments(notAnOption).SequenceEqual(notAnOption), "Only the exact retired option is discarded");
            Require(SensorStartup.ApplicationArguments(null).Length == 0, "Absent arguments remain a normal GUI launch");
            Require(SensorStartup.QuoteArgument("") == "\"\"", "Empty argument preserved");
            Require(SensorStartup.QuoteArgument("a\"b") == "\"a\\\"b\"", "Embedded quotes escaped");
            Require(SensorStartup.QuoteArgument("C:\\folder\\") == "\"C:\\folder\\\\\"", "Trailing slash before closing quote doubled");
            string executable = Assembly.GetExecutingAssembly().Location;
            const string fallback = "fallback-runtime.exe";
            Require(SensorStartup.ResolveElevationExecutable(executable, fallback) == Path.GetFullPath(executable), "Manual elevation uses the original existing launcher instead of its cached child");
            foreach (string invalid in new[] { null, "", Path.GetFileName(executable), "C:relative.exe", Path.ChangeExtension(executable, ".txt"), Path.Combine(Path.GetDirectoryName(executable), "missing-" + Guid.NewGuid().ToString("N") + ".exe"), "a\0b" })
            {
                Require(SensorStartup.ResolveElevationExecutable(invalid, fallback) == fallback, "Invalid or relative launcher routing falls back to this executable");
            }
            string[] outerArguments = { "--no-elevation", route };
            int ignoredExitCode;
            Require(!SensorStartup.TryRelaunch(ref outerArguments, out ignoredExitCode, executable), "An outer launcher with suppression does not relaunch");
            Require(outerArguments.Length == 0 && SensorStartup.GetElevationExecutable() == executable, "The outer launcher ignores supplied routing and removes internal arguments");
            string[] staleOuter = { "--no-elevation", route, retiredOption };
            Require(!SensorStartup.TryRelaunch(ref staleOuter, out ignoredExitCode, executable)
                && staleOuter.Length == 0, "Outer launcher discards the retired option without launching another process");
            string[] staleProbe = { retiredOption, "--probe", "C:\\cale cu spații\\măsurare.json", "--no-elevation" };
            Require(!SensorStartup.TryRelaunch(ref staleProbe, out ignoredExitCode, executable)
                && staleProbe.SequenceEqual(new[] { "--probe", "C:\\cale cu spații\\măsurare.json" }), "Relaunch processing leaves a stale shortcut probe usable without the removed provider");
        }

        private static void Require(bool condition, string message)
        { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
