using System;
using System.Threading;
using System.Threading.Tasks;

namespace PcPulse
{
    internal static class StartupSequence
    {
        internal static async Task Run(Func<Task> presentInformation, Func<Task> testDisks,
            Func<Task> findPorts, Action<string> report, CancellationToken token)
        {
            await Run(presentInformation, testDisks, null, findPorts, report, token);
        }

        internal static async Task Run(Func<Task> presentInformation, Func<Task> testDisks,
            Func<Task> testGpu, Func<Task> findPorts, Action<string> report, CancellationToken token, bool runBenchmarks = true)
        {
            token.ThrowIfCancellationRequested();
            await AwaitStage(presentInformation(), token);
            if (runBenchmarks)
            {
                report("Характеристики показаны · проверяем скорость накопителей…");
                token.ThrowIfCancellationRequested();
                await AwaitStage(testDisks(), token);
                if (testGpu != null)
                {
                    token.ThrowIfCancellationRequested();
                    report("Проверка накопителей закончена · выполняем короткий GPU-тест…");
                    token.ThrowIfCancellationRequested();
                    await AwaitStage(testGpu(), token);
                }
            }
            if (findPorts != null)
            {
                report(runBenchmarks ? "Проверка оборудования закончена · уточняем порты через интернет…" : "Сохранённые результаты · тесты запускаются кнопками");
                token.ThrowIfCancellationRequested();
                await AwaitStage(findPorts(), token);
            }
            token.ThrowIfCancellationRequested();
            report(runBenchmarks ? "Проверки завершены · показатели обновляются автоматически" : "Сохранённые результаты · текущие показатели обновляются");
        }

        private static async Task AwaitStage(Task stage, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var cancelled = new TaskCompletionSource<bool>();
            using (token.Register(delegate { cancelled.TrySetResult(true); }))
            {
                await Task.WhenAny(stage, cancelled.Task);
                token.ThrowIfCancellationRequested();
                await stage;
            }
        }
    }
}
