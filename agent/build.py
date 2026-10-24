#!/usr/bin/env python3
"""Builds mcsl-agent.jar, the same bytes on every machine.

The JDK's own `jar` tool cannot be used: it writes a `Created-By: <version> (<vendor>)` line into the
manifest, so two JDKs give two jars. Compiling with `--release 8` gives the same class file on every
JDK from 8 to 25 (checked: JDK 21 and 25 produce identical bytes), so only the container has to be made
deterministic, which a zip written here, stored without compression, with fixed dates and permissions
and entries in a fixed order, is.

Usage: python agent/build.py [--javac PATH] [--check]

  --javac PATH   the javac to use (default: $JAVA_HOME/bin/javac, then javac on the PATH)
  --check        build into a temporary folder and fail if the result differs from the committed jar
"""
import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SOURCES = [os.path.join('com', 'mcserverlauncher', 'agent', 'Agent.java')]
CLASS = 'com/mcserverlauncher/agent/Agent.class'
JAR = os.path.join(HERE, 'mcsl-agent.jar')
SHA = JAR + '.sha256'
EMBEDDED = os.path.join(ROOT, 'McServerLauncher', 'Resources', 'agent', 'mcsl-agent.jar')

MANIFEST = (
    'Manifest-Version: 1.0\r\n'
    'Premain-Class: com.mcserverlauncher.agent.Agent\r\n'
    'Implementation-Title: MC Server Launcher memory agent\r\n'
    'Implementation-Version: 1\r\n'
    '\r\n'
).encode('ascii')


def find_javac(explicit):
    if explicit:
        return explicit
    home = os.environ.get('JAVA_HOME')
    if home:
        for name in ('javac.exe', 'javac'):
            p = os.path.join(home, 'bin', name)
            if os.path.exists(p):
                return p
    found = shutil.which('javac')
    if not found:
        sys.exit('javac not found: set JAVA_HOME or pass --javac')
    return found


def entry(name):
    info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_STORED
    info.create_system = 3
    info.external_attr = 0o100644 << 16
    return info


def build(javac, out_jar):
    with tempfile.TemporaryDirectory() as classes:
        srcs = [os.path.join(HERE, 'src', s) for s in SOURCES]
        subprocess.run([javac, '--release', '8', '-Xlint:-options', '-encoding', 'UTF-8', '-d', classes] + srcs,
                       check=True)
        with open(os.path.join(classes, *CLASS.split('/')), 'rb') as f:
            data = f.read()
    if data[:4] != b'\xca\xfe\xba\xbe' or int.from_bytes(data[6:8], 'big') != 52:
        sys.exit('the class file is not Java 8 (major version 52)')

    with zipfile.ZipFile(out_jar, 'w') as z:
        z.writestr(entry('META-INF/MANIFEST.MF'), MANIFEST)
        z.writestr(entry(CLASS), data)
    with open(out_jar, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--javac')
    ap.add_argument('--check', action='store_true')
    args = ap.parse_args()
    javac = find_javac(args.javac)

    if args.check:
        with tempfile.TemporaryDirectory() as tmp:
            built = build(javac, os.path.join(tmp, 'mcsl-agent.jar'))
        committed = open(SHA, encoding='ascii').read().split()[0].strip()
        if built != committed:
            sys.exit(f'the agent no longer builds to the committed jar:\n  built     {built}\n  committed {committed}')
        print('mcsl-agent.jar is reproducible:', built)
        return

    digest = build(javac, JAR)
    with open(SHA, 'w', encoding='ascii', newline='\n') as f:
        f.write(f'{digest}  mcsl-agent.jar\n')
    os.makedirs(os.path.dirname(EMBEDDED), exist_ok=True)
    shutil.copyfile(JAR, EMBEDDED)
    print('built', JAR)
    print('sha256', digest)


if __name__ == '__main__':
    main()
