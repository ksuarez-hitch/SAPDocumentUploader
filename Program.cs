using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Configuration.Json;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
namespace SAPDocumentUploader;
class Program
{
    static async Task<int> Main(string[] args)
    {
        string type;
        int quantity;
        bool interactive = args.Length < 2;

        if (args.Length >= 2)
        {
            type = args[0];
            if (!int.TryParse(args[1], out quantity) || quantity <= 0)
            {
                Console.WriteLine("Quantity debe ser un entero > 0");
                return 1;
            }
        }
        else
        {
            Console.WriteLine("CARGADOR DE DOCUMENTOS SAP\n");
            Console.Write("Ingrese el tipo de documento: \n");
            Console.Write("- Orden de Venta \n");
            Console.Write("- Factura \n");
            type = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(type))
            {
                Console.WriteLine("Tipo de documento no puede estar vacío");
                return 1;
            }

            Console.Write("\nIngrese la cantidad: ");
            var qtyInput = Console.ReadLine();
            if (!int.TryParse(qtyInput, out quantity) || quantity <= 0)
            {
                Console.WriteLine("Cantidad debe ser un entero > 0");
                return 1;
            }
        }

        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var baseUrl = config["BaseUrl"];
        var user = config["UserName"];
        var pass = config["Password"];
        var company = config["DBCompany"];

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(user))
        {
            Console.WriteLine("Config incompleta en appsettings.json");
            return 1;
        }

        string endpoint;
        if (type == "Orden de Venta")
        {
            endpoint = "Orders";
        }
        else if (type == "Factura")
        {
            endpoint = "Invoices";
        }
        else
        {
            Console.WriteLine($"Tipo no soportado: {type}");
            return 1;
        }

        // Cada envío en paralelo usa su propia sesión de Service Layer.
        int maxParallel = int.TryParse(config["MaxParallel"], out var mp) ? Math.Clamp(mp, 1, 16) : 4;
        int sessionCount = Math.Min(maxParallel, quantity);

        var allClients = Enumerable.Range(0, sessionCount).Select(_ => new ServiceLayerClient(baseUrl)).ToList();
        var logins = await Task.WhenAll(allClients.Select(c => c.LoginAsync(user, pass ?? "", company ?? "")));
        var clients = allClients.Where((_, idx) => logins[idx].Success).ToList();

        if (clients.Count == 0)
        {
            Console.WriteLine($"Error login Service Layer: {logins[0].Error}");
            allClients.ForEach(c => c.Dispose());
            return 1;
        }
        if (clients.Count < sessionCount)
        {
            var firstError = logins.First(l => !l.Success).Error;
            Console.WriteLine($"Aviso: solo {clients.Count} de {sessionCount} sesiones iniciaron ({firstError}). Se continúa con {clients.Count}.");
        }

        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var startTime = DateTime.Now;
        var logPath = Path.Combine(logDir, $"carga_{endpoint}_{startTime:yyyyMMdd_HHmmss}.csv");
        // Prefijo del NumAtCard de todos los documentos de esta carga, p.ej. CARGA-20260930153000-00001
        var batchId = $"CARGA-{startTime:yyyyMMddHHmmss}";

        try
        {
            using var log = new StreamWriter(logPath, false, new UTF8Encoding(true)) { AutoFlush = true };
            log.WriteLine("Ronda;Numero;Referencia;Estado;DocEntry;Intentos;Mensaje;FechaHora");
            Console.WriteLine($"\nLog: {logPath}");
            Console.WriteLine($"Referencia de la carga (NumAtCard): {batchId}-*");
            Console.WriteLine($"Envíos en paralelo: {clients.Count}\n");

            var totalWatch = Stopwatch.StartNew();
            int totalOk = 0;
            int totalUncertain = 0;
            int pending = quantity;
            int round = 1;
            int nextSeq = 1;

            while (true)
            {
                var (failed, uncertain) = await RunRoundAsync(clients, endpoint, pending, round, batchId, nextSeq, log);
                nextSeq += pending;
                totalOk += pending - failed - uncertain;
                totalUncertain += uncertain;

                if (failed == 0 || !interactive) break;

                Console.Write($"\n{failed} documentos fallaron. ¿Reintentar solo esos? (s/n): ");
                if (Console.ReadLine()?.Trim().ToLowerInvariant() != "s") break;
                pending = failed;
                round++;
            }

            Console.WriteLine("\n========== RESUMEN FINAL ==========");
            Console.WriteLine($"Solicitados: {quantity}");
            Console.WriteLine($"Creados:     {totalOk}");
            if (totalUncertain > 0)
            {
                Console.WriteLine($"Inciertos:   {totalUncertain} (revisar en SAP por NumAtCard antes de volver a cargarlos)");
            }
            Console.WriteLine($"Pendientes:  {quantity - totalOk - totalUncertain}");
            Console.WriteLine($"Duración:    {totalWatch.Elapsed:hh\\:mm\\:ss}");
            Console.WriteLine($"Referencia:  {batchId}-*");
            Console.WriteLine($"Log:         {logPath}");

            return totalOk == quantity ? 0 : 2;
        }
        finally
        {
            await Task.WhenAll(clients.Select(c => c.LogoutAsync()));
            allClients.ForEach(c => c.Dispose());
        }
    }

    // Crea "count" documentos (secuencia desde firstSeq) repartidos entre las sesiones,
    // y devuelve cuántos fallaron y cuántos quedaron inciertos.
    static async Task<(int Failed, int Uncertain)> RunRoundAsync(IReadOnlyList<ServiceLayerClient> clients, string endpoint, int count, int round,
        string batchId, int firstSeq, StreamWriter log)
    {
        const int progressEvery = 100;
        var watch = Stopwatch.StartNew();
        int ok = 0, failed = 0, uncertain = 0, done = 0;
        int next = 0; // último número de documento tomado por alguna sesión
        var sync = new object(); // protege contadores, consola y log

        if (round > 1) Console.WriteLine($"\n--- Ronda {round}: reintentando {count} documentos ---\n");

        async Task Worker(ServiceLayerClient client)
        {
            while (true)
            {
                int i = Interlocked.Increment(ref next);
                if (i > count) return;

                var reference = $"{batchId}-{firstSeq + i - 1:D5}";
                var result = await client.PostDocumentAsync(endpoint, BuildOrderJson(reference), reference);

                lock (sync)
                {
                    done++;
                    string status;
                    if (result.Success)
                    {
                        ok++;
                        status = "OK";
                        var note = string.IsNullOrEmpty(result.Message) ? "" : $" ({result.Message})";
                        Console.WriteLine($"[{done}/{count}] #{i} OK DocEntry={result.DocEntry}{note}");
                    }
                    else if (result.Uncertain)
                    {
                        uncertain++;
                        status = "INCIERTO";
                        Console.WriteLine($"[{done}/{count}] #{i} INCIERTO {reference}: {result.Message}");
                    }
                    else
                    {
                        failed++;
                        status = "ERROR";
                        Console.WriteLine($"[{done}/{count}] #{i} ERROR: {result.Message}");
                    }

                    log.WriteLine(string.Join(";",
                        round,
                        i,
                        reference,
                        status,
                        result.DocEntry ?? "",
                        result.Attempts,
                        CsvField(result.Message),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

                    if (done % progressEvery == 0 && done < count)
                    {
                        var remaining = TimeSpan.FromTicks(watch.Elapsed.Ticks / done * (count - done));
                        Console.WriteLine($"--- Progreso: {done}/{count} | OK {ok} | Errores {failed} | Inciertos {uncertain} | " +
                                          $"Transcurrido {watch.Elapsed:hh\\:mm\\:ss} | Restante aprox. {remaining:hh\\:mm\\:ss} ---");
                    }
                }
            }
        }

        await Task.WhenAll(clients.Select(Worker));

        var uncertainText = uncertain > 0 ? $", {uncertain} inciertos" : "";
        Console.WriteLine($"\nRonda {round}: {ok} OK, {failed} con error{uncertainText} ({watch.Elapsed:hh\\:mm\\:ss})");
        return (failed, uncertain);
    }

    static string CsvField(string value) =>
        value.Contains(';') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    static JObject BuildOrderJson(string reference)
    {
        var line = new JObject
        {
            ["ItemCode"] = null,
            ["ItemDescription"] = "Prueba",
            ["Quantity"] = 0.0,
            ["ShipDate"] = null,
            ["Price"] = 100000.0,
            ["AccountCode"] = "1-1-010-10-001",
            ["TaxCode"] = "IVA"
        };

        var doc = new JObject
        {
            ["DocType"] = "dDocument_Service",
            ["DocDate"] = DateTime.Now.ToString("yyyy-MM-dd"),
            ["DocDueDate"] = DateTime.Now.ToString("yyyy-MM-dd"),
            ["CardCode"] = "772447825P",
            ["CardName"] = "G1ntegration SPA",
            ["NumAtCard"] = reference,
            ["DocTotal"] = 119000.0,
            ["Comments"] = "Creada con cargador automatico",
            ["JournalMemo"] = "Pedidos de cliente - 772447825P",
            ["DocumentLines"] = new JArray { line }
        };

        return doc;
    }
}
