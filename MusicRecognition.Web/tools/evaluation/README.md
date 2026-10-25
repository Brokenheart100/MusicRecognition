# 前端渲染器评估

在 `MusicRecognition.Web` 中运行，复用已有 `verovio` 依赖，不另建 npm 包或复制依赖。

```powershell
# 最小 MusicXML 自定义音符 ID -> MEI / SVG 保留检查
npm run probe:verovio
# 输入为后端输出的 ReflowView JSON，生成同目录的 *-import.json
npm run evaluate:reflow -- ../output/my-validation/reflow.json
```

这些工具不计算识谱准确率。真实多页覆盖矩阵入口是仓库根目录的 `tools/evaluation/p4-quality-matrix.py`。
