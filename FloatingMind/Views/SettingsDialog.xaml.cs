using System.Windows;
using System.Windows.Controls;
using FloatingMind.Models.Config;

namespace FloatingMind;

public partial class SettingsDialog : Window
{
    private readonly AppConfig _config;
    private readonly Action _onSave;

    public SettingsDialog(AppConfig config, Action onSave)
    {
        InitializeComponent();
        _config = config;
        _onSave = onSave;

        // 加载当前配置到UI
        TxtApiKey.Text = config.DeepSeekApiKey;
        TxtApiUrl.Text = config.DeepSeekApiUrl;
        TxtLowCost.Text = config.LowCostModel;
        TxtHighPerf.Text = config.HighPerformanceModel;
        TxtWorkspace.Text = config.WorkspacePath;

        NumMaxConcurrency.Value = config.MaxConcurrentAgents;
        NumLockTimeout.Value = config.ResourceLockTimeoutSeconds;

        
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _config.DeepSeekApiKey = TxtApiKey.Text.Trim();
        _config.DeepSeekApiUrl = TxtApiUrl.Text.Trim();
        _config.LowCostModel = TxtLowCost.Text.Trim();
        _config.HighPerformanceModel = TxtHighPerf.Text.Trim();
        _config.WorkspacePath = TxtWorkspace.Text.Trim();
        _config.MaxConcurrentAgents = (int)NumMaxConcurrency.Value;
        _config.ResourceLockTimeoutSeconds = (int)NumLockTimeout.Value;

        

        _onSave();
        MessageBox.Show("配置已保存", "Floating Mind", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择工作区目录",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            TxtWorkspace.Text = dialog.FolderName;
        }
    }
}
