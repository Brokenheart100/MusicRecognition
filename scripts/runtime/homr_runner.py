"""Pinned homr API bridge, executed ONLY by the separately provisioned interpreter.

Does not patch upstream code or download weights during recognition.
The upstream point mapping reverses cropping/resizing back to the input image.
"""
import argparse
import hashlib
import importlib.metadata
import json
from pathlib import Path
import sys
import time
import xml.etree.ElementTree as ET


def runtime_manifest():
    marker = Path(sys.prefix).parent / 'runtime.json'
    if not marker.is_file():
        raise RuntimeError('homr independent runtime is not provisioned; run scripts/setup/setup-homr.ps1')
    manifest = json.loads(marker.read_text(encoding='utf-8'))
    if importlib.metadata.version('homr') != manifest['version']:
        raise RuntimeError('homr installed version differs from the provisioned manifest')
    import homr
    package = Path(homr.__file__).parent
    for model in manifest['models']:
        path = package / model['path']
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != model['sha256']:
            raise RuntimeError('homr model missing or modified: ' + model['path'])
    return manifest


def recognize(image, output):
    started = time.monotonic()
    manifest = runtime_manifest()
    import cv2
    import numpy as np
    import onnxruntime as ort
    from homr.main import ProcessingConfig, detect_staffs_in_image
    from homr.music_xml_generator import XmlGeneratorArguments, generate_xml
    from homr.staff_parsing import parse_staffs
    from homr.system_repair import repair_systems
    from homr.transformer.configs import Config
    ort.set_default_logger_severity(3)
    config = ProcessingConfig(enable_debug=False, enable_cache=False, write_staff_positions=False,
                              read_staff_positions=False, selected_staff=-1, transformer_use_gpu=False,
                              segnet_use_gpu=False, coreml_encoder=False, title_detection=False)
    systems, pixels, debug, title, _, mapping = detect_staffs_in_image(str(image), config)
    try:
        systems = repair_systems(debug, systems)
        heads, staves = [], []
        interlines = []
        for system_index, system in enumerate(systems, 1):
            # homr merges the two piano staves before transformer inference.
            # The annotation layout requires individual five-line staves.
            individual = []
            for staff in system.staffs:
                split = staff.split() if staff.is_grandstaff else None
                if staff.is_grandstaff and split is None:
                    raise ValueError('homr grand staff cannot be split into individual five-line staves')
                individual.extend(split if split else [staff])
            for staff in individual:
                staff_id = len(staves) + 1
                top_left = mapping((staff.min_x, staff.min_y))
                bottom_right = mapping((staff.max_x, staff.max_y))
                origin = mapping((0., 0.))
                unit = mapping((0., float(staff.average_unit_size)))
                interline = abs(unit[1] - origin[1])
                interlines.append(interline)
                first, last = staff.grid[0], staff.grid[-1]
                staves.append(dict(id=staff_id, system=system_index, left=top_left[0], right=bottom_right[0],
                    top=[mapping((first.x, first.y[0]))[1], mapping((last.x, last.y[0]))[1]],
                    bottom=[mapping((first.x, first.y[-1]))[1], mapping((last.x, last.y[-1]))[1]],
                    interline=interline))
                for note in staff.get_notes():
                    corners = [mapping((float(x), float(y))) for x, y in cv2.boxPoints(note.box.box)]
                    x1, y1 = min(p[0] for p in corners), min(p[1] for p in corners)
                    x2, y2 = max(p[0] for p in corners), max(p[1] for p in corners)
                    heads.append(dict(id=len(heads)+1, staff=staff_id, system=system_index,
                                      bounds=[x1, y1, x2-x1, y2-y1]))
        model_config = Config()
        model_config.use_gpu_inference = False
        model_config.use_coreml_encoder = False
        tokens = parse_staffs(debug, systems, pixels, selected_staff=-1, config=model_config,
                              page_to_input_image=mapping)
        xml = generate_xml(XmlGeneratorArguments(), tokens, title.result(60))
        ET.ElementTree(xml).write(output / 'score.musicxml', encoding='utf-8', xml_declaration=True)
        data = dict(schemaVersion=2, engine='homr ' + manifest['version'], commit=manifest['commit'],
                    staves=staves, heads=heads, interline=float(np.median(interlines)),
                    elapsedSeconds=time.monotonic()-started, providers=ort.get_available_providers())
        (output / 'homr-geometry.json').write_text(json.dumps(data, ensure_ascii=False), encoding='utf-8')
    finally:
        debug.clean_debug_files_from_previous_runs()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--image', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if args.check:
        print(json.dumps(runtime_manifest(), ensure_ascii=False))
    else:
        if not args.image or not args.output:
            parser.error('--image and --output are required')
        args.output.mkdir(parents=True, exist_ok=True)
        recognize(args.image, args.output)
