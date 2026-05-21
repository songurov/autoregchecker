using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenAI;
using OpenAI.Chat;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

const string CheckerUrl = "https://e-services.md/public/calc/RegNumberChecker.php";
const string CaptchaUrl = "https://e-services.md/public/calc/captcha/captcha_image.php";
const string RefererUrl = "https://e-services.md/ro/content/verifisa-disponibilitatea-numarului-de-inmatriculare";
const string BaseUrl = "https://e-services.md";

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== Verificare Range Numere de Înmatriculare ===");
Console.WriteLine("              e-services.md");
Console.WriteLine("================================================\n");

// ── Configurare ───────────────────────────────────────────────────────────────

Console.Write("Prefix (ex: BOS, AB, CJ): ");
string prefix = (Console.ReadLine() ?? "").Trim().ToUpper();
if (string.IsNullOrEmpty(prefix))
{
    Console.WriteLine("Prefix gol. Ieșire.");
    return;
}

Console.Write("Start (ex: 1  sau  001  sau  01): ");
string startStr = (Console.ReadLine() ?? "").Trim();
Console.Write("End   (ex: 10 sau  999  sau  99): ");
string endStr = (Console.ReadLine() ?? "").Trim();

if (!int.TryParse(startStr, out int startNum) || !int.TryParse(endStr, out int endNum) || startNum > endNum)
{
    Console.WriteLine("Interval invalid. Ieșire.");
    return;
}

int padWidth = startStr.Length > 1 && startStr[0] == '0' ? startStr.Length : 0;

Console.Write("Tip placuță [1=Standard, 2=Personalizat, 0=Null] (implicit 1): ");
string signDestRaw = Console.ReadLine()?.Trim() ?? "1";
string signDest = signDestRaw == "2" ? "2" : signDestRaw == "0" ? "" : "1";

Console.Write("Delay între cereri în secunde (implicit 2): ");
string delayInput = Console.ReadLine()?.Trim() ?? "";
int delayMs = (int.TryParse(delayInput, out int d) && d >= 0 ? d : 2) * 1000;

int total = endNum - startNum + 1;
int maxRetry = 3;

string fmtS = padWidth > 0 ? startNum.ToString().PadLeft(padWidth, '0') : startStr;
string fmtE = padWidth > 0 ? endNum.ToString().PadLeft(padWidth, '0') : endStr;
Console.WriteLine($"\nRange: {prefix}{fmtS} → {prefix}{fmtE}  ({total} numere)\n");

// ── OpenAI key (config.json → env → input) ────────────────────────────────────

var config = LoadConfig();
string apiKey = config.OpenAiKey
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ?? "";

if (string.IsNullOrEmpty(apiKey))
{
    Console.Write("OPENAI_API_KEY: ");
    apiKey = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(apiKey))
    {
        config = config with { OpenAiKey = apiKey };
        SaveConfig(config);
        Console.WriteLine("      Cheia salvată în config.json.");
    }
}
else
{
    Console.WriteLine($"OpenAI key: {MaskKey(apiKey)}  (din {(config.OpenAiKey != null ? "config.json" : "env")})");
}

// ── Proxy list ────────────────────────────────────────────────────────────────

var proxies = LoadProxies();

Console.Write("\nDescarci listă nouă de proxy-uri din internet? [y/N]: ");
if ((Console.ReadLine()?.Trim().ToLower() ?? "") == "y")
{
    Console.WriteLine("Descarc proxy-uri din surse publice...");
    var fresh = await FetchPublicProxies();
    if (fresh.Count > 0)
    {
        SaveProxyFile(fresh);
        proxies = fresh.Concat(new string?[] { null }).ToList();
        Console.WriteLine($"Descărcate {fresh.Count} proxy-uri → salvate în proxies.txt (vor fi testate on-the-fly).");
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

Console.WriteLine($"Proxy-uri disponibile: {proxies.Count - 1} + direct fallback");

// ── Sesiune inițială (găsește primul proxy funcțional) ────────────────────────

int proxyIdx = 0;
HttpClient http = null!;
CookieContainer jar = null!;

while (proxyIdx < proxies.Count)
{
    (http, jar) = CreateHttpClient(proxies[proxyIdx]);
    if (await InitSession(http, jar)) break;
    http.Dispose();
    Console.WriteLine($"      Sar la proxy următor...");
    proxyIdx++;
}

if (proxyIdx >= proxies.Count)
{
    Console.WriteLine("Niciun proxy funcțional. Ieșire.");
    return;
}

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
            Console.WriteLine($"      ↳ Block/timeout pe: {ProxyLabel(proxies[proxyIdx])}");
            http.Dispose();
            proxyIdx++;

            // Găsește următorul proxy care acceptă sesiunea
            bool found = false;
            while (proxyIdx < proxies.Count)
            {
                (http, jar) = CreateHttpClient(proxies[proxyIdx]);
                if (await InitSession(http, jar))
                {
                    found = true;
                    break;
                }

                http.Dispose();
                proxyIdx++;
            }

            if (!found)
            {
                Console.WriteLine("      ✗ Toate proxy-urile epuizate. Oprire.");
                goto Done;
            }

            Console.WriteLine($"      ↳ Activ: {ProxyLabel(proxies[proxyIdx])}");
        }
        else break;
    }

    results.Add(pr);
    PrintRow(pr, results.Count, proxies[proxyIdx]);
    if (i < endNum && delayMs > 0) await Task.Delay(delayMs);
}

Done:
http.Dispose();

// ── Retry automat erori ───────────────────────────────────────────────────────

var errorIdx = results.Select((r, i) => (r, i)).Where(x => x.r.Status == PlateStatus.Error).ToList();
if (errorIdx.Count > 0 && proxyIdx < proxies.Count)
{
    Console.WriteLine($"\n[RETRY] {errorIdx.Count} erori reprocesate automat...\n");
    var (http2, jar2) = CreateHttpClient(proxies[proxyIdx]);
    await InitSession(http2, jar2);

    foreach (var (old, idx) in errorIdx)
    {
        var newR = await CheckPlate(http2, apiKey, old.Plate, signDest, maxRetry);
        results[idx] = newR;
        PrintRow(newR, idx + 1, proxies[proxyIdx], isRetry: true);
        if (delayMs > 0) await Task.Delay(delayMs);
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

try
{
    Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
}
catch
{
    /* ignoră */
}

// ═════════════════════════════════════════════════════════════════════════════
// Proxy helpers
// ═════════════════════════════════════════════════════════════════════════════

static List<string?> LoadProxies()
{
    var list = new List<string?>();

    // 1. proxies.txt lângă executabil
    string proxyFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "proxies.txt");
    if (File.Exists(proxyFile))
    {
        foreach (string line in File.ReadAllLines(proxyFile))
        {
            string t = line.Trim();
            if (!string.IsNullOrEmpty(t) && !t.StartsWith('#'))
                list.Add(t);
        }

        if (list.Count > 0)
            Console.WriteLine($"Proxy-uri încărcate din proxies.txt: {list.Count}");
    }

    // 2. variabile de mediu
    string envP = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                  ?? Environment.GetEnvironmentVariable("HTTP_PROXY") ?? "";
    if (!string.IsNullOrEmpty(envP) && !list.Contains(envP))
        list.Add(envP);

    // 3. conexiune directă ca fallback final
    list.Add(null);
    return list;
}

static (HttpClient, CookieContainer) CreateHttpClient(string? proxyUri)
{
    var jar = new CookieContainer();
    var handler = new SocketsHttpHandler
    {
        CookieContainer = jar,
        UseCookies = true,
        AllowAutoRedirect = true,
        ConnectTimeout = TimeSpan.FromSeconds(20),
    };

    if (!string.IsNullOrEmpty(proxyUri))
    {
        handler.Proxy = new WebProxy(new Uri(proxyUri));
        handler.UseProxy = true;
    }

    var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    client.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Add("Accept-Language", "en-GB,en-US;q=0.9,en;q=0.8,ru;q=0.7");
    client.DefaultRequestHeaders.Add("sec-ch-ua",
        "\"Chromium\";v=\"148\", \"Google Chrome\";v=\"148\", \"Not/A)Brand\";v=\"99\"");
    client.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
    client.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"macOS\"");
    client.DefaultRequestHeaders.Add("sec-gpc", "1");

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
    proxy is null ? "direct" : Regex.Replace(proxy, @"//[^:@]+:[^@]+@", "//***:***@"); // ascunde parola


// ═════════════════════════════════════════════════════════════════════════════
// Logică verificare
// ═════════════════════════════════════════════════════════════════════════════

static async Task<PlateResult> CheckPlate(
    HttpClient http, string apiKey, string plate, string signDest, int maxRetry)
{
    for (int attempt = 1; attempt <= maxRetry; attempt++)
    {
        byte[]? captchaBytes = await FetchWithRetry(http, CaptchaUrl, maxAttempts: 2);
        if (captchaBytes is null)
            return new PlateResult(plate, "?", "network_timeout", PlateStatus.Error, "Timeout rețea");

        string captcha = !string.IsNullOrEmpty(apiKey)
            ? await ReadCaptchaWithOpenAI(apiKey, captchaBytes)
            : string.Empty;

        if (string.IsNullOrEmpty(captcha))
        {
            string rp = Path.Combine(Path.GetTempPath(), $"cap_{plate}_raw.png");
            string pp = Path.Combine(Path.GetTempPath(), $"cap_{plate}_proc.png");
            await File.WriteAllBytesAsync(rp, captchaBytes);
            PreprocessImage(rp, pp);
            captcha = TryTesseract(rp, pp);
        }

        if (!Regex.IsMatch(captcha, @"^\d{4}$"))
        {
            if (attempt < maxRetry) continue;
            return new PlateResult(plate, "?", "captcha_fail", PlateStatus.Error, "Eroare captcha");
        }

        var fields = new Dictionary<string, string>
        {
            ["numbermask"] = plate,
            ["signdestination"] = signDest,
            ["carplatelocality"] = "",
            ["captcha"] = captcha
        };
        string payload = string.Join("&", fields.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        var postReq = new HttpRequestMessage(HttpMethod.Post, CheckerUrl)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        postReq.Headers.Add("Accept", "text/plain, */*; q=0.01");
        postReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
        postReq.Headers.Add("Referer", RefererUrl);
        postReq.Headers.Add("Origin", BaseUrl);
        postReq.Headers.Add("Sec-Fetch-Dest", "empty");
        postReq.Headers.Add("Sec-Fetch-Mode", "cors");
        postReq.Headers.Add("Sec-Fetch-Site", "same-origin");

        string raw;
        try
        {
            var resp = await http.SendAsync(postReq);
            raw = (await resp.Content.ReadAsStringAsync()).Trim();
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
        try
        {
            return await http.GetByteArrayAsync(url);
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException or HttpRequestException)
        {
            if (i == maxAttempts) return null;
            await Task.Delay(i * 2000);
        }
    }

    return null;
}

static (PlateStatus status, string text, decimal cost) ClassifyResult(string raw)
{
    if (raw.TrimStart().StartsWith('<'))
    {
        try
        {
            var doc = XDocument.Parse(raw);
            string result = doc.Root?.Element("result")?.Value ?? "";
            decimal cost = decimal.TryParse(doc.Root?.Element("number_cost")?.Value, out var c) ? c : 0;
            return result.ToLowerInvariant() switch
            {
                "number_free" => (PlateStatus.Available, "Disponibil", cost),
                "number_busy" => (PlateStatus.Occupied, "Ocupat", cost),
                "error" => (PlateStatus.Occupied, "Indisponibil", cost),
                _ => (PlateStatus.Error, $"XML necunoscut: {result}", cost)
            };
        }
        catch
        {
            /* fallback */
        }
    }

    return raw.ToLowerInvariant() switch
    {
        "error0" => (PlateStatus.Error, "Captcha expirat", 0),
        "error1" => (PlateStatus.Error, "Format invalid", 0),
        "ok" or "ok1" or "free" => (PlateStatus.Available, "Disponibil", 0),
        "used" or "0" or "no" => (PlateStatus.Occupied, "Ocupat", 0),
        _ => (PlateStatus.Error, $"Necunoscut ({raw})", 0)
    };
}

static void PrintRow(PlateResult r, int idx, string? proxy, bool isRetry = false)
{
    string tag = isRetry ? "[R]" : "   ";
    string icon = r.Status switch { PlateStatus.Available => "✓", PlateStatus.Occupied => "✗", _ => "!" };
    string price = r.Cost > 0 ? $"  {r.Cost:N0} MDL" : "";
    string plbl = ProxyLabel(proxy)[..Math.Min(20, ProxyLabel(proxy).Length)];
    Console.WriteLine($"{tag}{idx,-4} {plbl,-22} {r.Plate,-12} {r.Captcha,-8} {icon} {r.StatusText}{price}");
}

// ═════════════════════════════════════════════════════════════════════════════
// Raport HTML
// ═════════════════════════════════════════════════════════════════════════════

static string GenerateHtml(List<PlateResult> results, string prefix, string start, string end, string signDest)
{
    int cntA = results.Count(r => r.Status == PlateStatus.Available);
    int cntO = results.Count(r => r.Status == PlateStatus.Occupied);
    int cntE = results.Count(r => r.Status == PlateStatus.Error);
    string tip = signDest switch { "1" => "Standard", "2" => "Personalizat", _ => "Null (fără destinație)" };
    string genDate = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");

    var rows = new StringBuilder();
    for (int i = 0; i < results.Count; i++)
    {
        var r = results[i];
        string css = r.Status switch
        {
            PlateStatus.Available => "row-available",
            PlateStatus.Occupied => "row-occupied",
            _ => "row-error"
        };
        string icon = r.Status switch
        {
            PlateStatus.Available => "✓ Disponibil", PlateStatus.Occupied => "✗ Ocupat", _ => "⚠ Eroare"
        };
        string price = r.Cost > 0 ? $"{r.Cost:N0} MDL" : "—";
        // "BOS001" → "BOS 001", "BOS1" → "BOS 1"
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
// OCR helpers
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
        var resp = await chat.CompleteChatAsync(msgs, new ChatCompletionOptions { MaxOutputTokenCount = 10 });
        string clean = Regex.Replace(resp.Value.Content[0].Text.Trim(), @"\D", "");
        return clean.Length == 4 ? clean : string.Empty;
    }
    catch
    {
        return string.Empty;
    }
}

static void PreprocessImage(string input, string output)
{
    try
    {
        using var img = Image.Load<Rgba32>(input);
        img.Mutate(ctx => ctx
            .Grayscale().Contrast(2.5f).BinaryThreshold(0.50f)
            .Resize(img.Width * 4, img.Height * 4, KnownResamplers.NearestNeighbor));
        img.SaveAsPng(output);
    }
    catch
    {
        File.Copy(input, output, overwrite: true);
    }
}

static string TryTesseract(string rawPath, string procPath)
{
    string bin = new[] { "/opt/homebrew/bin/tesseract", "/usr/local/bin/tesseract", "tesseract" }
        .FirstOrDefault(File.Exists) ?? "tesseract";
    foreach (var (path, psm) in new[] { (procPath, "7"), (procPath, "13"), (rawPath, "7") })
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = bin,
                Arguments = $"\"{path}\" stdout --psm {psm} -c tessedit_char_whitelist=0123456789",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            string clean = Regex.Replace(proc.StandardOutput.ReadToEnd(), @"\D", "");
            proc.WaitForExit(8_000);
            if (clean.Length == 4) return clean;
        }
        catch
        {
            /* continuă */
        }
    }

    return string.Empty;
}

// ═════════════════════════════════════════════════════════════════════════════
// Proxy fetcher
// ═════════════════════════════════════════════════════════════════════════════

static async Task<List<string?>> FetchPublicProxies()
{
    // Surse publice cu liste de proxy-uri (ip:port, câte unul pe linie)
    var sources = new[]
    {
        // SOCKS5
        ("socks5", "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks5.txt"),
        ("socks5", "https://raw.githubusercontent.com/ShiftyTR/Proxy-List/master/socks5.txt"),
        ("socks5", "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/socks5.txt"),
        ("socks5", "https://raw.githubusercontent.com/hookzof/socks5_list/master/proxy.txt"),
        ("socks5", "https://api.proxyscrape.com/v2/?request=displayproxies&protocol=socks5&timeout=5000&country=all"),
        // HTTP
        ("http", "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt"),
        ("http", "https://raw.githubusercontent.com/ShiftyTR/Proxy-List/master/http.txt"),
        ("http", "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/http.txt"),
        ("http", "https://raw.githubusercontent.com/clarketm/proxy-list/master/proxy-list-raw.txt"),
        ("http", "https://api.proxyscrape.com/v2/?request=displayproxies&protocol=http&timeout=5000&country=all"),
        ("http", "https://raw.githubusercontent.com/mertguvencli/http-proxy-list/main/proxy-list/data.txt"),
        ("http", "https://raw.githubusercontent.com/almroot/proxylist/master/list.txt"),
    };

    using var fetcher = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    fetcher.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

    var all = new HashSet<string?>();
    var tasks = sources.Select(async s =>
    {
        var (scheme, url) = s;
        try
        {
            string body = await fetcher.GetStringAsync(url);
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines
                .Where(l => Regex.IsMatch(l, @"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}:\d{2,5}$"))
                .Select(l => (string?)$"{scheme}://{l}")
                .ToList();
        }
        catch
        {
            return new List<string?>();
        }
    });

    var results = await Task.WhenAll(tasks);
    foreach (var batch in results)
    foreach (var p in batch)
        all.Add(p);

    // Amestecă aleator pentru distribuție uniformă între surse
    var list = all.ToList();
    var rng = new Random();
    for (int i = list.Count - 1; i > 0; i--)
    {
        int j = rng.Next(i + 1);
        (list[i], list[j]) = (list[j], list[i]);
    }

    return list;
}

static async Task<List<string?>> TestProxies(List<string?> proxies, int maxConcurrent = 30, int timeoutSec = 8)
{
    var working = new System.Collections.Concurrent.ConcurrentBag<string?>();
    var sem = new SemaphoreSlim(maxConcurrent);
    int tested = 0;
    int total = proxies.Count;

    var tasks = proxies.Select(async proxy =>
    {
        await sem.WaitAsync();
        try
        {
            bool ok = await TestProxy(proxy, timeoutSec);
            int done = Interlocked.Increment(ref tested);
            if (ok)
            {
                working.Add(proxy);
                Console.Write($"\r  Testat {done}/{total} | Funcționale: {working.Count}    ");
            }
            else if (done % 50 == 0)
            {
                Console.Write($"\r  Testat {done}/{total} | Funcționale: {working.Count}    ");
            }
        }
        finally
        {
            sem.Release();
        }
    });

    await Task.WhenAll(tasks);
    Console.WriteLine();
    return working.OrderBy(_ => Guid.NewGuid()).ToList(); // shuffle
}

static async Task<bool> TestProxy(string? proxyUri, int timeoutSec)
{
    try
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(timeoutSec),
            UseProxy = !string.IsNullOrEmpty(proxyUri)
        };
        if (!string.IsNullOrEmpty(proxyUri))
            handler.Proxy = new WebProxy(new Uri(proxyUri));

        using var testClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        testClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        // Testăm direct pe site-ul țintă
        var resp = await testClient.GetAsync(RefererUrl);
        return resp.IsSuccessStatusCode || (int)resp.StatusCode < 500;
    }
    catch
    {
        return false;
    }
}

static void SaveProxyFile(List<string?> proxies)
{
    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "proxies.txt");
    var lines = new List<string>
    {
        $"# Generat automat la {DateTime.Now:dd.MM.yyyy HH:mm:ss}",
        $"# {proxies.Count} proxy-uri testate și funcționale pe e-services.md",
        ""
    };
    lines.AddRange(proxies.Where(p => p != null).Select(p => p!));
    File.WriteAllLines(path, lines);
}

// ═════════════════════════════════════════════════════════════════════════════
// Config helpers
// ═════════════════════════════════════════════════════════════════════════════

static string ConfigPath() =>
    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

static AppConfig LoadConfig()
{
    try
    {
        string path = ConfigPath();
        if (File.Exists(path))
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new AppConfig();
        }
    }
    catch
    {
        /* ignoră erori de deserializare */
    }

    return new AppConfig();
}

static void SaveConfig(AppConfig cfg)
{
    try
    {
        File.WriteAllText(ConfigPath(),
            JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Nu s-a putut salva config: {ex.Message}");
    }
}

static string MaskKey(string key) =>
    key.Length > 8 ? key[..7] + "..." + key[^4..] : "***";

// ═════════════════════════════════════════════════════════════════════════════
// Tipuri
// ═════════════════════════════════════════════════════════════════════════════

enum PlateStatus
{
    Available,
    Occupied,
    Error
}

record PlateResult(
    string Plate,
    string Captcha,
    string RawResponse,
    PlateStatus Status,
    string StatusText,
    decimal Cost = 0,
    string Payload = "");

record AppConfig(string? OpenAiKey = null);