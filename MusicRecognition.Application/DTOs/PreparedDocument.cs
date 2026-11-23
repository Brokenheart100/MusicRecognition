namespace MusicRecognition.Application.DTOs;

// 拆页结果（prepared document）：一份文档先数清有多少页，后面的任务按页拆分。
// 页数由识别引擎的文档脚本给出：PDF 按真实页数算，单张图片固定算 1 页。
// 这里只带回页数，不保存文件内容——拆好的页面文件留在任务自己的目录里。
public sealed record PreparedDocument(int PageCount);
