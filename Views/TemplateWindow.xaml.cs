using System.Windows;
using TaxCodeCollector.Models;
using TaxCodeCollector.ViewModels;

namespace TaxCodeCollector.Views;

public partial class TemplateWindow : Window
{
    public TemplateWindow(FieldTemplate field)
    {
        InitializeComponent();
        var viewModel = new TemplateViewModel(field);
        viewModel.RequestClose += result =>
        {
            DialogResult = result;
            Close();
        };
        DataContext = viewModel;
    }
}
