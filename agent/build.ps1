# Builds the memory agent: the same jar on every machine (see build.py for why `jar` is not used).
# Needs a JDK 8 or later (JAVA_HOME or javac on the PATH) and Python 3.
#   .\agent\build.ps1            builds agent/mcsl-agent.jar and copies it to the app's resources
#   .\agent\build.ps1 -Check     only checks that it still builds to the committed jar
param([switch]$Check)

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command python3 -ErrorAction SilentlyContinue }
if (-not $python) { throw 'Python 3 not found' }

$arguments = @((Join-Path $PSScriptRoot 'build.py'))
if ($Check) { $arguments += '--check' }
& $python.Source @arguments
exit $LASTEXITCODE
