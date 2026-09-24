#!/usr/bin/env bash
# macOS/Linux: builds the whole solution (Windows projects compile, XAML is validated) and
# runs the portable Notchle.Core tests. Notchle.Windows.Tests only run on Windows (CI).
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build Notchle.Windows.slnx -warnaserror:nullable -v q -nologo "$@"
dotnet test tests/Notchle.Core.Tests --no-build -nologo --logger "console;verbosity=minimal"
