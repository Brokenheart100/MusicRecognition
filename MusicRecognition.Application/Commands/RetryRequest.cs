namespace MusicRecognition.Application.Commands;

// 区域重识别的选区，坐标与识别图像素一致：左上角加宽高。
// 只有单页/子页的 Ready 修订可以带选区重识别；区域外的音符和人工修改保持不动。
public sealed record RecognitionRegion(double X, double Y, double Width, double Height);

// 重试请求体。Region 为空时沿用原来的整页重试/重新识别行为。
public sealed record RetryRequest(RecognitionRegion? Region = null);
