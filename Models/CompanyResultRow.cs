using TaxCodeCollector.ViewModels;

namespace TaxCodeCollector.Models;

public class CompanyResultRow : ObservableObject
{
    private string _companyName = string.Empty;
    private string _url = string.Empty;

    public string CompanyName
    {
        get => _companyName;
        set => SetProperty(ref _companyName, value);
    }

    public string Url
    {
        get => _url;
        set => SetProperty(ref _url, value);
    }

    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string this[string fieldName]
    {
        get => Values.TryGetValue(fieldName, out var value) ? value : string.Empty;
        set
        {
            Values[fieldName] = value;
            OnPropertyChanged("Item[]");
        }
    }
}
