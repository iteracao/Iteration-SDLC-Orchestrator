using System.Text.Json;

namespace Iteration.Orchestrator.Application.Workflows;

public static class WorkflowFailureCatalog
{
    public const string UnsupportedWorkflowCode = "UNSUPPORTED_WORKFLOW_CODE";
    public const string ExecutorUnhandledException = "EXECUTOR_UNHANDLED_EXCEPTION";
    public const string ExecutorExitedWithoutTerminalState = "EXECUTOR_EXITED_WITHOUT_TERMINAL_STATE";
    public const string WorkflowExecutionCancelled = "WORKFLOW_EXECUTION_CANCELLED";
    public const string HostShutdownCancelled = "HOST_SHUTDOWN_CANCELLED";
    public const string OrphanedActiveRunRecovered = "ORPHANED_ACTIVE_RUN_RECOVERED";
    public const string ModelProviderTimeout = "MODEL_PROVIDER_TIMEOUT";
    public const string ModelProviderUnavailable = "MODEL_PROVIDER_UNAVAILABLE";
    public const string ModelInvalidResponse = "MODEL_INVALID_RESPONSE";
    public const string WorkflowContractViolation = "WORKFLOW_CONTRACT_VIOLATION";

    public static WorkflowFailureDescriptor Classify(Exception ex)
    {
        return ex switch
        {
            TimeoutException => new WorkflowFailureDescriptor(ModelProviderTimeout, "The model provider call timed out."),
            HttpRequestException => new WorkflowFailureDescriptor(ModelProviderUnavailable, "The model provider request failed."),
            JsonException => new WorkflowFailureDescriptor(ModelInvalidResponse, "The model provider returned an invalid response payload."),
            InvalidOperationException => new WorkflowFailureDescriptor(WorkflowContractViolation, ex.Message),
            _ => new WorkflowFailureDescriptor(ExecutorUnhandledException, ex.Message)
        };
    }

    public static string Format(string code, string message)
    {
        var normalizedMessage = string.IsNullOrWhiteSpace(message) ? "Workflow execution failed." : message.Trim();
        return $"[{code}] {normalizedMessage}";
    }
}

public sealed record WorkflowFailureDescriptor(string Code, string Message);
