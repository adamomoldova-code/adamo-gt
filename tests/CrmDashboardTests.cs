using System;
using System.Windows;
using System.Windows.Controls;
using PcPulse;

namespace PcPulseTests
{
    public static class CrmDashboardTests
    {
        public static void Run()
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            var button = (Button)window.FindName("CrmButton");
            Require(button != null && button.Visibility == Visibility.Collapsed && !button.IsEnabled, "CRM starts hidden until office access is confirmed");
            int crm = 0, report = 0;
            dashboard.SendToCrmRequested += delegate { crm++; };
            dashboard.SaveReportRequested += delegate { report++; };
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(crm == 0, "Unconfirmed access cannot dispatch CRM programmatically");
            dashboard.SetCrmOfficeAccess(true);
            Require(button.Visibility == Visibility.Visible && !button.IsEnabled, "Confirmed office access still waits for inventory");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(crm == 0, "Office access without inventory cannot dispatch CRM");
            dashboard.SetCrmOfficeAccess(false);
            var sample = new Snapshot { ComputerManufacturer = "HP", ComputerModel = "HP ZBook Firefly 14 inch G9 Mobile Workstation PC", CpuName = "Fixture CPU" };
            sample.Disks.Add(new DiskSnapshot { Index = 0, Name = "Fixture SSD" });
            sample.Gpus.Add(new GpuSnapshot { AdapterIndex = 0, Name = "Fixture GPU" });
            dashboard.Render(sample);
            Require(button.Visibility == Visibility.Collapsed && !button.IsEnabled, "Inventory does not expose CRM without office confirmation");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(crm == 0, "Inventory cannot bypass the office gate");
            dashboard.SetCrmOfficeAccess(true);
            Require(button.Visibility == Visibility.Visible && button.IsEnabled, "CRM becomes available with office confirmation and inventory");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(crm == 1 && report == 0, "CRM action does not dispatch another compartment's button");
            Require(dashboard.CreateCrmDraft().BuildDescription().Contains("Fixture CPU"), "CRM draft uses the displayed inventory");
            dashboard.SetChecksRunning(true);
            Require(button.Visibility == Visibility.Visible && !button.IsEnabled, "Running checks keep office CRM visible but disabled");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(crm == 1, "Running checks cannot be bypassed by programmatic clicks");
            dashboard.SetChecksRunning(false); Require(button.IsEnabled, "CRM restored after checks");
            dashboard.SetInventoryRefreshing(true);
            Require(!button.IsEnabled, "Inventory refresh disables CRM");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(crm == 1, "Inventory refresh cannot be bypassed by programmatic clicks");
            dashboard.SetInventoryRefreshing(false); Require(button.IsEnabled, "CRM restored after inventory refresh");
            dashboard.RenderBenchmark(new BenchmarkProgress { DiskIndex = 0, IsRunning = true });
            Require(!button.IsEnabled, "Running disk benchmark disables CRM even outside the check sequence");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(crm == 1, "Running disk benchmark cannot be bypassed by programmatic clicks");
            dashboard.RenderBenchmark(new BenchmarkProgress { DiskIndex = 0, IsRunning = false });
            Require(button.IsEnabled, "CRM restored after disk benchmark");
            dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress { AdapterIndex = 0, IsRunning = true });
            Require(!button.IsEnabled, "Running GPU benchmark disables CRM");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(crm == 1, "Running GPU benchmark cannot be bypassed by programmatic clicks");
            dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress { AdapterIndex = 0, IsRunning = false });
            Require(button.IsEnabled, "CRM restored after GPU benchmark");
            dashboard.SetCrmOfficeAccess(false);
            Require(button.Visibility == Visibility.Collapsed && !button.IsEnabled, "Revoked office access immediately hides CRM");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(crm == 1 && report == 0, "Revoked access cannot dispatch CRM or other actions");
            dashboard.Render(sample);
            dashboard.SetChecksRunning(true);
            dashboard.SetChecksRunning(false);
            Require(button.Visibility == Visibility.Collapsed && !button.IsEnabled, "Hardware refresh and completed checks do not restore revoked access");
            dashboard.SetCrmOfficeAccess(true);
            Require(button.Visibility == Visibility.Visible && button.IsEnabled, "A new office confirmation restores CRM");
            dashboard.SetSnapshotMode();
            Require(!button.IsEnabled, "Snapshot mode cannot submit a saved computer as a current product");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(crm == 1, "Snapshot mode cannot be bypassed by programmatic clicks");
            window.Close();
        }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
