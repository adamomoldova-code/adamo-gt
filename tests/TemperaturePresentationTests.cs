using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PcPulse;

namespace PcPulseTests
{
    public static class TemperaturePresentationTests
    {
        public static void Run()
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            int helpRequests = 0;
            dashboard.SensorsHelpRequested += delegate { helpRequests++; };
            var data = new Snapshot
            {
                CpuTemperatureStatus = "Нужны права администратора для доступа к температуре CPU.",
                SensorStatus = "Тест отображения датчиков"
            };
            data.Gpus.Add(new GpuSnapshot
            {
                AdapterIndex = 0, Name = "Intel Iris Xe Graphics", IsIntegrated = true,
                TemperatureStatus = "Драйвер Intel не предоставляет отдельную температуру GPU."
            });
            dashboard.Render(data);
            var cpu = (TextBlock)window.FindName("CpuTemperature");
            var gpu = (TextBlock)window.FindName("GpuTemperature");
            Require(cpu.Text == "Нужны права", "CPU shows the actionable reason inline");
            Require(cpu.Focusable && cpu.Cursor == Cursors.Hand && cpu.TextDecorations != null && cpu.TextDecorations.Count > 0,
                "The actionable CPU reason is a focusable, visibly underlined link");
            Require(Tooltip(cpu).Contains(data.CpuTemperatureStatus), "The full CPU reason stays available");
            Click(cpu);
            Require(helpRequests == 1, "Clicking the CPU reason opens the existing sensor settings flow");

            Require(gpu.Text == "Intel не сообщает", "Unsupported Intel GPU temperature is explained separately");
            Require(!gpu.Focusable && gpu.Cursor != Cursors.Hand, "Unsupported GPU temperature does not pretend that permissions will fix it");
            Require(Tooltip(gpu).Contains(data.Gpus[0].TemperatureStatus), "The GPU tooltip preserves the actual provider reason");
            Click(gpu);
            Require(helpRequests == 1, "An unsupported GPU reading does not trigger setup");

            data.CpuTemperatureStatus = "Требуется драйвер PawnIO для чтения температуры CPU.";
            dashboard.Render(data);
            Require(cpu.Text == "Датчик недоступен", "Missing CPU access does not imply PawnIO is mandatory for the application");
            Click(cpu);
            Require(helpRequests == 2, "The missing-driver link uses the existing setup flow");

            data.CpuTemperatureStatus = "Нет доступного датчика CPU через Windows. Дополнительный драйвер PawnIO может расширить доступ; установка необязательна.";
            dashboard.Render(data);
            Require(cpu.Text == "Датчик недоступен" && cpu.Focusable && Tooltip(cpu).Contains("необязательна"),
                "Driverless CPU unavailability keeps an honest optional setup explanation");

            data.CpuTemperature = 61;
            data.CpuTemperatureStatus = "Температура CPU: CPU Package";
            data.Gpus[0].Temperature = 59;
            data.Gpus[0].TemperatureStatus = "Температура GPU: GPU Core";
            dashboard.Render(data);
            Require(cpu.Text == "61 °C" && gpu.Text == "59 °C", "A later numeric reading replaces the missing-sensor reason");
            Require(!cpu.Focusable && cpu.Cursor != Cursors.Hand && (cpu.TextDecorations == null || cpu.TextDecorations.Count == 0),
                "A recovered CPU reading clears the obsolete action styling");
            Click(cpu);
            Require(helpRequests == 2, "A recovered reading cannot trigger an obsolete setup action");

            data.CpuTemperature = null;
            data.CpuTemperatureStatus = "Датчики подключаются…";
            dashboard.Render(data);
            Require(cpu.Text == "Подключение…" && !cpu.Focusable, "Sensor startup is a pending state, not a failure");
            data.CpuTemperatureStatus = "";
            dashboard.Render(data);
            Require(cpu.Text == "Нет данных" && !cpu.Focusable, "Legacy snapshots without a reason do not invent a hardware diagnosis");
            foreach (string name in new[] { "CpuCache", "RamMaximum", "DiskHealth", "PowerOnHours", "DiskTemperature", "StatusText" })
            {
                Require(((TextBlock)window.FindName(name)).FontSize >= 14, name + " keeps the stronger minimum type size even when data is absent");
            }
        }

        private static string Tooltip(TextBlock text)
        {
            var tooltip = text.ToolTip as TextBlock;
            return tooltip == null ? Convert.ToString(text.ToolTip) : tooltip.Text;
        }

        private static void Click(TextBlock text)
        {
            text.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            });
        }

        private static void Require(bool value, string message)
        {
            if (!value) { throw new InvalidOperationException(message); }
        }
    }
}
