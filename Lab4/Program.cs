using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Lab4;

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
/// Задача 6. Вычисление значения (Σx + Σy + Σz) / (3N) методом функциональной декомпозиции:
/// три потока считают суммы по x, y и z и добавляют их в общую (глобальную) сумму.
/// Обращение к глобальной сумме защищено критической секцией (lock).
/// Режимы:
///   critical - критическая секция на каждое слагаемое (прямой аналог omp critical внутри цикла);
///   local    - каждый поток копит локальную сумму и входит в критическую секцию один раз;
///   race     - без синхронизации (демонстрация гонки данных, результат может быть неверным).
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

        if (args.Length < 1)
        {
            PrintUsage();
            return 1;
        }

        string path = args[0];
        string mode = args.Length >= 2 ? args[1].ToLowerInvariant() : "all";
        if (mode is not ("critical" or "local" or "race" or "all"))
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

        int n = points.Count;
        if (n == 0)
        {
            Console.Error.WriteLine("Файл не содержит точек.");
            return 1;
        }

        Console.WriteLine($"Файл: {path}, точек: {n}");

        // Эталон: последовательное вычисление по формуле (Σx + Σy + Σz) / (3N).
        double sequentialSum = Sum(points.X) + Sum(points.Y) + Sum(points.Z);
        double expected = sequentialSum / (3.0 * n);
        Console.WriteLine($"Последовательно: сумма = {sequentialSum:F6}, результат = {expected:F9}");

        bool criticalAndLocalOk = true;
        foreach (string m in new[] { "critical", "local", "race" })
        {
            if (mode != "all" && mode != m)
            {
                continue;
            }

            var sw = Stopwatch.StartNew();
            double total = ComputeGlobalSum(points, m);
            sw.Stop();

            double result = total / (3.0 * n);
            bool match = Math.Abs(result - expected) <= 1e-9 * Math.Max(1.0, Math.Abs(expected));
            Console.WriteLine(
                $"Режим {m,-8}: сумма = {total:F6}, результат = {result:F9}, время = {sw.Elapsed.TotalMilliseconds:F3} мс, совпадает: {match}");

            if (m != "race")
            {
                criticalAndLocalOk &= match;
            }
        }

        if (mode is "race" or "all")
        {
            Console.WriteLine("Примечание: в режиме race результат не гарантирован; для малых N гонка может не проявиться.");
        }

        Console.WriteLine($"OK: {criticalAndLocalOk}");
        return criticalAndLocalOk ? 0 : 2;
    }

    /// <summary>Три потока (по одному на координату) наполняют общую сумму total.</summary>
    private static double ComputeGlobalSum(Points p, string mode)
    {
        double total = 0;          // глобальная сумма
        var sync = new object();   // объект критической секции
        double[][] parts = { p.X, p.Y, p.Z };
        var workers = new Thread[parts.Length];

        for (int t = 0; t < parts.Length; t++)
        {
            double[] data = parts[t];
            workers[t] = new Thread(() =>
            {
                switch (mode)
                {
                    case "critical":
                        foreach (double v in data)
                        {
                            lock (sync)
                            {
                                total += v;
                            }
                        }

                        break;

                    case "local":
                    {
                        double local = 0;
                        foreach (double v in data)
                        {
                            local += v;
                        }

                        lock (sync)
                        {
                            total += local;
                        }

                        break;
                    }

                    case "race":
                        foreach (double v in data)
                        {
                            total += v; // гонка данных: чтение-изменение-запись без синхронизации
                        }

                        break;
                }
            });
            workers[t].Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        return total;
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
        Console.Error.WriteLine("  dotnet run -c Release -- <файл> [critical|local|race|all]");
        Console.Error.WriteLine("  dotnet run -c Release -- gen <файл> <количество_точек>");
    }
}
