namespace MusicRecognition.Domain.ValueObjects;

// 标注位置（annotation placement）：简谱数字画在识别图上的哪个像素坐标。
// X/Y 以识别图左上角为原点、向右向下为正；要么都有值，要么都为 null（自动布局）。
// Hidden 为 true 表示用户不想在这个音旁边显示数字，但仍保留这个音的识别结果。
public sealed record AnnotationPlacement(bool Hidden, double? X, double? Y)
{
    // 校验坐标成对且落在识别图内；都为空是"自动布局"，也是合法情况。
    public void Validate(int pageWidth, int pageHeight)
    {
        if (X.HasValue != Y.HasValue || (X.HasValue &&
            (!double.IsFinite(X.Value) || !double.IsFinite(Y!.Value) || X < 0 || Y < 0 || X >= pageWidth || Y >= pageHeight)))
            throw new ArgumentException("Placement requires both finite coordinates inside the recognition page, or neither for automatic layout.");
    }
}
