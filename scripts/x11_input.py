#!/usr/bin/env python3
"""Small X11 input tool for live co-op tests; no xdotool or wmctrl required."""

import argparse
import ctypes as C
import ctypes.util
import json
import os
import re
import sys
import time


class HarnessError(RuntimeError):
    pass


def c_proto(lib, name, result, *args):
    fn = getattr(lib, name)
    fn.restype = result
    fn.argtypes = list(args)
    return fn


Display = C.c_void_p
Window = C.c_ulong
Atom = C.c_ulong


class XImage(C.Structure):
    _fields_ = [
        ("width", C.c_int), ("height", C.c_int), ("xoffset", C.c_int), ("format", C.c_int),
        ("data", C.c_void_p), ("byte_order", C.c_int), ("bitmap_unit", C.c_int),
        ("bitmap_bit_order", C.c_int), ("bitmap_pad", C.c_int), ("depth", C.c_int),
        ("bytes_per_line", C.c_int), ("bits_per_pixel", C.c_int),
        ("red_mask", C.c_ulong), ("green_mask", C.c_ulong), ("blue_mask", C.c_ulong),
    ]


class InputEvent(C.Structure):
    _fields_ = [
        ("type", C.c_int), ("serial", C.c_ulong), ("send_event", C.c_int),
        ("display", Display), ("window", Window), ("root", Window),
        ("subwindow", Window), ("time", C.c_ulong),
        ("x", C.c_int), ("y", C.c_int), ("x_root", C.c_int), ("y_root", C.c_int),
        ("state", C.c_uint), ("detail", C.c_uint), ("same_screen", C.c_int),
    ]


class ClientMessageData(C.Union):
    _fields_ = [("b", C.c_char * 20), ("s", C.c_short * 10), ("l", C.c_long * 5)]


class ClientMessageEvent(C.Structure):
    _fields_ = [
        ("type", C.c_int), ("serial", C.c_ulong), ("send_event", C.c_int),
        ("display", Display), ("window", Window), ("message_type", Atom),
        ("format", C.c_int), ("data", ClientMessageData),
    ]


class XEvent(C.Union):
    _fields_ = [("input", InputEvent), ("client", ClientMessageEvent), ("pad", C.c_long * 24)]


class X11:
    def __init__(self, display_name=None):
        self.x = C.CDLL(ctypes.util.find_library("X11") or "libX11.so.6")
        self.t = C.CDLL(ctypes.util.find_library("Xtst") or "libXtst.so.6")
        d = Display
        w = Window
        a = Atom
        p = C.c_void_p
        i = C.c_int
        u = C.c_uint
        ul = C.c_ulong
        self.XOpenDisplay = c_proto(self.x, "XOpenDisplay", d, C.c_char_p)
        self.XCloseDisplay = c_proto(self.x, "XCloseDisplay", i, d)
        self.XDefaultRootWindow = c_proto(self.x, "XDefaultRootWindow", w, d)
        self.XFlush = c_proto(self.x, "XFlush", i, d)
        self.XSync = c_proto(self.x, "XSync", i, d, i)
        self.XFree = c_proto(self.x, "XFree", i, p)
        self.XQueryTree = c_proto(self.x, "XQueryTree", i, d, w, C.POINTER(w), C.POINTER(w), C.POINTER(C.POINTER(w)), C.POINTER(u))
        self.XGetGeometry = c_proto(self.x, "XGetGeometry", i, d, w, C.POINTER(w), C.POINTER(i), C.POINTER(i), C.POINTER(u), C.POINTER(u), C.POINTER(u), C.POINTER(u))
        self.XTranslateCoordinates = c_proto(self.x, "XTranslateCoordinates", i, d, w, w, i, i, C.POINTER(i), C.POINTER(i), C.POINTER(w))
        self.XInternAtom = c_proto(self.x, "XInternAtom", a, d, C.c_char_p, i)
        self.XGetWindowProperty = c_proto(self.x, "XGetWindowProperty", i, d, w, a, C.c_long, C.c_long, i, a, C.POINTER(a), C.POINTER(i), C.POINTER(ul), C.POINTER(ul), C.POINTER(C.POINTER(C.c_ubyte)))
        self.XFetchName = c_proto(self.x, "XFetchName", i, d, w, C.POINTER(C.c_char_p))
        self.XRaiseWindow = c_proto(self.x, "XRaiseWindow", i, d, w)
        self.XSetInputFocus = c_proto(self.x, "XSetInputFocus", i, d, w, i, ul)
        self.XGetInputFocus = c_proto(self.x, "XGetInputFocus", i, d, C.POINTER(w), C.POINTER(i))
        self.XQueryPointer = c_proto(self.x, "XQueryPointer", i, d, w, C.POINTER(w), C.POINTER(w), C.POINTER(i), C.POINTER(i), C.POINTER(i), C.POINTER(i), C.POINTER(u))
        self.XGetImage = c_proto(self.x, "XGetImage", C.POINTER(XImage), d, w, i, i, u, u, ul, i)
        self.XGetPixel = c_proto(self.x, "XGetPixel", ul, C.POINTER(XImage), i, i)
        self.XDestroyImage = c_proto(self.x, "XDestroyImage", i, C.POINTER(XImage))
        self.XStringToKeysym = c_proto(self.x, "XStringToKeysym", ul, C.c_char_p)
        self.XKeysymToKeycode = c_proto(self.x, "XKeysymToKeycode", C.c_ubyte, d, ul)
        self.XKeycodeToKeysym = c_proto(self.x, "XKeycodeToKeysym", ul, d, C.c_ubyte, i)
        self.XTestFakeMotionEvent = c_proto(self.t, "XTestFakeMotionEvent", i, d, i, i, i, ul)
        self.XTestFakeButtonEvent = c_proto(self.t, "XTestFakeButtonEvent", i, d, u, i, ul)
        self.XTestFakeKeyEvent = c_proto(self.t, "XTestFakeKeyEvent", i, d, u, i, ul)
        self.XWarpPointer = c_proto(self.x, "XWarpPointer", i, d, w, w, i, i, u, u, i, i)
        self.XSendEvent = c_proto(self.x, "XSendEvent", i, d, w, i, C.c_long, p)
        self.display = self.XOpenDisplay(display_name.encode() if display_name else None)
        if not self.display:
            raise HarnessError("cannot open X display %r" % (display_name or os.environ.get("DISPLAY", "")))
        self.root = self.XDefaultRootWindow(self.display)
        self.input_target = None
        self.position = None
        self.pressed_keys = set()
        self.state = self.pointer()["state"]

    def close(self):
        if self.display:
            self.XCloseDisplay(self.display)
            self.display = None

    def flush(self):
        self.XFlush(self.display)

    def atom(self, name):
        return self.XInternAtom(self.display, name.encode(), 0)

    def property(self, window, name):
        actual_type = Atom()
        actual_format = C.c_int()
        count = C.c_ulong()
        remaining = C.c_ulong()
        data = C.POINTER(C.c_ubyte)()
        status = self.XGetWindowProperty(
            self.display, window, self.atom(name), 0, 4096, 0, 0,
            C.byref(actual_type), C.byref(actual_format), C.byref(count),
            C.byref(remaining), C.byref(data),
        )
        if status != 0 or not data:
            return None
        try:
            if actual_format.value == 8:
                return C.string_at(data, count.value).decode("utf-8", "replace").rstrip("\0")
            if actual_format.value == 16:
                values = (C.c_ushort * count.value).from_address(C.addressof(data.contents))
                return list(values)
            if actual_format.value == 32:
                # Xlib expands each 32-bit property item to an unsigned long.
                values = (C.c_ulong * count.value).from_address(C.addressof(data.contents))
                return [int(value) for value in values]
            return None
        finally:
            self.XFree(data)

    def title(self, window):
        title = self.property(window, "_NET_WM_NAME")
        if title:
            return title
        name = C.c_char_p()
        if self.XFetchName(self.display, window, C.byref(name)) and name:
            try:
                return name.value.decode("utf-8", "replace")
            finally:
                self.XFree(name)
        return ""

    def geometry(self, window):
        root = Window()
        x, y = C.c_int(), C.c_int()
        width, height, border, depth = C.c_uint(), C.c_uint(), C.c_uint(), C.c_uint()
        if not self.XGetGeometry(self.display, window, C.byref(root), C.byref(x), C.byref(y),
                                 C.byref(width), C.byref(height), C.byref(border), C.byref(depth)):
            raise HarnessError("window %s no longer exists" % window_id(window))
        child = Window()
        if not self.XTranslateCoordinates(self.display, window, self.root, 0, 0,
                                          C.byref(x), C.byref(y), C.byref(child)):
            raise HarnessError("cannot translate coordinates for window %s" % window_id(window))
        return {"x": x.value, "y": y.value, "width": width.value, "height": height.value,
                "depth": depth.value}

    def info(self, window):
        item = self.geometry(window)
        item.update({"id": window_id(window), "title": self.title(window)})
        window_class = self.property(window, "WM_CLASS") or ""
        item["class"] = window_class.replace("\0", " ").strip()
        pid = self.property(window, "_NET_WM_PID")
        item["pid"] = pid[0] if isinstance(pid, list) and pid else None
        return item

    def windows(self):
        root, parent = Window(), Window()
        children = C.POINTER(Window)()
        count = C.c_uint()
        if not self.XQueryTree(self.display, self.root, C.byref(root), C.byref(parent),
                               C.byref(children), C.byref(count)):
            raise HarnessError("XQueryTree failed")
        try:
            ids = [children[index] for index in range(count.value)]
        finally:
            if children:
                self.XFree(children)
        result = []
        for window in ids:
            try:
                result.append(self.info(window))
            except HarnessError:
                pass  # It may have closed while the tree was being inspected.
        return result

    def children(self, window):
        root, parent = Window(), Window()
        children = C.POINTER(Window)()
        count = C.c_uint()
        if not self.XQueryTree(self.display, window, C.byref(root), C.byref(parent),
                               C.byref(children), C.byref(count)):
            raise HarnessError("XQueryTree failed for window %s" % window_id(window))
        try:
            return [children[index] for index in range(count.value)]
        finally:
            if children:
                self.XFree(children)

    def input_window(self, window):
        candidates = []

        def visit(parent, depth):
            if depth > 8:
                return
            try:
                children = self.children(parent)
            except HarnessError:
                return
            for child in children:
                try:
                    item = self.info(child)
                except HarnessError:
                    continue
                identified = item["title"] or item["class"] or item["pid"] is not None
                if identified and item["width"] > 1 and item["height"] > 1:
                    candidates.append((depth, item))
                visit(child, depth + 1)

        visit(window, 1)
        if candidates:
            largest_area = max(item["width"] * item["height"] for _, item in candidates)
            largest = [(depth, item) for depth, item in candidates
                       if item["width"] * item["height"] == largest_area]
            deepest = max(depth for depth, _ in largest)
            largest = [item for depth, item in largest if depth == deepest]
            if len(largest) > 1:
                choices = ", ".join("%s %r" % (item["id"], item["title"]) for item in largest)
                raise HarnessError("client window selection is ambiguous (%s); select a client directly" % choices)
            return parse_window(largest[0]["id"])
        return window

    def window_at(self, x, y):
        for item in reversed(self.windows()):
            identified = item["title"] or item["class"] or item["pid"] is not None
            inside = (item["x"] <= x < item["x"] + item["width"]
                      and item["y"] <= y < item["y"] + item["height"])
            if item["depth"] and identified and inside:
                return self.input_window(parse_window(item["id"]))
        return None

    def focus(self, window):
        event = XEvent()
        event.client.type = 33
        event.client.display = self.display
        event.client.window = window
        event.client.message_type = self.atom("_NET_ACTIVE_WINDOW")
        event.client.format = 32
        event.client.data.l[0] = 2
        self.XSendEvent(self.display, self.root, 0, (1 << 19) | (1 << 20), C.byref(event))
        self.XRaiseWindow(self.display, window)
        self.XSetInputFocus(self.display, window, 2, 0)  # RevertToParent, CurrentTime
        self.XSync(self.display, 0)

    def focused(self):
        window = Window()
        revert = C.c_int()
        self.XGetInputFocus(self.display, C.byref(window), C.byref(revert))
        return window.value

    def screenshot(self, window, x, y, width, height):
        from PIL import Image
        native = self.XGetImage(self.display, window, x, y, width, height, C.c_ulong(-1).value, 2)
        if not native:
            raise HarnessError("XGetImage failed for rectangle %d,%d %dx%d" % (x, y, width, height))
        try:
            masks = (native.contents.red_mask, native.contents.green_mask, native.contents.blue_mask)
            channels = []
            for mask in masks:
                shift = (mask & -mask).bit_length() - 1 if mask else 0
                maximum = mask >> shift
                channels.append((mask, shift, maximum))
            pixels = bytearray(width * height * 3)
            offset = 0
            for row in range(height):
                for column in range(width):
                    pixel = self.XGetPixel(native, column, row)
                    for mask, shift, maximum in channels:
                        pixels[offset] = ((pixel & mask) >> shift) * 255 // maximum if mask else 0
                        offset += 1
            return Image.frombytes("RGB", (width, height), bytes(pixels))
        finally:
            self.XDestroyImage(native)

    def pointer(self):
        root, child = Window(), Window()
        root_x, root_y, win_x, win_y = C.c_int(), C.c_int(), C.c_int(), C.c_int()
        state = C.c_uint()
        if not self.XQueryPointer(self.display, self.root, C.byref(root), C.byref(child),
                                  C.byref(root_x), C.byref(root_y), C.byref(win_x), C.byref(win_y),
                                  C.byref(state)):
            raise HarnessError("XQueryPointer failed")
        return {"x": root_x.value, "y": root_y.value, "child": child.value, "state": state.value}

    def send_input(self, event_type, detail):
        if self.input_target is None:
            return False
        if self.position is None:
            pointer = self.pointer()
            self.position = pointer["x"], pointer["y"]
        geometry = self.geometry(self.input_target)
        root_x, root_y = self.position
        event = XEvent()
        event.input.type = event_type
        event.input.display = self.display
        event.input.window = self.input_target
        event.input.root = self.root
        event.input.x = round(root_x - geometry["x"])
        event.input.y = round(root_y - geometry["y"])
        event.input.x_root = round(root_x)
        event.input.y_root = round(root_y)
        event.input.state = self.state
        event.input.detail = detail
        event.input.same_screen = 1
        mask = {2: 1, 3: 2, 4: 4, 5: 8, 6: 64}[event_type]
        if not self.XSendEvent(self.display, self.input_target, 0, mask, C.byref(event)):
            raise HarnessError("XSendEvent failed for window %s" % window_id(self.input_target))
        self.XSync(self.display, 0)
        return True

    def move(self, x, y, window=None):
        self.XWarpPointer(self.display, 0, self.root, 0, 0, 0, 0, x, y)
        self.XSync(self.display, 0)
        pointer = self.pointer()
        self.position = pointer["x"], pointer["y"]
        if self.position != (x, y):
            self.XTestFakeMotionEvent(self.display, -1, x, y, 0)
            self.XSync(self.display, 0)
            pointer = self.pointer()
            self.position = pointer["x"], pointer["y"]
        if self.position != (x, y):
            raise HarnessError("pointer warp to %s reached %s" % ((x, y), self.position))
        self.input_target = window
        if self.input_target is None:
            self.input_target = self.window_at(x, y)
        self.send_input(6, 0)

    def button(self, button, pressed):
        if not self.send_input(4 if pressed else 5, button):
            self.XTestFakeButtonEvent(self.display, button, int(pressed), 0)
            self.flush()
        if 1 <= button <= 5:
            mask = 1 << (button + 7)
            if pressed:
                self.state |= mask
            else:
                self.state &= ~mask

    def key(self, code, pressed):
        if not self.send_input(2 if pressed else 3, code):
            self.XTestFakeKeyEvent(self.display, code, int(pressed), 0)
            self.flush()
        keysym = self.XKeycodeToKeysym(self.display, code, 0)
        modifier_names = {
            "Shift_L": 1, "Shift_R": 1, "Caps_Lock": 2,
            "Control_L": 4, "Control_R": 4,
            "Alt_L": 8, "Alt_R": 8, "Meta_L": 8, "Meta_R": 8,
            "Num_Lock": 16, "Super_L": 64, "Super_R": 64,
            "Hyper_L": 64, "Hyper_R": 64, "ISO_Level3_Shift": 128,
        }
        mask = 0
        for name, modifier_mask in modifier_names.items():
            if keysym == self.XStringToKeysym(name.encode()):
                mask = modifier_mask
                break
        if pressed:
            self.pressed_keys.add(code)
            self.state |= mask
        else:
            self.pressed_keys.discard(code)
            self.state &= ~mask


def window_id(window):
    return "0x%x" % int(window)


def parse_window(value):
    try:
        number = int(value, 0)
    except ValueError:
        raise HarnessError("invalid window id %r; use decimal or 0x hexadecimal" % value)
    if number <= 0:
        raise HarnessError("window id must be positive")
    return number


def add_selector(parser, local=False, focus_option=False):
    group = parser.add_argument_group("window selection")
    group.add_argument("--window", help="window id (decimal or 0x hex)")
    group.add_argument("--pid", type=int, help="process id from _NET_WM_PID")
    group.add_argument("--title", help="case-insensitive regular expression matched against the title")
    group.add_argument("--window-class", help="case-insensitive regular expression matched against WM_CLASS")
    if local:
        parser.add_argument("--local", action="store_true", help="coordinates are relative to the selected window's client area")
    if focus_option:
        parser.add_argument("--focus", action="store_true", help="focus the selected window before sending mouse input")


def resolve_window(x11, args, required=True):
    selectors = [args.window is not None, args.pid is not None, args.title is not None,
                 getattr(args, "window_class", None) is not None]
    if sum(selectors) > 1:
        raise HarnessError("use only one of --window, --pid, --title, --window-class")
    if not any(selectors):
        if required:
            raise HarnessError("select a window with --window, --pid, --title or --window-class")
        return None
    if args.window is not None:
        return parse_window(args.window)
    matches = x11.windows()
    if args.pid is not None:
        matches = [item for item in matches if item["pid"] == args.pid]
    else:
        pattern_text = args.title if args.title is not None else args.window_class
        field = "title" if args.title is not None else "class"
        try:
            pattern = re.compile(pattern_text, re.IGNORECASE)
        except re.error as exc:
            raise HarnessError("invalid window regular expression: %s" % exc)
        matches = [item for item in matches if pattern.search(item[field])]
    if not matches:
        raise HarnessError("no matching window")
    if len(matches) > 1:
        candidates = ", ".join("%s pid=%s %r" % (item["id"], item["pid"], item["title"]) for item in matches)
        raise HarnessError("window selection is ambiguous (%s); use --window or --pid" % candidates)
    return parse_window(matches[0]["id"])


def point(x11, args, x, y, window=None):
    if args.local:
        if window is None:
            window = resolve_window(x11, args)
        geometry = x11.geometry(window)
        return geometry["x"] + x, geometry["y"] + y
    return x, y


def key_spec(text):
    aliases = {
        "esc": "Escape", "escape": "Escape", "enter": "Return", "return": "Return",
        "space": "space", "tab": "Tab", "backspace": "BackSpace", "delete": "Delete",
        "del": "Delete", "insert": "Insert", "home": "Home", "end": "End",
        "pageup": "Prior", "pagedown": "Next", "up": "Up", "down": "Down",
        "left": "Left", "right": "Right", "minus": "minus", "plus": "plus",
        "comma": "comma", "period": "period", "slash": "slash", "backslash": "backslash",
    }
    lowered = text.lower()
    if lowered in aliases:
        return aliases[lowered]
    if len(text) == 1:
        return text
    if re.fullmatch(r"f(?:[1-9]|1[0-2])", lowered):
        return lowered.upper()
    return text


def key_code(x11, name):
    spec = key_spec(name)
    keysym = x11.XStringToKeysym(spec.encode())
    if not keysym and len(spec) == 1:
        keysym = ord(spec)
    if not keysym:
        raise HarnessError("unknown key %r" % name)
    code = x11.XKeysymToKeycode(x11.display, keysym)
    if not code:
        raise HarnessError("key %r is not available in the active X11 keyboard layout" % name)
    unshifted = x11.XKeycodeToKeysym(x11.display, code, 0)
    return code, unshifted != keysym


MODIFIERS = {
    "ctrl": "Control_L", "control": "Control_L", "shift": "Shift_L",
    "alt": "Alt_L", "super": "Super_L", "meta": "Super_L", "mod1": "Alt_L",
    "mod4": "Super_L",
}


def tap_key(x11, name, hold=0.0):
    code, shifted = key_code(x11, name)
    shift = key_code(x11, "Shift_L")[0]
    add_shift = shifted and shift not in x11.pressed_keys
    if add_shift:
        x11.key(shift, True)
    try:
        x11.key(code, True)
        time.sleep(hold)
        x11.key(code, False)
    finally:
        if add_shift:
            x11.key(shift, False)


def chord(x11, text, hold=0.0):
    parts = text.split("+")
    if len(parts) < 2 or not all(parts):
        tap_key(x11, text, hold)
        return
    modifiers = []
    for part in parts[:-1]:
        name = MODIFIERS.get(part.lower())
        if not name:
            raise HarnessError("unknown chord modifier %r" % part)
        modifiers.append(key_code(x11, name)[0])
    pressed = []
    try:
        for code in modifiers:
            if code not in x11.pressed_keys:
                x11.key(code, True)
                pressed.append(code)
        tap_key(x11, parts[-1], hold)
    finally:
        for code in reversed(pressed):
            x11.key(code, False)


def type_text(x11, text, delay):
    for char in text:
        if char == "\n":
            tap_key(x11, "Return")
        elif char == "\t":
            tap_key(x11, "Tab")
        elif ord(char) < 128:
            tap_key(x11, char)
        else:
            raise HarnessError("cannot type non-ASCII character %r in the active layout" % char)
        if delay:
            time.sleep(delay)


def emit(item):
    print(json.dumps(item, ensure_ascii=False, sort_keys=True))


def run(args):
    x11 = X11(args.display)
    try:
        if args.command == "windows":
            items = x11.windows()
            if not args.all:
                items = [item for item in items if item["width"] >= 16 and item["height"] >= 16]
            if args.match:
                try:
                    pattern = re.compile(args.match, re.IGNORECASE)
                except re.error as exc:
                    raise HarnessError("invalid match regular expression: %s" % exc)
                items = [item for item in items if pattern.search(" ".join((item["id"], item["title"], item["class"])))]
            for item in items:
                emit(item)
            return
        if args.command == "find":
            window = resolve_window(x11, args)
            emit(x11.info(window))
            return
        if args.command == "geometry":
            emit(x11.info(resolve_window(x11, args)))
            return
        if args.command == "focus":
            window = resolve_window(x11, args)
            x11.focus(window)
            emit({"focused": window_id(window), "actual": window_id(x11.focused())})
            return
        if args.command == "pointer":
            pointer = x11.pointer()
            emit({"x": pointer["x"], "y": pointer["y"], "focused": window_id(x11.focused())})
            return
        if args.command in ("move", "click", "drag"):
            window = resolve_window(x11, args, required=False)
            target = x11.input_window(window) if window is not None else None
            if getattr(args, "focus", False):
                if target is None:
                    raise HarnessError("--focus requires a selected window")
                x11.focus(target)
                time.sleep(0.05)
            x, y = point(x11, args, args.x, args.y, target)
            x11.move(x, y, target)
            time.sleep(0.03)
            if args.command == "move":
                emit({"x": x, "y": y})
                return
            if args.command == "click":
                for index in range(args.clicks):
                    x11.button(args.button, True)
                    time.sleep(args.hold)
                    x11.button(args.button, False)
                    if index + 1 < args.clicks:
                        time.sleep(0.08)
                emit({"x": x, "y": y, "button": args.button, "clicks": args.clicks})
                return
            x11.button(args.button, True)
            try:
                steps = max(1, args.steps)
                end_x, end_y = args.x2, args.y2
                if args.local:
                    geometry = x11.geometry(x11.input_target)
                    end_x += geometry["x"]
                    end_y += geometry["y"]
                for step in range(1, steps + 1):
                    x11.move(round(x + (end_x - x) * step / steps), round(y + (end_y - y) * step / steps), target)
                    time.sleep(args.delay / steps)
            finally:
                x11.button(args.button, False)
            emit({"from": [x, y], "to": [end_x, end_y], "button": args.button})
            return
        if args.command in ("key", "type"):
            window = resolve_window(x11, args, required=False)
            if window is not None:
                x11.input_target = x11.input_window(window)
                x11.focus(x11.input_target)
                time.sleep(0.05)
            else:
                focused = x11.focused()
                if focused > 1:
                    x11.input_target = x11.input_window(focused)
            if args.command == "key":
                for index in range(args.repeat):
                    chord(x11, args.chord, args.hold)
                    if index + 1 < args.repeat:
                        time.sleep(args.delay)
                emit({"key": args.chord, "repeat": args.repeat})
            else:
                type_text(x11, args.text, args.delay)
                emit({"typed": len(args.text)})
            return
        if args.command == "screenshot":
            window = resolve_window(x11, args)
            geometry = x11.geometry(window)
            x11.XSync(x11.display, 0)
            image = x11.screenshot(window, 0, 0, geometry["width"], geometry["height"])
            image.save(args.output)
            emit({"output": os.path.abspath(args.output), "width": image.width, "height": image.height})
            return
        raise HarnessError("unknown command")
    finally:
        x11.close()


def parser():
    result = argparse.ArgumentParser(
        description=__doc__,
        epilog="Coordinates are absolute screen pixels unless --local is given. "
               "Use --pid to distinguish host and guest windows with the same title.",
    )
    result.add_argument("--display", default=os.environ.get("DISPLAY", ":0"), help="X display (default: DISPLAY or :0)")
    commands = result.add_subparsers(dest="command", required=True)

    command = commands.add_parser("windows", help="list top-level windows as JSON lines")
    command.add_argument("--match", help="case-insensitive regex over id, title and class")
    command.add_argument("--all", action="store_true", help="include tiny utility windows")
    for name in ("find", "geometry", "focus"):
        command = commands.add_parser(name, help="%s exactly one window" % name)
        add_selector(command)

    command = commands.add_parser("pointer", help="show pointer position and focused window")
    command = commands.add_parser("move", help="move the pointer")
    command.add_argument("x", type=int)
    command.add_argument("y", type=int)
    add_selector(command, local=True)
    command = commands.add_parser("click", help="move and click")
    command.add_argument("x", type=int)
    command.add_argument("y", type=int)
    command.add_argument("--button", type=int, choices=(1, 2, 3, 4, 5), default=1)
    command.add_argument("--clicks", type=int, choices=(1, 2), default=1)
    command.add_argument("--hold", type=float, default=0.05, help="seconds to hold each click")
    add_selector(command, local=True, focus_option=True)
    command = commands.add_parser("drag", help="press, move in steps, and release")
    command.add_argument("x", type=int)
    command.add_argument("y", type=int)
    command.add_argument("x2", type=int)
    command.add_argument("y2", type=int)
    command.add_argument("--button", type=int, choices=(1, 2, 3), default=1)
    command.add_argument("--steps", type=int, default=20)
    command.add_argument("--delay", type=float, default=0.3, help="total drag duration in seconds")
    add_selector(command, local=True, focus_option=True)
    command = commands.add_parser("key", help="send a key or chord such as Escape, F5, ctrl+s, shift+a")
    command.add_argument("chord")
    command.add_argument("--repeat", type=int, default=1)
    command.add_argument("--delay", type=float, default=0.08, help="delay between repeats")
    command.add_argument("--hold", type=float, default=0.03)
    add_selector(command)
    command = commands.add_parser("type", help="focus an optional window and type ASCII text")
    command.add_argument("text")
    command.add_argument("--delay", type=float, default=0.02, help="delay between characters")
    add_selector(command)
    command = commands.add_parser("screenshot", help="save a selected window's client area as an image")
    command.add_argument("output")
    add_selector(command)
    return result


def main():
    args = parser().parse_args()
    try:
        run(args)
    except (HarnessError, OSError) as exc:
        print("x11_input: %s" % exc, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
