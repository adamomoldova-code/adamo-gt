using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PcPulse;

namespace PcPulseTests
{
    internal static class HistoryStartupTests
    {
        internal static void Run()
        {
            var calls = new List<string>();
            StartupSequence.Run(
                delegate { calls.Add("display"); return Task.FromResult(true); },
                delegate { throw new InvalidOperationException("Cached startup must never invoke SSD testing"); },
                delegate { throw new InvalidOperationException("Cached startup must never invoke GPU testing"); },
                delegate { calls.Add("ports"); return Task.FromResult(true); },
                delegate(string message) { Require(!message.Contains("проверяем скорость") && !message.Contains("выполняем короткий GPU"), "No benchmark-in-progress message on cached startup"); },
                CancellationToken.None, false).GetAwaiter().GetResult();
            Require(calls.SequenceEqual(new[] { "display", "ports" }), "Cached startup presents information first and only refreshes specifications");

            var stopped = new CancellationTokenSource();
            stopped.Cancel();
            bool cancelled = false;
            try
            {
                StartupSequence.Run(delegate { throw new InvalidOperationException("Cancelled startup must not display"); },
                    delegate { throw new InvalidOperationException("Cancelled startup must not test"); }, null,
                    delegate { throw new InvalidOperationException("Cancelled startup must not look up ports"); },
                    delegate { }, stopped.Token, false).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { cancelled = true; }
            finally { stopped.Dispose(); }
            Require(cancelled, "Cached startup retains normal cancellation behavior");
        }

        private static void Require(bool condition, string message)
        { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
