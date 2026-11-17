#!/usr/bin/env python3
"""Zapisuje paczki dla update-functions-test.sh do katalogu podanego jako jedyny argument.

tarfile Pythona tworzy wpisy, których samo tar nie utworzy (ścieżki bezwzględne, '..', urządzenia) – dokładnie
to, co update.sh musi odrzucić. Uruchamiać przez `python3 -I`.
"""
import io
import os
import sys
import tarfile


def add_file(tar, name, data=b"x", mode=0o644):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = mode
    tar.addfile(info, io.BytesIO(data))


def add_dir(tar, name):
    info = tarfile.TarInfo(name)
    info.type = tarfile.DIRTYPE
    info.mode = 0o755
    tar.addfile(info)


def add_link(tar, name, target, kind=tarfile.SYMTYPE):
    info = tarfile.TarInfo(name)
    info.type = kind
    info.linkname = target
    tar.addfile(info)


def write(folder, name, build):
    with tarfile.open(os.path.join(folder, name), "w:gz", format=tarfile.GNU_FORMAT) as tar:
        build(tar)


def good(tar):
    add_dir(tar, "./")
    add_file(tar, "./ZdrowieInfo.Server", b"#!/bin/sh\necho ok\n", 0o755)
    add_dir(tar, "./runtimes")
    add_file(tar, "./runtimes/libfoo.so.1", b"lib")
    add_link(tar, "./runtimes/libfoo.so", "libfoo.so.1")
    add_link(tar, "./libfoo.so", "runtimes/../runtimes/libfoo.so.1")
    add_dir(tar, "./deploy")
    add_file(tar, "./deploy/update.sh", b"#!/bin/sh\n", 0o4755)  # setuid bit must not survive


def main():
    folder = sys.argv[1]
    os.makedirs(folder, exist_ok=True)
    write(folder, "good.tar.gz", good)
    write(folder, "absolute.tar.gz", lambda t: (add_file(t, "./ok"), add_file(t, "/tmp/dn-evil-absolute")))
    write(folder, "dotdot.tar.gz", lambda t: (add_file(t, "./ok"), add_file(t, "../dn-evil-dotdot")))
    write(folder, "dotdot-inner.tar.gz", lambda t: (add_file(t, "./ok"), add_file(t, "./a/../../dn-evil-inner")))
    write(folder, "link-outside.tar.gz", lambda t: (add_file(t, "./ok"), add_link(t, "./etc", "../../etc")))
    write(folder, "link-absolute.tar.gz", lambda t: (add_file(t, "./ok"), add_link(t, "./etc", "/etc")))
    write(folder, "link-then-write.tar.gz",
          lambda t: (add_link(t, "./out", "../outside"), add_file(t, "./out/dn-evil-through-link")))
    # Each link stays inside on its own text, but together they leave: sub/up -> .. is the root, so
    # sub/up/../.. is above it. Only the realpath check in zi_check_tree (GNU realpath) catches this.
    write(folder, "link-chain.tar.gz",
          lambda t: (add_dir(t, "./sub"), add_link(t, "./sub/up", ".."), add_link(t, "./escape", "sub/up/../..")))
    write(folder, "hardlink.tar.gz", lambda t: (add_file(t, "./ok"), add_link(t, "./hard", "./ok", tarfile.LNKTYPE)))

    def fifo(t):
        add_file(t, "./ok")
        info = tarfile.TarInfo("./pipe")
        info.type = tarfile.FIFOTYPE
        t.addfile(info)
    write(folder, "fifo.tar.gz", fifo)

    def chardev(t):
        add_file(t, "./ok")
        info = tarfile.TarInfo("./null")
        info.type = tarfile.CHRTYPE
        info.devmajor, info.devminor = 1, 3
        t.addfile(info)
    write(folder, "chardev.tar.gz", chardev)


if __name__ == "__main__":
    main()
