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

/** Audiveris 5.11.0-only adapter; reads geometry and pitch from the SAME HeadInter. */
public final class ExportNotes extends RunClass {
    public ExportNotes(Book book, SortedSet<Integer> sheetIds) {
        super(book, sheetIds);
    }

    // Mirror PartwiseBuilder.processKeys/processKeyVoid: an absent key on one
    // staff does not reset that staff while another staff has an explicit key.
    // Measure.getKeyBefore only searches the current physical staff/system.
    // Ignore KeyInter entries with null fifths: processKey catches their conversion
    // failure and leaves the previous staff key intact. Never infer a key from
    // another staff; the original HeadInter still supplies accidentals and ties.
    private static Map<Measure, Map<Integer, Integer>> exportKeys(Sheet sheet) {
        var states = new HashMap<Integer, Map<Integer, Integer>>();
        var result = new IdentityHashMap<Measure, Map<Integer, Integer>>();
        for (SystemInfo system : sheet.getSystems()) {
            for (var systemPart : system.getParts()) {
                boolean first = true;
                for (Measure measure : systemPart.getMeasures()) {
                    if (measure.getStack().isCautionary()) continue;
                    var part = measure.getPart();
                    var state = states.computeIfAbsent(part.getId(), ignored -> new HashMap<>());
                    int staffCount = part.getStaves().size();
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
                    } else if (first) {
                        for (int index = 0; index < staffCount; index++) state.put(index, 0);
                    }
                    result.put(measure, new HashMap<>(state));
                    if (Boolean.getBoolean("musicrecognition.debugKeys"))
                        System.out.println("KEY_CONTEXT system=" + system.getId() + " part=" + part.getId() + " staves=" + part.getStaves().size() + " measure=" + measure.getStack().getScoreId(0) + " state=" + state);
                    first = false;
                }
            }
        }
        return result;
    }

    private static boolean untimedWedge(WedgeInter wedge) {
        for (HorizontalSide side : HorizontalSide.values()) {
            var chord = wedge.getChord(side);
            if (chord != null && chord.getTimeOffset() == null) return true;
        }
        return false;
    }

    // Audiveris 5.11.0 issue #1098: WedgeIterators throws on null time offsets,
    // removing a whole measure from MusicXML. Suppress ONLY those dynamics in
    // an isolated export-work.omr model, then use the official exporter again.
    // The caller preserves score.omr and the PDF as immutable source evidence.
    private List<String> repairMusicXmlExport(Path output) throws IOException {
        var stubs = book.getStubs(sheetIds);
        boolean needed = false;
        for (SheetStub stub : stubs)
            for (SystemInfo system : stub.getSheet().getSystems())
                for (Inter inter : system.getSig().inters(WedgeInter.class))
                    if (untimedWedge((WedgeInter) inter)) needed = true;
        var repairs = new ArrayList<String>();
        if (!needed) return repairs;
        var scores = new ArrayList<Score>();
        if (!book.transcribe(stubs, scores, false) || scores.size() != 1)
            throw new IOException("Cannot prepare single-score compatibility export");
        for (SheetStub stub : stubs) {
            for (SystemInfo system : stub.getSheet().getSystems()) {
                for (Inter inter : new ArrayList<>(system.getSig().inters(WedgeInter.class))) {
                    WedgeInter wedge = (WedgeInter) inter;
                    if (untimedWedge(wedge)) {
                        repairs.add("\"musicxml-untimed-wedge-suppressed:" + wedge.getId() + "\"");
                        wedge.remove();
                    }
                }
            }
        }
        Path target = output.resolve("score.mxl");
        if (Files.exists(target)) Files.copy(target, output.resolve("engine-original-score.mxl"));
        try { new ScoreExporter(scores.get(0)).export(target, "score", false, true); }
        catch (Exception e) { throw new IOException("MusicXML compatibility export failed", e); }
        System.out.println("MUSICRECOGNITION_EXPORT_COMPATIBILITY=" + String.join(",", repairs));
        return repairs;
    }

    @Override public void process() {
        Path output = Path.of(System.getProperty("musicrecognition.output"));
        try {
            Files.createDirectories(output);
            List<String> exportRepairs = repairMusicXmlExport(output);
            for (SheetStub stub : book.getStubs(sheetIds)) {
                Sheet sheet = stub.getSheet();
                var keys = exportKeys(sheet);
                List<String> notes = new ArrayList<>();
                List<String> unsupported = new ArrayList<>();
                List<String> warnings = new ArrayList<>();
                warnings.addAll(exportRepairs);
                List<String> shifts = new ArrayList<>();
                List<String> staves = new ArrayList<>();
                int headCount = 0;
                var measureNumbers = new IdentityHashMap<MeasureStack, Integer>();
                int nextMeasure = 1;
                for (SystemInfo system : sheet.getSystems()) {
                    for (MeasureStack stack : system.getStacks()) measureNumbers.put(stack, nextMeasure++);
                }
                for (SystemInfo system : sheet.getSystems()) {
                    for (Staff staff : system.getStaves()) {
                        int left = staff.getAbscissa(HorizontalSide.LEFT), right = staff.getAbscissa(HorizontalSide.RIGHT);
                        staves.add(String.format(Locale.ROOT,
                            "{\"id\":%d,\"system\":%d,\"left\":%d,\"right\":%d,\"top\":[%d,%d],\"bottom\":[%d,%d]}",
                            staff.getId(), system.getId(), left, right, staff.getFirstLine().yAt(left), staff.getFirstLine().yAt(right), staff.getLastLine().yAt(left), staff.getLastLine().yAt(right)));
                    }
                    for (Inter inter : system.getSig().vertexSet()) {
                        if (inter.isRemoved()) continue;
                        if (inter instanceof OctaveShiftInter os) {
                            Rectangle b = os.getBounds();
                            shifts.add(String.format(Locale.ROOT,
                                "{\"id\":%d,\"staff\":%d,\"value\":%d,\"shift\":%d,\"bounds\":[%d,%d,%d,%d]}",
                                os.getId(), os.getStaff().getId(), os.getValue(), os.getShift(), b.x,b.y,b.width,b.height));
                            if (os.getChord(HorizontalSide.LEFT) == null || os.getChord(HorizontalSide.RIGHT) == null) {
                                // PartwiseBuilder.getOctaveShift also ignores spans without both chords.
                                warnings.add("\"unattached-octave-shift:" + inter.getId() + "\"");
                            }
                        }
                        if (!(inter instanceof HeadInter head)) continue;
                        headCount++;
                        if (head.getChord() == null || head.getChord().getMeasure() == null
                                || head.getStaff() == null || head.getBounds() == null) {
                            unsupported.add("\"unlinked-head:" + head.getId() + "\"");
                            continue;
                        }
                        Staff staff = head.getStaff();
                        if (staff.isOneLineStaff()) {
                            unsupported.add("\"unpitched-head:" + head.getId() + "\"");
                            continue;
                        }
                        Measure measure = head.getChord().getMeasure();
                        KeyInter key = measure.getKeyBefore(staff);
                        Integer sourceFifths = key == null ? null : key.getFifths();
                        var keySnapshot = keys.get(measure);
                        Integer fifths = keySnapshot == null ? null : keySnapshot.get(staff.getIndexInPart());
                        if (key != null && sourceFifths == null)
                            warnings.add("\"invalid-key-signature:" + key.getId() + ":head:" + head.getId() + "\"");
                        Rectangle b = head.getBounds();
                        int octaveShift = 0;
                        for (Inter candidate : system.getSig().inters(OctaveShiftInter.class)) {
                            OctaveShiftInter os = (OctaveShiftInter) candidate;
                            var first = os.getChord(HorizontalSide.LEFT);
                            var last = os.getChord(HorizontalSide.RIGHT);
                            if (os.getStaff() == staff && first != null && last != null
                                && head.getChord().getCenter().x >= first.getCenter().x && head.getChord().getCenter().x <= last.getCenter().x) {
                                if (octaveShift != 0 && octaveShift != os.getShift()) unsupported.add("\"conflicting-octave-shift:" + head.getId() + "\"");
                                octaveShift = os.getShift();
                            }
                        }
                        if (head.getChord().getVoice() == null) warnings.add("\"unresolved-rhythm:" + head.getId() + "\"");
                        // Mirror PartwiseBuilder.processMeasure: the engine can put the
                        // SAME chord in BEGIN slots of more than one voice. Record that
                        // provenance so XML repetitions never become duplicate annotations.
                        int xmlOccurrences = 0;
                        for (var exportVoice : measure.getVoices()) {
                            if (exportVoice.isMeasureRest()) {
                                if (exportVoice.getWholeChord() == head.getChord()) xmlOccurrences++;
                            } else {
                                for (var slot : measure.getStack().getSlots()) {
                                    var info = exportVoice.getSlotInfo(slot);
                                    if (info != null && info.status == SlotVoice.ChordStatus.BEGIN && info.chord == head.getChord()) xmlOccurrences++;
                                }
                            }
                        }
                        if (xmlOccurrences > 1) warnings.add("\"duplicate-xml-export:" + head.getId() + ":" + xmlOccurrences + "\"");
                        notes.add(String.format(Locale.ROOT,
                            "{\"interId\":%d,\"system\":%d,\"staff\":%d,\"staffInPart\":%d,"
                            + "\"measure\":%d,\"part\":%d,\"voice\":%d,\"chordId\":%d,\"step\":\"%s\",\"alter\":%d,\"octave\":%d,"
                            + "\"writtenOctave\":%d,\"octaveShift\":%d,\"chordX\":%d,\"xmlMeasure\":\"%s\",\"xmlX\":%d,\"xmlExportOccurrences\":%d,\"fifths\":%s,\"sourceFifths\":%s,\"sourceAlter\":%d,\"grade\":%.6f,\"bounds\":[%d,%d,%d,%d]}",
                            head.getId(), system.getId(), staff.getId(), staff.getIndexInPart(),
                            measureNumbers.get(measure.getStack()), measure.getPart().getId(),
                            head.getChord().getVoice() == null ? 0 : head.getChord().getVoice().getId(),
                            head.getChord().getId(), head.getStep().name(), head.getAlteration(fifths),
                            head.getOctave() + octaveShift, head.getOctave(), octaveShift, head.getChord().getCenter().x, measure.getStack().getScoreId(0),
                            (int) Math.rint((10f * (head.getCenterLeft().x - measure.getAbscissa(HorizontalSide.LEFT, staff))) / sheet.getInterline()), xmlOccurrences, fifths == null ? "null" : fifths.toString(),
                            sourceFifths == null ? "null" : sourceFifths.toString(), head.getAlteration(sourceFifths),
                            head.getGrade(), b.x, b.y, b.width, b.height));
                    }
                }
                String json = String.format(Locale.ROOT,
                    "{\"engine\":\"Audiveris 5.11.0\",\"sheet\":%d,\"width\":%d,\"height\":%d,"
                    + "\"interline\":%d,\"headCount\":%d,\"unsupported\":[%s],\"warnings\":[%s],\"octaveShifts\":[%s],\"staves\":[%s],\"notes\":[%s]}%n",
                    stub.getNumber(), sheet.getWidth(), sheet.getHeight(), sheet.getInterline(),
                    headCount, String.join(",", unsupported), String.join(",", warnings), String.join(",", shifts), String.join(",", staves), String.join(",\n", notes));
                Path target = output.resolve("notes-" + stub.getNumber() + ".json");
                Files.writeString(target, json);
                System.out.println("MUSICRECOGNITION_NOTES=" + target + " count=" + notes.size());
            }
        } catch (IOException e) {
            throw new IllegalStateException("Cannot export note anchors", e);
        }
    }
}
