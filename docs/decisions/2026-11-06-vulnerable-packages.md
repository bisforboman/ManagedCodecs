# No vulnerable packages (2026-11-06)

Decided by the owner, with the multiple-choice prompt; built in the same pull request.

## Question

How strictly should the repository keep NuGet packages with known advisories (direct or transitive) out of what it
builds and ships, and how should an advisory published after a package was added be found?

## Choices

- When a build meets a vulnerable package: fail every build (local, CI, pack, release), fail only CI's builds (which
  already build with `-warnaserror`, under NuGet's default audit settings), or only warn.
- For advisories published later: a weekly check on main that files an issue, or rely on GitHub's Dependabot alerts
  alone.

## Answer

- **Every build fails.** Directory.Build.props turns NuGet's audit on explicitly for every project (`NuGetAudit`,
  `NuGetAuditMode` `all`, so transitive packages count too, `NuGetAuditLevel` `low`) and appends NU1901 to NU1904
  (low to critical) to `WarningsAsErrors`. That covers the projects outside the solution and the builds that don't
  pass `-warnaserror` (the netstandard2.1 Unity build, the wasm, Blazor, fuzz and benchmark builds, pack and release),
  and it doesn't depend on the SDK's defaults, which have changed between versions. NU1905 (the audit source can't be
  read) stays a warning: an offline restore shouldn't fail on it.
- **A vulnerable package is fixed, never suppressed**: the package is moved past the advisory (a `PackageVersion` in
  Directory.Packages.props, or for a transitive one a direct reference at the fixed version), with a comment naming
  the advisory. No `NuGetAuditSuppress`, no `NoWarn`.
- **A weekly check** (`.github/workflows/vulnerable-packages.yml`, Tuesdays, and on demand): it restores main with the
  errors off and runs `dotnet list package --vulnerable --include-transitive` over the solution and the projects
  outside it (`tools/vulnerable-packages.py`). On a finding it comments on the open "Vulnerable NuGet packages" issue,
  or opens one when none is open (one issue per streak; closing it ends the streak), with each package, version,
  severity, advisory and project, and fails. It is not a required check: an advisory published today shouldn't block
  unrelated pull requests beyond what the build itself does.
- **The owner's step**: turn on Dependabot alerts and Dependabot security updates (Settings, Code security), so that
  GitHub also opens alerts and fix pull requests between the weekly runs.

## Consequences

- A new advisory against a package the repository uses fails every build of every branch, local ones too, until it is
  fixed on main. That is the point: nothing vulnerable is packed or released in the meantime.
- On 2026-11-06 no package, direct or transitive, of any project had a known advisory.
