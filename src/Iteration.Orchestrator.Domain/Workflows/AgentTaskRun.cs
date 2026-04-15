namespace Iteration.Orchestrator.Domain.Workflows;

public sealed class AgentTaskRun
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid WorkflowRunId { get; private set; }
    public string AgentCode { get; private set; } = string.Empty;
    public AgentTaskStatus Status { get; private set; } = AgentTaskStatus.Pending;
    public DateTime StartedUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; private set; }
    public string InputPayloadJson { get; private set; } = "{}";
    public string? OutputPayloadJson { get; private set; }
    public string? FailureReason { get; private set; }

    private AgentTaskRun() { }

    public AgentTaskRun(Guid workflowRunId, string agentCode, string inputPayloadJson)
    {
        WorkflowRunId = workflowRunId;
        AgentCode = agentCode.Trim();
        InputPayloadJson = inputPayloadJson;
    }

    public void Start() => Status = AgentTaskStatus.Running;

    public void Succeed(string outputPayloadJson)
    {
        Status = AgentTaskStatus.Succeeded;
        OutputPayloadJson = outputPayloadJson;
        CompletedUtc = DateTime.UtcNow;
    }

    public void Fail(string reason)
    {
        Status = AgentTaskStatus.Failed;
        FailureReason = reason;
        CompletedUtc = DateTime.UtcNow;
    }
}
