namespace MusicRecognition.Domain.Enums;

// 音名（step）：五线谱上七个基本音级的代号，C D E F G A B 对应简谱的 1~7 级。
// 它只回答"是第几个白键"，不带升降、不带八度，要和 Pitch 里另外两项合起来才能定位一个音。
// 所有乐理换算都先看音名，再补升降号和八度点。
public enum Step { C, D, E, F, G, A, B }
