namespace MusicRecognition.Application.Enums;

// 识别前的图像预处理（preprocessing）选项，主要针对光线不匀、偏暗的拍照或扫描件。
// None 直接把原图送识别；Adaptive 先用 OpenCV 做自适应阈值等处理，可能增强对比，也可能伤到细线。
// 默认 None：不改变几何、结果更可预期；用户可以在上传时主动选 Adaptive。
public enum PreprocessingMode { None, Adaptive }
