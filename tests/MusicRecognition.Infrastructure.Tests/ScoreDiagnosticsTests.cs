using MusicRecognition.Application;
using MusicRecognition.Infrastructure;
using Xunit;

public class ScoreDiagnosticsTests
{
    [Fact]
    public async Task Existing_page_tasks_use_the_parent_pdf_name_and_keep_correlation_fields()
    {
        var parent = new ScoreJob { Id = Guid.NewGuid(), FileName = "六页钢琴谱.pdf", PageCount = 6 };
        var page = new ScoreJob { Id = Guid.NewGuid(), ParentId = parent.Id, FileName = "Page 3.pdf", PageNumber = 3 };
        var info = await ScoreDiagnostics.ResolveAsync(new DiagnosticScoreRepository(parent, page), page, CancellationToken.None);
        Assert.Equal("六页钢琴谱.pdf - 第3页", info.ScoreName);
        Assert.Equal(parent.Id, info.Scope()["DocumentId"]);
        Assert.Equal(page.Id, info.Scope()["ScoreId"]);
        Assert.Equal(3, info.Scope()["PageNumber"]);
    }

    [Theory]
    [InlineData(6, false, "整份文档")]
    [InlineData(1, true, "第1页")]
    public async Task Document_and_single_page_names_are_distinct(int pageCount, bool prepared, string suffix)
    {
        var job = new ScoreJob { Id = Guid.NewGuid(), FileName = "曲谱.pdf", PageCount = pageCount, PagesPrepared = prepared };
        var info = await ScoreDiagnostics.ResolveAsync(new DiagnosticScoreRepository(job), job, CancellationToken.None);
        Assert.Equal($"曲谱.pdf - {suffix}", info.ScoreName);
    }

    [Theory]
    [InlineData("C:\\private\\曲谱\r\n.pdf", "曲谱  .pdf")]
    [InlineData("/private/曲谱.pdf", "曲谱.pdf")]
    [InlineData("\u202e.pdf", ".pdf")]
    [InlineData("", "未命名乐谱")]
    public void Display_names_remove_paths_and_log_control_characters(string input, string expected) =>
        Assert.Equal(expected, ScoreDiagnostics.SafeFileName(input));
}
