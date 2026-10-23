using System.Text;

namespace Juice;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--dump"))
            return Dump();
        if (args.Contains("--listen"))
        {
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            KeychronCodes.Watch(output, TimeSpan.FromMinutes(3));
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\Juice", out bool firstInstance);
        if (!firstInstance)
            return 0;

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Write($"Fehler: {e.Exception}");
        Log.Write($"Start {Environment.ProcessPath}");
        Application.Run(new TrayApp());
        return 0;
    }

    /// <summary>Fragt einmal ab und schreibt das Ergebnis auf die Standardausgabe (zum Testen).</summary>
    static int Dump()
    {
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var result = Poller.Poll();
        if (result.CorsairBlockedBy is not null)
            output.WriteLine($"Corsair pausiert, solange {result.CorsairBlockedBy} läuft");
        foreach (var r in result.Readings)
        {
            string value = r.Percent is int percent ? $"{percent} %" : "-";
            output.WriteLine($"{r.Source,-9} {r.Name}: {value} {r.StateText} {r.Note}".TrimEnd());
        }
        if (result.Readings.Count == 0)
            output.WriteLine("Keine Geräte gefunden.");
        return 0;
    }
}
