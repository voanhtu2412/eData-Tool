namespace TaxCodeCollector.Models;

public class CompanyRecord
{
    public int Id { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string JsonData { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
