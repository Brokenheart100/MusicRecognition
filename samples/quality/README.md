# P4 独立质量集

本阶段固定三组真实输入进行回归：Mutopia《小步舞曲》1 页、用户《My Heart Will Go On》3 页、用户《ninelie》6 页，共 10 页。前者公开来源与原件哈希见 `samples/README.md`；后两者是用户本地验证文件，不将私人 PDF 加入源码或下载到外部服务。

运行 `tools/evaluation/p4-quality-matrix.py --url <Aspire API 地址>` 会从忽略目录的验证凭据读取各页保存修订，生成源文件哈希、符头总数、MusicXML 关联数、Verovio MEI / SVG ID 覆盖及导入准入结果。原谱墨迹保护与重排图形避让属于不同验收集，不能共用坐标或混算。

## 人工真值要求

当前各组没有完整、独立、经人审核的音符真值，报告明确保持 `pitchAccuracy`、`associationAccuracy`、`detectionRecall` 为 null，不把引擎分数、C# 校对值或 MusicXML 自我一致性当正确率。

人工参考文件至少包含：

```json
{
  "sourceSha256": "原始单页 PDF 的 SHA256",
  "page": 1,
  "completePage": false,
  "reviewedBy": null,
  "reviewedAt": null,
  "coordinateSystem": "recognition-raster-pixels",
  "width": 1786,
  "height": 2526,
  "notes": [
    {"truthId": "独立编号", "bounds": [100, 100, 12, 9], "step": "C", "alter": 0, "octave": 4}
  ]
}
```

例子只是格式，不是真实参考答案。`truthId` 不引用 Audiveris interId；标注人必须直接对照未标注原谱，覆盖没有被引擎识别的符头，并记录升降音拼写与实际八度。部分小节真值只允许报告该小节分母，不能扩展成整页准确率。

人工参考补齐后，分别统计音高正确数 / 可比符头、关联正确数 / 已关联符头、检出符头 / 独立真值符头、额外误检及版面碰撞。模糊几何匹配需单列待复核，不应按音高或音符顺序凑成正确匹配。

需要优先补齐的类型：双谱表、密集和弦、同音不同八度、调号取消与转调、多声部、8va/8vb、连线、带指法或歌词、扫描质量变差。重排验证仍要单列导入丢失，不把渲染器丢掉的音符算成 OMR 漏识别。
