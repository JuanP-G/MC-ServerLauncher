#!/usr/bin/env sh
# Builds the memory agent: the same jar on every machine (see build.py for why `jar` is not used).
# Needs a JDK 8 or later (JAVA_HOME or javac on the PATH) and Python 3.
#   agent/build.sh            builds agent/mcsl-agent.jar and copies it to the app's resources
#   agent/build.sh --check    only checks that it still builds to the committed jar
set -eu
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
py=$(command -v python3 || command -v python) || { echo "Python 3 not found" >&2; exit 1; }
exec "$py" "$here/build.py" "$@"
