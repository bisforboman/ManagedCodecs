#!/usr/bin/env python3
"""For vulnerable-packages.yml: lists every NuGet package with a known advisory, direct or transitive, in what the
repository builds or ships (the solution and the projects outside it), as a Markdown table on stdout.

Each target is restored with the audit's errors turned off (-p:WarningsAsErrors=, so a vulnerable package doesn't stop
the restore it is reported from; Directory.Build.props makes them errors in every other build), then listed with
`dotnet list package --vulnerable --include-transitive --format json`. Exits 1 when a package is vulnerable, 2 when a
target could not be restored or listed (the list would be incomplete), 0 otherwise.

    tools/vulnerable-packages.py [TARGET ...]    # default: the targets below; --json FILE reads a saved listing instead
"""
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# The solution, and the projects outside it (they need the wasm-tools workload, or are one script's harness).
TARGETS = [
    "ManagedCodecs.slnx",
    "tools/ManagedCodecs.Wasm/ManagedCodecs.Wasm.csproj",
    "tools/ManagedCodecs.Thumbnails.Blazor/ManagedCodecs.Thumbnails.Blazor.csproj",
    "tools/ManagedCodecs.Cli.Unity/ManagedCodecs.Cli.Unity.csproj",
]

SEVERITIES = ["Critical", "High", "Moderate", "Low"]


def listing(target):
    """The JSON listing of one target, or None (after saying why on stderr) if it could not be made."""
    restore = subprocess.run(["dotnet", "restore", target, "-p:WarningsAsErrors=", "-nologo", "-v", "q"], cwd=ROOT,
                             capture_output=True, text=True)
    if restore.returncode != 0:
        print(f"Restoring {target} failed:\n{restore.stdout}{restore.stderr}", file=sys.stderr)
        return None
    listed = subprocess.run(["dotnet", "list", target, "package", "--vulnerable", "--include-transitive", "--format",
                             "json", "--no-restore"], cwd=ROOT, capture_output=True, text=True)
    try:
        data = json.loads(listed.stdout)
    except json.JSONDecodeError:
        print(f"Listing {target} gave no JSON:\n{listed.stdout}{listed.stderr}", file=sys.stderr)
        return None
    errors = [p.get("text", "") for p in data.get("problems", []) if p.get("level") == "error"]
    if listed.returncode != 0 or errors:
        print(f"Listing {target} failed: {'; '.join(errors) or listed.stderr}", file=sys.stderr)
        return None
    return data


def findings(data, found):
    """Adds the listing's vulnerable packages to found: (id, version) -> {"advisories": {url: severity},
    "projects": set, "transitive": bool}."""
    for project in data.get("projects", []):
        path = Path(project.get("path", "?"))
        name = str(path.relative_to(ROOT)) if path.is_relative_to(ROOT) else str(path)
        for framework in project.get("frameworks", []):
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(kind, []):
                    vulnerabilities = package.get("vulnerabilities", [])
                    if not vulnerabilities:
                        continue
                    entry = found.setdefault((package["id"], package.get("resolvedVersion", "?")),
                                             {"advisories": {}, "projects": set(), "transitive": True})
                    entry["projects"].add(f"{name} ({framework.get('framework', '?')})")
                    entry["transitive"] &= kind == "transitivePackages"
                    for v in vulnerabilities:
                        entry["advisories"][v.get("advisoryurl", "?")] = v.get("severity", "?")


def report(found):
    def rank(entry):
        severities = entry["advisories"].values()
        return min((SEVERITIES.index(s) for s in severities if s in SEVERITIES), default=len(SEVERITIES))

    lines = ["| Package | Version | Severity | Advisory | Projects |", "|---|---|---|---|---|"]
    for (package, version), entry in sorted(found.items(), key=lambda item: (rank(item[1]), item[0])):
        how = "transitive" if entry["transitive"] else "direct"
        projects = "<br>".join(sorted(entry["projects"]))
        for url, severity in sorted(entry["advisories"].items()):
            lines.append(f"| {package} ({how}) | {version} | {severity} | {url} | {projects} |")
    return "\n".join(lines)


def main(args):
    found = {}
    incomplete = False
    if args[:1] == ["--json"]:
        for file in args[1:]:
            findings(json.loads(Path(file).read_text()), found)
    else:
        for target in args or TARGETS:
            data = listing(target)
            if data is None:
                incomplete = True
            else:
                findings(data, found)
    if found:
        print(report(found))
    if incomplete:
        return 2
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
