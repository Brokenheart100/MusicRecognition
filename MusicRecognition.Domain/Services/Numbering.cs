using MusicRecognition.Domain.Enums;
using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Domain.Services;

// 简谱换算器：把一个五线谱上的音，翻译成简谱的写法（第几个音、要不要升降、高/低八度）。
// 五线谱是"绝对地址"，音高是钉死的；简谱是"相对地址"，以 do 为基准数第几个音。
// 所以同一个音，在 C 调里是 1，在 G 调里可能变成 4——这个类就是那张换算表。
public static class Numbering
{
    // 换算单个音。measure 是它所在的小节号：一首曲子可能中途转调，
    // 从某小节起 do 的位置就变了，所以必须按小节去查当时用的是哪个调。
    public static NumberedLabel Convert(Pitch pitch, int measure, NumberingOptions options)
    {
        options.Validate();
        // 小节从 1 开始数；算出 0 或负数说明上游数据有问题，直接报错，别带着错数据往下算。
        if (measure < 1) throw new ArgumentException("Measure starts at 1.");

        // 默认按 C 调算：do 就是中央 C。
        // 28 是中央 C 的白键序号（C0 记 0，每八度加 7），60 是它的 MIDI 编号。
        var doIndex = 28; var doMidi = 60;
        // 首调唱名法：do 跟着调号跑。先找出当前小节生效的调（调号可能有好几个，取最近的那个）。
        if (options.Mode == NumberingMode.MovableDo)
        {
            var key = options.Keys.Where(k => k.StartMeasure <= measure).MaxBy(k => k.StartMeasure)!;
            doIndex = key.Tonic.Diatonic; doMidi = key.Tonic.Midi;
            // 小调的主音是 la，但简谱习惯按"关系大调"记（A 小调仍以 C 为 do）。
            // 所以把基准从主音挪到关系大调的主音：白键退 5 步，音高退 9 个半音。
            if (key.Scale == ScaleKind.NaturalMinor) { doIndex -= 5; doMidi -= 9; }
        }
        // 先看这个音比 do 高多少个白键；除以 7 得到"差几个八度"，余数就是 0~6 级的第几个音。
        var relative = pitch.Diatonic - doIndex;
        var octave = (int)Math.Floor(relative / 7d);
        var degree = relative - octave * 7;
        // 这个级数"本该"是多高：do 的音高 + 八度差 + 该级数在半音阶上的偏移。
        var expected = doMidi + octave * 12 + new[] { 0, 2, 4, 5, 7, 9, 11 }[degree];
        // 实际音高减去"本该"的音高，差值就是升降号个数（0 还原、+1 升、-1 降）。
        // degree + 1 就是简谱上写出来的数字 1~7。
        return new(degree + 1, pitch.Midi - expected, octave);
    }
}
