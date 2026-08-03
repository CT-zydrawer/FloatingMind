using System.Collections.ObjectModel;
using System.Windows;
using FloatingMind.Models.Journal;

namespace FloatingMind;

public partial class JournalDialog : Window
{
    public JournalDialog(ObservableCollection<JournalEntry> entries)
    {
        InitializeComponent();

        JournalList.ItemsSource = entries;
        TxtCount.Text = $"共 {entries.Count} 条记录";
    }
}
