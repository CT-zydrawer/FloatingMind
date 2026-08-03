using System.Windows;
using FloatingMind.Models.Workflow;
using FloatingMind.ViewModels;

namespace FloatingMind;

public partial class WorkflowDialog : Window
{
    public WorkflowDialog(WorkflowDef workflow)
    {
        InitializeComponent();

        TxtWorkflowName.Text = workflow.Name;
        TxtWorkflowDesc.Text = workflow.Description;
        TxtStatus.Text = workflow.Status;

        var nodes = new System.Collections.ObjectModel.ObservableCollection<WorkflowNodeVM>();
        foreach (var edge in workflow.Edges)
        {
            var src = workflow.Nodes.FirstOrDefault(n => n.Id == edge.Source);
            var tgt = workflow.Nodes.FirstOrDefault(n => n.Id == edge.Target);
            nodes.Add(new WorkflowNodeVM
            {
                SourceLabel = src?.Label ?? edge.Source,
                TargetLabel = tgt?.Label ?? edge.Target,
                SourceAgent = src?.AgentType ?? "",
                TargetAgent = tgt?.AgentType ?? "",
                SourceStatus = src?.Status ?? "",
                TargetStatus = tgt?.Status ?? ""
            });
        }
        WorkflowNodesList.ItemsSource = nodes;
    }
}
