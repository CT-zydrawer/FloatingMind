using System.Windows;
using System.Windows.Controls;
using FloatingMind.Services;

namespace FloatingMind;

/// <summary>
/// 文件历史回溯对话框 —— 查看SQLite记录的文件修改快照, 一键恢复到修改前
/// </summary>
public partial class HistoryDialog : Window
{
    private readonly FileHistoryService _fileHistory;
    private FileSnapshot? _selected;

    public HistoryDialog(FileHistoryService fileHistory)
    {
        InitializeComponent();
        _fileHistory = fileHistory;
        RefreshList();
    }

    private void RefreshList()
    {
        var list = _fileHistory.GetHistory();
        HistoryList.ItemsSource = list;
        TxtCount.Text = $"共 {list.Count} 条文件修改记录";
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = HistoryList.SelectedItem as FileSnapshot;
        if (_selected == null)
        {
            TxtBefore.Text = "";
            TxtAfter.Text = "";
            return;
        }
        TxtBefore.Text = _selected.BeforeContent;
        TxtAfter.Text = _selected.AfterContent;
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            MessageBox.Show("请先选择一条修改记录", "文件历史",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"将把文件\n{_selected.FilePath}\n恢复到 {_selected.CreatedAt:MM-dd HH:mm:ss} 的修改前状态。\n\n确定继续吗?",
            "确认回溯", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (ok, reason) = await _fileHistory.RestoreAsync(_selected.Id);
        MessageBox.Show(reason, "文件历史", MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        RefreshList();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
