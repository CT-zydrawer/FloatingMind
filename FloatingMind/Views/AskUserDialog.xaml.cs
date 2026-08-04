using System.Windows;
using System.Windows.Controls;
using FloatingMind.Models.AskUser;

namespace FloatingMind.Views;

public partial class AskUserDialog : Window
{
    private readonly AskUserRequest _request;
    private readonly List<List<Control>> _optionControls = new();

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
            var optionControls = new List<Control>();

            if (question.MultiSelect)
            {
                // 多选: CheckBox
                foreach (var opt in question.Options)
                {
                    var cb = new CheckBox { Margin = new Thickness(0, 4, 0, 4) };
                    var sp = new StackPanel { Orientation = Orientation.Vertical };
                    sp.Children.Add(new TextBlock
                    {
                        Text = opt.Label,
                        FontSize = 13,
                        Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush")
                    });
                    if (!string.IsNullOrEmpty(opt.Description))
                    {
                        sp.Children.Add(new TextBlock
                        {
                            Text = opt.Description,
                            FontSize = 11,
                            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
                            Margin = new Thickness(0, 2, 0, 0)
                        });
                    }
                    cb.Content = sp;
                    panel.Children.Add(cb);
                    optionControls.Add(cb);
                }
            }
            else
            {
                // 单选: RadioButton (同组)
                var group = $"q{_request.Questions.IndexOf(question)}";
                foreach (var opt in question.Options)
                {
                    var rb = new RadioButton
                    {
                        GroupName = group,
                        Margin = new Thickness(0, 4, 0, 4)
                    };
                    var sp = new StackPanel { Orientation = Orientation.Vertical };
                    sp.Children.Add(new TextBlock
                    {
                        Text = opt.Label,
                        FontSize = 13,
                        Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush")
                    });
                    if (!string.IsNullOrEmpty(opt.Description))
                    {
                        sp.Children.Add(new TextBlock
                        {
                            Text = opt.Description,
                            FontSize = 11,
                            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
                            Margin = new Thickness(0, 2, 0, 0)
                        });
                    }
                    rb.Content = sp;
                    panel.Children.Add(rb);
                    optionControls.Add(rb);
                }
            }

            _optionControls.Add(optionControls);
            questionBorder.Child = panel;
            QuestionsPanel.Children.Add(questionBorder);
        }
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        var response = new AskUserResponse { IsCancelled = false };

        for (int i = 0; i < _request.Questions.Count; i++)
        {
            var question = _request.Questions[i];
            var controls = _optionControls[i];
            var selected = new List<string>();

            foreach (var ctrl in controls)
            {
                if (ctrl is RadioButton rb && rb.IsChecked == true)
                {
                    selected.Add(question.Options[controls.IndexOf(ctrl)].Label);
                }
                else if (ctrl is CheckBox cb && cb.IsChecked == true)
                {
                    selected.Add(question.Options[controls.IndexOf(cb)].Label);
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
