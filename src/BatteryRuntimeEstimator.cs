using System;
using System.Globalization;

namespace PcPulse
{
    internal sealed class BatteryRuntimeEstimate
    {
        internal double? MinimumHours;
        internal double? MaximumHours;
        internal double AssumedMinimumWatts;
        internal double AssumedMaximumWatts;
        internal string Status = "";
    }

    internal static class BatteryRuntimeEstimator
    {
        internal const double DefaultMinimumPowerWatts = 7;
        internal const double DefaultMaximumPowerWatts = 15;

        internal static BatteryRuntimeEstimate Estimate(double? fullChargeWh, bool? batteryPresent)
        {
            return Estimate(fullChargeWh, batteryPresent, DefaultMinimumPowerWatts, DefaultMaximumPowerWatts);
        }

        internal static BatteryRuntimeEstimate Estimate(double? fullChargeWh, bool? batteryPresent,
            double minimumPowerWatts, double maximumPowerWatts)
        {
            var result = new BatteryRuntimeEstimate
            {
                AssumedMinimumWatts = minimumPowerWatts,
                AssumedMaximumWatts = maximumPowerWatts
            };
            if (batteryPresent == false)
            {
                result.Status = "Батарея не установлена";
                return result;
            }
            if (batteryPresent != true || !PositiveFinite(fullChargeWh))
            {
                result.Status = "Для оценки нужна текущая полная ёмкость батареи в Вт·ч";
                return result;
            }
            if (!PositiveFinite(minimumPowerWatts) || !PositiveFinite(maximumPowerWatts) || minimumPowerWatts > maximumPowerWatts)
            {
                result.Status = "Диапазон предполагаемого потребления не задан корректно";
                return result;
            }
            double minimumHours = fullChargeWh.Value / maximumPowerWatts;
            double maximumHours = fullChargeWh.Value / minimumPowerWatts;
            if (!PositiveFinite(minimumHours) || !PositiveFinite(maximumHours))
            {
                result.Status = "Недостаточно достоверных данных для оценки времени работы";
                return result;
            }
            result.MinimumHours = minimumHours;
            result.MaximumHours = maximumHours;
            result.Status = "Оценка от полного заряда для лёгкой работы при условном потреблении "
                + minimumPowerWatts.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + "–"
                + maximumPowerWatts.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"))
                + " Вт, по текущей полной ёмкости батареи. Потребление не измерялось; реальное время зависит от яркости, нагрузки и оборудования.";
            return result;
        }

        private static bool PositiveFinite(double? value)
        {
            return value.HasValue && value > 0 && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
        }
    }
}
