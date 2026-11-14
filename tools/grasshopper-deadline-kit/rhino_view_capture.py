#! python 3
"""
Minimal Rhino active viewport capture smoke test.

Run from Rhino:

    _RunPythonScript /path/to/rhino_view_capture.py
"""

import os
import traceback

OUT_PATH = os.environ.get(
    "RHINO_CAPTURE_OUT",
    os.path.join(os.path.expanduser("~"), "Desktop", "rhino_view_capture.png"),
)
REPORT_PATH = os.path.splitext(OUT_PATH)[0] + "_report.txt"


def main():
    import Rhino
    import System.Drawing

    view = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView
    if view is None:
        raise RuntimeError("No active Rhino view")

    capture = Rhino.Display.ViewCapture()
    capture.Width = int(os.environ.get("RHINO_CAPTURE_WIDTH", "1800"))
    capture.Height = int(os.environ.get("RHINO_CAPTURE_HEIGHT", "1200"))
    capture.ScaleScreenItems = False
    capture.DrawAxes = False
    capture.DrawGrid = False
    capture.DrawGridAxes = False
    capture.TransparentBackground = False

    bitmap = capture.CaptureToBitmap(view)
    if bitmap is None:
        raise RuntimeError("CaptureToBitmap returned None")

    bitmap.Save(OUT_PATH, System.Drawing.Imaging.ImageFormat.Png)
    with open(REPORT_PATH, "w") as f:
        f.write("Captured active Rhino view to %s\n" % OUT_PATH)
        f.write("view: %s\n" % view.ActiveViewport.Name)


try:
    main()
except Exception:
    with open(REPORT_PATH, "w") as f:
        f.write("FAILED\n")
        f.write(traceback.format_exc())

