namespace MusicRecognition.Domain.Enums;

// 调式（scale）的类型：大调（Major）和自然小调（NaturalMinor）。
// 它决定首调换算时拿哪个音当 do：大调直接拿主音当 do；
// 自然小调习惯按"关系大调"记，例如 A 小调仍把 C 当 do，所以换算前要先挪基准。
public enum ScaleKind { Major, NaturalMinor }
