using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Lab3;

internal readonly record struct Centroid(double X, double Y, double Z)
{
    public override string ToString() => $"({X:F6}, {Y:F6}, {Z:F6})";
}

/// <summary>Координаты точек в трёх отдельных массивах (удобно для функциональной декомпозиции).</summary>
internal sealed class Points
{
    public Points(double[] x, double[] y, double[] z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double[] X { get; }
    public double[] Y { get; }
    public double[] Z { get; }
    public int Count => X.Length;
}

/// <summary>
/// Задача 5. Чтение координат точек (x, y, z) из файла и вычисление геометрического центра
/// (среднее по x, y и z). Две параллельные версии:
///   loop       - распараллеливание цикла (данные делятся между потоками);
///   functional - функциональная декомпозиция (отдельные потоки считают сумму x, y и z).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 3 && args[0] == "gen")
        {
            return Generate(args[1], args[2]);
        }

        if (args.Length < 2 || !int.TryParse(args[0], out int threads) || threads <= 0)
        {
            PrintUsage();
            return 1;
        }

        string path = args[1];
        string mode = args.Length >= 3 ? args[2].ToLowerInvariant() : "all";
        if (mode is not ("loop" or "functional" or "all"))
        {
            PrintUsage();
            return 1;
        }

        Points points;
        try
        {
            points = ReadPoints(path);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Ошибка чтения файла: {ex.Message}");
            return 1;
        }

        if (points.Count == 0)
        {
            Console.Error.WriteLine("Файл не содержит точек.");
            return 1;
        }

        Console.WriteLine($"Файл: {path}, точек: {points.Count}");

        var (reference, seqMs) = Measure(() => Sequential(points));
        Console.WriteLine($"Последовательно:                 центр = {reference}, время = {seqMs:F3} мс");

        bool allOk = true;

        if (mode is "loop" or "all")
        {
            var (result, ms) = Measure(() => ParallelLoop(points, threads));
            bool ok = Same(result, reference);
            allOk &= ok;
            Console.WriteLine($"Распараллеливание цикла ({threads} потоков): центр = {result}, время = {ms:F3} мс, совпадает: {ok}");
        }

        if (mode is "functional" or "all")
        {
            var (result, ms) = Measure(() => ParallelFunctional(points));
            bool ok = Same(result, reference);
            allOk &= ok;
            Console.WriteLine($"Функциональная декомпозиция (3 потока): центр = {result}, время = {ms:F3} мс, совпадает: {ok}");
        }

        Console.WriteLine($"OK: {allOk}");
        return allOk ? 0 : 2;
    }

    // ---------- вычисления ----------

    private static Centroid Sequential(Points p)
    {
        int n = p.Count;
        double sx = 0, sy = 0, sz = 0;
        for (int i = 0; i < n; i++)
        {
            sx += p.X[i];
            sy += p.Y[i];
            sz += p.Z[i];
        }

        return new Centroid(sx / n, sy / n, sz / n);
    }

    /// <summary>
    /// Версия 1: распараллеливание цикла. Точки делятся на непрерывные блоки,
    /// каждый поток считает частичные суммы по своему блоку, затем суммы объединяются (аналог reduction).
    /// </summary>
    private static Centroid ParallelLoop(Points p, int threads)
    {
        int n = p.Count;
        var partial = new (double X, double Y, double Z)[threads];
        var workers = new Thread[threads];

        int baseSize = n / threads;
        int rem = n % threads;
        int start = 0;

        for (int t = 0; t < threads; t++)
        {
            int id = t;
            int lo = start;
            int hi = lo + baseSize + (t < rem ? 1 : 0);
            start = hi;

            workers[t] = new Thread(() =>
            {
                double sx = 0, sy = 0, sz = 0; // локальные накопители потока
                for (int i = lo; i < hi; i++)
                {
                    sx += p.X[i];
                    sy += p.Y[i];
                    sz += p.Z[i];
                }

                partial[id] = (sx, sy, sz);
            });
            workers[t].Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        double totalX = 0, totalY = 0, totalZ = 0;
        foreach (var part in partial)
        {
            totalX += part.X;
            totalY += part.Y;
            totalZ += part.Z;
        }

        return new Centroid(totalX / n, totalY / n, totalZ / n);
    }

    /// <summary>
    /// Версия 2: функциональная декомпозиция. Задача разбивается не по данным, а по функциям:
    /// один поток считает среднее по x, второй по y, третий по z.
    /// </summary>
    private static Centroid ParallelFunctional(Points p)
    {
        int n = p.Count;
        double meanX = 0, meanY = 0, meanZ = 0;

        var tx = new Thread(() => meanX = Sum(p.X) / n);
        var ty = new Thread(() => meanY = Sum(p.Y) / n);
        var tz = new Thread(() => meanZ = Sum(p.Z) / n);

        tx.Start();
        ty.Start();
        tz.Start();
        tx.Join();
        ty.Join();
        tz.Join();

        return new Centroid(meanX, meanY, meanZ);
    }

    private static double Sum(double[] values)
    {
        double s = 0;
        foreach (double v in values)
        {
            s += v;
        }

        return s;
    }

    // ---------- вспомогательные методы ----------

    private static (Centroid Result, double Ms) Measure(Func<Centroid> compute)
    {
        compute(); // прогрев JIT, чтобы компиляция не попала в замер
        var sw = Stopwatch.StartNew();
        Centroid result = compute();
        sw.Stop();
        return (result, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Порядок сложения в потоках отличается, поэтому сравниваем с небольшой относительной погрешностью.</summary>
    private static bool Same(Centroid a, Centroid b) =>
        Close(a.X, b.X) && Close(a.Y, b.Y) && Close(a.Z, b.Z);

    private static bool Close(double a, double b) =>
        Math.Abs(a - b) <= 1e-9 * Math.Max(1.0, Math.Abs(b));

    /// <summary>Формат файла: по одной точке в строке, три числа через пробел. Строки с # и пустые пропускаются.</summary>
    private static Points ReadPoints(string path)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        var zs = new List<double>();
        int lineNo = 0;

        foreach (string raw in File.ReadLines(path))
        {
            lineNo++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3
                || !TryParse(parts[0], out double x)
                || !TryParse(parts[1], out double y)
                || !TryParse(parts[2], out double z))
            {
                throw new FormatException($"строка {lineNo}: ожидалось три числа \"x y z\".");
            }

            xs.Add(x);
            ys.Add(y);
            zs.Add(z);
        }

        return new Points(xs.ToArray(), ys.ToArray(), zs.ToArray());
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Режим gen: создаёт файл со случайными точками из диапазона [-100; 100].</summary>
    private static int Generate(string path, string countText)
    {
        if (!int.TryParse(countText, out int count) || count <= 0)
        {
            Console.Error.WriteLine("Количество точек должно быть положительным целым числом.");
            return 1;
        }

        var rng = new Random(42);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        for (int i = 0; i < count; i++)
        {
            writer.WriteLine($"{Coordinate(rng)} {Coordinate(rng)} {Coordinate(rng)}");
        }

        Console.WriteLine($"Записано точек: {count}, файл: {path}");
        return 0;
    }

    private static string Coordinate(Random rng) =>
        (rng.NextDouble() * 200.0 - 100.0).ToString("R", CultureInfo.InvariantCulture);

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Использование:");
        Console.Error.WriteLine("  dotnet run -c Release -- <число_потоков> <файл> [loop|functional|all]");
        Console.Error.WriteLine("  dotnet run -c Release -- gen <файл> <количество_точек>");
    }
}
