using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Configuration.Json;
using System;
using System.Threading.Tasks;
namespace SAPDocumentUploader;
class Program
{
    static async Task<int> Main(string[] args)
    {
        string type;
        int quantity;

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

        var client = new ServiceLayerClient(baseUrl);

        bool logged = await client.LoginAsync(user, pass, company);
        if (!logged)
        {
            Console.WriteLine("Error login Service Layer");
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

        for (int i = 1; i <= quantity; i++)
        {
            var doc = BuildOrderJson();
            var (success, response) = await client.PostDocumentAsync(endpoint, doc);
            if (success)
            {
                Console.WriteLine($"[{i}/{quantity}] Creada correctamente: {response}");
            }
            else
            {
                Console.WriteLine($"[{i}/{quantity}] Error creando documento: {response}");
            }
        }

        await client.LogoutAsync();
        return 0;
    }

    static JObject BuildOrderJson()
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
            ["DocTotal"] = 119000.0,
            ["Comments"] = "Creada con cargador automatico",
            ["JournalMemo"] = "Pedidos de cliente - 772447825P",
            ["DocumentLines"] = new JArray { line }
        };

        return doc;
    }
}
