using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SAPDocumentUploader;

// Uncertain: no se pudo confirmar si SAP creó el documento; no debe reprocesarse sin revisar.
public record DocumentResult(bool Success, string? DocEntry, int Attempts, string Message, bool Uncertain = false);

public class ServiceLayerClient : IDisposable
{
    // Reintentos ante errores transitorios (red, timeout, 502/503/504, 408, 429).
    private const int MaxRetries = 3;

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;
    private readonly string _baseUrl;

    // Se guardan para poder renovar la sesión cuando expira.
    private string _user = "";
    private string _password = "";
    private string _companyDB = "";

    public ServiceLayerClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _cookies = new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = _cookies, UseCookies = true };
        _http = new HttpClient(handler) { BaseAddress = new Uri(_baseUrl) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<(bool Success, string Error)> LoginAsync(string user, string password, string companyDB)
    {
        _user = user;
        _password = password;
        _companyDB = companyDB;
        return await LoginAsync();
    }

    private async Task<(bool Success, string Error)> LoginAsync()
    {
        var payload = new JObject
        {
            ["UserName"] = _user,
            ["Password"] = _password,
            ["CompanyDB"] = _companyDB
        };

        try
        {
            using var resp = await _http.PostAsync("/b1s/v1/Login", new StringContent(payload.ToString(), Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode) return (true, "");
            // Cookies are stored in CookieContainer automatically.
            var text = await resp.Content.ReadAsStringAsync();
            return (false, $"HTTP {(int)resp.StatusCode}: {ExtractErrorMessage(text)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task LogoutAsync()
    {
        try { await _http.PostAsync("/b1s/v1/Logout", null); } catch { }
    }

    // "reference" debe ser el NumAtCard único del documento: se usa para verificar si un intento
    // fallido (timeout, error de red, 502/503/504) alcanzó a crearlo antes de reintentar.
    public async Task<DocumentResult> PostDocumentAsync(string endpoint, JObject document, string reference)
    {
        var body = document.ToString(Formatting.None);
        var lastError = "";
        var posts = 0;
        var mustCheck = false; // un intento previo pudo haber creado el documento

        for (int attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            if (mustCheck)
            {
                var (checkedOk, existing, checkError) = await FindByReferenceAsync(endpoint, reference);
                if (existing != null)
                {
                    return new DocumentResult(true, existing, posts, "Ya existía en SAP tras un intento fallido (no se duplicó)");
                }
                if (!checkedOk)
                {
                    // Sin poder verificar no se reenvía, para no duplicar.
                    lastError = $"{lastError} | Verificación falló: {checkError}";
                    if (attempt <= MaxRetries) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                    continue;
                }
                mustCheck = false;
            }

            try
            {
                posts++;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"/b1s/v1/{endpoint}")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                // Evita que SAP devuelva el documento completo en cada respuesta.
                req.Headers.Add("Prefer", "return-no-content");

                using var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var docEntry = await ExtractDocEntryAsync(resp);
                    return new DocumentResult(true, docEntry, posts, "");
                }

                var text = await resp.Content.ReadAsStringAsync();
                lastError = $"HTTP {(int)resp.StatusCode}: {ExtractErrorMessage(text)}";

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Sesión expirada: se renueva y se reintenta de inmediato.
                    var (logged, loginError) = await LoginAsync();
                    if (!logged)
                    {
                        return new DocumentResult(false, null, posts, $"{lastError} (re-login falló: {loginError})");
                    }
                    continue;
                }

                if (!IsTransient(resp.StatusCode))
                {
                    return new DocumentResult(false, null, posts, lastError);
                }
                mustCheck = true;
            }
            catch (TaskCanceledException)
            {
                lastError = "Timeout esperando respuesta de SAP";
                mustCheck = true;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
                mustCheck = true;
            }

            if (attempt <= MaxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }

        if (mustCheck)
        {
            var (checkedOk, existing, checkError) = await FindByReferenceAsync(endpoint, reference);
            if (existing != null)
            {
                return new DocumentResult(true, existing, posts, "Ya existía en SAP tras un intento fallido (no se duplicó)");
            }
            if (!checkedOk)
            {
                return new DocumentResult(false, null, posts,
                    $"{lastError} | No se pudo verificar si se creó: buscar NumAtCard = {reference}", Uncertain: true);
            }
        }

        return new DocumentResult(false, null, posts, lastError);
    }

    private async Task<(bool Ok, string? DocEntry, string Error)> FindByReferenceAsync(string endpoint, string reference)
    {
        var filter = Uri.EscapeDataString($"NumAtCard eq '{EscapeODataString(reference)}'");
        var (json, error) = await GetJsonAsync($"/b1s/v1/{endpoint}?$select=DocEntry&$filter={filter}");
        if (json == null) return (false, null, error);

        var first = (json["value"] as JArray)?.FirstOrDefault();
        return (true, first?["DocEntry"]?.ToString(), "");
    }

    // Documentos cuyo NumAtCard empieza con "prefix", ordenados por DocNum (sigue la paginación de Service Layer).
    public async Task<(bool Ok, List<(string Reference, string DocEntry, string DocNum)> Docs, string Error)> GetDocumentsByReferencePrefixAsync(
        string endpoint, string prefix)
    {
        var docs = new List<(string, string, string)>();
        var filter = Uri.EscapeDataString($"startswith(NumAtCard,'{EscapeODataString(prefix)}')");
        string? url = $"/b1s/v1/{endpoint}?$select=DocEntry,DocNum,NumAtCard&$filter={filter}&$orderby=DocNum";

        while (url != null)
        {
            var (json, error) = await GetJsonAsync(url, "odata.maxpagesize=500");
            if (json == null) return (false, docs, error);

            foreach (var item in json["value"] as JArray ?? new JArray())
            {
                docs.Add((item["NumAtCard"]?.ToString() ?? "", item["DocEntry"]?.ToString() ?? "", item["DocNum"]?.ToString() ?? ""));
            }

            // v1 usa "odata.nextLink" y v2 "@odata.nextLink"; el enlace es relativo a /b1s/v1/
            var next = (json["odata.nextLink"] ?? json["@odata.nextLink"])?.ToString();
            url = string.IsNullOrEmpty(next) ? null : next.StartsWith("/") ? next : $"/b1s/v1/{next}";
        }

        return (true, docs, "");
    }

    // GET que renueva la sesión una vez si expiró. Devuelve el JSON o el error.
    private async Task<(JObject? Json, string Error)> GetJsonAsync(string url, string? prefer = null, bool allowRelogin = true)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (prefer != null) req.Headers.Add("Prefer", prefer);

            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();

            if (resp.StatusCode == HttpStatusCode.Unauthorized && allowRelogin)
            {
                var (logged, loginError) = await LoginAsync();
                if (!logged) return (null, $"re-login falló: {loginError}");
                return await GetJsonAsync(url, prefer, allowRelogin: false);
            }
            if (!resp.IsSuccessStatusCode)
            {
                return (null, $"HTTP {(int)resp.StatusCode}: {ExtractErrorMessage(text)}");
            }

            return (JObject.Parse(text), "");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static string EscapeODataString(string value) => value.Replace("'", "''");

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static async Task<string?> ExtractDocEntryAsync(HttpResponseMessage resp)
    {
        // Con "return-no-content" SAP responde 204 y la ubicación del documento, p.ej. .../Orders(1234)
        var location = resp.Headers.Location?.ToString();
        if (location != null)
        {
            var match = Regex.Match(location, @"\((\d+)\)");
            if (match.Success) return match.Groups[1].Value;
        }

        // Si el servidor ignora el header y devuelve el documento, se toma el DocEntry del cuerpo.
        var text = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JObject.Parse(text)["DocEntry"]?.ToString(); }
        catch (JsonException) { return null; }
    }

    private static string ExtractErrorMessage(string text)
    {
        string message;
        try
        {
            var msg = JObject.Parse(text)["error"]?["message"];
            message = msg switch
            {
                JValue v => v.ToString(),
                JObject o => o["value"]?.ToString() ?? text,
                _ => text
            };
        }
        catch (JsonException)
        {
            message = text;
        }

        message = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return message.Length > 300 ? message[..300] + "..." : message;
    }

    public void Dispose()
    {
        _http?.Dispose();
    }
}
