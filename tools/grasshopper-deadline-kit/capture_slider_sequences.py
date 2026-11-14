#! python 3
"""
Rhino Python template for capturing Grasshopper Number Slider sequences.

Run from Rhino with:

    _RunPythonScript /path/to/capture_slider_sequences.py

Edit SLIDERS and SEQUENCES before running. This script intentionally uses real
Grasshopper slider values and active Rhino viewport captures.
"""

import json
import os
import time
import traceback

OUT_DIR = os.environ.get(
    "GH_CAPTURE_OUT",
    os.path.join(os.path.expanduser("~"), "Desktop", "grasshopper-slider-captures"),
)
REPORT_PATH = os.path.join(OUT_DIR, "capture_report.txt")

# Replace these GUIDs with the Number Slider GUIDs from the active definition.
SLIDERS = {
    "depth": "00000000-0000-0000-0000-000000000000",
    "width": "00000000-0000-0000-0000-000000000000",
    "height": "00000000-0000-0000-0000-000000000000",
}

SEQUENCES = [
    {
        "stem": "FINAL_01_DEPTH",
        "frames": [{"depth": 1.0}, {"depth": 3.0}, {"depth": 6.0}, {"depth": 8.0}],
    },
    {
        "stem": "FINAL_02_WIDTH",
        "frames": [{"width": 4.0}, {"width": 6.0}, {"width": 8.0}, {"width": 10.0}],
    },
    {
        "stem": "FINAL_03_HEIGHT",
        "frames": [{"height": 4.0}, {"height": 6.0}, {"height": 8.0}, {"height": 10.0}],
    },
]


def get_doc():
    import Grasshopper

    canvas = Grasshopper.Instances.ActiveCanvas
    if canvas is not None and canvas.Document is not None:
        return canvas.Document

    server = Grasshopper.Instances.DocumentServer
    if server is not None and server.DocumentCount > 0:
        return server[server.DocumentCount - 1]

    raise RuntimeError("No active Grasshopper document")


def by_guid(doc, guid):
    import System

    target = System.Guid(guid)
    obj = doc.FindObject(target, False)
    if obj is not None:
        return obj

    for item in doc.Objects:
        try:
            if item.InstanceGuid == target:
                return item
        except Exception:
            pass

    raise KeyError("Grasshopper object not found: %s" % guid)


def current_value(slider):
    import System

    try:
        return float(System.Decimal.ToDouble(slider.CurrentValue))
    except Exception:
        return float(slider.CurrentValue.ToString())


def set_slider(slider, value):
    import System
    from System.Globalization import CultureInfo

    decimal_value = System.Decimal.Parse(str(value), CultureInfo.InvariantCulture)
    slider.SetSliderValue(decimal_value)
    slider.ExpireSolution(False)


def solve(doc):
    doc.NewSolution(True)
    time.sleep(0.35)


def capture_bmp(path):
    import Rhino
    import System.Drawing

    view = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView
    if view is None:
        raise RuntimeError("No active Rhino view")

    capture = Rhino.Display.ViewCapture()
    capture.Width = int(os.environ.get("GH_CAPTURE_WIDTH", "1280"))
    capture.Height = int(os.environ.get("GH_CAPTURE_HEIGHT", "720"))
    capture.ScaleScreenItems = False
    capture.DrawAxes = False
    capture.DrawGrid = False
    capture.DrawGridAxes = False
    capture.TransparentBackground = False

    bitmap = capture.CaptureToBitmap(view)
    if bitmap is None:
        raise RuntimeError("CaptureToBitmap returned None")

    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Bmp)
    return view.ActiveViewport.Name


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    doc = get_doc()
    sliders = {name: by_guid(doc, guid) for name, guid in SLIDERS.items()}
    originals = {name: current_value(slider) for name, slider in sliders.items()}

    report = [
        "Grasshopper slider sequence capture",
        "out_dir: %s" % OUT_DIR,
        "originals: %s" % json.dumps(originals, sort_keys=True),
    ]
    manifest = {"out_dir": OUT_DIR, "originals": originals, "sequences": []}

    try:
        for sequence in SEQUENCES:
            stem = sequence["stem"]
            sequence_manifest = {"stem": stem, "frames": []}
            report.append("sequence %s started" % stem)

            for index, values in enumerate(sequence["frames"]):
                for slider_name, value in values.items():
                    set_slider(sliders[slider_name], value)
                solve(doc)

                path = os.path.join(OUT_DIR, "%s_%03d.bmp" % (stem, index))
                viewport = capture_bmp(path)
                sequence_manifest["frames"].append(
                    {
                        "index": index,
                        "path": path,
                        "viewport": viewport,
                        "values": dict(values),
                    }
                )
                report.append("  frame %03d viewport=%s values=%s" % (index, viewport, values))

            manifest["sequences"].append(sequence_manifest)
    finally:
        for name, value in originals.items():
            set_slider(sliders[name], value)
        solve(doc)
        report.append("restored originals")

    with open(os.path.join(OUT_DIR, "capture_manifest.json"), "w") as f:
        json.dump(manifest, f, indent=2, sort_keys=True)

    with open(REPORT_PATH, "w") as f:
        f.write("\n".join(report) + "\n")


try:
    main()
except Exception:
    os.makedirs(OUT_DIR, exist_ok=True)
    with open(REPORT_PATH, "w") as f:
        f.write("FAILED\n")
        f.write(traceback.format_exc())

