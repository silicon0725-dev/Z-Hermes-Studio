namespace ZHS.Presentation;

/// <summary>工作区种类（开发文档 §10.4 首期三工作区的导航级差异）。</summary>
public enum WorkspaceKind
{
    General,
    Engineering,
    Research,
}

/// <summary>工作区配置：聊天保存的产物类型 / 产物面板默认过滤 / 一句话定位。</summary>
public sealed record WorkspaceProfile(
    WorkspaceKind Kind,
    string DisplayName,
    string Description,
    string ChatArtifactType,
    string? ArtifactTypeFilter)
{
    public static WorkspaceProfile For(WorkspaceKind kind) => kind switch
    {
        WorkspaceKind.Engineering => new WorkspaceProfile(
            WorkspaceKind.Engineering,
            "Engineering",
            "代码、测试、diff 与仓库任务（ZCAC 集群）",
            ZHS.Domain.Artifacts.ArtifactTypes.Document,
            ZHS.Domain.Artifacts.ArtifactTypes.WorkflowRun),
        WorkspaceKind.Research => new WorkspaceProfile(
            WorkspaceKind.Research,
            "Research",
            "来源、证据、结论与技术比较",
            ZHS.Domain.Artifacts.ArtifactTypes.Research,
            ZHS.Domain.Artifacts.ArtifactTypes.Research),
        _ => new WorkspaceProfile(
            WorkspaceKind.General,
            "General",
            "通用对话与产物",
            ZHS.Domain.Artifacts.ArtifactTypes.Document,
            null),
    };
}
