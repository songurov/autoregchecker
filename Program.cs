using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenAI;
using OpenAI.Chat;

const string CheckerUrl         = "https://e-services.md/public/calc/RegNumberChecker.php";
const string CaptchaUrl         = "https://e-services.md/public/calc/captcha/captcha_image.php";
const string RefererUrl         = "https://e-services.md/ro/content/verifisa-disponibilitatea-numarului-de-inmatriculare";
const string BaseUrl            = "https://e-services.md";
const string ProxiesAllFile     = "proxies_all.txt";
const string ProxiesWorkingFile = "proxies_working.txt";

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== Verificare Range Numere de Înmatriculare ===");
Console.WriteLine("              e-services.md");
Console.WriteLine("================================================\n");

// ── Configurare ───────────────────────────────────────────────────────────────

Console.Write("Prefix (ex: BOS, AB, CJ): ");
string prefix = (Console.ReadLine() ?? "").Trim().ToUpper();
if (string.IsNullOrEmpty(prefix)) { Console.WriteLine("Prefix gol. Ieșire."); return; }

Console.Write("Start (ex: 1  sau  001  sau  01): ");
string startStr = (Console.ReadLine() ?? "").Trim();
Console.Write("End   (ex: 10 sau  999  sau  99): ");
string endStr   = (Console.ReadLine() ?? "").Trim();

if (!int.TryParse(startStr, out int startNum) || !int.TryParse(endStr, out int endNum) || startNum > endNum)
{ Console.WriteLine("Interval invalid. Ieșire."); return; }

int padWidth = startStr.Length > 1 && startStr[0] == '0' ? startStr.Length : 0;

Console.Write("Tip placuță [1=Standard, 2=Personalizat, 0=Null] (implicit 1): ");
string signDestRaw = Console.ReadLine()?.Trim() ?? "1";
string signDest    = signDestRaw == "2" ? "2" : signDestRaw == "0" ? "" : "1";

Console.Write("Delay între cereri în secunde (implicit 2): ");
string delayInput = Console.ReadLine()?.Trim() ?? "";
int delayMs = (int.TryParse(delayInput, out int d) && d >= 0 ? d : 2) * 1000;

int total    = endNum - startNum + 1;
int maxRetry = 3;

string fmtS = padWidth > 0 ? startNum.ToString().PadLeft(padWidth, '0') : startStr;
string fmtE = padWidth > 0 ? endNum.ToString().PadLeft(padWidth, '0')   : endStr;
Console.WriteLine($"\nRange: {prefix}{fmtS} → {prefix}{fmtE}  ({total} numere)\n");

// ── OpenAI key ────────────────────────────────────────────────────────────────

var config = LoadConfig();
string apiKey = config.OpenAiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";

if (string.IsNullOrEmpty(apiKey))
{
    Console.Write("OPENAI_API_KEY: ");
    apiKey = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(apiKey))
    {
        SaveConfig(config with { OpenAiKey = apiKey });
        Console.WriteLine("      Cheia salvată în config.json.");
    }
}
else
{
    Console.WriteLine($"OpenAI key: {MaskKey(apiKey)}  (din {(config.OpenAiKey != null ? "config.json" : "env")})");
}

// ── Proxy list ────────────────────────────────────────────────────────────────

var (proxies, untestedProxies) = LoadProxies();

Console.Write("\nDescarci listă nouă de proxy-uri din internet? [y/N]: ");
if ((Console.ReadLine()?.Trim().ToLower() ?? "") == "y")
{
    Console.WriteLine("Descarc proxy-uri din surse publice...");
    var fresh = await FetchPublicProxies();
    if (fresh.Count > 0)
    {
        SaveAllProxiesFile(fresh);
        var workingSet  = new HashSet<string?>(proxies.Where(p => p != null));
        var newUntested = fresh.Where(p => p != null && !workingSet.Contains(p)).Cast<string?>().ToList();
        int nullIdx = proxies.IndexOf(null);
        proxies.InsertRange(nullIdx >= 0 ? nullIdx : proxies.Count, newUntested);
        foreach (var p in newUntested)
            if (!untestedProxies.Contains(p)) untestedProxies.Add(p);
        Console.WriteLine($"Descărcate {fresh.Count} proxy-uri → {ProxiesAllFile}.");
        Console.WriteLine($"  {proxies.Count(p => p != null)} total | {untestedProxies.Count} de testat în fundal");
    }
    else
    {
        Console.WriteLine("Nu s-au putut descărca proxy-uri. Continui cu lista existentă.");
    }
}

if (proxies.Count == 1 && proxies[0] == null)
{
    Console.Write("Proxy manual (socks5://ip:port sau http://...), ENTER = direct: ");
    string inp = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(inp)) proxies.Insert(0, inp);
}

int knownWorking = proxies.Count(p => p != null) - untestedProxies.Count;
Console.WriteLine($"Proxy-uri: {proxies.Count(p => p != null)} total ({knownWorking} verificate, {untestedProxies.Count} în testare fundal) + direct");

// ── Sesiune inițială ──────────────────────────────────────────────────────────

int proxyIdx    = 0;
HttpClient http = null!;
CookieContainer jar = null!;

while (proxyIdx < proxies.Count)
{
    (http, jar) = CreateHttpClient(proxies[proxyIdx]);
    if (await InitSession(http, jar)) break;
    http.Dispose();
    Console.WriteLine("      Sar la proxy următor...");
    proxyIdx++;
}

if (proxyIdx >= proxies.Count) { Console.WriteLine("Niciun proxy funcțional. Ieșire."); return; }

string? currentProxy = proxies[proxyIdx];

// ── Fundal: testare proxy-uri netestate ───────────────────────────────────────

var freshWorking = new ConcurrentQueue<string?>();
using var bgCts  = new CancellationTokenSource();
var bgTask = Task.Run(() => TestProxiesInBackground(untestedProxies, freshWorking, bgCts.Token));

// ── Batch loop ────────────────────────────────────────────────────────────────

var results = new List<PlateResult>();
Console.WriteLine($"\n{"Nr",-5} {"Proxy",-22} {"Număr",-12} {"Captcha",-8} {"Rezultat"}");
Console.WriteLine(new string('-', 70));

for (int i = startNum; i <= endNum; i++)
{
    string plate = prefix + (padWidth > 0 ? i.ToString().PadLeft(padWidth, '0') : i.ToString());
    PlateResult pr;

    while (true)
    {
        pr = await CheckPlate(http, apiKey, plate, signDest, maxRetry);

        if (pr.RawResponse is "network_timeout" or "network_fail")
        {
            Console.WriteLine($"      ↳ Block/timeout pe: {ProxyLabel(currentProxy)}");
            http.Dispose();

            bool found = false;
            while (!found)
            {
                string? candidate;
                if (freshWorking.TryDequeue(out var bgProxy))
                {
                    candidate = bgProxy;
                    Console.WriteLine($"      ↳ Candidat fundal: {ProxyLabel(candidate)}");
                }
                else
                {
                    proxyIdx++;
                    if (proxyIdx >= proxies.Count) break;
                    candidate = proxies[proxyIdx];
                }

                (http, jar) = CreateHttpClient(candidate);
                if (await InitSession(http, jar))
                {
                    currentProxy = candidate;
                    found = true;
                }
                else
                    http.Dispose();
            }

            if (!found) { Console.WriteLine("      ✗ Toate proxy-urile epuizate. Oprire."); goto Done; }
            Console.WriteLine($"      ↳ Activ: {ProxyLabel(currentProxy)}");
        }
        else break;
    }

    results.Add(pr);
    PrintRow(pr, results.Count, currentProxy);
    if (i < endNum && delayMs > 0) await Task.Delay(delayMs);
}

Done:
bgCts.Cancel();
try { await bgTask; } catch { }
http.Dispose();

// ── Retry automat erori ───────────────────────────────────────────────────────

var errorIdx = results.Select((r, i) => (r, i)).Where(x => x.r.Status == PlateStatus.Error).ToList();
if (errorIdx.Count > 0)
{
    Console.WriteLine($"\n[RETRY] {errorIdx.Count} erori reprocesate automat...\n");
    var (http2, jar2) = CreateHttpClient(currentProxy);
    if (await InitSession(http2, jar2))
    {
        foreach (var (old, idx) in errorIdx)
        {
            var newR = await CheckPlate(http2, apiKey, old.Plate, signDest, maxRetry);
            results[idx] = newR;
            PrintRow(newR, idx + 1, currentProxy, isRetry: true);
            if (delayMs > 0) await Task.Delay(delayMs);
        }
    }
    http2.Dispose();
}

// ── Sumar + raport ────────────────────────────────────────────────────────────

int cntA = results.Count(r => r.Status == PlateStatus.Available);
int cntO = results.Count(r => r.Status == PlateStatus.Occupied);
int cntE = results.Count(r => r.Status == PlateStatus.Error);

Console.WriteLine($"\n{"=",70}");
Console.WriteLine($"SUMAR: {results.Count} verificate | {cntA} disponibile | {cntO} ocupate | {cntE} erori");

string reportPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
    $"raport_{prefix}_{fmtS}_{fmtE}_{DateTime.Now:yyyyMMdd_HHmmss}.html");

File.WriteAllText(reportPath, GenerateHtml(results, prefix, fmtS, fmtE, signDest), Encoding.UTF8);
Console.WriteLine($"Raport: {reportPath}");

try { Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true }); }
catch { /* ignoră */ }

// ═════════════════════════════════════════════════════════════════════════════
// HTTP / Proxy
// ═════════════════════════════════════════════════════════════════════════════

static (List<string?>, List<string?>) LoadProxies()
{
    var working    = LoadProxyFile(ProxiesWorkingFile);
    var workingSet = new HashSet<string?>(working.Where(p => p != null));
    if (working.Count > 0)
        Console.WriteLine($"Proxy-uri verificate: {working.Count} din {ProxiesWorkingFile}");

    var all      = LoadProxyFile(ProxiesAllFile);
    var untested = all.Where(p => p != null && !workingSet.Contains(p)).Cast<string?>().ToList();
    if (all.Count > 0)
        Console.WriteLine($"Proxy-uri totale în {ProxiesAllFile}: {all.Count} ({untested.Count} netestate)");

    var combined = new List<string?>(working);
    combined.AddRange(untested);

    string envP = Environment.GetEnvironmentVariable("HTTPS_PROXY")
               ?? Environment.GetEnvironmentVariable("HTTP_PROXY") ?? "";
    if (!string.IsNullOrEmpty(envP) && !combined.Contains(envP))
        combined.Insert(0, envP);

    combined.Add(null);
    return (combined, untested);
}

static List<string?> LoadProxyFile(string filename)
{
    var list = new List<string?>();
    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
    if (!File.Exists(path)) return list;
    foreach (string line in File.ReadAllLines(path))
    {
        string t = line.Trim();
        if (!string.IsNullOrEmpty(t) && !t.StartsWith('#'))
            list.Add(t);
    }
    return list;
}

static (HttpClient, CookieContainer) CreateHttpClient(string? proxyUri)
{
    var jar     = new CookieContainer();
    var handler = new SocketsHttpHandler
    {
        CookieContainer   = jar,
        UseCookies        = true,
        AllowAutoRedirect = true,
        ConnectTimeout    = TimeSpan.FromSeconds(20)
    };

    if (!string.IsNullOrEmpty(proxyUri))
    {
        handler.Proxy    = new WebProxy(new Uri(proxyUri));
        handler.UseProxy = true;
    }

    var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    client.DefaultRequestHeaders.Add("User-Agent",         "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Add("Accept-Language",    "en-GB,en-US;q=0.9,en;q=0.8,ru;q=0.7");
    client.DefaultRequestHeaders.Add("sec-ch-ua",          "\"Chromium\";v=\"148\", \"Google Chrome\";v=\"148\", \"Not/A)Brand\";v=\"99\"");
    client.DefaultRequestHeaders.Add("sec-ch-ua-mobile",   "?0");
    client.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"macOS\"");
    client.DefaultRequestHeaders.Add("sec-gpc",            "1");
    return (client, jar);
}

static async Task<bool> InitSession(HttpClient http, CookieContainer jar)
{
    try
    {
        var req = new HttpRequestMessage(HttpMethod.Get, RefererUrl);
        req.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        var resp = await http.SendAsync(req);
        jar.Add(new Uri(BaseUrl), new Cookie("has_js", "1"));
        Console.WriteLine($"Sesiune OK (HTTP {(int)resp.StatusCode})");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Sesiune EȘUATĂ: {ex.Message[..Math.Min(60, ex.Message.Length)]}");
        return false;
    }
}

static string ProxyLabel(string? proxy) =>
    proxy is null ? "direct" : Regex.Replace(proxy, @"//[^:@]+:[^@]+@", "//***:***@");

// ═════════════════════════════════════════════════════════════════════════════
// Fundal: testare proxy-uri netestate
// ═════════════════════════════════════════════════════════════════════════════

static async Task TestProxiesInBackground(
    List<string?> toTest, ConcurrentQueue<string?> freshWorking, CancellationToken ct)
{
    int found = 0;
    foreach (var proxy in toTest)
    {
        if (ct.IsCancellationRequested) break;
        if (proxy == null) continue;

        if (await TestProxyQuick(proxy, ct))
        {
            found++;
            freshWorking.Enqueue(proxy);
            AppendToWorkingFile(proxy);
            Console.WriteLine($"[BG] ✓ ({found}) {ProxyLabel(proxy)}");
        }

        try { await Task.Delay(200, ct); }
        catch (OperationCanceledException) { break; }
    }

    if (!ct.IsCancellationRequested)
        Console.WriteLine($"[BG] Gata: {found} proxy-uri funcționale adăugate în {ProxiesWorkingFile}.");
}

static async Task<bool> TestProxyQuick(string proxyUri, CancellationToken ct)
{
    try
    {
        var (http, _) = CreateHttpClient(proxyUri);
        http.Timeout = TimeSpan.FromSeconds(10);
        using (http)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            linked.CancelAfter(TimeSpan.FromSeconds(10));
            var resp = await http.GetAsync(RefererUrl, linked.Token);
            return (int)resp.StatusCode < 500;
        }
    }
    catch { return false; }
}

static void AppendToWorkingFile(string proxy)
{
    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ProxiesWorkingFile);
    File.AppendAllLines(path, new[] { proxy });
}

// ═════════════════════════════════════════════════════════════════════════════
// Verificare placuță
// ═════════════════════════════════════════════════════════════════════════════

static async Task<PlateResult> CheckPlate(
    HttpClient http, string apiKey, string plate, string signDest, int maxRetry)
{
    for (int attempt = 1; attempt <= maxRetry; attempt++)
    {
        byte[]? captchaBytes = await FetchWithRetry(http, CaptchaUrl);
        if (captchaBytes is null)
            return new PlateResult(plate, "?", "network_timeout", PlateStatus.Error, "Timeout rețea");

        string captcha = await ReadCaptchaWithOpenAI(apiKey, captchaBytes);

        if (!Regex.IsMatch(captcha, @"^\d{4}$"))
        {
            if (attempt < maxRetry) continue;
            return new PlateResult(plate, "?", "captcha_fail", PlateStatus.Error, "Eroare captcha");
        }

        var fields = new Dictionary<string, string>
        {
            ["numbermask"]       = plate,
            ["signdestination"]  = signDest,
            ["carplatelocality"] = "",
            ["captcha"]          = captcha
        };
        string payload = string.Join("&", fields.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        var postReq = new HttpRequestMessage(HttpMethod.Post, CheckerUrl)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        postReq.Headers.Add("Accept",           "text/plain, */*; q=0.01");
        postReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
        postReq.Headers.Add("Referer",          RefererUrl);
        postReq.Headers.Add("Origin",           BaseUrl);
        postReq.Headers.Add("Sec-Fetch-Dest",   "empty");
        postReq.Headers.Add("Sec-Fetch-Mode",   "cors");
        postReq.Headers.Add("Sec-Fetch-Site",   "same-origin");

        string raw;
        try
        {
            raw = (await (await http.SendAsync(postReq)).Content.ReadAsStringAsync()).Trim();
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            return new PlateResult(plate, captcha, "network_fail", PlateStatus.Error, "Rețea căzută");
        }

        if (raw.Equals("error0", StringComparison.OrdinalIgnoreCase) && attempt < maxRetry)
            continue;

        var (status, text, cost) = ClassifyResult(raw);
        return new PlateResult(plate, captcha, raw, status, text, cost, payload);
    }

    return new PlateResult(plate, "?", "max_retry", PlateStatus.Error, "Max retry depășit");
}

static async Task<byte[]?> FetchWithRetry(HttpClient http, string url, int maxAttempts = 2)
{
    for (int i = 1; i <= maxAttempts; i++)
    {
        try { return await http.GetByteArrayAsync(url); }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException or HttpRequestException)
        {
            if (i == maxAttempts) return null;
            await Task.Delay(i * 2000);
        }
    }
    return null;
}

static (PlateStatus, string, decimal) ClassifyResult(string raw)
{
    if (raw.TrimStart().StartsWith('<'))
    {
        try
        {
            var doc    = XDocument.Parse(raw);
            string res = doc.Root?.Element("result")?.Value ?? "";
            decimal cost = decimal.TryParse(doc.Root?.Element("number_cost")?.Value, out var c) ? c : 0;
            return res.ToLowerInvariant() switch
            {
                "number_free" => (PlateStatus.Available, "Disponibil",   cost),
                "number_busy" => (PlateStatus.Occupied,  "Ocupat",       cost),
                "error"       => (PlateStatus.Occupied,  "Indisponibil", cost),
                _             => (PlateStatus.Error,     $"XML necunoscut: {res}", cost)
            };
        }
        catch { /* fallback */ }
    }

    return raw.ToLowerInvariant() switch
    {
        "error0"                => (PlateStatus.Error,     "Captcha expirat", 0),
        "error1"                => (PlateStatus.Error,     "Format invalid",  0),
        "ok" or "ok1" or "free" => (PlateStatus.Available, "Disponibil",      0),
        "used" or "0" or "no"   => (PlateStatus.Occupied,  "Ocupat",          0),
        _                       => (PlateStatus.Error,     $"Necunoscut ({raw})", 0)
    };
}

static void PrintRow(PlateResult r, int idx, string? proxy, bool isRetry = false)
{
    string tag   = isRetry ? "[R]" : "   ";
    string icon  = r.Status switch { PlateStatus.Available => "✓", PlateStatus.Occupied => "✗", _ => "!" };
    string price = r.Cost > 0 ? $"  {r.Cost:N0} MDL" : "";
    string plbl  = ProxyLabel(proxy)[..Math.Min(20, ProxyLabel(proxy).Length)];
    Console.WriteLine($"{tag}{idx,-4} {plbl,-22} {r.Plate,-12} {r.Captcha,-8} {icon} {r.StatusText}{price}");
}

// ═════════════════════════════════════════════════════════════════════════════
// OpenAI captcha
// ═════════════════════════════════════════════════════════════════════════════

static async Task<string> ReadCaptchaWithOpenAI(string apiKey, byte[] imageBytes)
{
    try
    {
        var chat = new OpenAIClient(apiKey).GetChatClient("gpt-4o-mini");
        var msgs = new List<ChatMessage>
        {
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(
                    "This is a CAPTCHA with exactly 4 digits (0-9). " +
                    "Digits may be handwritten or stylized. Reply with ONLY the 4 digits."),
                ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imageBytes), "image/png"))
        };
        var resp  = await chat.CompleteChatAsync(msgs, new ChatCompletionOptions { MaxOutputTokenCount = 10 });
        string clean = Regex.Replace(resp.Value.Content[0].Text.Trim(), @"\D", "");
        return clean.Length == 4 ? clean : string.Empty;
    }
    catch { return string.Empty; }
}

// ═════════════════════════════════════════════════════════════════════════════
// Proxy fetcher
// ═════════════════════════════════════════════════════════════════════════════

static async Task<List<string?>> FetchPublicProxies()
{
    var sources = new[]
    {
        ("socks5", "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks5.txt"),
        ("socks5", "https://raw.githubusercontent.com/ShiftyTR/Proxy-List/master/socks5.txt"),
        ("socks5", "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/socks5.txt"),
        ("socks5", "https://raw.githubusercontent.com/hookzof/socks5_list/master/proxy.txt"),
        ("socks5", "https://api.proxyscrape.com/v2/?request=displayproxies&protocol=socks5&timeout=5000&country=all"),
        ("http",   "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt"),
        ("http",   "https://raw.githubusercontent.com/ShiftyTR/Proxy-List/master/http.txt"),
        ("http",   "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/http.txt"),
        ("http",   "https://raw.githubusercontent.com/clarketm/proxy-list/master/proxy-list-raw.txt"),
        ("http",   "https://api.proxyscrape.com/v2/?request=displayproxies&protocol=http&timeout=5000&country=all"),
        ("http",   "https://raw.githubusercontent.com/mertguvencli/http-proxy-list/main/proxy-list/data.txt"),
        ("http",   "https://raw.githubusercontent.com/almroot/proxylist/master/list.txt"),
    };

    using var fetcher = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    fetcher.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

    var all   = new HashSet<string?>();
    var tasks = sources.Select(async s =>
    {
        var (scheme, url) = s;
        try
        {
            string body = await fetcher.GetStringAsync(url);
            return body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => Regex.IsMatch(l, @"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}:\d{2,5}$"))
                .Select(l => (string?)$"{scheme}://{l}")
                .ToList();
        }
        catch { return new List<string?>(); }
    });

    foreach (var batch in await Task.WhenAll(tasks))
        foreach (var p in batch) all.Add(p);

    var list = all.ToList();
    var rng  = new Random();
    for (int i = list.Count - 1; i > 0; i--)
    {
        int j = rng.Next(i + 1);
        (list[i], list[j]) = (list[j], list[i]);
    }
    return list;
}

static void SaveAllProxiesFile(List<string?> proxies)
{
    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ProxiesAllFile);
    var lines   = new List<string>
    {
        $"# Generat automat la {DateTime.Now:dd.MM.yyyy HH:mm:ss}",
        $"# {proxies.Count} proxy-uri (testate în fundal în timpul rulării)",
        ""
    };
    lines.AddRange(proxies.Where(p => p != null).Select(p => p!));
    File.WriteAllLines(path, lines);
}

// ═════════════════════════════════════════════════════════════════════════════
// Config
// ═════════════════════════════════════════════════════════════════════════════

static string ConfigPath() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

static AppConfig LoadConfig()
{
    try
    {
        string path = ConfigPath();
        if (File.Exists(path))
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfig();
    }
    catch { /* ignoră */ }
    return new AppConfig();
}

static void SaveConfig(AppConfig cfg) =>
    File.WriteAllText(ConfigPath(), JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));

static string MaskKey(string key) => key.Length > 8 ? key[..7] + "..." + key[^4..] : "***";

// ═════════════════════════════════════════════════════════════════════════════
// Raport HTML
// ═════════════════════════════════════════════════════════════════════════════

static string GenerateHtml(List<PlateResult> results, string prefix, string start, string end, string signDest)
{
    int cntA = results.Count(r => r.Status == PlateStatus.Available);
    int cntO = results.Count(r => r.Status == PlateStatus.Occupied);
    int cntE = results.Count(r => r.Status == PlateStatus.Error);
    string tip     = signDest switch { "1" => "Standard", "2" => "Personalizat", _ => "Null" };
    string genDate = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");

    var rows = new StringBuilder();
    for (int i = 0; i < results.Count; i++)
    {
        var r       = results[i];
        string css  = r.Status switch { PlateStatus.Available => "row-available", PlateStatus.Occupied => "row-occupied", _ => "row-error" };
        string icon = r.Status switch { PlateStatus.Available => "✓ Disponibil", PlateStatus.Occupied => "✗ Ocupat", _ => "⚠ Eroare" };
        string price   = r.Cost > 0 ? $"{r.Cost:N0} MDL" : "—";
        string dispNum = Regex.Replace(r.Plate, @"^([A-Za-z]+)(\d+)$", "$1 $2");
        string payDisp = string.IsNullOrEmpty(r.Payload) ? "—" : WebUtility.HtmlEncode(r.Payload);
        rows.AppendLine(
            $"<tr class=\"{css}\"><td>{i + 1}</td>" +
            $"<td><strong>{WebUtility.HtmlEncode(dispNum)}</strong></td>" +
            $"<td>{icon}</td>" +
            $"<td class=\"price\">{price}</td>" +
            $"<td class=\"payload\">{payDisp}</td>" +
            $"<td class=\"raw\">{WebUtility.HtmlEncode(r.RawResponse)}</td></tr>");
    }

    return $$"""
    <!DOCTYPE html>
    <html lang="ro">
    <head>
      <meta charset="UTF-8"/>
      <meta name="viewport" content="width=device-width, initial-scale=1"/>
      <title>Raport {{prefix}}{{start}}–{{end}}</title>
      <link rel="stylesheet" href="https://cdn.datatables.net/1.13.8/css/jquery.dataTables.min.css"/>
      <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; background: #f0f2f5; color: #1a1a2e; padding: 28px; }
        h1 { font-size: 1.6rem; margin-bottom: 4px; }
        .meta { color: #666; font-size: .875rem; margin-bottom: 20px; }
        .cards { display: flex; gap: 16px; margin-bottom: 24px; flex-wrap: wrap; }
        .card { flex: 1; min-width: 140px; border-radius: 10px; padding: 18px 22px; color: #fff; box-shadow: 0 2px 8px rgba(0,0,0,.15); }
        .card-total { background: #4a4e69; } .card-avail { background: #2d9e6b; }
        .card-occupied { background: #e05252; } .card-error { background: #3b82f6; }
        .card .num { font-size: 2rem; font-weight: 700; line-height: 1; }
        .card .lbl { font-size: .8rem; opacity: .85; margin-top: 4px; }
        .table-wrap { background: #fff; border-radius: 12px; padding: 20px; box-shadow: 0 2px 12px rgba(0,0,0,.08); }
        table.dataTable { width: 100% !important; border-collapse: collapse; }
        table.dataTable thead th { background: #1a1a2e; color: #fff; padding: 12px 14px; font-size: .85rem; letter-spacing: .05em; text-transform: uppercase; }
        table.dataTable tbody td { padding: 10px 14px; font-size: .92rem; border-bottom: 1px solid #f0f0f0; }
        table.dataTable tbody tr:last-child td { border-bottom: none; }
        .row-available td { background: #eafaf2; color: #1a5c3a; }
        .row-available td strong { color: #176b41; }
        .row-occupied td { background: #fdecea; color: #7c2020; }
        .row-occupied td strong { color: #9b2c2c; }
        .row-error td { background: #eff6ff; color: #1e3a6e; }
        .row-error td strong { color: #1d4ed8; }
        .price { font-weight: 600; white-space: nowrap; }
        .payload { font-family: monospace; font-size: .78rem; color: #555; word-break: break-all; }
        .raw { font-family: monospace; font-size: .78rem; color: #888; word-break: break-all; }
        .dataTables_wrapper .dataTables_filter input { border: 1px solid #ddd; border-radius: 6px; padding: 5px 10px; }
        .dataTables_wrapper .dataTables_length select { border: 1px solid #ddd; border-radius: 6px; padding: 4px 8px; }
        footer { margin-top: 20px; font-size: .75rem; color: #aaa; text-align: center; }
      </style>
    </head>
    <body>
      <h1>Raport Verificare — {{prefix}}{{start}} → {{prefix}}{{end}}</h1>
      <p class="meta">Tip: <strong>{{tip}}</strong> &nbsp;|&nbsp; Generat: {{genDate}}</p>
      <div class="cards">
        <div class="card card-total"><div class="num">{{results.Count}}</div><div class="lbl">Total</div></div>
        <div class="card card-avail"><div class="num">{{cntA}}</div><div class="lbl">Disponibile</div></div>
        <div class="card card-occupied"><div class="num">{{cntO}}</div><div class="lbl">Ocupate</div></div>
        <div class="card card-error"><div class="num">{{cntE}}</div><div class="lbl">Erori</div></div>
      </div>
      <div class="table-wrap">
        <table id="tbl" class="dataTable display" style="width:100%">
          <thead><tr><th>#</th><th>Număr</th><th>Status</th><th>Preț</th><th>Payload trimis</th><th>Răspuns brut</th></tr></thead>
          <tbody>{{rows}}</tbody>
        </table>
      </div>
      <footer>Generat automat de AutoRegChecker · e-services.md</footer>
      <script src="https://code.jquery.com/jquery-3.7.1.min.js"></script>
      <script src="https://cdn.datatables.net/1.13.8/js/jquery.dataTables.min.js"></script>
      <script>
        $(function() {
          $('#tbl').DataTable({
            pageLength: 50, order: [[0, 'asc']],
            language: { search: 'Caută:', lengthMenu: 'Afișează _MENU_ rânduri',
              info: 'Rândurile _START_–_END_ din _TOTAL_',
              paginate: { previous: '‹', next: '›' } }
          });
        });
      </script>
    </body>
    </html>
    """;
}

// ═════════════════════════════════════════════════════════════════════════════
// Tipuri
// ═════════════════════════════════════════════════════════════════════════════

enum PlateStatus { Available, Occupied, Error }

record PlateResult(string Plate, string Captcha, string RawResponse, PlateStatus Status, string StatusText, decimal Cost = 0, string Payload = "");

record AppConfig(string? OpenAiKey = null);
