"""Check each XML-exported note against its original Audiveris head anchor.

Rhythm-unassigned heads are not omitted: they remain in the annotation model and
are counted separately because Audiveris exports voice/slot notes, not all heads.
"""
from collections import Counter, defaultdict
import xml.etree.ElementTree as ET
from typing import cast
import zipfile


def musicxml_root(path):
    with zipfile.ZipFile(path) as archive:
        if len(archive.infolist()) > 1000:
            raise ValueError("MusicXML archive exceeds entry limit")
        if sum(i.file_size for i in archive.infolist()) > 64*1024*1024 or any(i.file_size > 32*1024*1024 for i in archive.infolist()):
            raise ValueError("MusicXML archive exceeds decompression limits")
        container = ET.fromstring(archive.read("META-INF/container.xml"))
        rootfile = next(e for e in container.iter() if e.tag.endswith("rootfile"))
        return ET.fromstring(archive.read(rootfile.attrib["full-path"]))


def musicxml_pitches(path):
    return Counter((pitch.findtext("step"), int(pitch.findtext("alter", "0")), int(cast(str, pitch.findtext("octave"))), int(note.findtext("staff", "1"))-1)
        for note in musicxml_root(path).findall(".//note") if (pitch := note.find("pitch")) is not None)


def validate_pitches(data, mxl, bindings=None, xml_root=None):
    notes = data["notes"]
    if data.get("octaveShifts") and "octaveRepair" not in data:
        raise ValueError("Octave candidates require source-line recovery in the runtime pipeline")
    if "octaveRepair" in data:
        for note in notes:
            spans = [span for span in data["octaveRepair"]["repairs"] if span["staff"] == note["staff"] and span["x1"] <= note["chordX"] <= span["x2"]]
            shifts = {(span["value"]-1)//7 * (1 if span["kind"] == "ALTA" else -1) for span in spans}
            if len(shifts) > 1 or note["octaveShift"] != next(iter(shifts), 0) or note["octave"] != note["writtenOctave"] + note["octaveShift"]:
                raise ValueError("Sounding octave disagrees with verified span evidence")
    if not all("xmlX" in note for note in notes):
        # Immutable legacy revisions keep their original strict validation policy.
        heads = Counter((n["step"], n["alter"], n["octave"], n["staffInPart"]) for n in notes)
        if heads != musicxml_pitches(mxl):
            raise ValueError("Legacy Audiveris heads and MusicXML pitches disagree")
        return {"policy": "legacy-full-multiset", "matchedMusicXmlNotes": len(notes), "unresolvedRhythmHeads": 0}
    indexed = defaultdict(list)
    for note in notes:
        indexed[(note["part"], note["xmlMeasure"], note["staffInPart"], note["xmlX"])].append(note)
    xml_groups = defaultdict(list)
    def signature(element):
        return (element.tag, tuple(sorted(element.attrib.items())), (element.text or "").strip(), tuple(signature(child) for child in element))
    for part_number, part in enumerate((xml_root if xml_root is not None else musicxml_root(mxl)).findall("part"), 1):
        for measure_number, measure in enumerate(part.findall("measure"), 1):
            for xml in measure.findall("note"):
                pitch = xml.find("pitch")
                if pitch is None:
                    continue
                key = (part_number, measure.attrib["number"], int(xml.findtext("staff", "1"))-1, round(float(xml.attrib["default-x"])))
                step, alter, octave = pitch.findtext("step"), int(pitch.findtext("alter", "0")), int(cast(str, pitch.findtext("octave")))
                voice = int(xml.findtext("voice", "0"))
                xml_groups[key].append((step, alter, octave, voice, signature(xml), xml))
    matched = set()
    normalized = 0
    duplicates = []
    for key, xml_notes in xml_groups.items():
        unique_heads = sorted(indexed[key], key=lambda n: (n["voice"] == 0, n["interId"]))
        heads = [note for note in unique_heads for _ in range(max(1, note.get("xmlExportOccurrences", 1)))]
        edges = [[index for index, n in enumerate(heads)
            if n["step"] == step and n["alter"] == alter and n["voice"] in (0, voice)
            and octave in {n["writtenOctave"], n["writtenOctave"] + n.get("nativeOctaveShift", n["octaveShift"])}]
            for step, alter, octave, voice, _, _ in xml_notes]
        # A chord may contain the same pitch class in different octaves. Find a
        # one-to-one assignment rather than greedily accepting the first head.
        owners = {}
        def assign(xml_index, seen):
            for head_index in edges[xml_index]:
                if head_index in seen:
                    continue
                seen.add(head_index)
                if head_index not in owners or assign(owners[head_index], seen):
                    owners[head_index] = xml_index
                    return True
            return False
        if not all(assign(index, set()) for index in range(len(xml_notes))):
            candidates = [{field: note.get(field) for field in ('interId', 'step', 'alter', 'writtenOctave', 'voice')} for note in unique_heads[:8]]
            expected = [note[:4] for note in xml_notes[:8]]
            raise ValueError(f"MusicXML pitch/voice/anchor mismatch at {key}; part={key[0]} measure={key[1]} staff={key[2]+1} xmlX={key[3]}; xmlNotes(step,alter,octave,voice)={expected}; candidateHeads={candidates}")
        occurrences = defaultdict(list)
        for head_index, xml_index in owners.items():
            note = heads[head_index]
            matched.add(note["interId"])
            normalized += xml_notes[xml_index][2] != note["writtenOctave"]
            occurrences[note["interId"]].append(xml_notes[xml_index][4])
            if bindings is not None:
                bindings.append((xml_notes[xml_index][5], note))
        for note in unique_heads:
            expected = note.get("xmlExportOccurrences", 1)
            actual = occurrences[note["interId"]]
            if expected > 1:
                if len(actual) != expected or len(set(actual)) != 1:
                    raise ValueError("Repeated MusicXML export does not match identical engine voice-slot occurrences")
                duplicates.append({"interId": note["interId"], "chordId": note["chordId"], "occurrences": expected})
    unmatched = [n for n in notes if n["interId"] not in matched]
    missing_assigned = [n for n in unmatched if n['voice'] != 0]
    if missing_assigned:
        details = [{field: note.get(field) for field in ('interId', 'part', 'xmlMeasure', 'staffInPart', 'xmlX', 'step', 'alter', 'writtenOctave', 'voice')} for note in missing_assigned[:8]]
        raise ValueError(f"MusicXML omitted a rhythm-assigned head; missingCount={len(missing_assigned)}; heads(staffInPart is zero-based)={details}")
    if not matched:
        raise ValueError("No independently exported pitches were validated")
    return {"policy": "head-anchor-written-pitch-v2", "matchedMusicXmlNotes": len(matched),
        "unresolvedRhythmHeads": len(unmatched), "nativeOctaveNormalizations": normalized,
        "duplicateMusicXmlOccurrences": sum(item["occurrences"]-1 for item in duplicates), "duplicateExports": duplicates,
        "allHeadsRetained": len(matched) + len(unmatched) == data["headCount"]}
