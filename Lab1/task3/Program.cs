using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Lab1.Task3;

/// <summary>
/// Задача 3. Потоки печатают свои идентификаторы в обратном порядке (n-1, n-2, ..., 0).
/// Реализовано 5 способов синхронизации.
/// </summary>
internal static class Program
{
    private delegate void Method(int n, Action<int> emit);

    private static readonly (string Name, string Description, Method Run)[] Methods =
    {
        ("mutex", "активное ожидание на атомарном счётчике очереди", RunSpin),
        ("cond", "пассивное ожидание через Monitor.Wait/PulseAll (условная переменная)", RunCond),
        ("chan_chain", "цепочка очередей: эстафета от старшего потока к младшему", RunChanChain),
        ("chan_array", "координатор и массив очередей с подтверждением завершения", RunChanArray),
        ("wg_chain", "цепочка на CountdownEvent (аналог WaitGroup)", RunWgChain),
    };

    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length < 2
            || !int.TryParse(args[0], out int n) || n <= 0
            || !int.TryParse(args[1], out int repeats) || repeats <= 0)
        {
            Console.Error.WriteLine(
                "Использование: dotnet run -c Release -- <число_потоков> <число_повторов> [mutex|cond|chan_chain|chan_array|wg_chain]");
            return 1;
        }

        string? only = args.Length >= 3 ? args[2].ToLowerInvariant() : null;
        if (only is not null && !Methods.Any(m => m.Name == only))
        {
            Console.Error.WriteLine($"Неизвестный способ: {only}");
            return 1;
        }

        bool allOk = true;
        foreach (var (name, description, run) in Methods)
        {
            if (only is not null && only != name)
            {
                continue;
            }

            Console.WriteLine($"=== {name}: {description} ===");

            var sw = Stopwatch.StartNew();
            int correct = 0;
            for (int r = 0; r < repeats; r++)
            {
                // Первый запуск печатает вывод потоков, остальные - только проверяют порядок.
                if (Execute(n, run, verbose: r == 0))
                {
                    correct++;
                }
            }
            sw.Stop();

            bool ok = correct == repeats;
            allOk &= ok;
            Console.WriteLine($"Повторов: {repeats}, с верным порядком: {correct}, время: {sw.Elapsed.TotalMilliseconds:F1} мс");
            Console.WriteLine($"OK: {ok}");
            Console.WriteLine();
        }

        return allOk ? 0 : 2;
    }

    /// <summary>Один запуск способа: возвращает true, если порядок строго обратный.</summary>
    private static bool Execute(int n, Method run, bool verbose)
    {
        var order = new List<int>(n);
        var sink = new object();

        void Emit(int id)
        {
            lock (sink)
            {
                order.Add(id);
                if (verbose)
                {
                    Console.WriteLine($"  Поток {id} из {n}: Hello World");
                }
            }
        }

        run(n, Emit);

        if (order.Count != n)
        {
            return false;
        }

        for (int i = 0; i < n; i++)
        {
            if (order[i] != n - 1 - i)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Запускает n потоков; afterStart выполняется в главном потоке после старта, до ожидания.</summary>
    private static void RunThreads(int n, Action<int> body, Action? afterStart = null)
    {
        var workers = new Thread[n];
        for (int i = 0; i < n; i++)
        {
            int id = i;
            workers[i] = new Thread(() => body(id));
            workers[i].Start();
        }

        afterStart?.Invoke();

        foreach (Thread worker in workers)
        {
            worker.Join();
        }
    }

    // 1. Активное ожидание: поток крутится, пока turn не станет равен его id.
    private static void RunSpin(int n, Action<int> emit)
    {
        int turn = n - 1;
        RunThreads(n, id =>
        {
            var spin = new SpinWait();
            while (Volatile.Read(ref turn) != id)
            {
                spin.SpinOnce();
            }

            emit(id);
            Interlocked.Exchange(ref turn, id - 1);
        });
    }

    // 2. Условная переменная: поток спит на Monitor.Wait, пока не наступит его очередь.
    private static void RunCond(int n, Action<int> emit)
    {
        var gate = new object();
        int turn = n - 1;
        RunThreads(n, id =>
        {
            lock (gate)
            {
                while (turn != id)
                {
                    Monitor.Wait(gate);
                }

                emit(id);
                turn--;
                Monitor.PulseAll(gate);
            }
        });
    }

    // 3. Цепочка очередей: поток id ждёт сигнал в signals[id], печатает и будит поток id-1.
    private static void RunChanChain(int n, Action<int> emit)
    {
        var signals = NewQueues(n);
        RunThreads(
            n,
            id =>
            {
                signals[id].Take();
                emit(id);
                if (id > 0)
                {
                    signals[id - 1].Add(true);
                }
            },
            afterStart: () => signals[n - 1].Add(true)); // запуск эстафеты со старшего потока
        DisposeAll(signals);
    }

    // 4. Координатор по очереди даёт слово каждому потоку (от старшего) и ждёт подтверждения.
    private static void RunChanArray(int n, Action<int> emit)
    {
        var turn = NewQueues(n);
        var done = NewQueues(n);
        RunThreads(
            n,
            id =>
            {
                turn[id].Take();
                emit(id);
                done[id].Add(true);
            },
            afterStart: () =>
            {
                for (int id = n - 1; id >= 0; id--)
                {
                    turn[id].Add(true);
                    done[id].Take();
                }
            });
        DisposeAll(turn);
        DisposeAll(done);
    }

    // 5. Цепочка на счётчиках: поток id ждёт, пока поток id+1 не завершит печать.
    private static void RunWgChain(int n, Action<int> emit)
    {
        var gates = new CountdownEvent[n];
        for (int i = 0; i < n; i++)
        {
            gates[i] = new CountdownEvent(1);
        }

        RunThreads(n, id =>
        {
            if (id < n - 1)
            {
                gates[id + 1].Wait();
            }

            emit(id);
            gates[id].Signal();
        });

        foreach (CountdownEvent gate in gates)
        {
            gate.Dispose();
        }
    }

    private static BlockingCollection<bool>[] NewQueues(int n)
    {
        var queues = new BlockingCollection<bool>[n];
        for (int i = 0; i < n; i++)
        {
            queues[i] = new BlockingCollection<bool>(1);
        }

        return queues;
    }

    private static void DisposeAll(BlockingCollection<bool>[] queues)
    {
        foreach (BlockingCollection<bool> q in queues)
        {
            q.Dispose();
        }
    }
}
