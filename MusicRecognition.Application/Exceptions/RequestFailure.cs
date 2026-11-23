namespace MusicRecognition.Application.Exceptions;

// 应用层主动抛出的失败（request failure）：携带 HTTP 状态码、错误码和给人看的说明。
// 它代替到处返回错误对象，让用例方法可以在任何地方直接 throw，由 API 层统一转成 HTTP 响应。
// Code 是稳定的机器码（如 revision-conflict），前端按它决定提示什么；message 只用于展示。
public sealed class RequestFailure(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
