namespace MusicRecognition.Infrastructure.Recognition;

// 工具失败异常：把外部 Python 工具的失败翻译成统一的三部分信息。
// 错误码（Code）给前端和日志归类，Message 是能直接展示给用户的安全文案。
// 工具返回的原始诊断单独保存，只写服务器日志，不随接口返回。
public sealed class ToolFailure(string code, string message, string? diagnosticReason = null) : Exception(message)
{
    public string Code { get; } = code;
    // Internal diagnostics are logged separately from the safe client-facing Message.
    public string? DiagnosticReason { get; } = diagnosticReason;
}
