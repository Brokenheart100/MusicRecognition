# P0 样谱来源

使用 Audiveris 官方仓库公开样例 `data/examples/BachInvention5.jpg`，内容为 J. S. Bach《二部创意曲》第 5 首，BWV 776，一页双谱表印刷谱。图像页脚标明 **Public Domain**。本项目保留来源与哈希，不把其说明扩展为其他样谱的许可结论。

- 仓库：https://github.com/Audiveris/audiveris
- 固定提交：`7a36078e7ba0c006052c1f661b949cf9b729f505`
- 原件：https://raw.githubusercontent.com/Audiveris/audiveris/7a36078e7ba0c006052c1f661b949cf9b729f505/data/examples/BachInvention5.jpg
- SHA-256：`9ab9a9a8ebf609d60a7d0cddcccd5ffc91d433812fd86153244595371d282805`
- 尺寸：1936 × 2592 像素。
- 选择原因：真实扫描、双谱表、多个系统、升降及还原记号、低八度音符，难度高于无调号单旋律。
- 局限：不适合作为密集同时和弦 / 跨谱表写法的充分验收集；没有整页人工真值。

脚本首次运行把样谱下载到 `output/poc/input/`，文件不混入应用源码。

## 工具来源

- Audiveris `5.11.0` Windows Console 发布包，SHA-256：`5f1b4e96a12c53c7da426814b76e599363c4181e291855996e0a6878dda95f71`，校验值来自 GitHub release asset digest。
- 版本源码接口核对：`app/src/main/java/org/audiveris/omr/step/RunClass.java`、`sig/inter/HeadInter.java`、`sig/inter/AbstractNoteInter.java`、`sheet/rhythm/Measure.java`、`score/PartwiseBuilder.java`，均固定 `5.11.0` tag。
- `-run` 扩展说明：https://audiveris.github.io/audiveris/_pages/guides/advanced/cli/
- 编译适配器使用 Temurin JDK `25.0.4.1+1`；运行使用 Audiveris 自带 Java `25.0.3`。JDK zip SHA-256：`00c847d804f4a78e9f04f2683faf14fed898535b177b7fc704486cb0284e9283`。
- 第三方发布包保留其许可；Audiveris 使用 AGPL-3.0，正式产品集成方式需在发行前确认。当前只是仓库内隔离验证适配器。

## 证据含义

Java 适配器从同一个 `HeadInter` 同时读取 `bounds / step / octave / alteration`，变化音调用引擎的 `getAlteration()`，调号由 `Measure.getKeyBefore()` 取得。因此原谱关联不依赖按音符顺序猜配，也不需要把 MusicXML 排版坐标当像素。

MusicXML 音高多重集合对比只是识别产物的一致性检查，不是与独立人工真值的对比。`grade` 是引擎评分，不是正确率。出现具有完整起止和弦关联的 `OctaveShiftInter` 时适配器仍拒绝加注；没有完整关联、且引擎导出也不应用的候选记录为 warning，不能当作有效 8va / 8vb 忽略。

## 新增真实 PDF：小步舞曲

- 原件：https://www.mutopiaproject.org/ftp/BachJS/BWVAnh114/Minuet-xpose/Minuet-xpose-a4.pdf
- 文件标题：Minuet，BWV Anh. 114；源文件标注 J. S. Bach。
- 排版：LilyPond 2.14.1；源 PDF 页脚注明 Public Domain，排版者 Shamim Mohamed，Mutopia-2011/06/19-258。
- SHA-256：`5f167ba9902c6630514dbe63e4f2a99c04f9de6ff28d88470329b58804436ad7`。
- 真实单页矢量 PDF，A4，MediaBox `[0,0,595.28,841.89]`，原始绘图指令 3,858 条，页内没有图像 XObject。
- 下载原件保存在 `output/poc-pdf/input/Minuet-xpose-a4.pdf`。9 个案例来自同一份原件及其受控几何变体，不是 9 份独立谱子。
- 裁剪框 `[12.25,72.5,583.75,825.25]`：保留乐谱，裁去部分页边与页脚。变体保留来源记录，原件完整保存。

## 保留的失败 PDF：圣母颂

- 原件：https://raw.githubusercontent.com/Audiveris/audiveris/7a36078e7ba0c006052c1f661b949cf9b729f505/data/examples/SchbAvMaSample.pdf
- SHA-256：`b2db823d96c5dce1880f5bc869af9f874f2ac99a1bff761bb3ac2f8a2a4ee896`。
- 官方公开示例，页脚注明 Copyright 2002 Recordare LLC；本项目不将它称为公版。
- 真实单页矢量 PDF，钢琴与声乐混合谱表。引擎检测 437 个符头，但 MusicXML 仅输出 275 个有音高音符；导出日志含 `WedgeIterators` 空时间偏移异常，严格一致性检查阻止了加注。
- 失败证据位于 `output/poc-pdf/base/`；不能据此宣称这份 PDF 已通过标注验证。
