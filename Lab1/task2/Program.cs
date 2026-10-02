using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Lab1.Task2;

internal enum ScheduleKind { Static, Dynamic, Guided }

/// <summary>Тип распределения итераций. Chunk = 0 означает "значение по умолчанию".</summary>
internal readonly record struct Schedule(ScheduleKind Kind, int Chunk)
{
    public override string ToString()
    {
        string name = Kind.ToString().ToLowerInvariant();
        return Chunk > 0 ? $"{name}, chunk={Chunk}" : $"{name}, chunk по умолчанию";
    }
}

/// <summary>
/// Задача 2. Массив a[i] = i, результат b[i] = (a[i-1] + a[i] + a[i+1]) / 3.0 для внутренних элементов.
/// Распределение итераций между потоками: static / dynamic / guided / runtime.
/// Аналог: #pragma omp parallel for schedule(...).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length < 2
            || !int.TryParse(args[0], out int threads) || threads <= 0
            || !int.TryParse(args[1], out int n) || n < 3)
        {
            Console.Error.WriteLine(
                "Использование: dotnet run -c Release -- <число_потоков> <размер_массива> [static|dynamic|guided|runtime] [chunk]");
            return 1;
        }

        if (!TryReadSchedule(args, out Schedule schedule))
        {
            Console.Error.WriteLine("Неверно задан тип распределения или chunk.");
            return 1;
        }

        // Инициализация: значение элемента равно его порядковому номеру.
        var a = new double[n];
        for (int i = 0; i < n; i++)
        {
            a[i] = i;
        }

        int total = n - 2; // число внутренних элементов (итераций цикла)

        // Эталон: последовательное вычисление.
        var expected = new double[n];
        ProcessRange(a, expected, 0, total);

        // Параллельное вычисление.
        var b = new double[n];
        var iterations = new long[threads];
        var chunks = new int[threads];
        var sw = Stopwatch.StartNew();
        RunParallel(a, b, total, threads, schedule, iterations, chunks);
        sw.Stop();

        Console.WriteLine($"Потоков: {threads}, размер массива: {n}, распределение: {schedule}");
        for (int t = 0; t < threads; t++)
        {
            Console.WriteLine($"  Поток {t}: итераций = {iterations[t]}, блоков = {chunks[t]}");
        }

        double checksum = 0;
        double expectedChecksum = 0;
        bool same = true;
        for (int i = 1; i < n - 1; i++)
        {
            checksum += b[i];
            expectedChecksum += expected[i];
            if (b[i] != expected[i])
            {
                same = false;
            }
        }

        long done = 0;
        foreach (long x in iterations)
        {
            done += x;
        }

        Console.WriteLine($"Время: {sw.Elapsed.TotalMilliseconds:F3} мс");
        Console.WriteLine($"Контрольная сумма: {checksum} (ожидается {expectedChecksum})");
        Console.WriteLine($"Выполнено итераций: {done} из {total}");
        Console.WriteLine($"OK: {same && done == total}");
        return 0;
    }

    /// <summary>Обработка итераций [lo, hi) внутреннего диапазона; итерации k соответствует индекс i = k + 1.</summary>
    private static void ProcessRange(double[] a, double[] b, int lo, int hi)
    {
        for (int k = lo; k < hi; k++)
        {
            int i = k + 1;
            b[i] = (a[i - 1] + a[i] + a[i + 1]) / 3.0;
        }
    }

    private static void RunParallel(
        double[] a, double[] b, int total, int threads, Schedule schedule, long[] iterations, int[] chunks)
    {
        int next = 0;            // следующая невыданная итерация (dynamic / guided)
        var sync = new object(); // критическая секция выдачи блоков

        void Take(int id, int lo, int hi)
        {
            ProcessRange(a, b, lo, hi);
            iterations[id] += hi - lo;
            chunks[id]++;
        }

        void Worker(int id)
        {
            switch (schedule.Kind)
            {
                case ScheduleKind.Static when schedule.Chunk <= 0:
                {
                    // Один непрерывный блок на поток.
                    int baseSize = total / threads;
                    int rem = total % threads;
                    int lo = id * baseSize + Math.Min(id, rem);
                    int hi = lo + baseSize + (id < rem ? 1 : 0);
                    if (hi > lo)
                    {
                        Take(id, lo, hi);
                    }
                    break;
                }
                case ScheduleKind.Static:
                {
                    // Блоки по chunk итераций раздаются потокам по кругу.
                    int size = schedule.Chunk;
                    for (long start = (long)id * size; start < total; start += (long)threads * size)
                    {
                        Take(id, (int)start, (int)Math.Min(total, start + size));
                    }
                    break;
                }
                case ScheduleKind.Dynamic:
                {
                    // Свободный поток берёт следующий блок фиксированного размера.
                    int size = Math.Max(1, schedule.Chunk);
                    while (true)
                    {
                        int start;
                        lock (sync)
                        {
                            start = next;
                            next = Math.Min(total, next + size);
                        }
                        if (start >= total)
                        {
                            break;
                        }
                        Take(id, start, Math.Min(total, start + size));
                    }
                    break;
                }
                case ScheduleKind.Guided:
                {
                    // Размер блока пропорционален оставшейся работе, но не меньше chunk.
                    int minChunk = Math.Max(1, schedule.Chunk);
                    while (true)
                    {
                        int start, end;
                        lock (sync)
                        {
                            start = next;
                            int remaining = total - start;
                            int size = Math.Min(remaining, Math.Max(minChunk, (remaining + threads - 1) / threads));
                            end = start + size;
                            next = end;
                        }
                        if (start >= total)
                        {
                            break;
                        }
                        Take(id, start, end);
                    }
                    break;
                }
            }
        }

        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int id = t;
            workers[t] = new Thread(() => Worker(id));
            workers[t].Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join(); // барьер в конце параллельной области
        }
    }

    // ---------- разбор параметров распределения ----------

    private static bool TryReadSchedule(string[] args, out Schedule schedule)
    {
        schedule = new Schedule(ScheduleKind.Static, 0);
        if (args.Length < 3)
        {
            return true;
        }

        if (args[2].Equals("runtime", StringComparison.OrdinalIgnoreCase))
        {
            // Как в OpenMP: тип берётся из переменной окружения (OMP_SCHEDULE или LAB2_SCHEDULE).
            string? spec = Environment.GetEnvironmentVariable("LAB2_SCHEDULE")
                           ?? Environment.GetEnvironmentVariable("OMP_SCHEDULE");
            return spec is null || TryParseSpec(spec, out schedule);
        }

        if (!TryParseKind(args[2], out ScheduleKind kind))
        {
            return false;
        }

        int chunk = 0;
        if (args.Length >= 4 && (!int.TryParse(args[3], out chunk) || chunk <= 0))
        {
            return false;
        }

        schedule = new Schedule(kind, chunk);
        return true;
    }

    private static bool TryParseSpec(string spec, out Schedule schedule)
    {
        schedule = default;
        string[] parts = spec.Split(',', 2, StringSplitOptions.TrimEntries);
        if (!TryParseKind(parts[0], out ScheduleKind kind))
        {
            return false;
        }

        int chunk = 0;
        if (parts.Length == 2 && parts[1].Length > 0 && (!int.TryParse(parts[1], out chunk) || chunk <= 0))
        {
            return false;
        }

        schedule = new Schedule(kind, chunk);
        return true;
    }

    private static bool TryParseKind(string text, out ScheduleKind kind)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "static": kind = ScheduleKind.Static; return true;
            case "dynamic": kind = ScheduleKind.Dynamic; return true;
            case "guided": kind = ScheduleKind.Guided; return true;
            default: kind = default; return false;
        }
    }
}
