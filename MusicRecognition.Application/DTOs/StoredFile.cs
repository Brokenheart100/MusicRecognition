namespace MusicRecognition.Application.DTOs;

// 待下载文件（stored file）：找到产物后，告诉上层文件在哪、是什么类型、下载时用什么名字。
// Path 是服务器本地路径，ContentType 是 HTTP 响应的 MIME 类型，DownloadName 是浏览器保存的文件名。
// 真正读取文件、设置下载响应头由 API 层完成，应用层只负责定位和命名。
public sealed record StoredFile(string Path, string ContentType, string DownloadName);
