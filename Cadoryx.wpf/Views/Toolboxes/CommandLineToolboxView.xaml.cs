using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Cadoryx.ViewModels.Toolboxes;

namespace Cadoryx.wpf.Views.Toolboxes;

public partial class CommandLineToolboxView : UserControl
{
    private INotifyCollectionChanged? entries;
    private bool scrollPending;
    public CommandLineToolboxView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach(DataContext as CommandLineToolboxViewModel);
        Unloaded += (_, _) => Attach(null);
        DataContextChanged += (_, e) => Attach(IsLoaded ? e.NewValue as CommandLineToolboxViewModel : null);
    }
    private void Attach(CommandLineToolboxViewModel? model)
    {
        if (entries is not null) entries.CollectionChanged -= OnEntriesChanged;
        entries = model?.Entries;
        if (entries is not null) entries.CollectionChanged += OnEntriesChanged;
    }
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (scrollPending) return;
        scrollPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            scrollPending = false;
            if (IsLoaded && OutputList.Items.Count > 0)
                OutputList.ScrollIntoView(OutputList.Items[^1]);
        }, DispatcherPriority.Background);
    }
    private void OnSuggestionMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is CommandLineToolboxViewModel model && model.AcceptSelectedSuggestion())
        {
            CommandInput.Focus();
            CommandInput.CaretIndex = CommandInput.Text.Length;
        }
    }
    private void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CommandLineToolboxViewModel model) return;
        if (e.Key == Key.Enter)
        {
            if (!model.AcceptSelectedSuggestion()) model.ExecuteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab)
        {
            if (model.CompleteFirstSuggestion()) { CommandInput.CaretIndex = CommandInput.Text.Length; e.Handled = true; }
        }
        else if (e.Key == Key.Up) { if (model.Suggestions.Count > 0) model.SelectSuggestion(-1); else model.PreviousCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Down) { if (model.Suggestions.Count > 0) model.SelectSuggestion(1); else model.NextCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape && model.Suggestions.Count > 0) { model.CommandText += " "; e.Handled = true; }
    }
}
