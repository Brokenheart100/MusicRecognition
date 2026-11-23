using MusicRecognition.Domain.Enums;

namespace MusicRecognition.Domain.ValueObjects;

// 简谱换算设置（numbering options）：告诉换算器用固定 C 还是首调，以及整首曲子在哪里换调。
// 固定 C 模式不需要调号表；首调模式必须逐段列出从第几小节起、主音是什么、调式是什么。
// 这些设置随谱面快照保存：用户切换模式或改调号后，全部简谱数字都要按它重算。
// 一次转调（key change）：从 StartMeasure 小节开始，主音（Tonic）和调式（Scale）换成新的。
public sealed record KeyChange(int StartMeasure, Pitch Tonic, ScaleKind Scale);
// 整份谱的换算设置：模式 + 按小节排序的转调列表。
public sealed record NumberingOptions(NumberingMode Mode, KeyChange[] Keys)
{
    // 最常用的默认值：固定 C、没有转调，新建或刚识别完先用它。
    public static NumberingOptions FixedC => new(NumberingMode.FixedC, []);
    // 校验模式、调号数量和转调位置是否合法；不合法直接报错，别带着错设置去算数字。
    public void Validate()
    {
        // 先挡掉非法枚举和过多调号：100 是防御上限，正常曲子不会转调上百次。
        if (!Enum.IsDefined(Mode) || Keys is null || Keys.Length > 100)
            throw new ArgumentException("Invalid numbering options.");
        // 每条调号必须合法，且起始小节不能重复：同一小节只能有一个生效的调。
        if (Keys.Any(k => k is null || k.Tonic is null || k.StartMeasure < 1 || !Enum.IsDefined(k.Scale)) ||
            Keys.Select(k => k.StartMeasure).Distinct().Count() != Keys.Length)
            throw new ArgumentException("Key changes require unique positive measure numbers and explicit tonic.");
        // 首调模式必须从第 1 小节就给出调，否则开头的音不知道拿谁当 do。
        if (Mode == NumberingMode.MovableDo && !Keys.Any(k => k.StartMeasure == 1))
            throw new ArgumentException("Movable do requires an explicit key starting at measure 1.");
    }
}
