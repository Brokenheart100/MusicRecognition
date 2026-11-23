namespace MusicRecognition.Domain.ValueObjects;

// PDF 页面几何（PDF geometry）：记录一页 PDF 内容框（CropBox）在 PDF 坐标系里的四条边，
// 以及这页的旋转角度和识别时渲染图片用的分辨率（DPI）。
// 有了它才能在识别图像素坐标和 PDF 坐标（1/72 英寸）之间换算，把数字画回原谱。
// Coordinates are always the upright CropBox raster; /Rotate is display metadata.
public sealed record PdfGeometry(double Left, double Bottom, double Right, double Top, int Rotation, double Dpi = 216)
{
    // 识别图像素 → PDF 坐标：72 是每英寸的 PDF 点数，216 是识别图的固定渲染 DPI，
    // 所以乘 72/216 完成像素换点；PDF 的 Y 轴向上，图片向下，故纵坐标用 Top 减。
    public (double X, double Y) ImageToPdf(double x, double y)
    {
        Validate(); return (Left + x * 72 / Dpi, Top - y * 72 / Dpi);
    }
    // 按 /Rotate 顺时针旋转把坐标换到预览方向；90/270 度时宽高互换。
    public (double X, double Y) ImageToDisplay(double x, double y)
    {
        Validate(); var w = (Right - Left) * Dpi / 72; var h = (Top - Bottom) * Dpi / 72;
        return Rotation switch { 0 => (x, y), 90 => (h - y, x), 180 => (w - x, h - y), _ => (y, w - x) };
    }
    // 检查四边顺序、DPI 和旋转角是否合法，挡掉会让后续换算悄悄出错的几何数据。
    private void Validate()
    {
        if (!double.IsFinite(Left + Bottom + Right + Top + Dpi) || Left >= Right || Bottom >= Top || Dpi <= 0 || Rotation is not (0 or 90 or 180 or 270))
            throw new ArgumentException("Unsupported PDF geometry.");
    }
}
