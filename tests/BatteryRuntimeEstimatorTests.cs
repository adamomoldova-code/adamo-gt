using System;
using PcPulse;

namespace PcPulseTests
{
    public static class BatteryRuntimeEstimatorTests
    {
        public static void Run()
        {
            CurrentFullCapacityDeterminesTheAssumedRange();
            MissingOrAbsentBatteryNeverInventsCapacity();
            ConfiguredPowerBoundsAreExplicitAndValidated();
        }

        private static void CurrentFullCapacityDeterminesTheAssumedRange()
        {
            BatteryRuntimeEstimate result = BatteryRuntimeEstimator.Estimate(31.937, true);
            Require(Close(result.MinimumHours, 31.937 / 15d), "shorter estimate uses high assumed drain");
            Require(Close(result.MaximumHours, 31.937 / 7d), "longer estimate uses low assumed drain");
            Require(result.AssumedMinimumWatts == 7 && result.AssumedMaximumWatts == 15, "assumptions available for display");
            Require(result.Status.Contains("7–15 Вт") && result.Status.Contains("Потребление не измерялось"), "model estimate cannot look like a measured discharge test");
            BatteryRuntimeEstimate degraded = BatteryRuntimeEstimator.Estimate(15.9685, true);
            Require(Close(degraded.MinimumHours, result.MinimumHours.Value / 2) && Close(degraded.MaximumHours, result.MaximumHours.Value / 2), "degraded full capacity halves runtime without a design-capacity fallback");
        }

        private static void MissingOrAbsentBatteryNeverInventsCapacity()
        {
            foreach (double? invalid in new double?[] { null, 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                BatteryRuntimeEstimate result = BatteryRuntimeEstimator.Estimate(invalid, true);
                Require(!result.MinimumHours.HasValue && !result.MaximumHours.HasValue, "unknown capacity stays unavailable");
            }
            foreach (bool? presence in new bool?[] { false, null })
            {
                BatteryRuntimeEstimate result = BatteryRuntimeEstimator.Estimate(50, presence);
                Require(!result.MinimumHours.HasValue && !result.MaximumHours.HasValue, "absent or uncertain battery cannot use stale capacity");
            }
        }

        private static void ConfiguredPowerBoundsAreExplicitAndValidated()
        {
            BatteryRuntimeEstimate result = BatteryRuntimeEstimator.Estimate(40, true, 10, 20);
            Require(result.MinimumHours == 2 && result.MaximumHours == 4 && result.Status.Contains("10–20 Вт"), "custom assumed workload retains its actual bounds");
            result = BatteryRuntimeEstimator.Estimate(40, true, 10, 10);
            Require(result.MinimumHours == 4 && result.MaximumHours == 4, "equal assumptions do not invent variability");
            foreach (double[] bounds in new[]
            {
                new[] { 0d, 15d }, new[] { 7d, 0d }, new[] { 15d, 7d }, new[] { -1d, 15d },
                new[] { double.NaN, 15d }, new[] { 7d, double.PositiveInfinity }, new[] { double.Epsilon, 15d }
            })
            {
                result = BatteryRuntimeEstimator.Estimate(40, true, bounds[0], bounds[1]);
                Require(!result.MinimumHours.HasValue && !result.MaximumHours.HasValue, "invalid or overflowing estimates are unavailable");
            }
        }

        private static bool Close(double? actual, double expected)
        {
            return actual.HasValue && Math.Abs(actual.Value - expected) < 0.0000001;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}
