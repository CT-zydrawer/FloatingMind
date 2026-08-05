using System.Windows;
using System.Windows.Controls;
using FloatingMind.Models.AskUser;

namespace FloatingMind.Views;

public partial class AskUserDialog : Window
{
    private readonly AskUserRequest _request;

    /// <summary>每个问题对应的选项槽位(含自动附加的"其他(自行填写)"槽位)</summary>
    private sealed class OptionSlot
    {
        public Control Control { get; set; } = null!;
        public string? Label { get; set; }        // null => 该槽位是"其他(自行填写)"
        public TextBox? CustomTextBox { get; set; } // 其他选项的输入框
    }

    private readonly List<List<OptionSlot>> _optionSlots = new();

    /// <summary>对话框结果, 为 null 表示用户取消</summary>
    public AskUserResponse? Result { get; private set; }

    public AskUserDialog(AskUserRequest request)
    {
        InitializeComponent();
        _request = request;
        BuildQuestions();
    }

    private void BuildQuestions()
    {
        foreach (var question in _request.Questions)
        {
            // 问题区块容器
            var questionBorder = new Border
            {
                Margin = new Thickness(0, 0, 0, 16),
                Padding = new Thickness(16, 12, 16, 12),
                CornerRadius = new CornerRadius(8),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1)
            };

            var panel = new StackPanel();

            // 问题标题
            var titleBlock = new TextBlock
            {
                Text = question.Question,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush")
            };
            panel.Children.Add(titleBlock);

            // 选项列表
            var slots = new List<OptionSlot>();
            var group = $"q{_request.Questions.IndexOf(question)}";

            foreach (var opt in question.Options)
            {
                var slot = new OptionSlot { Label = opt.Label };
                var toggle = CreateOptionToggle(question.MultiSelect, group, opt.Label, opt.Description);
                slot.Control = toggle;
                panel.Children.Add(toggle);
                slots.Add(slot);
            }

            // === 自动附加"其他(自行填写)"选项: 勾选后启用输入框, 提交时文本并入回答 ===
            var otherSlot = new OptionSlot { Label = null };
            Control otherToggle;
            if (question.MultiSelect)
            {
                var cb = new CheckBox { Margin = new Thickness(0, 4, 0, 0) };
                cb.Content = new TextBlock
                {
                    Text = "其他(自行填写)",
                    FontSize = 13,
                    Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush")
                };
                otherToggle = cb;
            }
            else
            {
                var rb = new RadioButton { GroupName = group, Margin = new Thickness(0, 4, 0, 0) };
                rb.Content = new TextBlock
                {
                    Text = "其他(自行填写)",
                    FontSize = 13,
                    Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush")
                };
                otherToggle = rb;
            }
            var otherTextBox = new TextBox
            {
                Margin = new Thickness(24, 4, 0, 0),
                Padding = new Thickness(6, 4, 6, 4),
                IsEnabled = false,
                MaxLength = 500,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            // 勾选"其他"时启用/禁用输入框
            void OnOtherToggled(object sender, RoutedEventArgs e)
            {
                var isChecked = sender switch
                {
                    RadioButton rb => rb.IsChecked == true,
                    CheckBox cb => cb.IsChecked == true,
                    _ => false
                };
                otherTextBox.IsEnabled = isChecked;
                if (isChecked) otherTextBox.Focus();
            }
            if (otherToggle is RadioButton otherRb) otherRb.Checked += OnOtherToggled;
            if (otherToggle is CheckBox otherCb) otherCb.Checked += OnOtherToggled;
            if (otherToggle is RadioButton otherRb2) otherRb2.Unchecked += OnOtherToggled;
            if (otherToggle is CheckBox otherCb2) otherCb2.Unchecked += OnOtherToggled;

            otherSlot.Control = otherToggle;
            otherSlot.CustomTextBox = otherTextBox;
            panel.Children.Add(otherToggle);
            panel.Children.Add(otherTextBox);
            slots.Add(otherSlot);

            _optionSlots.Add(slots);
            questionBorder.Child = panel;
            QuestionsPanel.Children.Add(questionBorder);
        }
    }

    /// <summary>创建单选(RadioButton)或多选(CheckBox)选项控件</summary>
    private static Control CreateOptionToggle(bool multiSelect, string group, string label, string description)
    {
        var sp = new StackPanel { Orientation = Orientation.Vertical };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("PrimaryTextBrush")
        });
        if (!string.IsNullOrEmpty(description))
        {
            sp.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("SecondaryTextBrush"),
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        if (multiSelect)
        {
            return new CheckBox { Margin = new Thickness(0, 4, 0, 4), Content = sp };
        }

        return new RadioButton { GroupName = group, Margin = new Thickness(0, 4, 0, 4), Content = sp };
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        var response = new AskUserResponse { IsCancelled = false };

        for (int i = 0; i < _request.Questions.Count; i++)
        {
            var question = _request.Questions[i];
            var slots = _optionSlots[i];
            var selected = new List<string>();

            foreach (var slot in slots)
            {
                bool isChecked = slot.Control switch
                {
                    RadioButton rb => rb.IsChecked == true,
                    CheckBox cb => cb.IsChecked == true,
                    _ => false
                };
                if (!isChecked) continue;

                if (slot.Label != null)
                {
                    // 常规选项
                    selected.Add(slot.Label);
                }
                else if (slot.CustomTextBox != null)
                {
                    // "其他(自行填写)": 文本并入回答
                    var custom = slot.CustomTextBox.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(custom))
                        selected.Add(custom);
                }
            }

            response.Answers.Add(new QuestionAnswer
            {
                Question = question.Question,
                SelectedLabels = selected
            });
        }

        Result = response;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = new AskUserResponse { IsCancelled = true };
        DialogResult = false;
        Close();
    }
}
