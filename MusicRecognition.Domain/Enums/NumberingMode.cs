namespace MusicRecognition.Domain.Enums;

// 简谱数字（label）的定调方式（numbering mode）：同一个音在不同模式下会写成不同数字。
// 固定 C（FixedC）：C 永远记 1，不看调号；首调（MovableDo）：do 跟着调号走，G 调里 G 记 1。
// 用户在校对页切换后，整份谱的数字要重算，所以这项设置随谱面快照一起保存。
public enum NumberingMode { FixedC, MovableDo }
