using System.Globalization;
using System.Text;

namespace Lab1.Task1;

/// <summary>
/// Задача 1. Каждый поток печатает свой идентификатор, общее число потоков и "Hello World".
/// Аналог: #pragma omp parallel + omp_get_thread_num() + omp_get_num_threads().
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length < 1 || !int.TryParse(args[0], out int threads) || threads <= 0)
        {
            Console.Error.WriteLine("Использование: dotnet run -c Release -- <число_потоков> [размер_задачи]");
            return 1;
        }

        // Параллельная область: создаём threads потоков и ждём их завершения (неявный барьер).
        var workers = new Thread[threads];
        for (int i = 0; i < threads; i++)
        {
            int id = i; // своя копия для каждого потока
            workers[i] = new Thread(() =>
                Console.WriteLine($"Поток {id} из {threads}: Hello World"));
            workers[i].Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        return 0;
    }
}
