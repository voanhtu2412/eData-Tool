using TaxCodeCollector.ViewModels;

namespace TaxCodeCollector.Models;

public class FieldTemplate : ObservableObject
{
    private string _fieldName = string.Empty;
    private string _cssSelector = string.Empty;
    private string _xPath = string.Empty;
    private string _sampleValue = string.Empty;
    private int _orderIndex;

    public int Id { get; set; }

    public string FieldName
    {
        get => _fieldName;
        set => SetProperty(ref _fieldName, value);
    }

    public string CssSelector
    {
        get => _cssSelector;
        set => SetProperty(ref _cssSelector, value);
    }

    public string XPath
    {
        get => _xPath;
        set => SetProperty(ref _xPath, value);
    }

    public string SampleValue
    {
        get => _sampleValue;
        set => SetProperty(ref _sampleValue, value);
    }

    public int OrderIndex
    {
        get => _orderIndex;
        set => SetProperty(ref _orderIndex, value);
    }
}
