using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using TaxCodeCollector.Commands;
using TaxCodeCollector.Data;
using TaxCodeCollector.Models;
using TaxCodeCollector.Services;

namespace TaxCodeCollector.ViewModels;

public class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly TemplateService _templateService = new();
    private readonly BrowserAutomationService _browserService = new();
    private readonly ExcelExportService _excelExportService = new();
    private CancellationTokenSource? _crawlCancellation;
    private FieldTemplate? _selectedTemplate;
    private string _listUrl = string.Empty;
    private string _pickUrl = string.Empty;
    private string _nextPageCssSelector = string.Empty;
    private string _nextPageXPath = string.Empty;
    private string _nextPageUrl = string.Empty;
    private int _maxPagesToCrawl = 1;
    private string _statusMessage = "Sẵn sàng.";
    private bool _isCrawling;

    public MainViewModel()
    {
        LoadTemplateCommand = new AsyncRelayCommand(LoadTemplatesAsync);
        AddFieldCommand = new RelayCommand(_ => AddField());
        RemoveFieldCommand = new RelayCommand(_ => RemoveSelectedField(), _ => SelectedTemplate is not null);
        SaveTemplateCommand = new AsyncRelayCommand(SaveTemplatesAsync, () => Templates.Count > 0);
        PickElementCommand = new AsyncRelayCommand(PickElementAsync, () => SelectedTemplate is not null);
        PickNextPageCommand = new AsyncRelayCommand(PickNextPageAsync);
        StartCrawlCommand = new AsyncRelayCommand(StartCrawlAsync, () => !IsCrawling);
        StopCrawlCommand = new RelayCommand(_ => StopCrawl(), _ => IsCrawling);
        ExportExcelCommand = new AsyncRelayCommand(ExportExcelAsync, () => Results.Count > 0);
    }

    public ObservableCollection<FieldTemplate> Templates { get; } = [];
    public ObservableCollection<CompanyResultRow> Results { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    public FieldTemplate? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetProperty(ref _selectedTemplate, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string ListUrl
    {
        get => _listUrl;
        set => SetProperty(ref _listUrl, value);
    }

    public string PickUrl
    {
        get => _pickUrl;
        set => SetProperty(ref _pickUrl, value);
    }

    public string NextPageCssSelector
    {
        get => _nextPageCssSelector;
        set => SetProperty(ref _nextPageCssSelector, value);
    }

    public string NextPageXPath
    {
        get => _nextPageXPath;
        set => SetProperty(ref _nextPageXPath, value);
    }

    public string NextPageUrl
    {
        get => _nextPageUrl;
        set => SetProperty(ref _nextPageUrl, value);
    }

    public int MaxPagesToCrawl
    {
        get => _maxPagesToCrawl;
        set
        {
            var normalized = Math.Clamp(value, 1, 500);
            SetProperty(ref _maxPagesToCrawl, normalized);
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsCrawling
    {
        get => _isCrawling;
        set
        {
            if (SetProperty(ref _isCrawling, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public ICommand LoadTemplateCommand { get; }
    public ICommand AddFieldCommand { get; }
    public ICommand RemoveFieldCommand { get; }
    public ICommand SaveTemplateCommand { get; }
    public ICommand PickElementCommand { get; }
    public ICommand PickNextPageCommand { get; }
    public ICommand StartCrawlCommand { get; }
    public ICommand StopCrawlCommand { get; }
    public ICommand ExportExcelCommand { get; }

    public async Task LoadTemplatesAsync()
    {
        await EnsureDatabaseAsync();
        Templates.Clear();

        var templates = await _templateService.LoadTemplatesAsync();
        foreach (var template in templates)
        {
            Templates.Add(template);
        }

        AddLog(templates.Count == 0
            ? "Chưa có template. Bấm Add Field để tạo trường đầu tiên."
            : $"Đã tải {templates.Count} field template.");
    }

    private void AddField()
    {
        var nextIndex = Templates.Count + 1;
        var template = new FieldTemplate
        {
            FieldName = $"Field {nextIndex}",
            OrderIndex = nextIndex
        };

        Templates.Add(template);
        SelectedTemplate = template;
        AddLog("Đã thêm field mới.");
    }

    private void RemoveSelectedField()
    {
        if (SelectedTemplate is null)
        {
            return;
        }

        Templates.Remove(SelectedTemplate);
        SelectedTemplate = Templates.FirstOrDefault();
        ReorderTemplates();
        AddLog("Đã xóa field khỏi template.");
    }

    private async Task SaveTemplatesAsync()
    {
        ReorderTemplates();
        var saved = await _templateService.SaveTemplatesAsync(Templates);

        Templates.Clear();
        foreach (var template in saved)
        {
            Templates.Add(template);
        }

        SelectedTemplate = Templates.FirstOrDefault();
        AddLog($"Đã lưu {Templates.Count} field template.");
    }

    private async Task PickElementAsync()
    {
        if (SelectedTemplate is null)
        {
            AddLog("Hãy chọn một field trước khi Pick Element.");
            return;
        }

        var targetUrl = string.IsNullOrWhiteSpace(PickUrl) ? ListUrl : PickUrl;
        if (!TryNormalizeUrl(targetUrl, out var normalizedUrl, out var validationMessage))
        {
            AddLog(validationMessage);
            MessageBox.Show(validationMessage, "Pick Element", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AddLog("Đang bật chế độ Pick Element. Hãy click vào phần tử trên trình duyệt Playwright.");

        ElementPickResult? result;
        try
        {
            result = await _browserService.PickElementAsync(normalizedUrl, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var message = ex is InvalidOperationException ? ex.Message : $"Lỗi Pick Element: {ex.Message}";
            AddLog(message);
            MessageBox.Show(message, "Pick Element", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (result is null)
        {
            AddLog("Không lấy được selector từ phần tử đã chọn.");
            return;
        }

        SelectedTemplate.CssSelector = result.CssSelector;
        SelectedTemplate.XPath = result.XPath;
        SelectedTemplate.SampleValue = result.SampleValue;
        AddLog($"Đã pick selector cho {SelectedTemplate.FieldName}.");
    }

    private async Task PickNextPageAsync()
    {
        if (!TryNormalizeUrl(ListUrl, out var normalizedUrl, out var validationMessage))
        {
            AddLog(validationMessage);
            MessageBox.Show(validationMessage, "Pick Next Page", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddLog("Dang bat che do Pick Next Page. Hay click vao nut/link chuyen trang tiep theo.");

        ElementPickResult? result;
        try
        {
            result = await _browserService.PickElementAsync(normalizedUrl, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var message = ex is InvalidOperationException ? ex.Message : $"Loi Pick Next Page: {ex.Message}";
            AddLog(message);
            MessageBox.Show(message, "Pick Next Page", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (result is null)
        {
            AddLog("Khong lay duoc selector nut chuyen trang.");
            return;
        }

        NextPageCssSelector = result.CssSelector;
        NextPageXPath = result.XPath;
        AddLog("Da pick selector nut chuyen trang tiep theo.");
    }

    private static bool TryNormalizeUrl(string? input, out string normalizedUrl, out string message)
    {
        normalizedUrl = string.Empty;
        message = string.Empty;

        var value = input?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            message = "Hay nhap URL dung khi Pick Element hoac URL danh sach cong ty truoc khi bam Pick Element.";
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = $"https://{value}";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !uri.Host.Contains('.', StringComparison.Ordinal))
        {
            message = "URL Pick Element khong hop le. Vui long nhap URL day du, vi du: https://masothue.com/...";
            return false;
        }

        normalizedUrl = uri.ToString();
        return true;
    }

    private async Task StartCrawlAsync()
    {
        if (string.IsNullOrWhiteSpace(ListUrl))
        {
            AddLog("Hãy nhập URL danh sách công ty.");
            return;
        }

        if (Templates.Count == 0)
        {
            AddLog("Hãy tạo và lưu ít nhất một field template trước khi crawl.");
            return;
        }

        _crawlCancellation = new CancellationTokenSource();
        IsCrawling = true;
        Results.Clear();

        var session = await CreateCrawlSessionAsync();
        try
        {


            var templates = Templates.OrderBy(x => x.OrderIndex).ToList();
            var rows = await _browserService.CrawlListByPageAsync(
                ListUrl.Trim(),
                templates,
                MaxPagesToCrawl,
                NextPageUrl,
                NextPageCssSelector,
                NextPageXPath,
                AddLog,
                AddResultAsync,
                _crawlCancellation.Token);

            session.Status = "Completed";
            session.TotalCompanies = rows.Count;
            session.SuccessCount = rows.Count;
            AddLog($"Hoàn tất crawl {rows.Count} công ty.");
        }
        catch (OperationCanceledException)
        {
            session.Status = "Stopped";
            session.TotalCompanies = Results.Count;
            session.SuccessCount = Results.Count;
            AddLog("Đã dừng crawl theo yêu cầu.");
        }
        catch (Exception ex)
        {
            session.Status = "Error";
            session.TotalCompanies = Results.Count;
            session.SuccessCount = Results.Count;
            session.ErrorCount = 1;
            AddLog($"Lỗi crawl: {ex.Message}");
        }
        finally
        {
            session.FinishedAt = DateTime.Now;
            await UpdateCrawlSessionAsync(session);
            _crawlCancellation?.Dispose();
            _crawlCancellation = null;
            IsCrawling = false;
        }
    }

    private void StopCrawl()
    {
        _crawlCancellation?.Cancel();
        AddLog("Đang yêu cầu dừng crawl...");
    }

    private async Task ExportExcelAsync()
    {
        if (Results.Count == 0)
        {
            AddLog("Không có dữ liệu để export.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = $"tax-code-data-{DateTime.Now:yyyyMMdd-HHmm}.xlsx"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _excelExportService.ExportAsync(dialog.FileName, Templates, Results);
        AddLog($"Đã export Excel: {dialog.FileName}");
    }

    private async Task AddResultAsync(CompanyResultRow row)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => Results.Add(row));

        await using var db = new AppDbContext();
        await db.Database.EnsureCreatedAsync();
        db.CompanyRecords.Add(new CompanyRecord
        {
            CompanyName = row.CompanyName,
            Url = row.Url,
            JsonData = JsonSerializer.Serialize(row.Values, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }),
            CreatedAt = DateTime.Now
        });
        await db.SaveChangesAsync();
    }

    private async Task<CrawlSession> CreateCrawlSessionAsync()
    {
        await using var db = new AppDbContext();
        await db.Database.EnsureCreatedAsync();

        var session = new CrawlSession
        {
            ListUrl = ListUrl.Trim(),
            StartedAt = DateTime.Now,
            Status = "Running"
        };
        db.CrawlSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private static async Task UpdateCrawlSessionAsync(CrawlSession session)
    {
        await using var db = new AppDbContext();
        await db.Database.EnsureCreatedAsync();
        db.CrawlSessions.Update(session);
        await db.SaveChangesAsync();
    }

    private static async Task EnsureDatabaseAsync()
    {
        await using var db = new AppDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    private void ReorderTemplates()
    {
        for (var i = 0; i < Templates.Count; i++)
        {
            Templates[i].OrderIndex = i + 1;
        }
    }

    private void AddLog(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = message;
            Logs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
            while (Logs.Count > 300)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        });
    }

    private void RaiseCommandStates()
    {
        (RemoveFieldCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveTemplateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PickElementCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StartCrawlCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopCrawlCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportExcelCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _crawlCancellation?.Cancel();
        _crawlCancellation?.Dispose();
        await _browserService.DisposeAsync();
    }
}
