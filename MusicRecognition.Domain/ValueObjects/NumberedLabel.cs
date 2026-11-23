namespace MusicRecognition.Domain.ValueObjects;

// 简谱数字（numbered label）：一个音换算成简谱后，真正要画在谱上的三部分内容。
// Degree 是 1~7 的级数（1 表示 do）；Alteration 是升降号个数，正数升号、负数降号。
// OctaveDots 是高低八度点的数量，正数画在数字上方，负数画在下方，0 表示不加点。
public sealed record NumberedLabel(int Degree, int Alteration, int OctaveDots);
