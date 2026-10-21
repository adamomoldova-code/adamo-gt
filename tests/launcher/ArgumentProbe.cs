using System;
using System.IO;
using System.Linq;
using System.Text;

internal static class ArgumentProbe
{
    public static int Main(string[] args)
    {
        int relaunchedExitCode;
        if (PcPulse.SensorStartup.TryRelaunch(ref args, out relaunchedExitCode)) { return relaunchedExitCode; }
        if (args.Length == 0) { return 2; }
        string[] lines = new[] { Environment.CurrentDirectory }.Concat(args.Skip(1))
            .Concat(new[] { PcPulse.SensorStartup.GetElevationExecutable() })
            .Select(value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))).ToArray();
        File.WriteAllLines(args[0], lines, new UTF8Encoding(false));
        return 37;
    }
}
