using MusicRecognition.Domain.Enums;

namespace MusicRecognition.Domain.ValueObjects;

// 一个"音"的三件套：音名（do/re/mi…）+ 升降记号 + 八度。
// 举例：中央 C 记作 C、不升不降、第 4 八度；升 fa 记作 F、升 1 个半音、第 4 八度。
// 为什么不用一个数字表示？因为五线谱关心"画在第几线"，简谱关心"是第几个音"，
// 两边都需要"音名"这个身份，所以拆成三部分保存。
public sealed record Pitch
{
    public Step Step { get; init; }
    public int Alter { get; init; }
    public int Octave { get; init; }
    // 创建时就把不可能的音挡掉：音名只能 C..B，升降最多两个，八度 0..9。
    // 这样后面的换算就不用处处判空、判越界。
    public Pitch(Step step, int alter, int octave)
    {
        if (!Enum.IsDefined(step) || alter is < -2 or > 2 || octave is < 0 or > 9)
            throw new ArgumentException("Pitch requires C..B, alteration -2..2, octave 0..9.");
        Step = step; Alter = alter; Octave = octave;
    }
    // MIDI 编号：把音高换成一个整数（0~127），让程序能直接比大小、算半音距离。
    // 规律：一个八度 12 个半音，中央 C（C4）正好是 60；方括号里是 7 个白键在一个八度内的位置。
    public int Midi => (Octave + 1) * 12 + new[] { 0, 2, 4, 5, 7, 9, 11 }[(int)Step] + Alter;
    // 白键序号：只数白键，一个八度 7 个，升降号不影响它。
    // 用来算"这是 do 上方的第几个音"，也就是简谱里 1~7 的位置。
    public int Diatonic => Octave * 7 + (int)Step;
}
