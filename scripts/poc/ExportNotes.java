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
import org.audiveris.omr.sheet.rhythm.MeasureStack;
import org.audiveris.omr.sheet.Book;
import org.audiveris.omr.sheet.Sheet;
import org.audiveris.omr.sheet.SheetStub;
import org.audiveris.omr.sheet.Staff;
import org.audiveris.omr.sheet.SystemInfo;
import org.audiveris.omr.sheet.rhythm.Measure;
import org.audiveris.omr.sig.inter.HeadInter;
import org.audiveris.omr.sig.inter.Inter;
import org.audiveris.omr.sig.inter.KeyInter;
import org.audiveris.omr.sig.inter.OctaveShiftInter;
import org.audiveris.omr.step.RunClass;
import org.audiveris.omr.util.HorizontalSide;

/** Audiveris 5.11.0-only adapter; reads geometry and pitch from the SAME HeadInter. */
public final class ExportNotes extends RunClass {
    public ExportNotes(Book book, SortedSet<Integer> sheetIds) {
        super(book, sheetIds);
    }

    @Override public void process() {
        Path output = Path.of(System.getProperty("musicrecognition.output"));
        try {
            Files.createDirectories(output);
            for (SheetStub stub : book.getStubs(sheetIds)) {
                Sheet sheet = stub.getSheet();
                List<String> notes = new ArrayList<>();
                List<String> unsupported = new ArrayList<>();
                List<String> warnings = new ArrayList<>();
                int headCount = 0;
                var measureNumbers = new IdentityHashMap<MeasureStack, Integer>();
                int nextMeasure = 1;
                for (SystemInfo system : sheet.getSystems()) {
                    for (MeasureStack stack : system.getStacks()) measureNumbers.put(stack, nextMeasure++);
                }
                for (SystemInfo system : sheet.getSystems()) {
                    for (Inter inter : system.getSig().vertexSet()) {
                        if (inter.isRemoved()) continue;
                        if (inter instanceof OctaveShiftInter os) {
                            if (os.getChord(HorizontalSide.LEFT) != null && os.getChord(HorizontalSide.RIGHT) != null) {
                                unsupported.add("\"octave-shift:" + inter.getId() + "\"");
                            } else {
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
                        Integer fifths = key == null ? null : key.getFifths();
                        Rectangle b = head.getBounds();
                        notes.add(String.format(Locale.ROOT,
                            "{\"interId\":%d,\"system\":%d,\"staff\":%d,\"staffInPart\":%d,"
                            + "\"measure\":%d,\"part\":%d,\"voice\":%d,\"chordId\":%d,\"step\":\"%s\",\"alter\":%d,\"octave\":%d,"
                            + "\"fifths\":%s,\"grade\":%.6f,\"bounds\":[%d,%d,%d,%d]}",
                            head.getId(), system.getId(), staff.getId(), staff.getIndexInPart(),
                            measureNumbers.get(measure.getStack()), measure.getPart().getId(),
                            head.getChord().getVoice() == null ? 0 : head.getChord().getVoice().getId(),
                            head.getChord().getId(), head.getStep().name(), head.getAlteration(fifths),
                            head.getOctave(), fifths == null ? "null" : fifths.toString(),
                            head.getGrade(), b.x, b.y, b.width, b.height));
                    }
                }
                String json = String.format(Locale.ROOT,
                    "{\"engine\":\"Audiveris 5.11.0\",\"sheet\":%d,\"width\":%d,\"height\":%d,"
                    + "\"interline\":%d,\"headCount\":%d,\"unsupported\":[%s],\"warnings\":[%s],\"notes\":[%s]}%n",
                    stub.getNumber(), sheet.getWidth(), sheet.getHeight(), sheet.getInterline(),
                    headCount, String.join(",", unsupported), String.join(",", warnings), String.join(",\n", notes));
                Path target = output.resolve("notes-" + stub.getNumber() + ".json");
                Files.writeString(target, json);
                System.out.println("MUSICRECOGNITION_NOTES=" + target + " count=" + notes.size());
            }
        } catch (IOException e) {
            throw new IllegalStateException("Cannot export note anchors", e);
        }
    }
}
