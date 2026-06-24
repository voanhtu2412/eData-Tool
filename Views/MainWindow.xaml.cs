using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TaxCodeCollector.Models;
using TaxCodeCollector.ViewModels;

namespace TaxCodeCollector.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        _viewModel.Templates.CollectionChanged += Templates_CollectionChanged;
        RebuildResultColumns();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadTemplatesAsync();
        RebuildResultColumns();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        await _viewModel.DisposeAsync();
    }

    private void Templates_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (FieldTemplate template in e.NewItems)
            {
                template.PropertyChanged += Template_PropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (FieldTemplate template in e.OldItems)
            {
                template.PropertyChanged -= Template_PropertyChanged;
            }
        }

        RebuildResultColumns();
    }

    private void Template_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FieldTemplate.FieldName) or nameof(FieldTemplate.OrderIndex))
        {
            RebuildResultColumns();
        }
    }

    private void RebuildResultColumns()
    {
        ResultsGrid.Columns.Clear();

        foreach (var template in _viewModel.Templates
                     .Where(x => !string.IsNullOrWhiteSpace(x.FieldName))
                     .OrderBy(x => x.OrderIndex))
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = template.FieldName,
                Binding = new Binding($"[{template.FieldName}]"),
                Width = new DataGridLength(180)
            });
        }
    }

    private void TemplatesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedTemplate is null)
        {
            return;
        }

        var window = new TemplateWindow(_viewModel.SelectedTemplate)
        {
            Owner = this
        };
        window.ShowDialog();
        RebuildResultColumns();
    }
}

