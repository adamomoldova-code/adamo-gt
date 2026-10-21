using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;

namespace PcPulse
{
    // Request the normal Windows UAC flow once, before opening sensor handles.
    // Cancelling UAC preserves the usable, non-administrator application.
    internal static class SensorStartup
    {
        internal const string LauncherArgumentPrefix = "--adamo-launcher=";
        private static string originalLauncher;

        internal static bool TryRelaunch(ref string[] arguments, out int exitCode)
        {
            return TryRelaunch(ref arguments, out exitCode, null);
        }

        internal static bool TryRelaunch(ref string[] arguments, out int exitCode, string exactExecutable)
        {
            exitCode = 0;
            string[] original = arguments ?? new string[0];
            // The outer launcher supplies its own exact executable and must never
            // accept routing supplied by an inherited environment or command line.
            originalLauncher = exactExecutable == null ? LauncherArgument(original) : null;
            arguments = ApplicationArguments(original);
            bool administrator = false;
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                { administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
                if (!ShouldElevate(original, administrator)) { return false; }
                string[] childArguments = arguments.Concat(new[] { "--no-elevation" }).ToArray();
                using (Process child = Process.Start(new ProcessStartInfo
                {
                    FileName = exactExecutable ?? GetElevationExecutable(),
                    Arguments = JoinArguments(childArguments),
                    WorkingDirectory = Environment.CurrentDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                }))
                {
                    if (child == null) { return false; }
                    // Command-line probes need the real child result. The normal window
                    // owns its lifetime independently, including single-file cache reuse.
                    if (!IsInteractiveStartup(arguments)) { child.WaitForExit(); exitCode = child.ExitCode; }
                    return true;
                }
            }
            catch (Win32Exception) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        internal static string GetElevationExecutable()
        {
            return ResolveElevationExecutable(originalLauncher, Assembly.GetExecutingAssembly().Location);
        }

        internal static string LauncherArgument(string[] arguments)
        {
            string[] routes = (arguments ?? new string[0]).Where(arg => arg != null && arg.StartsWith(LauncherArgumentPrefix, StringComparison.Ordinal)).ToArray();
            return routes.Length == 1 ? routes[0].Substring(LauncherArgumentPrefix.Length) : null;
        }

        internal static string ResolveElevationExecutable(string launcher, string fallback)
        {
            try
            {
                string root = launcher == null ? "" : Path.GetPathRoot(launcher);
                if (!string.IsNullOrWhiteSpace(launcher) && Path.IsPathRooted(launcher) && root.Length >= 3
                    && string.Equals(Path.GetExtension(launcher), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(launcher))
                {
                    return Path.GetFullPath(launcher);
                }
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            return fallback;
        }

        internal static bool ShouldElevate(string[] arguments, bool administrator)
        {
            string[] args = arguments ?? new string[0];
            if (administrator || args.Contains("--no-elevation")) { return false; }
            return args.Contains("--elevate") || IsInteractiveStartup(args);
        }

        internal static bool IsInteractiveStartup(string[] arguments)
        {
            return ApplicationArguments(arguments).Length == 0;
        }

        internal static string[] ApplicationArguments(string[] arguments)
        {
            // Discard the retired lab option from old shortcuts. It enables no
            // provider and must not suppress normal GUI elevation or shift CLI arguments.
            return (arguments ?? new string[0]).Where(arg => arg != "--elevate" && arg != "--no-elevation"
                && arg != "--adamo-thermal"
                && (arg == null || !arg.StartsWith(LauncherArgumentPrefix, StringComparison.Ordinal))).ToArray();
        }

        internal static string JoinArguments(string[] arguments)
        {
            return String.Join(" ", arguments.Select(QuoteArgument).ToArray());
        }

        internal static string QuoteArgument(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value ?? "")
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); }
                else { result.Append('\\', slashes); result.Append(character); }
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }
    }
}
