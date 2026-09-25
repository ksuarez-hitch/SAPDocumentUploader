using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SAPDocumentUploader;

public class ServiceLayerClient : IDisposable
{ 
    private readonly HttpClient _http; 
    private readonly CookieContainer _cookies; 
    private readonly string _baseUrl;

    public ServiceLayerClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _cookies = new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = _cookies, UseCookies = true };
        _http = new HttpClient(handler) { BaseAddress = new Uri(_baseUrl) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<bool> LoginAsync(string user, string password, string companyDB)
    {
        var payload = new JObject
        {
            ["UserName"] = user,
            ["Password"] = password,
            ["CompanyDB"] = companyDB
        };

        var resp = await _http.PostAsync("/b1s/v1/Login", new StringContent(payload.ToString(), Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode) return false;
        // Cookies are stored in CookieContainer automatically.
        return true;
    }

    public async Task LogoutAsync()
    {
        try { await _http.PostAsync("/b1s/v1/Logout", null); } catch { }
    }

    public async Task<(bool Success, string Response)> PostDocumentAsync(string endpoint, JObject document)
    {
        var content = new StringContent(document.ToString(), Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.PostAsync($"/b1s/v1/{endpoint}", content);
            var text = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                return (true, text);
            }
            else
            {
                return (false, $"HTTP {(int)resp.StatusCode}: {text}");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void Dispose()
    {
        _http?.Dispose();
    }
}
