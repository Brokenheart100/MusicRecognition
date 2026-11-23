package musicrecognition.poc;

import java.awt.Rectangle;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.SortedSet;
import java.util.IdentityHashMap;
import java.util.HashMap;
import java.util.Map;
import org.audiveris.omr.score.Score;
import org.audiveris.omr.score.ScoreExporter;
import org.audiveris.omr.sheet.rhythm.MeasureStack;
import org.audiveris.omr.sheet.Book;
import org.audiveris.omr.sheet.Sheet;
import org.audiveris.omr.sheet.SheetStub;
import org.audiveris.omr.sheet.Staff;
import org.audiveris.omr.sheet.SystemInfo;
import org.audiveris.omr.sheet.rhythm.Measure;
import org.audiveris.omr.sheet.rhythm.SlotVoice;
import org.audiveris.omr.sig.inter.HeadInter;
import org.audiveris.omr.sig.inter.Inter;
import org.audiveris.omr.sig.inter.KeyInter;
import org.audiveris.omr.sig.inter.OctaveShiftInter;
import org.audiveris.omr.sig.inter.WedgeInter;
import org.audiveris.omr.step.RunClass;
import org.audiveris.omr.util.HorizontalSide;
// 这个文件是 Audiveris 引擎的「取数插件」：Audiveris 识别完乐谱后，
// 由它把每个音符的音高和画在纸上的坐标一起导出成 notes-<页号>.json。
// 为什么不用 Audiveris 自带的 MusicXML 导出？因为那会丢掉符头坐标，
// 而本项目要把简谱写回原图正确位置，音高和坐标必须来自同一次识别的同一个符头。
// 注意：本文件按 Audiveris 5.11.0 的内部 API 编写，升级引擎前要先核对签名。

/** Audiveris 5.11.0-only adapter; reads geometry and pitch from the SAME HeadInter. */
public final class ExportNotes extends RunClass {
    // 构造时拿到整本书（book）和要处理的页号集合（sheetIds），页号来自命令行参数。
    public ExportNotes(Book book, SortedSet<Integer> sheetIds) {
        super(book, sheetIds);
    }

    // 导出「每一小节、每个谱表当时生效的调号」（fifths：0 是 C 大调，正数升号、负数降号）。
    // 为什么要自己算？Audiveris 的 getKeyBefore 只在当前物理谱表里往前找，
    // 换谱表或换行时会漏；调号一错，音符的升降和简谱数字都会跟着错。
    // Mirror PartwiseBuilder.processKeys/processKeyVoid: an absent key on one
    // staff does not reset that staff while another staff has an explicit key.
    // Measure.getKeyBefore only searches the current physical staff/system.
    // Ignore KeyInter entries with null fifths: processKey catches their conversion
    // failure and leaves the previous staff key intact. Never infer a key from
    // another staff; the original HeadInter still supplies accidentals and ties.
    private static Map<Measure, Map<Integer, Integer>> exportKeys(Sheet sheet) {
        // states 记每个声部组每个谱表当前的调号，跨小节沿用；result 存每个小节的快照。
        var states = new HashMap<Integer, Map<Integer, Integer>>();
        var result = new IdentityHashMap<Measure, Map<Integer, Integer>>();
        // 逐行、逐声部组、逐小节扫描；cautionary 小节是排版提示，不代表正式内容，跳过。
        for (SystemInfo system : sheet.getSystems()) {
            for (var systemPart : system.getParts()) {
                boolean first = true;
                for (Measure measure : systemPart.getMeasures()) {
                    if (measure.getStack().isCautionary()) continue;
                    var part = measure.getPart();
                    var state = states.computeIfAbsent(part.getId(), ignored -> new HashMap<>());
                    int staffCount = part.getStaves().size();
                    // 本小节写了调号：所有谱表调号相同就统一更新，不同就按谱表各取各的。
                    // 只有取到 fifths 才更新状态；取不到时保留上值（对应下面英文注释的约定）。
                    if (measure.hasKeys()) {
                        if (measure.hasSameKeys()) {
                            KeyInter common = measure.getKey(0);
                            Integer fifths = common == null ? null : common.getFifths();
                            if (fifths != null) for (int index = 0; index < staffCount; index++) state.put(index, fifths);
                        } else {
                            for (int index = 0; index < staffCount; index++) {
                                KeyInter key = measure.getKey(index);
                                Integer fifths = key == null ? null : key.getFifths();
                                if (fifths != null) state.put(index, fifths);
                            }
                        }
                    // 该声部组在这行里的第一个小节没写调号，就按 C 大调（0 个升降号）起算。
                    } else if (first) {
                        for (int index = 0; index < staffCount; index++) state.put(index, 0);
                    }
                    // 每个小节存一份状态副本，后面的变化不会改到已存的快照。
                    result.put(measure, new HashMap<>(state));
                    // 加 -Dmusicrecognition.debugKeys=true 参数时打印调号上下文，排查用。
                    if (Boolean.getBoolean("musicrecognition.debugKeys"))
                        System.out.println("KEY_CONTEXT system=" + system.getId() + " part=" + part.getId() + " staves=" + part.getStaves().size() + " measure=" + measure.getStack().getScoreId(0) + " state=" + state);
                    first = false;
                }
            }
        }
        return result;
    }

    // 判断一个渐强/渐弱记号（wedge，俗称「发卡」）是否缺时间坐标。
    // 缺时间坐标会让 Audiveris 5.11.0 的 MusicXML 导出报错，下面要专门绕开。
    private static boolean untimedWedge(WedgeInter wedge) {
        // 任一端挂到的和弦没有时间偏移（timeOffset 为空），就算缺时间坐标。
        for (HorizontalSide side : HorizontalSide.values()) {
            var chord = wedge.getChord(side);
            if (chord != null && chord.getTimeOffset() == null) return true;
        }
        return false;
    }

    // 这里是绕过 Audiveris 的已知缺陷（issue #1098）：遇到缺时间坐标的渐强/渐弱记号，
    // 官方导出会抛异常并丢掉整小节。做法是在内存里删掉这些记号，再单独导出兼容版。
    // 磁盘上的 score.omr 和原 PDF 保持不动，作为原始证据；被删的记号记进警告。
    // Audiveris 5.11.0 issue #1098: WedgeIterators throws on null time offsets,
    // removing a whole measure from MusicXML. Suppress ONLY those dynamics in
    // an isolated export-work.omr model, then use the official exporter again.
    // The caller preserves score.omr and the PDF as immutable source evidence.
    private List<String> repairMusicXmlExport(Path output) throws IOException {
        var stubs = book.getStubs(sheetIds);
        // 先扫一遍所有选中页，确认真的存在这种坏记号；没有就完全不动，走官方导出。
        boolean needed = false;
        for (SheetStub stub : stubs)
            for (SystemInfo system : stub.getSheet().getSystems())
                for (Inter inter : system.getSig().inters(WedgeInter.class))
                    if (untimedWedge((WedgeInter) inter)) needed = true;
        // repairs 收集这次做了哪些修补，最终会作为警告出现在结果里。
        var repairs = new ArrayList<String>();
        // 没有坏记号就提前返回，走官方导出，不额外生成文件。
        if (!needed) return repairs;
        var scores = new ArrayList<Score>();
        // 把选中的页重新汇编成一份单谱 Score，后面的导出必须基于它。
        if (!book.transcribe(stubs, scores, false) || scores.size() != 1)
            throw new IOException("Cannot prepare single-score compatibility export");
        // 在内存模型里逐个去掉坏记号（不动磁盘文件），同时登记修补记录。
        for (SheetStub stub : stubs) {
            for (SystemInfo system : stub.getSheet().getSystems()) {
                for (Inter inter : new ArrayList<>(system.getSig().inters(WedgeInter.class))) {
                    WedgeInter wedge = (WedgeInter) inter;
                    // 只删 Audiveris 认出的那类坏记号，其它记号保持原样。
                    if (untimedWedge(wedge)) {
                        repairs.add("\"musicxml-untimed-wedge-suppressed:" + wedge.getId() + "\"");
                        wedge.remove();
                    }
                }
            }
        }
        // 如果官方导出已经生成过 score.mxl，先复制一份留底，方便对比。
        Path target = output.resolve("score.mxl");
        if (Files.exists(target)) Files.copy(target, output.resolve("engine-original-score.mxl"));
        // 用官方导出器重新导出；坏记号已摘除，这次不会再触发异常。
        try { new ScoreExporter(scores.get(0)).export(target, "score", false, true); }
        catch (Exception e) { throw new IOException("MusicXML compatibility export failed", e); }
        // 用固定前缀把修补清单打到标准输出，供 Worker 解析。
        System.out.println("MUSICRECOGNITION_EXPORT_COMPATIBILITY=" + String.join(",", repairs));
        return repairs;
    }

    // Audiveris 在整本书识别完成后会调用这里。
    // 输出目录来自 JVM 参数 musicrecognition.output，由外部启动脚本传入。
    @Override public void process() {
        Path output = Path.of(System.getProperty("musicrecognition.output"));
        try {
            // 确保输出目录存在（任务目录可能只建到上一级）。
            Files.createDirectories(output);
            // ① 先处理 MusicXML 导出兼容问题，得到的警告会并进每一页。
            List<String> exportRepairs = repairMusicXmlExport(output);
            // ② 逐页导出，每页写一个 notes-<页号>.json。
            for (SheetStub stub : book.getStubs(sheetIds)) {
                Sheet sheet = stub.getSheet();
                var keys = exportKeys(sheet);
                List<String> notes = new ArrayList<>();
                List<String> unsupported = new ArrayList<>();
                List<String> warnings = new ArrayList<>();
                warnings.addAll(exportRepairs);
                List<String> shifts = new ArrayList<>();
                List<String> staves = new ArrayList<>();
                // headCount 记录这页一共遇到多少符头，包括后面算作不支持的那些。
                int headCount = 0;
                // 给本页每个谱行编一个从 1 开始的显示小节号：从上到下、从左到右。
                // 这是给人看的顺序号，不等同于 MusicXML 里的原始小节号。
                var measureNumbers = new IdentityHashMap<MeasureStack, Integer>();
                int nextMeasure = 1;
                for (SystemInfo system : sheet.getSystems()) {
                    // 先给本页所有谱行编号，后面取号就不用再算。
                    for (MeasureStack stack : system.getStacks()) measureNumbers.put(stack, nextMeasure++);
                }
                // ③ 导出五线谱位置和音符。
                for (SystemInfo system : sheet.getSystems()) {
                    // 每条五线谱记左右两端的横坐标，以及上下两条谱线在这两端的纵坐标，
                    // 这样倾斜的谱面也能画准。
                    for (Staff staff : system.getStaves()) {
                        int left = staff.getAbscissa(HorizontalSide.LEFT), right = staff.getAbscissa(HorizontalSide.RIGHT);
                        // 手拼 JSON 是为了不引入序列化库；数字用 Locale.ROOT 保证小数点是点。
                        staves.add(String.format(Locale.ROOT,
                            "{\"id\":%d,\"system\":%d,\"left\":%d,\"right\":%d,\"top\":[%d,%d],\"bottom\":[%d,%d]}",
                            staff.getId(), system.getId(), left, right, staff.getFirstLine().yAt(left), staff.getFirstLine().yAt(right), staff.getLastLine().yAt(left), staff.getLastLine().yAt(right)));
                    }
                    // 遍历这一行的所有识别对象（Inter），先挑八度记号，再挑符头。
                    for (Inter inter : system.getSig().vertexSet()) {
                        if (inter.isRemoved()) continue;
                        // 八度记号（8va/8vb）：记下所属谱表、标注值、平移八度数和覆盖范围。
                        if (inter instanceof OctaveShiftInter os) {
                            Rectangle b = os.getBounds();
                            shifts.add(String.format(Locale.ROOT,
                                "{\"id\":%d,\"staff\":%d,\"value\":%d,\"shift\":%d,\"bounds\":[%d,%d,%d,%d]}",
                                os.getId(), os.getStaff().getId(), os.getValue(), os.getShift(), b.x,b.y,b.width,b.height));
                            // 左右两端没同时挂到和弦的八度记号会被导出逻辑忽略，提前记警告。
                            if (os.getChord(HorizontalSide.LEFT) == null || os.getChord(HorizontalSide.RIGHT) == null) {
                                // PartwiseBuilder.getOctaveShift also ignores spans without both chords.
                                warnings.add("\"unattached-octave-shift:" + inter.getId() + "\"");
                            }
                        }
                        // 只处理符头（HeadInter）：它是「一个音画在纸上的椭圆」，音高坐标都从它来。
                        if (!(inter instanceof HeadInter head)) continue;
                        headCount++;
                        // 符头没挂上和弦/小节，或没有谱表/包围框，就无法定位，记为不支持项。
                        if (head.getChord() == null || head.getChord().getMeasure() == null
                                || head.getStaff() == null || head.getBounds() == null) {
                            unsupported.add("\"unlinked-head:" + head.getId() + "\"");
                            continue;
                        }
                        Staff staff = head.getStaff();
                        // 单线谱是打击乐之类的无音高记谱，不参与简谱标注。
                        if (staff.isOneLineStaff()) {
                            unsupported.add("\"unpitched-head:" + head.getId() + "\"");
                            continue;
                        }
                        // 音高要按「当时生效的调号」算升降：先取 Audiveris 自己的 keyBefore，
                        // 再取 exportKeys 算好的快照；两者不一致时以快照为准并记警告。
                        Measure measure = head.getChord().getMeasure();
                        KeyInter key = measure.getKeyBefore(staff);
                        Integer sourceFifths = key == null ? null : key.getFifths();
                        var keySnapshot = keys.get(measure);
                        Integer fifths = keySnapshot == null ? null : keySnapshot.get(staff.getIndexInPart());
                        // 有调号对象却取不到 fifths，说明这个调号是坏的，记一条警告。
                        if (key != null && sourceFifths == null)
                            warnings.add("\"invalid-key-signature:" + key.getId() + ":head:" + head.getId() + "\"");
                        // 符头包围框（bounds）就是最终写进标注位置的原图像素框。
                        Rectangle b = head.getBounds();
                        // 默认没有八度记号；下面扫这一行的八度记号，看哪个盖住了当前符头。
                        int octaveShift = 0;
                        for (Inter candidate : system.getSig().inters(OctaveShiftInter.class)) {
                            OctaveShiftInter os = (OctaveShiftInter) candidate;
                            var first = os.getChord(HorizontalSide.LEFT);
                            var last = os.getChord(HorizontalSide.RIGHT);
                            // 横向范围用和弦中心横坐标比较：夹在中间才算盖住当前符头。
                            if (os.getStaff() == staff && first != null && last != null
                                && head.getChord().getCenter().x >= first.getCenter().x && head.getChord().getCenter().x <= last.getCenter().x) {
                                // 同一符头落在两个八度记号里且数值不同，无法判断，记为不支持。
                                if (octaveShift != 0 && octaveShift != os.getShift()) unsupported.add("\"conflicting-octave-shift:" + head.getId() + "\"");
                                octaveShift = os.getShift();
                            }
                        }
                        // 音符没解析出节奏声部，说明这小节的节奏识别不完整，记警告。
                        if (head.getChord().getVoice() == null) warnings.add("\"unresolved-rhythm:" + head.getId() + "\"");
                        // 统计这个和弦在 MusicXML 里会被写出几次：引擎可能把同一和弦塞进多个声部。
                        // 超过一次就记警告，避免下游把「一次发声」当成「多个音符」重复标注。
                        // Mirror PartwiseBuilder.processMeasure: the engine can put the
                        // SAME chord in BEGIN slots of more than one voice. Record that
                        // provenance so XML repetitions never become duplicate annotations.
                        int xmlOccurrences = 0;
                        // 整小节休止和普通小节分开算，看这个和弦在哪条路径上被当作起点。
                        for (var exportVoice : measure.getVoices()) {
                            if (exportVoice.isMeasureRest()) {
                                if (exportVoice.getWholeChord() == head.getChord()) xmlOccurrences++;
                            } else {
                                for (var slot : measure.getStack().getSlots()) {
                                    var info = exportVoice.getSlotInfo(slot);
                                    // 只有和弦落在某声部的起点（BEGIN）时，MusicXML 才会写一次。
                                    if (info != null && info.status == SlotVoice.ChordStatus.BEGIN && info.chord == head.getChord()) xmlOccurrences++;
                                }
                            }
                        }
                        if (xmlOccurrences > 1) warnings.add("\"duplicate-xml-export:" + head.getId() + ":" + xmlOccurrences + "\"");
                        // 把一条标注拼成 JSON。关键字段：step/alter/octave 是写出来的音高，
                        // writtenOctave 是记谱八度，octaveShift 是八度记号的额外偏移，
                        // bounds 是符头像素框；grade 是引擎给的置信度。
                        notes.add(String.format(Locale.ROOT,
                            "{\"interId\":%d,\"system\":%d,\"staff\":%d,\"staffInPart\":%d,"
                            + "\"measure\":%d,\"part\":%d,\"voice\":%d,\"chordId\":%d,\"step\":\"%s\",\"alter\":%d,\"octave\":%d,"
                            + "\"writtenOctave\":%d,\"octaveShift\":%d,\"chordX\":%d,\"xmlMeasure\":\"%s\",\"xmlX\":%d,\"xmlExportOccurrences\":%d,\"fifths\":%s,\"sourceFifths\":%s,\"sourceAlter\":%d,\"grade\":%.6f,\"bounds\":[%d,%d,%d,%d]}",
                            head.getId(), system.getId(), staff.getId(), staff.getIndexInPart(),
                            measureNumbers.get(measure.getStack()), measure.getPart().getId(),
                            head.getChord().getVoice() == null ? 0 : head.getChord().getVoice().getId(),
                            // getAlteration(fifths) 按调号算出这个音实际该升还是降。
                            head.getChord().getId(), head.getStep().name(), head.getAlteration(fifths),
                            head.getOctave() + octaveShift, head.getOctave(), octaveShift, head.getChord().getCenter().x, measure.getStack().getScoreId(0),
                            // xmlX 以「十分之一行距」为单位：用整数传输，前端再换算成像素。
                            (int) Math.rint((10f * (head.getCenterLeft().x - measure.getAbscissa(HorizontalSide.LEFT, staff))) / sheet.getInterline()), xmlOccurrences, fifths == null ? "null" : fifths.toString(),
                            sourceFifths == null ? "null" : sourceFifths.toString(), head.getAlteration(sourceFifths),
                            head.getGrade(), b.x, b.y, b.width, b.height));
                    }
                }
                // ④ 把这一页的符头、警告、五线谱、八度记号汇总成一个 JSON。
                String json = String.format(Locale.ROOT,
                    // 字符串模板里字段顺序固定，前端可以直接解析。
                    "{\"engine\":\"Audiveris 5.11.0\",\"sheet\":%d,\"width\":%d,\"height\":%d,"
                    + "\"interline\":%d,\"headCount\":%d,\"unsupported\":[%s],\"warnings\":[%s],\"octaveShifts\":[%s],\"staves\":[%s],\"notes\":[%s]}%n",
                    stub.getNumber(), sheet.getWidth(), sheet.getHeight(), sheet.getInterline(),
                    headCount, String.join(",", unsupported), String.join(",", warnings), String.join(",", shifts), String.join(",", staves), String.join(",\n", notes));
                // 文件名按页号：notes-1.json、notes-2.json……下游按页读取。
                Path target = output.resolve("notes-" + stub.getNumber() + ".json");
                Files.writeString(target, json);
                // 打印固定前缀和条数，Worker 靠这一行确认导出成功。
                System.out.println("MUSICRECOGNITION_NOTES=" + target + " count=" + notes.size());
            }
        // IO 错误统一转成运行时异常，让 Audiveris 以非零码退出，Worker 能感知失败。
        } catch (IOException e) {
            throw new IllegalStateException("Cannot export note anchors", e);
        }
    }
}
