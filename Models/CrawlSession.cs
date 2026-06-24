namespace TaxCodeCollector.Models;

public class CrawlSession
{
    public int Id { get; set; }
    public string ListUrl { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
    public string Status { get; set; } = "Running";
    public int TotalCompanies { get; set; }
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
}
