namespace MusicRecognition.Infrastructure.Recognition;

public sealed class ToolFailure(string code, string message, string? diagnosticReason = null) : Exception(message)
{
    public string Code { get; } = code;
    // Internal diagnostics are logged separately from the safe client-facing Message.
    public string? DiagnosticReason { get; } = diagnosticReason;
}
