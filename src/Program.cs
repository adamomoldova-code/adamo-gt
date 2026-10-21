using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: AssemblyTitle("ADAMO GT")]
[assembly: AssemblyProduct("ADAMO GT")]
[assembly: AssemblyDescription("Показатели компьютера на одном экране")]
[assembly: AssemblyVersion("2.7.1.0")]
[assembly: AssemblyFileVersion("2.7.1.0")]

namespace PcPulse
{
    public static class Program
    {
        private static readonly AutoResetEvent Refresh = new AutoResetEvent(false);
        private static int inventoryRefreshRequested;
        private static bool inventoryRefreshing; // UI dispatcher owns refresh/test admission.
        private static volatile bool stopping;
        private static volatile Snapshot latest;
        private static Task monitorTask;
        private static Task benchmarkTask;
        private static Task gpuTask;
        private static Task portsTask;
        private static Task startupTask;
        private static readonly CancellationTokenSource StartupCancellation = new CancellationTokenSource();
        private static readonly ManualResetEventSlim InitialDisplayReady = new ManualResetEventSlim(false);
        private static bool initialInformationShown;
        private static int benchmarkGeneration;
        private static int portsGeneration;
        private static int stopGeneration;
        private static CancellationTokenSource benchmarkCancellation;
        private static CancellationTokenSource gpuCancellation;
        private static CancellationTokenSource portsCancellation;
        private static bool closePending;
        private static bool shutdownReady;
        private static volatile bool checksRunning;
        private static int checkErrors;
        private static BenchmarkHistory history;
        private static StressTestsWindow stressWindow;
        private static StressRunResult lastStressResult;
        private static bool closeAfterStress;

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                int relaunchedExitCode;
                if (SensorStartup.TryRelaunch(ref args, out relaunchedExitCode)) { return relaunchedExitCode; }
                history = BenchmarkHistory.OpenDefault();
                if (args.Length >= 2 && args[0] == "--probe")
                {
                    WriteSnapshot(args[1], Capture());
                    return 0;
                }
                if (args.Length >= 2 && args[0] == "--ports-probe")
                {
                    var identity = Capture();
                    var ports = new PortLookup().Lookup(identity, CancellationToken.None, true);
                    File.WriteAllText(Path.GetFullPath(args[1]), new JavaScriptSerializer().Serialize(ports));
                    return 0;
                }
                var app = new Application();
                var window = LoadWindow();
                var dashboard = new Dashboard(window);
                if (args.Length >= 3 && args[0] == "--render-saved")
                {
                    RenderSaved(window, dashboard, args);
                    return 0;
                }
                if (args.Length >= 2 && (args[0] == "--render" || args[0] == "--render-history" || args[0] == "--verify" || args[0] == "--gpu-verify"))
                {
                    if (args[0] == "--render") { dashboard.SetSnapshotMode(); }
                    var snapshot = Capture();
                    dashboard.Render(snapshot);
                    var ports = PortLookup.CachedOnly(snapshot);
                    dashboard.RenderPorts(ports);
                    dashboard.RenderStartupStatus(args[0] == "--render" ? "Характеристики компьютера · тесты не запускались" : "Характеристики готовы · результаты появятся после тестов");
                    var results = new List<BenchmarkProgress>();
                    var gpuResults = new List<GpuBenchmarkProgress>();
                    if (args[0] == "--render-history")
                    {
                        BenchmarkHistoryResult previous = history.Load(snapshot);
                        dashboard.SetAutomaticTestsEnabled(false);
                        RenderHistory(dashboard, previous);
                        results.AddRange(previous.Disks);
                        gpuResults.AddRange(previous.Gpus);
                        dashboard.RenderStartupStatus(HistoryStatus(previous));
                    }
                    if (args[0] == "--verify" || args[0] == "--gpu-verify") { history.MarkAttempt(snapshot); }
                    if (args[0] == "--verify")
                    {
                        foreach (var disk in snapshot.Disks)
                        {
                            DiskBenchmark.Run(disk, delegate(BenchmarkProgress progress)
                            {
                                if (!progress.IsRunning)
                                {
                                    PersistDiskResult(snapshot, disk, progress);
                                    results.Add(progress);
                                }
                                dashboard.RenderBenchmark(progress);
                            }, CancellationToken.None);
                        }
                        dashboard.RenderStartupStatus("Тесты накопителей завершены");
                    }
                    if (args[0] == "--verify" || args[0] == "--gpu-verify")
                    {
                        using (var monitor = new HardwareMonitor())
                        {
                            DateTime nextReading = DateTime.MinValue;
                            Snapshot sampled = snapshot;
                            foreach (var gpu in snapshot.Gpus)
                            {
                                double? peakTemperature = null;
                                GpuBenchmark.Run(gpu, delegate(GpuBenchmarkProgress progress)
                                {
                                    if (DateTime.UtcNow >= nextReading)
                                    {
                                        sampled = monitor.Read();
                                        nextReading = DateTime.UtcNow.AddSeconds(2);
                                    }
                                    peakTemperature = PeakGpuTemperature(sampled, gpu, peakTemperature);
                                    progress.MaxTemperature = peakTemperature;
                                    if (!progress.IsRunning)
                                    {
                                        PersistGpuResult(snapshot, gpu, progress);
                                        gpuResults.Add(progress);
                                    }
                                    dashboard.RenderGpuBenchmark(progress);
                                }, CancellationToken.None);
                            }
                            snapshot = monitor.Read();
                            dashboard.Render(snapshot);
                        }
                        dashboard.RenderStartupStatus("Проверки закончены · результаты в блоках SSD и GPU");
                    }
                    double width = args.Length > 2 ? double.Parse(args[2]) : 1200;
                    double height = args.Length > 3 ? double.Parse(args[3]) : 760;
                    Render(window, args[1], width, height);
                    WriteSnapshot(Path.ChangeExtension(args[1], ".json"), snapshot);
                    File.WriteAllText(Path.ChangeExtension(args[1], ".benchmarks.json"), new JavaScriptSerializer().Serialize(results));
                    File.WriteAllText(Path.ChangeExtension(args[1], ".gpu.json"), new JavaScriptSerializer().Serialize(gpuResults));
                    File.WriteAllText(Path.ChangeExtension(args[1], ".ports.json"), new JavaScriptSerializer().Serialize(ports));
                    bool failed = results.Any(result => !String.IsNullOrWhiteSpace(result.Error) || !result.ReadBytesPerSecond.HasValue || !result.WriteBytesPerSecond.HasValue)
                        || gpuResults.Any(result => !result.Completed || !String.IsNullOrWhiteSpace(result.Error));
                    if (args[0] == "--verify" && snapshot.Disks.Count == 0) { failed = true; }
                    if ((args[0] == "--verify" || args[0] == "--gpu-verify") && snapshot.Gpus.Count == 0) { failed = true; }
                    return failed ? 3 : 0;
                }
                app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                {
                    Log(e.Exception);
                    MessageBox.Show(window, "Не удалось обновить окно. Перезапустите ADAMO GT.\n" + e.Exception.Message, "ADAMO GT", MessageBoxButton.OK, MessageBoxImage.Warning);
                    e.Handled = true;
                };
                dashboard.RefreshRequested += delegate
                {
                    Interlocked.Exchange(ref inventoryRefreshRequested, 1);
                    dashboard.RenderStartupStatus(checksRunning ? "Обновление оборудования выполнится после проверок…" : "Обновляем состав оборудования…");
                    Refresh.Set();
                };
                dashboard.SensorsHelpRequested += delegate { ShowSensorHelp(window); };
                dashboard.StressTestsRequested += delegate
                {
                    if (stopping || checksRunning || inventoryRefreshing || !initialInformationShown || latest == null || stressWindow != null) { return; }
                    checksRunning = true; dashboard.SetChecksRunning(true);
                    try
                    {
                        stressWindow = new StressTestsWindow(latest, delegate { return latest; }, lastStressResult, delegate(StressRunResult result)
                        { lastStressResult = result; dashboard.SetStressResult(result); });
                        stressWindow.Owner = window;
                        DialogViewport.Fit(stressWindow, DialogViewport.WorkArea(window));
                        stressWindow.ShowDialog();
                    }
                    finally
                    {
                        stressWindow = null; checksRunning = false; dashboard.SetChecksRunning(false);
                        if (closeAfterStress) { window.Close(); }
                    }
                };
                dashboard.SaveReportRequested += delegate { SaveReport(window, dashboard); };
                var crmOfficeAccess = new CrmOfficeAccess();
                var crmAccessCancellation = new CancellationTokenSource();
                CancellationToken crmAccessToken = crmAccessCancellation.Token;
                var crmAccessTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromSeconds(60) };
                Action applyCrmAccess = delegate
                {
                    // Read the current decision on the dispatcher: an older queued
                    // success must not undo a later network-change revocation.
                    dashboard.SetCrmOfficeAccess(!stopping && !crmAccessToken.IsCancellationRequested && crmOfficeAccess.IsAllowed);
                };
                crmOfficeAccess.AccessChanged += delegate
                {
                    if (window.Dispatcher.HasShutdownStarted) { return; }
                    window.Dispatcher.BeginInvoke(applyCrmAccess);
                };
                Action refreshCrmAccess = async delegate
                {
                    if (stopping || crmAccessToken.IsCancellationRequested) { return; }
                    await crmOfficeAccess.RefreshAsync(crmAccessToken);
                };
                System.Net.NetworkInformation.NetworkAddressChangedEventHandler crmNetworkChanged = delegate
                {
                    crmOfficeAccess.Invalidate();
                    refreshCrmAccess();
                };
                System.Net.NetworkInformation.NetworkAvailabilityChangedEventHandler crmNetworkAvailabilityChanged = delegate
                {
                    crmOfficeAccess.Invalidate();
                    refreshCrmAccess();
                };
                crmAccessTimer.Tick += delegate { refreshCrmAccess(); };
                window.Activated += delegate { refreshCrmAccess(); };
                window.Loaded += delegate
                {
                    System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += crmNetworkChanged;
                    System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += crmNetworkAvailabilityChanged;
                    crmAccessTimer.Start();
                    refreshCrmAccess();
                };
                window.Closed += delegate
                {
                    crmAccessTimer.Stop();
                    System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= crmNetworkChanged;
                    System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= crmNetworkAvailabilityChanged;
                    crmAccessCancellation.Cancel();
                    crmOfficeAccess.Dispose();
                    crmAccessCancellation.Dispose();
                };
                bool openingCrm = false;
                dashboard.SendToCrmRequested += async delegate
                {
                    if (openingCrm || stopping || checksRunning || inventoryRefreshing || !initialInformationShown || latest == null) { return; }
                    openingCrm = true;
                    try
                    {
                        await crmOfficeAccess.RefreshAsync(crmAccessToken);
                        if (stopping || crmAccessToken.IsCancellationRequested || !crmOfficeAccess.IsAllowed
                            || checksRunning || inventoryRefreshing || !initialInformationShown || latest == null) { return; }
                        // Capture the displayed page and its detached hardware data in
                        // one dispatcher turn, before any password dialog is shown.
                        CrmQuickSubmission prepared;
                        try { prepared = dashboard.CreateCrmQuickSubmission(CrmDashboardCapture.Capture(window)); }
                        catch (Exception)
                        {
                            MessageBox.Show(window, "Nu s-a putut pregăti captura. Deschide prima pagină și încearcă din nou.",
                                "CRM", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                        using (var crmClient = new CrmQuickIntakeClient())
                        {
                            var dialog = new CrmQuickIntakeWindow(prepared, crmClient) { Owner = window };
                            DialogViewport.Fit(dialog, DialogViewport.WorkArea(window));
                            dialog.ShowDialog();
                        }
                    }
                    catch (CrmException)
                    {
                        if (!stopping)
                            MessageBox.Show(window, "Nu se poate deschide trimiterea CRM. Închide celelalte ferestre CRM sau verifică trimiterea precedentă.",
                                "CRM", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    finally { openingCrm = false; }
                };
                string peripheralHistoryError;
                var peripheralResults = history.LoadPeripherals(out peripheralHistoryError);
                dashboard.SetPeripheralResults(peripheralResults);
                dashboard.PeripheralTestsRequested += delegate
                {
                    var tests = new PeripheralTestsWindow(peripheralResults, delegate(PeripheralTestResult result)
                    {
                        bool persisted = history.RecordPeripheral(result);
                        peripheralResults.RemoveAll(item => item.Kind == result.Kind);
                        peripheralResults.Add(result.Copy());
                        dashboard.SetPeripheralResults(peripheralResults);
                        return persisted;
                    }, historyError: peripheralHistoryError);
                    tests.Owner = window;
                    DialogViewport.Fit(tests, DialogViewport.WorkArea(window));
                    tests.ShowDialog();
                };
                dashboard.PortsRefreshRequested += delegate
                {
                    if (ConfirmOnlinePortLookup(window)) { StartPortLookup(window, dashboard, true); }
                };
                dashboard.PortsSearchRequested += delegate
                {
                    if (ConfirmOnlinePortLookup(window)) { OpenUrl(PortLookup.SearchUrl(latest)); }
                };
                dashboard.PortsSourceRequested += delegate(string url)
                {
                    if (latest != null && PortLookup.IsAllowedSource(url, latest.ComputerManufacturer)) { OpenUrl(url); }
                };
                dashboard.RunTestRequested += async delegate { await RunManualTest(window, dashboard, false); };
                dashboard.RunGpuTestRequested += async delegate { await RunManualTest(window, dashboard, true); };
                dashboard.StopTestRequested += delegate { StopChecks(); };
                window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
                {
                    if (stressWindow != null && stressWindow.IsRunning)
                    {
                        e.Cancel = true; closeAfterStress = true; stressWindow.Close(); return;
                    }
                    stopping = true;
                    Refresh.Set();
                    StopChecks();
                    InitialDisplayReady.Set();
                    if (benchmarkCancellation != null) { benchmarkCancellation.Cancel(); }
                    if (portsCancellation != null) { portsCancellation.Cancel(); }
                    if (shutdownReady) { return; }
                    var tasks = new List<Task>();
                    if (monitorTask != null) { tasks.Add(monitorTask); }
                    if (benchmarkTask != null) { tasks.Add(benchmarkTask); }
                    if (gpuTask != null) { tasks.Add(gpuTask); }
                    if (portsTask != null) { tasks.Add(portsTask); }
                    if (startupTask != null) { tasks.Add(startupTask); }
                    if (tasks.Exists(task => !task.IsCompleted))
                    {
                        e.Cancel = true;
                        if (!closePending)
                        {
                            closePending = true;
                            dashboard.RenderStartupStatus("Завершаем проверки и закрываем программу…");
                            ShutdownCoordinator.WaitAsync(tasks, TimeSpan.FromSeconds(8)).ContinueWith(delegate
                            {
                                window.Dispatcher.BeginInvoke(new Action(delegate { shutdownReady = true; window.Close(); }));
                            });
                        }
                    }
                };
                window.Closed += delegate { stopping = true; Refresh.Set(); };
                window.Loaded += delegate
                {
                    monitorTask = Task.Run(delegate
                    {
                        try
                        {
                            using (var monitor = new HardwareMonitor())
                            {
                                var initial = monitor.ReadInitialSnapshot();
                                if (stopping) { return; }
                                BenchmarkHistoryResult previous = history.Load(initial);
                                bool automaticTests = !previous.HasPreviousAttempt && history.TryBeginAutomaticAttempt(initial);
                                if (!automaticTests) { previous = history.Load(initial); }
                                window.Dispatcher.Invoke(new Action(delegate
                                {
                                    if (stopping) { return; }
                                    latest = initial;
                                    dashboard.Render(initial);
                                    dashboard.RenderPorts(PortLookup.CachedOnly(initial));
                                    dashboard.SetAutomaticTestsEnabled(automaticTests);
                                    if (!automaticTests) { RenderHistory(dashboard, previous); }
                                    initialInformationShown = true;
                                    dashboard.RenderStartupStatus(automaticTests ? "Характеристики готовы · запускаем первую проверку после показа окна…" : HistoryStatus(previous));
                                    startupTask = RunStartupChecks(window, dashboard, StartupCancellation.Token, automaticTests, previous);
                                }));
                                InitialDisplayReady.Wait();
                                while (!stopping)
                                {
                                    bool rebuild = false;
                                    if (Volatile.Read(ref inventoryRefreshRequested) != 0)
                                    {
                                        window.Dispatcher.Invoke(new Action(delegate
                                        {
                                            if (stopping || checksRunning || inventoryRefreshing
                                                || (benchmarkTask != null && !benchmarkTask.IsCompleted)
                                                || (gpuTask != null && !gpuTask.IsCompleted)) { return; }
                                            rebuild = Interlocked.Exchange(ref inventoryRefreshRequested, 0) != 0;
                                            if (rebuild)
                                            {
                                                inventoryRefreshing = true;
                                                dashboard.SetInventoryRefreshing(true);
                                            }
                                        }));
                                    }
                                    if (rebuild) { monitor.RefreshInventory(); }
                                    var snapshot = monitor.Read();
                                    if (stopping) { break; }
                                    window.Dispatcher.Invoke(new Action(delegate
                                    {
                                        if (stopping) { return; }
                                        latest = snapshot;
                                        dashboard.Render(snapshot);
                                        if (rebuild)
                                        {
                                            inventoryRefreshing = false;
                                            dashboard.SetInventoryRefreshing(false);
                                            dashboard.RenderStartupStatus("Состав оборудования и показания обновлены");
                                        }
                                    }));
                                    Refresh.WaitOne(2000);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log(ex);
                            if (!stopping)
                            {
                                window.Dispatcher.BeginInvoke(new Action(delegate
                                {
                                    MessageBox.Show(window, "Сбор данных остановлен: " + ex.Message + "\nПерезапустите ADAMO GT.", "ADAMO GT", MessageBoxButton.OK, MessageBoxImage.Warning);
                                }));
                            }
                        }
                    });
                };
                DashboardViewport.FitToWorkArea(window, SystemParameters.WorkArea);
                app.Run(window);
                return 0;
            }
            catch (Exception ex)
            {
                Log(ex);
                if (args.Length == 0) { MessageBox.Show("ADAMO GT: " + ex.Message, "Ошибка запуска"); }
                return 1;
            }
        }

        private static async Task RunStartupChecks(Window window, Dashboard dashboard, CancellationToken token, bool automaticTests, BenchmarkHistoryResult previous)
        {
            checksRunning = automaticTests;
            checkErrors = 0;
            dashboard.SetChecksRunning(automaticTests);
            try
            {
                await StartupSequence.Run(
                    delegate
                    {
                        return window.Dispatcher.InvokeAsync(new Action(delegate
                        {
                            token.ThrowIfCancellationRequested();
                            if (stopping) { return; }
                            initialInformationShown = true;
                            InitialDisplayReady.Set();
                        }), System.Windows.Threading.DispatcherPriority.ContextIdle).Task;
                    },
                    delegate { return StartBenchmarks(window, dashboard, true); },
                    delegate { return StartGpuTests(window, dashboard, true); },
                    null, // Startup uses the local catalog/cache; online lookup requires a user request.
                    delegate(string status)
                    {
                        if (!stopping && automaticTests)
                        {
                            dashboard.RenderStartupStatus(status.StartsWith("Проверки завершены", StringComparison.Ordinal)
                                ? CheckSummary() : status);
                        }
                    }, token, automaticTests);
            }
            catch (OperationCanceledException)
            {
                if (!stopping) { dashboard.RenderStartupStatus("Проверки остановлены · показатели продолжают обновляться"); }
            }
            catch (Exception exception)
            {
                Log(exception);
                if (!stopping) { dashboard.RenderStartupStatus("Не все проверки завершены · тест можно повторить кнопкой «Тест»"); }
            }
            finally
            {
                InitialDisplayReady.Set();
            }
            // A cached startup must not reset or await a manual test the user
            // may have started while the window was being presented.
            if (!automaticTests) { return; }
            // A cancelled sequence must still wait for the native workers to release their resources.
            if (benchmarkTask != null) { try { await benchmarkTask; } catch (Exception ex) { Log(ex); } }
            if (gpuTask != null) { try { await gpuTask; } catch (Exception ex) { Log(ex); } }
            checksRunning = false;
            if (!stopping) { dashboard.SetChecksRunning(false); }
        }

        private static void StopChecks()
        {
            if (stressWindow != null) { stressWindow.RequestStop(); }
            Interlocked.Increment(ref stopGeneration);
            StartupCancellation.Cancel();
            if (benchmarkCancellation != null) { benchmarkCancellation.Cancel(); }
            if (gpuCancellation != null) { gpuCancellation.Cancel(); }
            if (portsCancellation != null) { portsCancellation.Cancel(); }
        }

        private static async Task RunManualTest(Window window, Dashboard dashboard, bool gpuOnly)
        {
            if (checksRunning || inventoryRefreshing || stopping || !initialInformationShown) { return; }
            checksRunning = true;
            checkErrors = 0;
            dashboard.SetChecksRunning(true);
            dashboard.RenderStartupStatus(gpuOnly ? "Короткий GPU-тест · " + BenchmarkSettings.Current.GpuWarmupSeconds + " с разогрев + " + BenchmarkSettings.Current.GpuMeasurementSeconds + " с измерение" : "Проверяем скорость накопителей…");
            try
            {
                int admittedGeneration = Volatile.Read(ref stopGeneration);
                Snapshot testingSnapshot = latest;
                await Task.Run(delegate { history.MarkAttempt(testingSnapshot); });
                if (stopping || admittedGeneration != Volatile.Read(ref stopGeneration))
                {
                    if (!stopping) { dashboard.RenderStartupStatus("Тест остановлен"); }
                    return;
                }
                if (gpuOnly) { await StartGpuTests(window, dashboard, false); }
                else { await StartBenchmarks(window, dashboard); }
                bool cancelled = gpuOnly ? gpuCancellation != null && gpuCancellation.IsCancellationRequested
                    : benchmarkCancellation != null && benchmarkCancellation.IsCancellationRequested;
                if (!stopping) { dashboard.RenderStartupStatus(cancelled ? "Тест остановлен" : CheckSummary()); }
            }
            catch (Exception ex)
            {
                Log(ex);
                if (!stopping) { dashboard.RenderStartupStatus("Тест недоступен · " + ex.Message); }
            }
            finally
            {
                checksRunning = false;
                if (!stopping) { dashboard.SetChecksRunning(false); }
            }
        }

        private static void RenderHistory(Dashboard dashboard, BenchmarkHistoryResult previous)
        {
            foreach (BenchmarkProgress result in previous.Disks) { dashboard.RenderBenchmark(result); }
            foreach (GpuBenchmarkProgress result in previous.Gpus) { dashboard.RenderGpuBenchmark(result); }
        }

        private static string HistoryStatus(BenchmarkHistoryResult previous)
        {
            if (!previous.HasPreviousAttempt) { return "Автотест не запущен · тесты доступны кнопками"; }
            string status = String.IsNullOrWhiteSpace(previous.Status) ? "Сохранённые результаты · повторить тесты можно кнопками" : previous.Status;
            return previous.LastTestUtc.HasValue ? "Тесты от " + previous.LastTestUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm") + " · " + status : status;
        }

        private static string CheckSummary()
        {
            return checkErrors == 0 ? "Проверки закончены · результаты в блоках SSD и GPU"
                : "Проверки закончены · недоступных тестов или ошибок: " + checkErrors + " · подробности в блоках";
        }

        private static bool ConfirmOnlinePortLookup(Window window)
        {
            if (latest == null || stopping || !initialInformationShown) { return false; }
            return MessageBox.Show(window,
                "Для поиска разъёмов ADAMO GT передаст производителя и модель компьютера в DuckDuckGo и откроет страницы производителя. Эти сайты увидят ваш IP-адрес.\n\n"
                + "Серийные номера, имя компьютера, результаты тестов, изображение камеры и звук не отправляются. Можно отказаться: локальные данные и тесты продолжат работать.\n\n"
                + "Выполнить интернет-поиск сейчас?",
                "Поиск спецификации в интернете", MessageBoxButton.YesNo, MessageBoxImage.Information,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        private static Task StartPortLookup(Window window, Dashboard dashboard, bool forceRefresh)
        {
            if (latest == null || stopping || !initialInformationShown) { return Task.FromResult(false); }
            if (portsTask != null && (!portsTask.IsCompleted || !forceRefresh)) { return portsTask; }
            if (portsCancellation != null) { portsCancellation.Dispose(); }
            portsCancellation = !forceRefresh
                ? CancellationTokenSource.CreateLinkedTokenSource(StartupCancellation.Token) : new CancellationTokenSource();
            var token = portsCancellation.Token;
            int generation = ++portsGeneration;
            var computer = latest;
            PortSpecification pending = PortLookup.CachedOnly(computer);
            pending.IsLoading = true;
            pending.Status = "Уточняем спецификацию производителя…";
            dashboard.RenderPorts(pending);
            portsTask = Task.Run(delegate
            {
                PortSpecification result;
                try { result = new PortLookup().Lookup(computer, token, forceRefresh); }
                catch (OperationCanceledException)
                {
                    result = PortLookup.CachedOnly(computer);
                }
                catch (Exception ex)
                {
                    Log(ex);
                    result = new PortSpecification { Model = computer.ComputerModel, Status = "Не удалось получить спецификацию", Note = "Можно открыть поиск производителя кнопкой «Поиск»." };
                }
                if (!stopping)
                {
                    window.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (stopping || generation != portsGeneration) { return; }
                        if (token.IsCancellationRequested)
                        {
                            result = PortLookup.CachedOnly(computer);
                            result.Status = result.Ports.Count > 0 ? "Поиск остановлен · сохранённая спецификация" : "Поиск остановлен · можно повторить";
                        }
                        result.IsLoading = false;
                        dashboard.RenderPorts(result);
                    }));
                }
            });
            return portsTask;
        }

        private static Task StartBenchmarks(Window window, Dashboard dashboard, bool automatic = false)
        {
            if (latest == null || stopping || !initialInformationShown) { return Task.FromResult(false); }
            if (benchmarkTask != null && (!benchmarkTask.IsCompleted || automatic)) { return benchmarkTask; }
            if (benchmarkCancellation != null) { benchmarkCancellation.Dispose(); }
            benchmarkCancellation = automatic
                ? CancellationTokenSource.CreateLinkedTokenSource(StartupCancellation.Token) : new CancellationTokenSource();
            var token = benchmarkCancellation.Token;
            var testingSnapshot = latest;
            var disks = testingSnapshot.Disks.ToArray();
            int generation = ++benchmarkGeneration;
            foreach (var disk in disks)
            {
                dashboard.RenderBenchmark(new BenchmarkProgress { DiskIndex = disk.Index, Stage = "В очереди на тест", IsRunning = true });
            }
            benchmarkTask = Task.Run(delegate
            {
                foreach (var disk in disks)
                {
                    if (token.IsCancellationRequested)
                    {
                        BenchmarkProgress cancelled = history.Load(testingSnapshot).Disks.FirstOrDefault(item => item.DiskIndex == disk.Index);
                        if (cancelled == null) { cancelled = new BenchmarkProgress { DiskIndex = disk.Index, Stage = "Тест отменён" }; }
                        else { cancelled.Stage = "Новый тест отменён · " + cancelled.Stage; }
                        window.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (!stopping && generation == benchmarkGeneration) { dashboard.RenderBenchmark(cancelled); }
                        }));
                        continue;
                    }
                    DiskBenchmark.Run(disk, delegate(BenchmarkProgress progress)
                    {
                        if (!progress.IsRunning && !String.IsNullOrWhiteSpace(progress.Error)) { Interlocked.Increment(ref checkErrors); }
                        BenchmarkProgress shown = progress;
                        if (!progress.IsRunning)
                        {
                            bool completed = PersistDiskResult(testingSnapshot, disk, progress);
                            if (!completed)
                            {
                                BenchmarkProgress previous = history.Load(testingSnapshot).Disks.FirstOrDefault(item => item.DiskIndex == disk.Index);
                                if (previous != null)
                                {
                                    previous.Stage = "Новый тест не завершён · " + previous.Stage;
                                    previous.Error = progress.Error;
                                    shown = previous;
                                }
                            }
                        }
                        window.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (!stopping && generation == benchmarkGeneration) { dashboard.RenderBenchmark(shown); }
                        }));
                    }, token);
                }
            });
            return benchmarkTask;
        }

        private static Task StartGpuTests(Window window, Dashboard dashboard, bool automatic)
        {
            if (latest == null || stopping || !initialInformationShown) { return Task.FromResult(false); }
            if (gpuTask != null && !gpuTask.IsCompleted) { return gpuTask; }
            if (gpuCancellation != null) { gpuCancellation.Dispose(); }
            gpuCancellation = automatic
                ? CancellationTokenSource.CreateLinkedTokenSource(StartupCancellation.Token) : new CancellationTokenSource();
            var token = gpuCancellation.Token;
            uint? selected = dashboard.SelectedGpuAdapterIndex;
            var testingSnapshot = latest;
            var adapters = testingSnapshot.Gpus.Where(gpu => automatic || gpu.AdapterIndex == selected).ToArray();
            foreach (var gpu in adapters)
            {
                dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress { AdapterIndex = gpu.AdapterIndex, AdapterName = gpu.Name, Stage = "В очереди на GPU-тест", IsRunning = true });
            }
            gpuTask = Task.Run(delegate
            {
                foreach (var gpu in adapters)
                {
                    double? maximum = null;
                    GpuBenchmark.Run(gpu, delegate(GpuBenchmarkProgress progress)
                    {
                        maximum = PeakGpuTemperature(latest, gpu, maximum);
                        progress.MaxTemperature = maximum;
                        if (!progress.IsRunning && !String.IsNullOrWhiteSpace(progress.Error)) { Interlocked.Increment(ref checkErrors); }
                        GpuBenchmarkProgress shown = progress;
                        if (!progress.IsRunning)
                        {
                            bool completed = PersistGpuResult(testingSnapshot, gpu, progress);
                            if (!completed)
                            {
                                GpuBenchmarkProgress previous = history.Load(testingSnapshot).Gpus.FirstOrDefault(item => item.AdapterIndex == gpu.AdapterIndex);
                                if (previous != null)
                                {
                                    previous.Stage = "Новый тест не завершён · " + previous.Stage;
                                    previous.Error = progress.Error;
                                    shown = previous;
                                }
                            }
                        }
                        window.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (!stopping) { dashboard.RenderGpuBenchmark(shown); }
                        }));
                    }, token);
                }
            });
            return gpuTask;
        }

        private static bool PersistDiskResult(Snapshot testedComputer, DiskSnapshot disk, BenchmarkProgress progress)
        {
            bool completed = !progress.IsRunning && String.IsNullOrWhiteSpace(progress.Error)
                && progress.ReadBytesPerSecond.HasValue && progress.WriteBytesPerSecond.HasValue;
            if (!completed) { return false; }
            progress.TestedAtUtc = DateTime.UtcNow;
            progress.FromHistory = false;
            if (!history.RecordDisk(testedComputer, disk, progress)) { progress.Stage += " · не удалось сохранить историю"; }
            return true;
        }

        private static bool PersistGpuResult(Snapshot testedComputer, GpuSnapshot gpu, GpuBenchmarkProgress progress)
        {
            bool completed = !progress.IsRunning && progress.Completed && String.IsNullOrWhiteSpace(progress.Error)
                && progress.FramesPerSecond.HasValue;
            if (!completed) { return false; }
            progress.TestedAtUtc = DateTime.UtcNow;
            progress.FromHistory = false;
            if (!history.RecordGpu(testedComputer, gpu, progress)) { progress.Stage += " · не удалось сохранить историю"; }
            return true;
        }

        private static double? PeakGpuTemperature(Snapshot sample, GpuSnapshot adapter, double? previous)
        {
            if (sample == null) { return previous; }
            GpuSnapshot measured = sample.Gpus.FirstOrDefault(gpu => adapter.AdapterIndex.HasValue
                ? gpu.AdapterIndex == adapter.AdapterIndex && gpu.AdapterLuidLow == adapter.AdapterLuidLow && gpu.AdapterLuidHigh == adapter.AdapterLuidHigh
                : gpu.Name == adapter.Name);
            if (measured == null || !measured.Temperature.HasValue || Double.IsNaN(measured.Temperature.Value) || Double.IsInfinity(measured.Temperature.Value)) { return previous; }
            return previous.HasValue ? Math.Max(previous.Value, measured.Temperature.Value) : measured.Temperature;
        }

        private static void RenderSaved(Window window, Dashboard dashboard, string[] args)
        {
            dashboard.SetSnapshotMode();
            var serializer = new JavaScriptSerializer();
            string snapshotPath = Path.GetFullPath(args[2]);
            Snapshot snapshot = serializer.Deserialize<Snapshot>(File.ReadAllText(snapshotPath));
            snapshot.Timestamp = snapshot.Timestamp.ToLocalTime();
            dashboard.Render(snapshot);
            string disksPath = Path.ChangeExtension(snapshotPath, ".benchmarks.json");
            string gpuPath = Path.ChangeExtension(snapshotPath, ".gpu.json");
            string portsPath = Path.ChangeExtension(snapshotPath, ".ports.json");
            if (File.Exists(disksPath))
            {
                foreach (var progress in serializer.Deserialize<List<BenchmarkProgress>>(File.ReadAllText(disksPath))) { dashboard.RenderBenchmark(progress); }
            }
            if (File.Exists(gpuPath))
            {
                foreach (var progress in serializer.Deserialize<List<GpuBenchmarkProgress>>(File.ReadAllText(gpuPath))) { dashboard.RenderGpuBenchmark(progress); }
            }
            dashboard.RenderPorts(File.Exists(portsPath) ? serializer.Deserialize<PortSpecification>(File.ReadAllText(portsPath)) : PortLookup.CachedOnly(snapshot));
            dashboard.RenderStartupStatus("Сохранённые результаты проверки · " + snapshot.Timestamp.ToString("dd.MM.yyyy HH:mm:ss"));
            Render(window, args[1], args.Length > 3 ? Double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 1200,
                args.Length > 4 ? Double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 760);
        }

        private static Snapshot Capture()
        {
            using (var monitor = new HardwareMonitor())
            {
                monitor.Read();
                Thread.Sleep(1100);
                return monitor.Read();
            }
        }

        public static Window LoadWindow()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dashboard.xaml"))
            {
                var window = (Window)XamlReader.Load(stream);
                DashboardViewport.Attach(window);
                return window;
            }
        }

        public static void Render(Window window, string path, double width, double height)
        {
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            var host = new Border { Child = content, Width = width, Height = height, Background = window.Background, Resources = window.Resources,
                UseLayoutRounding = window.UseLayoutRounding, FlowDirection = window.FlowDirection };
            System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(host, window.FontSize);
            TextOptions.SetTextFormattingMode(host, TextOptions.GetTextFormattingMode(window));
            // Render is a 96-DPI export. Preserve native text/layout settings rather than
            // silently using Ideal formatting after detaching the page from its Window.
            VisualTreeHelper.SetRootDpi(host, new DpiScale(1, 1));
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.GetFullPath(path))) { encoder.Save(stream); }
        }

        private static void WriteSnapshot(string path, Snapshot snapshot)
        {
            File.WriteAllText(Path.GetFullPath(path), new JavaScriptSerializer().Serialize(snapshot));
        }

        private static void SaveReport(Window owner, Dashboard dashboard)
        {
            try
            {
                string json = dashboard.CreateReport();
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcPulse", "reports");
                Directory.CreateDirectory(directory);
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Сохранить отчёт ADAMO GT", Filter = "Отчёт JSON (*.json)|*.json",
                    DefaultExt = ".json", AddExtension = true, OverwritePrompt = true,
                    InitialDirectory = directory, FileName = "ADAMO-GT-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".json"
                };
                if (dialog.ShowDialog(owner) != true) { return; }
                File.WriteAllText(dialog.FileName, json, new System.Text.UTF8Encoding(false));
                MessageBox.Show(owner, "Отчёт сохранён:\n" + dialog.FileName, "ADAMO GT", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(owner, "Не удалось сохранить отчёт: " + exception.Message, "ADAMO GT", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void Log(Exception ex)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcPulse");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "last-error.log"), DateTime.Now + "\n" + ex);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void ShowSensorHelp(Window owner)
        {
            var dialog = new Window
            {
                Title = "Датчики и точность данных",
                Owner = owner, Width = 700, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(243, 246, 246)),
                Foreground = new SolidColorBrush(Color.FromRgb(29, 29, 31)), FontFamily = new FontFamily("Segoe UI")
            };
            var panel = new StackPanel { Margin = new Thickness(28) };
            panel.Children.Add(new TextBlock { Text = "Доступ к температурам", FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            panel.Children.Add(new TextBlock
            {
                Text = Display.CpuHeatNote,
                FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 22,
                Margin = new Thickness(0, 0, 0, 14)
            });
            panel.Children.Add(new TextBlock
            {
                Text = SensorHelpText(),
                FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 22
            });
            var setupStatus = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(52, 120, 31)) };
            var setupCancellation = new CancellationTokenSource();
            bool dialogClosed = false;
            bool installing = false;
            dialog.Closed += delegate { dialogClosed = true; setupCancellation.Cancel(); if (!installing) { setupCancellation.Dispose(); } };
            Version pawnVersion = SensorSetup.ReadInstalledVersion();
            setupStatus.Text = (pawnVersion == null ? "Дополнительный драйвер PawnIO не установлен. Доступные датчики Windows работают без него." : "Установлен дополнительный драйвер PawnIO " + pawnVersion + ".")
                + (latest != null && !latest.IsAdmin ? " Для расширенного доступа к оборудованию можно запустить программу от администратора." : "");
            panel.Children.Add(setupStatus);
            var buttons = new WrapPanel { Margin = new Thickness(0, 22, 0, 0) };
            var install = HelpButton("Установить PawnIO");
            install.IsEnabled = pawnVersion == null || pawnVersion < SensorSetup.InstallerVersion;
            var driver = HelpButton("Официальный PawnIO");
            driver.Click += delegate { OpenUrl(SensorSetup.OfficialWebsiteUrl); };
            var admin = HelpButton("Запустить от администратора");
            install.Click += async delegate
            {
                install.IsEnabled = false;
                admin.IsEnabled = false;
                installing = true;
                try
                {
                    SensorSetupResult result = await SensorSetup.InstallAsync(delegate(string message)
                    {
                        dialog.Dispatcher.BeginInvoke(new Action(delegate { if (!dialogClosed) { setupStatus.Text = message; } }));
                    }, setupCancellation.Token);
                    if (!dialogClosed) { setupStatus.Text = result.Message; }
                }
                finally
                {
                    installing = false;
                    if (dialogClosed) { setupCancellation.Dispose(); }
                    else { Version installed = SensorSetup.ReadInstalledVersion(); install.IsEnabled = installed == null || installed < SensorSetup.InstallerVersion; admin.IsEnabled = true; }
                }
            };
            admin.Click += async delegate
            {
                admin.IsEnabled = false;
                install.IsEnabled = false;
                try
                {
                    StopChecks();
                    setupStatus.Text = "Останавливаем текущие тесты перед перезапуском…";
                    var workers = new List<Task>();
                    if (benchmarkTask != null) { workers.Add(benchmarkTask); }
                    if (gpuTask != null) { workers.Add(gpuTask); }
                    if (startupTask != null) { workers.Add(startupTask); }
                    await Task.WhenAll(workers);
                    if (dialogClosed || stopping) { return; }
                    Process.Start(new ProcessStartInfo(SensorStartup.GetElevationExecutable()) {
                        UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.CurrentDirectory });
                    dialog.Close();
                    owner.Close();
                }
                catch (System.ComponentModel.Win32Exception) { if (!dialogClosed) { setupStatus.Text = "Запрос администратора отменён или Windows не разрешила запуск."; } }
                finally { if (!dialogClosed) { admin.IsEnabled = true; Version installed = SensorSetup.ReadInstalledVersion(); install.IsEnabled = installed == null || installed < SensorSetup.InstallerVersion; } }
            };
            var library = HelpButton("Исходники датчиков");
            library.Click += delegate { OpenUrl("https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6"); };
            buttons.Children.Add(install);
            buttons.Children.Add(driver);
            buttons.Children.Add(admin);
            buttons.Children.Add(library);
            panel.Children.Add(buttons);
            dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            DialogViewport.Fit(dialog, DialogViewport.WorkArea(owner));
            dialog.Loaded += delegate { DialogViewport.Position(dialog, DialogViewport.WorkArea(dialog)); };
            dialog.ShowDialog();
        }

        private static string SensorHelpText()
        {
            BenchmarkSettings settings = BenchmarkSettings.Current;
            string diskHelp = settings.Profile == "quick-v2"
                ? "SSD: Microsoft DiskSpd, временный файл 1 GB, последовательные блоки 1 MB, очередь 8 и один поток. После подготовки файла чтение и запись идут по очереди: по 1 с разогрева и 5 с измерения. Кэш Windows отключён; используется текущая политика записи накопителя. Временный файл удаляется."
                : "SSD: временный файл " + settings.DiskMiB + " MB, блок 4 MB, один поток и очередь 1, без кэша Windows; файл удаляется.";
            return "Программа проверяет доступные датчики Windows и производителя. Температура GPU может поступать от установленного видеодрайвера, SSD — из SMART, CPU — от датчика производителя, если он доступен через Windows. Источник указан в подсказке к температуре. На части компьютеров получить температуру CPU без дополнительного драйвера невозможно; вместо неё программа не подставляет температуру корпуса или приблизительное число."
                + "\n\nPawnIO — необязательный дополнительный драйвер для расширенного доступа к датчикам CPU. Он не нужен для запуска приложения и не гарантирует доступность всех датчиков. Кнопка установки загружает официальную версию, проверяет контрольную сумму и цифровую подпись, затем открывает установщик с запросом Windows. После установки перезапустите ADAMO GT от администратора."
                + "\n\nПри первой проверке этого компьютера тесты запускаются по очереди после показа характеристик. При следующих запусках отображаются сохранённые результаты с датой, а повторный тест запускается кнопкой. Температуры, заряд батареи и текущая нагрузка продолжают обновляться. Отмена или ошибка нового теста не удаляет прежний успешный результат. " + diskHelp + " Результат зависит от нагрузки и не является гарантированным максимумом SSD. GPU: фиксированная сцена Direct3D 11, 1280 × 720; разогрев " + settings.GpuWarmupSeconds + " с и измерение " + settings.GpuMeasurementSeconds + " с. FPS относится к этой сцене. Настройки коротких тестов находятся в data/benchmarks.json. " + settings.Note
                + "\n\nSMART NVMe читается через Windows; для поддерживаемых SATA/USB используется smartctl с базой накопителей. Процент — оценка остатка ресурса, предупреждения учитываются отдельно. Недоступные значения остаются неизвестными. Команды включения SMART и внутренние самотесты накопителя не запускаются."
                + "\n\nМаксимум RAM по спецификации производителя и сведения BIOS разделены в подсказке. Такт обычной DDR рассчитан как половина скорости в МТ/с; для LPDDR такой расчёт не используется. Сохранённая ёмкость батареи и её заряд — разные показатели. Время работы — оценка от полного заряда по сохранённой ёмкости и условному потреблению 7–15 Вт для лёгкой работы. Это не измеренный расход; игры, тесты и высокая яркость сокращают время."
                + "\n\nСправочник data/models.json содержит точные модели и порты, data/panels.json — подтверждённые панели. Тип матрицы и паспортная яркость без точного совпадения остаются неизвестными. Для поиска портов в интернет передаются производитель и модель, без серийного номера. Наличие разъёма в спецификации не подтверждает его исправность."
                + "\n\nКнопка «Отчёт» сохраняет характеристики, источники и результаты в JSON, включая незавершённые проверки. Короткая диагностика не проверяет все возможные неисправности."
                + "\n\nПамять, кэш и размер тестового файла показаны в привычном для Windows исчислении: KB = 1024 байта, MB = 1024 KB, GB = 1024 MB. Ёмкость SSD (GB, TB) и скорость MB/s используют десятичный расчёт: 1 GB = 1 000 000 000 байт, 1 MB/s = 1 000 000 байт/с."
                + "\n\nПри каждом обычном запуске программа запрашивает права администратора через Windows для чтения температур и других аппаратных данных. Если отменить запрос, остальные доступные данные и тесты остаются доступны. Температура встроенной GPU не заменяется температурой CPU, если отдельный датчик недоступен."
                + "\n\nADAMO GT " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + " · Windows x64 · LibreHardwareMonitor 0.9.6\nЛицензии компонентов — в папке Licenses; smartmontools — tools/smartmontools; DiskSpd — tools/diskspd.";
        }

        private static Button HelpButton(string text)
        {
            return new Button { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 8), Foreground = Brushes.Black };
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception ex) { MessageBox.Show(ex.Message, "Не удалось открыть браузер"); }
        }
    }
}
