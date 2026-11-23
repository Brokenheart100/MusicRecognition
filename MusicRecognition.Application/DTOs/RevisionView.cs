using MusicRecognition.Domain.ValueObjects;

namespace MusicRecognition.Application.DTOs;

// 修订视图（revision view）：接口返回某一次修订的完整谱面，前端据此画出校对页。
// 每次保存修改都会生成新版本，旧版本永久保留，所以要带上版本号和状态用于定位。
// 状态可能是 Queued/Processing/Ready/Failed：刚保存的修订要先等后台渲染完成。
public sealed record RevisionView(int Version, string State, ScoreSnapshot Score, string? ErrorCode);
