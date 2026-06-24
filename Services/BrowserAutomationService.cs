using Microsoft.Playwright;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Diagnostics;
using System.IO;
using TaxCodeCollector.Models;

namespace TaxCodeCollector.Services;

public class CompanyLink
{
    public string Text { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public record ElementPickResult(string CssSelector, string XPath, string SampleValue);

public class BrowserAutomationService : IAsyncDisposable
{
    private const int MaxListPages = 500;
    private static readonly TimeSpan DelayBetweenCompanies = TimeSpan.FromSeconds(5);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private Process? _chromeProcess;
    private string? _tempProfilePath;

    private static string FindChromePath()
    {
        string[] paths = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe")
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            if (key != null)
            {
                var val = key.GetValue(null);
                var path = val?.ToString();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch { }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
            if (key != null)
            {
                var val = key.GetValue(null);
                var path = val?.ToString();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch { }

        throw new FileNotFoundException("Không tìm thấy Google Chrome hoặc Microsoft Edge trên máy tính của bạn. Vui lòng cài đặt Chrome để sử dụng chức năng này.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task StartBrowserAsync(Action<string>? log = null)
    {
        if (_page is not null && !_page.IsClosed && _browser?.IsConnected == true)
        {
            return;
        }

        await ResetBrowserAsync();

        string chromePath;
        try
        {
            chromePath = FindChromePath();
        }
        catch (FileNotFoundException ex)
        {
            log?.Invoke(ex.Message);
            throw;
        }

        int port = GetFreeTcpPort();
        _tempProfilePath = Path.Combine(Path.GetTempPath(), "TaxCodeCollector_ChromeProfile_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempProfilePath);

        var arguments = new List<string>
        {
            $"--remote-debugging-port={port}",
            $"--user-data-dir=\"{_tempProfilePath}\"",
            "--lang=vi-VN",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-background-networking",
            "--disable-background-timer-throttling",
            "--disable-backgrounding-occluded-windows",
            "--disable-breakpad",
            "--disable-client-side-phishing-detection",
            "--disable-component-update",
            "--disable-default-apps",
            "--disable-dev-shm-usage",
            "--disable-domain-reliability",
            "--disable-extensions",
            "--disable-features=AudioServiceOutOfProcess",
            "--disable-hang-monitor",
            "--disable-ipc-flooding-protection",
            "--disable-notifications",
            "--disable-offer-store-unmasked-wallet-cards",
            "--disable-popup-blocking",
            "--disable-print-preview",
            "--disable-prompt-on-repost",
            "--disable-renderer-backgrounding",
            "--disable-setuid-sandbox",
            "--disable-speech-api",
            "--disable-sync",
            "--disable-blink-features=AutomationControlled",
            "--password-store=basic",
            "--use-mock-keychain",
            "about:blank"
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = chromePath,
            Arguments = string.Join(" ", arguments),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        log?.Invoke($"Đang khởi chạy trình duyệt từ: {chromePath} (Port: {port})...");
        _chromeProcess = Process.Start(startInfo);
        if (_chromeProcess == null)
        {
            throw new InvalidOperationException("Không thể khởi chạy tiến trình trình duyệt Chrome.");
        }

        _playwright = await Playwright.CreateAsync();
        string cdpUrl = $"http://127.0.0.1:{port}";

        IBrowser? browser = null;
        for (int i = 0; i < 20; i++)
        {
            try
            {
                browser = await _playwright.Chromium.ConnectOverCDPAsync(cdpUrl);
                break;
            }
            catch
            {
                await Task.Delay(250);
            }
        }

        if (browser == null)
        {
            throw new InvalidOperationException("Không thể kết nối Playwright tới cổng debugging của trình duyệt.");
        }
        _browser = browser;

        _context = _browser.Contexts.FirstOrDefault() ?? await _browser.NewContextAsync();

        await _context.RouteAsync("**/*", async route =>
        {
            var resourceType = route.Request.ResourceType;
            if (resourceType is "media" or "font")
            {
                await route.AbortAsync();
                return;
            }

            await route.ContinueAsync();
        });

        await _context.AddInitScriptAsync("""
            Object.defineProperty(navigator, 'webdriver', {
                get: () => undefined
            });
            """);

        _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
    }

    public Task<ElementPickResult?> PickElementAsync()
    {
        return PickElementAsync(null, CancellationToken.None);
    }

    public async Task<ElementPickResult?> PickElementAsync(string? url, CancellationToken cancellationToken)
    {
        await StartBrowserAsync();
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(url) && string.Equals(_page?.Url, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Hay nhap URL truoc khi Pick Element.");
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                await _page!.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60000
                });
            }
            catch (PlaywrightException ex) when (IsTargetClosedError(ex))
            {
                await ResetBrowserAsync();
                await StartBrowserAsync();
                await _page!.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60000
                });
            }
        }

        try
        {
            var json = await _page!.EvaluateAsync<string>("""
            () => new Promise(resolve => {
                if (!document.body) {
                    resolve("");
                    return;
                }

                const previousCursor = document.body.style.cursor;
                let lastElement = null;

                const getCssSelector = (element) => {
                    if (!element || element.nodeType !== Node.ELEMENT_NODE) return "";
                    if (element.id) return `#${CSS.escape(element.id)}`;

                    const parts = [];
                    let current = element;
                    while (current && current.nodeType === Node.ELEMENT_NODE && current !== document.body) {
                        let selector = current.nodeName.toLowerCase();
                        if (current.className && typeof current.className === "string") {
                            const classes = current.className
                                .split(/\s+/)
                                .filter(Boolean)
                                .slice(0, 3)
                                .map(c => `.${CSS.escape(c)}`)
                                .join("");
                            selector += classes;
                        }

                        const parent = current.parentElement;
                        if (parent) {
                            const sameTag = Array.from(parent.children)
                                .filter(x => x.nodeName === current.nodeName);
                            if (sameTag.length > 1) {
                                selector += `:nth-of-type(${sameTag.indexOf(current) + 1})`;
                            }
                        }

                        parts.unshift(selector);
                        current = parent;
                    }

                    return parts.join(" > ");
                };

                const getXPath = (element) => {
                    if (!element || element.nodeType !== Node.ELEMENT_NODE) return "";
                    if (element.id) return `//*[@id="${element.id.replace(/"/g, '\\"')}"]`;

                    const parts = [];
                    let current = element;
                    while (current && current.nodeType === Node.ELEMENT_NODE) {
                        let index = 1;
                        let sibling = current.previousElementSibling;
                        while (sibling) {
                            if (sibling.nodeName === current.nodeName) index++;
                            sibling = sibling.previousElementSibling;
                        }
                        parts.unshift(`${current.nodeName.toLowerCase()}[${index}]`);
                        current = current.parentElement;
                    }
                    return "/" + parts.join("/");
                };

                const cleanup = () => {
                    document.removeEventListener("mouseover", onMouseOver, true);
                    document.removeEventListener("mouseout", onMouseOut, true);
                    document.removeEventListener("click", onClick, true);
                    document.body.style.cursor = previousCursor;
                    if (lastElement) lastElement.style.outline = lastElement.dataset.tccOutline || "";
                };

                const onMouseOver = event => {
                    if (lastElement) lastElement.style.outline = lastElement.dataset.tccOutline || "";
                    lastElement = event.target;
                    lastElement.dataset.tccOutline = lastElement.style.outline || "";
                    lastElement.style.outline = "3px solid #2563eb";
                    event.stopPropagation();
                };

                const onMouseOut = event => {
                    if (event.target) event.target.style.outline = event.target.dataset.tccOutline || "";
                };

                const onClick = event => {
                    event.preventDefault();
                    event.stopPropagation();
                    const element = event.target;
                    const sampleValue = (element.innerText || element.value || element.textContent || "").trim();
                    const result = {
                        cssSelector: getCssSelector(element),
                        xpath: getXPath(element),
                        sampleValue
                    };
                    cleanup();
                    resolve(JSON.stringify(result));
                };

                document.body.style.cursor = "crosshair";
                document.addEventListener("mouseover", onMouseOver, true);
                document.addEventListener("mouseout", onMouseOut, true);
                document.addEventListener("click", onClick, true);
            })
            """);

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var result = JsonSerializer.Deserialize<ElementPickResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return result is null
                ? null
                : result with { SampleValue = CleanText(result.SampleValue) };
        }
        catch (PlaywrightException ex) when (IsTargetClosedError(ex))
        {
            await ResetBrowserAsync();
            throw new InvalidOperationException("Trinh duyet Pick Element da dong. Bam Pick Element lai de mo phien moi.", ex);
        }
    }

    public async Task<List<CompanyResultRow>> CrawlListAsync(
        string listUrl,
        List<FieldTemplate> templates,
        int maxListPages,
        string? nextPageCssSelector,
        string? nextPageXPath,
        Action<string>? log = null,
        Func<CompanyResultRow, Task>? rowReady = null,
        int maxCompaniesPerPage = 25,
        CancellationToken cancellationToken = default)
    {
        await StartBrowserAsync(log);
        log?.Invoke($"Đang mở danh sách: {listUrl}");

        var rows = new List<CompanyResultRow>();
        var visitedListPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedCompanyUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentListUrl = listUrl;
        var normalizedMaxListPages = Math.Clamp(maxListPages, 1, MaxListPages);

        for (var pageIndex = 1; pageIndex <= normalizedMaxListPages && !string.IsNullOrWhiteSpace(currentListUrl); pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await NavigateAsync(currentListUrl, cancellationToken, log))
            {
                log?.Invoke("Hủy bỏ tải trang danh sách do CAPTCHA.");
                break;
            }

            if (!visitedListPages.Add(_page!.Url))
            {
                log?.Invoke("Da gap lai trang danh sach da xu ly, dung crawl de tranh lap vo han.");
                break;
            }

            var links = await ExtractCompanyLinksAsync(maxCompaniesPerPage);

            var nextListUrl = await ExtractNextListPageUrlAsync(visitedListPages, nextPageCssSelector, nextPageXPath);
            log?.Invoke($"Trang danh sach {pageIndex}/{normalizedMaxListPages}: tim thay {links.Count} link cong ty.");
            log?.Invoke($"Tìm thấy {links.Count} link công ty.");

            for (var i = 0; i < links.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var link = links[i];
                if (!visitedCompanyUrls.Add(link.Url))
                {
                    continue;
                }

                try
                {
                    log?.Invoke($"Đang mở công ty {i + 1}/{links.Count}: {link.Text}");
                    if (!await NavigateAsync(link.Url, cancellationToken, log))
                    {
                        log?.Invoke($"Bỏ qua công ty do hủy xác minh CAPTCHA: {link.Text}");
                        continue;
                    }

                    var row = new CompanyResultRow
                    {
                        Url = link.Url,
                        CompanyName = CleanText(link.Text)
                    };

                    foreach (var template in templates.OrderBy(x => x.OrderIndex))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        log?.Invoke($"Đang lấy {template.FieldName}");
                        row[template.FieldName] = await ExtractFieldValueAsync(template);
                    }

                    var templateCompanyName = templates.FirstOrDefault(x =>
                        string.Equals(x.FieldName, "Tên công ty", StringComparison.OrdinalIgnoreCase));
                    if (templateCompanyName is not null && !string.IsNullOrWhiteSpace(row[templateCompanyName.FieldName]))
                    {
                        row.CompanyName = row[templateCompanyName.FieldName];
                    }
                    else
                    {
                        row.CompanyName = await ExtractCompanyNameAsync(row.CompanyName);
                    }

                    rows.Add(row);
                    if (rowReady is not null)
                    {
                        await rowReady(row);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Lỗi khi lấy {link.Text}: {ex.Message}");
                }

                if (i < links.Count - 1)
                {
                    log?.Invoke("Tam nghi 2 giay truoc khi loc cong ty tiep theo.");
                    await Task.Delay(DelayBetweenCompanies, cancellationToken);
                }
            }

            currentListUrl = nextListUrl;
            if (!string.IsNullOrWhiteSpace(currentListUrl) && pageIndex < normalizedMaxListPages)
            {
                log?.Invoke($"Chuyen sang trang danh sach tiep theo: {currentListUrl}");
            }
        }

        return rows;
    }

    public async Task<List<CompanyResultRow>> CrawlListByPageAsync(
        string listUrl,
        List<FieldTemplate> templates,
        int maxListPages,
        string? nextPageUrl,
        string? nextPageCssSelector,
        string? nextPageXPath,
        Action<string>? log = null,
        Func<CompanyResultRow, Task>? rowReady = null,
        int maxCompaniesPerPage = 25,
        CancellationToken cancellationToken = default)
    {
        await StartBrowserAsync(log);
        log?.Invoke($"Dang mo danh sach: {listUrl}");

        var listPage = _page!;
        await NavigatePageAsync(listPage, listUrl, cancellationToken);
        if (!await WaitForManualRobotCheckAsync(log, cancellationToken))
        {
            return [];
        }

        var detailPage = await _context!.NewPageAsync();
        var rows = new List<CompanyResultRow>();
        var visitedListPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedCompanyUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedMaxListPages = Math.Clamp(maxListPages, 1, MaxListPages);

        try
        {
            for (var pageIndex = 1; pageIndex <= normalizedMaxListPages; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _page = listPage;

                var fingerprint = await GetListPageFingerprintAsync();
                if (!visitedListPages.Add(fingerprint))
                {
                    log?.Invoke("Da gap lai trang danh sach da xu ly, dung crawl de tranh lap vo han.");
                    break;
                }

                var links = await ExtractCompanyLinksAsync(maxCompaniesPerPage);
                log?.Invoke($"Trang danh sach {pageIndex}/{normalizedMaxListPages}: tim thay {links.Count} link cong ty.");

                for (var i = 0; i < links.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var link = links[i];
                    if (!visitedCompanyUrls.Add(link.Url))
                    {
                        continue;
                    }

                    try
                    {
                        log?.Invoke($"Dang mo cong ty page {pageIndex} - {i + 1}/{links.Count}: {link.Text}");
                        _page = detailPage;
                        await NavigatePageAsync(detailPage, link.Url, cancellationToken);
                        if (!await WaitForManualRobotCheckAsync(log, cancellationToken))
                        {
                            continue;
                        }

                        var row = new CompanyResultRow
                        {
                            Url = link.Url,
                            CompanyName = CleanText(link.Text)
                        };

                        foreach (var template in templates.OrderBy(x => x.OrderIndex))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            log?.Invoke($"Dang lay {template.FieldName}");
                            row[template.FieldName] = await ExtractFieldValueAsync(template);
                        }

                        var templateCompanyName = templates.FirstOrDefault(x =>
                            string.Equals(x.FieldName, "Tên công ty", StringComparison.OrdinalIgnoreCase));
                        if (templateCompanyName is not null && !string.IsNullOrWhiteSpace(row[templateCompanyName.FieldName]))
                        {
                            row.CompanyName = row[templateCompanyName.FieldName];
                        }
                        else
                        {
                            row.CompanyName = await ExtractCompanyNameAsync(row.CompanyName);
                        }

                        rows.Add(row);
                        if (rowReady is not null)
                        {
                            await rowReady(row);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"Loi khi lay {link.Text}: {ex.Message}");
                    }
                    finally
                    {
                        _page = listPage;
                    }

                    if (i < links.Count - 1)
                    {
                        log?.Invoke("Tam nghi 2 giay truoc khi loc cong ty tiep theo.");
                        await Task.Delay(DelayBetweenCompanies, cancellationToken);
                    }
                }

                var advancedToNextPage = pageIndex < normalizedMaxListPages
                    && await AdvanceListPageAsync(
                        listUrl,
                        pageIndex,
                        visitedListPages,
                        nextPageUrl,
                        nextPageCssSelector,
                        nextPageXPath,
                        log,
                        cancellationToken);

                if (!advancedToNextPage)
                {
                    if (pageIndex < normalizedMaxListPages)
                    {
                        log?.Invoke("Khong the mo trang danh sach tiep theo. Kiem tra lai URL trang 2 hoac xem cua so Playwright co bi CAPTCHA/robot khong.");
                    }
                    break;
                }

                log?.Invoke("Da chuyen sang trang danh sach tiep theo.");
            }
        }
        finally
        {
            _page = listPage;
            await detailPage.CloseAsync();
        }

        return rows;
    }

    private async Task<bool> NavigateAsync(string url, CancellationToken cancellationToken, Action<string>? log = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page!.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60000
        });

        await WaitAfterNavigationHintAsync(800);
        return await WaitForManualRobotCheckAsync(log, cancellationToken);
    }

    private async Task NavigatePageAsync(IPage page, string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60000
        });

        await WaitForPageAfterNavigationHintAsync(page, 500);
    }

    private async Task WaitAfterNavigationHintAsync(float timeoutMs)
    {
        try
        {
            await _page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = timeoutMs
            });
        }
        catch (TimeoutException)
        {
            // DOMContentLoaded from GotoAsync/ClickAsync is enough for most target pages.
        }
    }

    private static async Task WaitForPageAfterNavigationHintAsync(IPage page, float timeoutMs)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = timeoutMs
            });
        }
        catch (TimeoutException)
        {
            // DOMContentLoaded from GotoAsync/ClickAsync is enough for most target pages.
        }
    }

    private async Task<string> GetListPageFingerprintAsync()
    {
        var links = await ExtractCompanyLinksAsync();
        return $"{_page!.Url}|{string.Join("|", links.Take(8).Select(x => x.Url))}";
    }

    private async Task<bool> LooksLikeRobotCheckAsync()
    {
        if (_page is null)
        {
            return false;
        }

        try
        {
            var title = await _page.TitleAsync() ?? string.Empty;
            if (title.Contains("captcha", StringComparison.OrdinalIgnoreCase)
                || title.Contains("robot", StringComparison.OrdinalIgnoreCase)
                || title.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
                || title.Contains("access denied", StringComparison.OrdinalIgnoreCase)
                || title.Contains("verify", StringComparison.OrdinalIgnoreCase)
                || title.Contains("verification", StringComparison.OrdinalIgnoreCase)
                || title.Contains("xác minh", StringComparison.OrdinalIgnoreCase)
                || title.Contains("người máy", StringComparison.OrdinalIgnoreCase)
                || title.Contains("kiểm tra bảo mật", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var url = _page.Url ?? string.Empty;
            if (url.Contains("captcha", StringComparison.OrdinalIgnoreCase)
                || url.Contains("blocked", StringComparison.OrdinalIgnoreCase)
                || url.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var text = await _page.Locator("body").First.TextContentAsync(new LocatorTextContentOptions
            {
                Timeout = 1000
            }) ?? string.Empty;

            return text.Contains("captcha", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("robot", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("access denied", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("verify", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("verification", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("xác minh", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("không phải người máy", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("kiểm tra bảo mật", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("mã xác nhận", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("mã xác minh", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("security check", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> WaitForManualRobotCheckAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return true;
    }

    private async Task<bool> AdvanceListPageAsync(
        string firstListUrl,
        int currentPageIndex,
        HashSet<string> visitedListPages,
        string? manualNextPageUrl,
        string? nextPageCssSelector,
        string? nextPageXPath,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return false;
        }

        var manualUrl = GetManualNextPageUrl(firstListUrl, _page.Url, currentPageIndex, manualNextPageUrl);
        if (!string.IsNullOrWhiteSpace(manualUrl))
        {
            log?.Invoke($"Dang mo trang danh sach tiep theo: {manualUrl}");
            await NavigatePageAsync(_page, manualUrl, cancellationToken);
            if (!await WaitForManualRobotCheckAsync(log, cancellationToken))
            {
                return false;
            }
            return await IsUsableNewListPageAsync(visitedListPages, log);
        }

        if (!string.IsNullOrWhiteSpace(nextPageCssSelector) || !string.IsNullOrWhiteSpace(nextPageXPath))
        {
            var before = await GetListPageFingerprintAsync();
            var href = await TryExtractNextPageUrlBySelectorAsync(nextPageCssSelector, nextPageXPath);
            if (Uri.TryCreate(href, UriKind.Absolute, out var hrefUri))
            {
                await NavigatePageAsync(_page, hrefUri.ToString(), cancellationToken);
                if (!await WaitForManualRobotCheckAsync(log, cancellationToken))
                {
                    return false;
                }
                return await IsUsableNewListPageAsync(visitedListPages, log);
            }

            var clicked = await TryClickNextPageBySelectorAsync(nextPageCssSelector, nextPageXPath);
            if (clicked is not null)
            {
                return !string.Equals(before, await GetListPageFingerprintAsync(), StringComparison.OrdinalIgnoreCase)
                       && await IsUsableNewListPageAsync(visitedListPages, log);
            }

            return false;
        }

        var nextUrl = await ExtractNextListPageUrlAsync(visitedListPages);
        if (string.IsNullOrWhiteSpace(nextUrl))
        {
            return false;
        }

        await NavigatePageAsync(_page, nextUrl, cancellationToken);
        if (!await WaitForManualRobotCheckAsync(log, cancellationToken))
        {
            return false;
        }
        return await IsUsableNewListPageAsync(visitedListPages, log);
    }

    private async Task<bool> IsUsableNewListPageAsync(HashSet<string> visitedListPages, Action<string>? log)
    {
        var fingerprint = await GetListPageFingerprintAsync();
        if (visitedListPages.Contains(fingerprint))
        {
            log?.Invoke("Trang danh sach tiep theo trung voi trang da loc, xem nhu da het trang.");
            return false;
        }

        var links = await ExtractCompanyLinksAsync();
        if (links.Count == 0)
        {
            log?.Invoke("Trang danh sach tiep theo khong co link cong ty, xem nhu da het trang.");
            return false;
        }

        return true;
    }

    private static string? GetManualNextPageUrl(
        string firstListUrl,
        string currentListUrl,
        int currentPageIndex,
        string? manualNextPageUrl)
    {
        if (string.IsNullOrWhiteSpace(manualNextPageUrl))
        {
            return null;
        }

        if (currentPageIndex == 1)
        {
            return EnsureAbsoluteUrl(firstListUrl, manualNextPageUrl);
        }

        var pageTwoUrl = EnsureAbsoluteUrl(firstListUrl, manualNextPageUrl);
        return GuessNextPageUrlFromFirstTwo(firstListUrl, pageTwoUrl, currentPageIndex + 1)
               ?? GuessNextPageUrl(currentListUrl)
               ?? (pageTwoUrl is null ? null : GuessNextPageUrl(pageTwoUrl));
    }

    private static string? EnsureAbsoluteUrl(string baseUrl, string url)
    {
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
               && Uri.TryCreate(baseUri, url.Trim(), out var relativeUri)
            ? relativeUri.ToString()
            : null;
    }

    private static string? GuessNextPageUrlFromFirstTwo(string firstListUrl, string? pageTwoUrl, int targetPageNumber)
    {
        if (string.IsNullOrWhiteSpace(pageTwoUrl)
            || !Uri.TryCreate(firstListUrl, UriKind.Absolute, out var firstUri)
            || !Uri.TryCreate(pageTwoUrl, UriKind.Absolute, out var secondUri))
        {
            return null;
        }

        var builder = new UriBuilder(secondUri);
        var firstQuery = System.Web.HttpUtility.ParseQueryString(firstUri.Query);
        var secondQuery = System.Web.HttpUtility.ParseQueryString(secondUri.Query);

        foreach (var key in secondQuery.AllKeys.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (key is null || !int.TryParse(secondQuery[key], out var secondValue))
            {
                continue;
            }

            var lowerKey = key.ToLowerInvariant();
            if (lowerKey.Contains("page") || lowerKey.Equals("p") || lowerKey.Contains("trang"))
            {
                secondQuery[key] = targetPageNumber.ToString();
                builder.Query = secondQuery.ToString();
                return builder.Uri.ToString();
            }

            if (lowerKey.Contains("offset") || lowerKey.Contains("start") || lowerKey.Contains("skip"))
            {
                var firstValue = int.TryParse(firstQuery[key], out var parsedFirstValue) ? parsedFirstValue : 0;
                var step = secondValue - firstValue;
                if (step > 0)
                {
                    secondQuery[key] = (firstValue + step * (targetPageNumber - 1)).ToString();
                    builder.Query = secondQuery.ToString();
                    return builder.Uri.ToString();
                }
            }
        }

        var secondPath = secondUri.AbsolutePath;
        var pathMatch = Regex.Match(secondPath, @"(?<prefix>/(?:page|trang)[-/]?)(?<page>\d+)(?<suffix>/?$)", RegexOptions.IgnoreCase);
        if (pathMatch.Success)
        {
            builder.Path = secondPath[..pathMatch.Groups["page"].Index]
                           + targetPageNumber
                           + secondPath[(pathMatch.Groups["page"].Index + pathMatch.Groups["page"].Length)..];
            return builder.Uri.ToString();
        }

        var firstTrailing = Regex.Match(firstUri.AbsolutePath, @"(?<page>\d+)(?<suffix>/?$)");
        var secondTrailing = Regex.Match(secondPath, @"(?<page>\d+)(?<suffix>/?$)");
        if (secondTrailing.Success && int.TryParse(secondTrailing.Groups["page"].Value, out var secondNumber))
        {
            var firstNumber = firstTrailing.Success && int.TryParse(firstTrailing.Groups["page"].Value, out var parsedFirstNumber)
                ? parsedFirstNumber
                : secondNumber - 1;
            var step = secondNumber - firstNumber;
            if (step > 0)
            {
                builder.Path = secondPath[..secondTrailing.Groups["page"].Index]
                               + (firstNumber + step * (targetPageNumber - 1))
                               + secondPath[(secondTrailing.Groups["page"].Index + secondTrailing.Groups["page"].Length)..];
                return builder.Uri.ToString();
            }
        }

        return null;
    }

    private static string? GuessNextPageUrl(string currentUrl)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var builder = new UriBuilder(uri);
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        foreach (var key in query.AllKeys.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (key is null)
            {
                continue;
            }

            if ((key.Contains("page", StringComparison.OrdinalIgnoreCase)
                 || key.Equals("p", StringComparison.OrdinalIgnoreCase)
                 || key.Contains("trang", StringComparison.OrdinalIgnoreCase))
                && int.TryParse(query[key], out var pageNumber))
            {
                query[key] = (pageNumber + 1).ToString();
                builder.Query = query.ToString();
                return builder.Uri.ToString();
            }
        }

        var path = uri.AbsolutePath;
        var match = Regex.Match(path, @"(?<prefix>/(?:page|trang)/)(?<page>\d+)(?<suffix>/?$)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups["page"].Value, out var pathPage))
        {
            builder.Path = path[..match.Groups["page"].Index]
                           + (pathPage + 1)
                           + path[(match.Groups["page"].Index + match.Groups["page"].Length)..];
            return builder.Uri.ToString();
        }

        var lastNumber = Regex.Match(path, @"(?<page>\d+)(?<suffix>/?$)");
        if (lastNumber.Success && int.TryParse(lastNumber.Groups["page"].Value, out var trailingPage))
        {
            builder.Path = path[..lastNumber.Groups["page"].Index]
                           + (trailingPage + 1)
                           + path[(lastNumber.Groups["page"].Index + lastNumber.Groups["page"].Length)..];
            return builder.Uri.ToString();
        }

        return null;
    }

    private async Task<string?> ExtractNextListPageUrlAsync(
        HashSet<string> visitedListPages,
        string? nextPageCssSelector = null,
        string? nextPageXPath = null)
    {
        if (_page is null || !Uri.TryCreate(_page.Url, UriKind.Absolute, out var currentUri))
        {
            return null;
        }

        var pickedNextUrl = await TryExtractNextPageUrlBySelectorAsync(nextPageCssSelector, nextPageXPath);
        if (IsUsableNextPageUrl(pickedNextUrl, currentUri, visitedListPages, out var pickedUri))
        {
            return pickedUri!.ToString();
        }

        var clickedNextUrl = await TryClickNextPageBySelectorAsync(nextPageCssSelector, nextPageXPath);
        if (IsUsableNextPageUrl(clickedNextUrl, currentUri, visitedListPages, out var clickedUri))
        {
            return clickedUri!.ToString();
        }

        var nextUrl = await _page.EvaluateAsync<string?>("""
            () => {
                const normalizeText = value => (value || "")
                    .replace(/\s+/g, " ")
                    .trim()
                    .toLowerCase();

                const candidates = Array.from(document.querySelectorAll([
                    'a[rel="next"][href]',
                    '.pagination a[href]',
                    'ul.pagination a[href]',
                    'nav a[href]',
                    'a.page-link[href]',
                    'a[href]'
                ].join(',')));

                const nextByLabel = candidates.find(anchor => {
                    const text = normalizeText(anchor.innerText || anchor.textContent);
                    const aria = normalizeText(anchor.getAttribute("aria-label"));
                    const title = normalizeText(anchor.getAttribute("title"));
                    const rel = normalizeText(anchor.getAttribute("rel"));
                    const label = `${text} ${aria} ${title} ${rel}`;
                    const disabled = anchor.closest(".disabled,[disabled],[aria-disabled='true']");
                    return !disabled && (
                        rel.includes("next")
                        || label.includes("next")
                        || label.includes("sau")
                        || label.includes("tiep")
                        || label.includes("tiếp")
                        || text === ">"
                        || text === "»"
                        || text === "›"
                    );
                });

                if (nextByLabel?.href) return nextByLabel.href;

                const currentNumber = Number(
                    new URL(location.href).pathname.match(/\/page\/(\d+)/i)?.[1]
                    || new URL(location.href).searchParams.get("page")
                    || "1");

                const numbered = candidates
                    .map(anchor => {
                        const text = normalizeText(anchor.innerText || anchor.textContent);
                        const number = Number(text);
                        return Number.isFinite(number) && number > currentNumber
                            ? { number, href: anchor.href }
                            : null;
                    })
                    .filter(Boolean)
                    .sort((a, b) => a.number - b.number);

                return numbered[0]?.href || null;
            }
            """);

        if (!IsUsableNextPageUrl(nextUrl, currentUri, visitedListPages, out var nextUri))
        {
            return null;
        }

        return nextUri!.ToString();
    }

    private async Task<string?> TryExtractNextPageUrlBySelectorAsync(string? cssSelector, string? xpath)
    {
        var selector = BuildLocatorSelector(cssSelector, xpath);
        if (selector is null)
        {
            return null;
        }

        try
        {
            var locator = _page!.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                return null;
            }

            return await locator.EvaluateAsync<string?>("""
                element => {
                    const anchor = element.closest?.("a[href]") || (element.matches?.("a[href]") ? element : null);
                    if (anchor?.href) return anchor.href;
                    const href = element.getAttribute?.("href") || element.getAttribute?.("data-href") || element.getAttribute?.("data-url");
                    return href ? new URL(href, location.href).href : null;
                }
                """, new LocatorEvaluateOptions { Timeout = 1000 });
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryClickNextPageBySelectorAsync(string? cssSelector, string? xpath)
    {
        var selector = BuildLocatorSelector(cssSelector, xpath);
        if (selector is null || _page is null)
        {
            return null;
        }

        try
        {
            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                return null;
            }

            await locator.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
            await WaitAfterNavigationHintAsync(1500);

            return _page.Url;
        }
        catch
        {
            return null;
        }
    }

    private static string? BuildLocatorSelector(string? cssSelector, string? xpath)
    {
        if (!string.IsNullOrWhiteSpace(cssSelector))
        {
            return cssSelector.Trim();
        }

        return !string.IsNullOrWhiteSpace(xpath)
            ? $"xpath={xpath.Trim()}"
            : null;
    }

    private static bool IsUsableNextPageUrl(
        string? nextUrl,
        Uri currentUri,
        HashSet<string> visitedListPages,
        out Uri? nextUri)
    {
        nextUri = null;
        if (string.IsNullOrWhiteSpace(nextUrl)
            || !Uri.TryCreate(nextUrl, UriKind.Absolute, out var parsedUri)
            || !parsedUri.Host.Equals(currentUri.Host, StringComparison.OrdinalIgnoreCase)
            || visitedListPages.Contains(parsedUri.ToString()))
        {
            return false;
        }

        nextUri = parsedUri;
        return true;
    }

    public async Task<List<CompanyLink>> ExtractCompanyLinksAsync(int maxCompaniesPerPage = 25)
    {
        if (_page is null)
        {
            return [];
        }

        var linksJson = await _page.EvaluateAsync<string>("""
            () => {
                const selectors = [
                    ".tax-listing h3 a[href]",
                    ".tax-listing h2 a[href]",
                    ".company-name a[href]",
                    ".company a[href]",
                    ".search-results a[href]",
                    ".list-group a[href]",
                    "article a[href]",
                    "h3 a[href]",
                    "h2 a[href]",
                    "a[href]"
                ];

                const links = [];
                for (const selector of selectors) {
                    for (const anchor of document.querySelectorAll(selector)) {
                        const text = (anchor.innerText || anchor.textContent || "").trim();
                        const url = anchor.href;
                        if (text && url) links.push({ text, url });
                    }
                }

                return JSON.stringify(links);
            }
            """);

        var rawLinks = JsonSerializer.Deserialize<List<CompanyLink>>(linksJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];

        if (!Uri.TryCreate(_page.Url, UriKind.Absolute, out var currentUri))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return rawLinks
            .Where(x => !string.IsNullOrWhiteSpace(x.Text) && !string.IsNullOrWhiteSpace(x.Url))
            .Where(x => Uri.TryCreate(x.Url, UriKind.Absolute, out var uri)
                        && uri.Host.Equals(currentUri.Host, StringComparison.OrdinalIgnoreCase)
                        && !uri.AbsolutePath.Contains("nganh-nghe", StringComparison.OrdinalIgnoreCase)
                        && !uri.AbsolutePath.Contains("page/", StringComparison.OrdinalIgnoreCase)
                        && x.Text.Trim().Length > 3)
            .Where(LooksLikeCompanyResultLink)
            .Where(x => seen.Add(x.Url))
            .Take(maxCompaniesPerPage)
            .ToList();
    }

    public async Task<string> ExtractFieldValueAsync(FieldTemplate template)
    {
        if (_page is null)
        {
            return string.Empty;
        }

        // --- Ưu tiên 1: Tự động trích xuất thông minh dựa vào nhãn (Label-based Smart Extraction) ---
        try
        {
            var smartValue = await _page.EvaluateAsync<string>("""
                (labelName) => {
                    if (!labelName) return "";
                    const cleanLabel = labelName.toLowerCase().replace(/[:：]/g, "").trim();
                    if (!cleanLabel) return "";

                    const synonyms = {
                        "mã số thuế": ["mã số thuế", "mst", "mã số dn", "mã số doanh nghiệp", "mã doanh nghiệp", "mã số thuế doanh nghiệp"],
                        "địa chỉ": ["địa chỉ", "địa chỉ trụ sở", "địa chỉ công ty", "địa chỉ trụ sở chính", "địa chỉ giao dịch"],
                        "đại diện": ["đại diện", "người đại diện", "đại diện pháp luật", "người đại diện pháp luật", "chủ sở hữu", "chủ doanh nghiệp"],
                        "điện thoại": ["điện thoại", "sđt", "số điện thoại", "phone", "tel"],
                        "trạng thái": ["trạng thái", "tình trạng", "tình trạng hoạt động", "trạng thái hoạt động"],
                        "ngày cấp": ["ngày cấp", "ngày thành lập", "ngày hoạt động", "ngày bắt đầu", "ngày hoạt động chính thức"],
                        "tên công ty": ["tên công ty", "tên doanh nghiệp", "tên chính thức"]
                    };

                    const targetLabels = [cleanLabel];
                    for (const [key, list] of Object.entries(synonyms)) {
                        if (cleanLabel.includes(key) || list.includes(cleanLabel)) {
                            list.forEach(item => {
                                if (!targetLabels.includes(item)) targetLabels.push(item);
                            });
                        }
                    }

                    const allElements = document.querySelectorAll("td, th, span, label, p, div, li, h3, h4, b, strong");
                    for (const el of allElements) {
                        const text = (el.innerText || el.textContent || "").trim();
                        if (!text || text.length > 100) continue;

                        const cleanText = text.toLowerCase().replace(/[:：]/g, "").trim();
                        const matches = targetLabels.some(target => 
                            cleanText === target || 
                            (cleanText.startsWith(target) && cleanText.length < target.length + 5)
                        );

                        if (matches) {
                            // TH1: Dữ liệu nằm chung dòng có dấu hai chấm (vd: "Mã số thuế: 0123456789")
                            if (text.includes(":") || text.includes("：")) {
                                const parts = text.split(/[:：]/);
                                if (parts.length > 1) {
                                    const val = parts.slice(1).join(":").trim();
                                    if (val) return val;
                                }
                            }

                            // TH2: Cấu trúc bảng (td tiếp theo hoặc ô cạnh bên)
                            if (el.tagName === "TD" || el.tagName === "TH") {
                                const nextCell = el.nextElementSibling;
                                if (nextCell) {
                                    return (nextCell.innerText || nextCell.textContent || "").trim();
                                }
                                const row = el.closest("tr");
                                if (row) {
                                    const cells = Array.from(row.querySelectorAll("td, th"));
                                    const idx = cells.indexOf(el);
                                    if (idx !== -1 && idx + 1 < cells.length) {
                                        return (cells[idx + 1].innerText || cells[idx + 1].textContent || "").trim();
                                    }
                                }
                            }

                            // TH3: Phần tử kế cận (vd: <span>Mã số thuế</span> <span>0123456789</span>)
                            let sibling = el.nextElementSibling;
                            while (sibling) {
                                const val = (sibling.innerText || sibling.textContent || "").trim();
                                if (val) return val;
                                sibling = sibling.nextElementSibling;
                            }

                            // TH4: Phần tử kế cận của cấp cha (vd: div label song song div value)
                            const parent = el.parentElement;
                            if (parent && parent.tagName !== "BODY" && parent.tagName !== "HTML") {
                                let parentNext = parent.nextElementSibling;
                                while (parentNext) {
                                    const val = (parentNext.innerText || parentNext.textContent || "").trim();
                                    if (val) return val;
                                    parentNext = parentNext.nextElementSibling;
                                }
                            }
                        }
                    }
                    return "";
                }
                """, template.FieldName);

            if (!string.IsNullOrWhiteSpace(smartValue))
            {
                return CleanText(smartValue);
            }
        }
        catch
        {
            // Bỏ qua lỗi và tiếp tục thử fallback bằng selector tĩnh
        }

        // --- Ưu tiên 2 (Fallback): Sử dụng CssSelector hoặc XPath được chọn thủ công ---
        if (!string.IsNullOrWhiteSpace(template.CssSelector))
        {
            var value = await TryExtractByLocatorAsync(template.CssSelector, false);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return CleanText(value);
            }
        }

        if (!string.IsNullOrWhiteSpace(template.XPath))
        {
            var value = await TryExtractByLocatorAsync(template.XPath, true);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return CleanText(value);
            }
        }

        return string.Empty;
    }

    private async Task<string> TryExtractByLocatorAsync(string selector, bool isXPath)
    {
        try
        {
            var locator = _page!.Locator(isXPath ? $"xpath={selector}" : selector).First;
            if (await locator.CountAsync() == 0)
            {
                return string.Empty;
            }

            var value = await locator.EvaluateAsync<string?>("""
                element => {
                    if ("value" in element && element.value) return element.value;
                    return element.innerText || element.textContent || "";
                }
                """, new LocatorEvaluateOptions { Timeout = 2500 });

            return value ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<string> ExtractCompanyNameAsync(string fallback)
    {
        try
        {
            var value = await _page!.Locator("h1").First.TextContentAsync(new LocatorTextContentOptions
            {
                Timeout = 1000
            });

            value = CleanText(value ?? string.Empty);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool LooksLikeCompanyLink(string text)
    {
        return text.Contains("CÔNG TY", StringComparison.OrdinalIgnoreCase)
               || text.Contains("DOANH NGHIỆP", StringComparison.OrdinalIgnoreCase)
               || text.Contains("CHI NHÁNH", StringComparison.OrdinalIgnoreCase)
               || text.Contains("HỢP TÁC XÃ", StringComparison.OrdinalIgnoreCase)
               || text.Contains("TNHH", StringComparison.OrdinalIgnoreCase)
               || text.Contains("CP", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCompanyResultLink(CompanyLink link)
    {
        var text = CleanText(link.Text);
        if (text.Length < 3)
        {
            return false;
        }

        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (path.Contains("nganh-nghe", StringComparison.OrdinalIgnoreCase)
            || path.Contains("page/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("tag/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("search", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var firstSegment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return Regex.IsMatch(firstSegment, @"^\d{7,}(?:-\d{3})?(?:-|$)")
               && (LooksLikeCompanyLink(text) || text.Length >= 5);
    }

    private static string CleanText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var value = WebUtility.HtmlDecode(input)
            .Replace('\u00A0', ' ')
            .Replace('\u200B', ' ');

        value = Regex.Replace(value, @"[\uE000-\uF8FF]", " ");
        value = Regex.Replace(value, @"\s+", " ").Trim();
        value = Regex.Replace(value, @"^(Mã số thuế|Tên công ty|Người đại diện|Địa chỉ|Điện thoại|Ngày hoạt động|Loại hình doanh nghiệp|Ngành nghề chính)\s*[:：]\s*", string.Empty, RegexOptions.IgnoreCase);
        return value.Trim();
    }

    private static bool IsTargetClosedError(PlaywrightException ex)
    {
        return ex.Message.Contains("Target page, context or browser has been closed", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ResetBrowserAsync()
    {
        _page = null;

        if (_context is not null)
        {
            try
            {
                await _context.CloseAsync();
                await _context.DisposeAsync();
            }
            catch (PlaywrightException)
            {
                // The user may have closed the browser manually.
            }
            finally
            {
                _context = null;
            }
        }

        if (_browser is not null)
        {
            try
            {
                await _browser.DisposeAsync();
            }
            catch (PlaywrightException)
            {
                // The browser may already be gone.
            }
            finally
            {
                _browser = null;
            }
        }

        _playwright?.Dispose();
        _playwright = null;

        if (_chromeProcess is not null)
        {
            try
            {
                if (!_chromeProcess.HasExited)
                {
                    _chromeProcess.Kill();
                }
                _chromeProcess.Dispose();
            }
            catch { }
            finally
            {
                _chromeProcess = null;
            }
        }

        if (!string.IsNullOrEmpty(_tempProfilePath) && Directory.Exists(_tempProfilePath))
        {
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        Directory.Delete(_tempProfilePath, true);
                        break;
                    }
                    catch
                    {
                        await Task.Delay(500);
                    }
                }
            }
            catch { }
            finally
            {
                _tempProfilePath = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetBrowserAsync();
    }
}
