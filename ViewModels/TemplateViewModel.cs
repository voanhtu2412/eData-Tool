using System.Windows.Input;
using TaxCodeCollector.Commands;
using TaxCodeCollector.Models;

namespace TaxCodeCollector.ViewModels;

public class TemplateViewModel : ObservableObject
{
    public TemplateViewModel(FieldTemplate field)
    {
        Field = field;
        SaveCommand = new RelayCommand(_ => RequestClose?.Invoke(true));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false));
    }

    public FieldTemplate Field { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action<bool>? RequestClose;
}
