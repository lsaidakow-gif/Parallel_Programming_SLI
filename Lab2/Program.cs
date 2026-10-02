using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Lab2;

/// <summary>
/// Задача 4. Перемножение больших матриц: последовательная и параллельная версии,
/// замер времени, ускорение (speedup), проверка результата по контрольной сумме.
/// Матрицы хранятся в одномерных массивах, порядок циклов i -> k -> j (дружелюбен к кэшу).
/// </summary>
internal static class Program
{
    private const int MaxSize = 10000;

    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length < 2
            || !int.TryParse(args[0], out int threads) || threads <= 0
            || !int.TryParse(args[1], out int n) || n <= 0 || n > MaxSize)
        {
            Console.Error.WriteLine($"Использование: dotnet run -c Release -- <число_потоков> <размер_матрицы (1..{MaxSize})>");
            return 1;
        }

        // Прогрев JIT на маленьких матрицах, чтобы компиляция не попала в замер.
        WarmUp();

        var rng = new Random(12345);
        double[] a = RandomMatrix(n, rng);
        double[] b = RandomMatrix(n, rng);

        var cSeq = new double[n * n];
        var sw = Stopwatch.StartNew();
        MultiplyRows(a, b, cSeq, n, 0, n);
        sw.Stop();
        double seqMs = sw.Elapsed.TotalMilliseconds;

        var cPar = new double[n * n];
        sw.Restart();
        MultiplyParallel(a, b, cPar, n, threads);
        sw.Stop();
        double parMs = sw.Elapsed.TotalMilliseconds;

        double sumSeq = Checksum(cSeq);
        double sumPar = Checksum(cPar);
        bool match = Math.Abs(sumSeq - sumPar) <= 1e-9 * Math.Max(1.0, Math.Abs(sumSeq));

        double speedup = seqMs / parMs;
        double efficiency = speedup / threads * 100.0;

        Console.WriteLine($"Размер матриц: {n} x {n}");
        Console.WriteLine($"Потоков: {threads}");
        Console.WriteLine($"Последовательно: {seqMs:F2} мс");
        Console.WriteLine($"Параллельно ({threads} потоков): {parMs:F2} мс");
        Console.WriteLine($"Ускорение (speedup): {speedup:F2}x");
        Console.WriteLine($"Эффективность: {efficiency:F1}%");
        Console.WriteLine($"Контрольная сумма (последовательно): {sumSeq:F6}");
        Console.WriteLine($"Контрольная сумма (параллельно): {sumPar:F6}");
        Console.WriteLine($"Results Match: {match}");
        return match ? 0 : 2;
    }

    private static double[] RandomMatrix(int n, Random rng)
    {
        var m = new double[n * n];
        for (int i = 0; i < m.Length; i++)
        {
            m[i] = rng.NextDouble();
        }

        return m;
    }

    /// <summary>Вычисляет строки [rowFrom, rowTo) матрицы C = A * B. C должна быть заполнена нулями.</summary>
    private static void MultiplyRows(double[] a, double[] b, double[] c, int n, int rowFrom, int rowTo)
    {
        for (int i = rowFrom; i < rowTo; i++)
        {
            int rowC = i * n;
            for (int k = 0; k < n; k++)
            {
                double aik = a[i * n + k];
                int rowB = k * n;
                for (int j = 0; j < n; j++)
                {
                    c[rowC + j] += aik * b[rowB + j];
                }
            }
        }
    }

    /// <summary>Строки матрицы делятся на непрерывные блоки между потоками.</summary>
    private static void MultiplyParallel(double[] a, double[] b, double[] c, int n, int threads)
    {
        var workers = new Thread[threads];
        int baseRows = n / threads;
        int rem = n % threads;
        int row = 0;

        for (int t = 0; t < threads; t++)
        {
            int lo = row;
            int hi = lo + baseRows + (t < rem ? 1 : 0);
            row = hi;

            workers[t] = new Thread(() => MultiplyRows(a, b, c, n, lo, hi));
            workers[t].Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }
    }

    private static double Checksum(double[] m)
    {
        double sum = 0;
        foreach (double x in m)
        {
            sum += x;
        }

        return sum;
    }

    private static void WarmUp()
    {
        const int n = 64;
        var rng = new Random(1);
        double[] a = RandomMatrix(n, rng);
        double[] b = RandomMatrix(n, rng);
        MultiplyParallel(a, b, new double[n * n], n, 2);
    }
}
