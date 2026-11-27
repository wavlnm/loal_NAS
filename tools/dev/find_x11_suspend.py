#!/usr/bin/env python3
"""找出是谁把 X 屏保「挂起」了（screenSaverSuspended）。

现象：xset q 显示 timeout/DPMS 全都正常，空闲远超阈值却既不息屏也不自动
DPMS off；手动 `xset s activate` / `xset dpms force off` 又立刻生效。
原因：任何客户端发 MIT-SCREEN-SAVER 的 Suspend 请求后，服务器会释放
屏保定时器（Xext/saver.c FreeScreenSaverTimer），此后不再自动激活。
Chromium/Electron 的 powerSaveBlocker('prevent-display-sleep') 就是这么干的。

用法：DISPLAY=:0 XAUTHORITY=$HOME/.Xauthority python3 find_x11_suspend.py
"""
import ctypes

x11 = ctypes.CDLL("libX11.so.6")
xres = ctypes.CDLL("libXRes.so.1")

x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
x11.XOpenDisplay.restype = ctypes.c_void_p
x11.XGetAtomName.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
x11.XGetAtomName.restype = ctypes.c_void_p
x11.XFree.argtypes = [ctypes.c_void_p]
xres.XResQueryClients.argtypes = [
    ctypes.c_void_p, ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_void_p)]
xres.XResQueryClientResources.argtypes = [
    ctypes.c_void_p, ctypes.c_ulong,
    ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_void_p)]


class Client(ctypes.Structure):
    _fields_ = [("base", ctypes.c_ulong), ("mask", ctypes.c_ulong)]


class RType(ctypes.Structure):
    # 服务器只回 32 位计数，libXRes 的 struct 里高 4 字节是垃圾，按 32 位读
    _fields_ = [("atom", ctypes.c_ulong), ("count", ctypes.c_uint32),
                ("pad", ctypes.c_uint32)]


dpy = x11.XOpenDisplay(None)
if not dpy:
    raise SystemExit("打不开 DISPLAY")

n = ctypes.c_int()
ptr = ctypes.c_void_p()
xres.XResQueryClients(dpy, ctypes.byref(n), ctypes.byref(ptr))
clients = ctypes.cast(ptr, ctypes.POINTER(Client))
print(f"客户端数: {n.value}")

found = []
for i in range(n.value):
    base = clients[i].base
    nt = ctypes.c_int()
    tp = ctypes.c_void_p()
    if not xres.XResQueryClientResources(
            dpy, base, ctypes.byref(nt), ctypes.byref(tp)):
        continue
    types = ctypes.cast(tp, ctypes.POINTER(RType))
    for j in range(nt.value):
        nm = x11.XGetAtomName(dpy, types[j].atom)
        name = ctypes.cast(nm, ctypes.c_char_p).value.decode(errors="replace") if nm else ""
        if nm:
            x11.XFree(nm)
        if name == "SaverSuspend" and types[j].count:
            found.append((base, types[j].count))
    x11.XFree(tp)
x11.XFree(ptr)

if not found:
    print("没有客户端持有 SaverSuspend：屏保定时器应正常，能自动息屏")
for base, cnt in found:
    print(f"持有 SaverSuspend: 资源基址=0x{base:x} 计数={cnt}")
    print(f"  → 用 `xwininfo -root -children` 找 id 以 0x{base:x} 开头的窗口，"
          f"即可知道是哪个程序（Chromium 系通常是 \"Chromium clipboard\"）")
