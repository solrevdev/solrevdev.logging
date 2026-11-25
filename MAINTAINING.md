# Maintaining solrevdev.logging

Release process and repository decisions. Consumer documentation is in
[README.md](README.md). Rules for changing the code are in [AGENTS.md](AGENTS.md).

## Publishing

Releases go to nuget.org from [.github/workflows/publish.yml](.github/workflows/publish.yml)
using NuGet trusted publishing. There is no long-lived API key in this repository and none
should be added.

The workflow requests a short-lived GitHub OIDC token, nuget.org validates it against a policy
registered on their site, and returns a temporary API key valid for one hour and one push. That
is why the `Log in to NuGet` step sits immediately before the push rather than at the top of
the job, and why the publish job carries `id-token: write` alongside the `contents: write` it
needs for the tag and the release. The pull-request `validate` job does not publish and does
not get `id-token: write`.

### One-time nuget.org setup

This cannot be scripted. On nuget.org, click the username, choose Trusted Publishing, and add a
policy:

| Field | Value |
| --- | --- |
| Package owner | `solrevdev` |
| Publisher | GitHub Actions |
| Repository owner | `solrevdev` |
| Repository | `solrevdev.logging` |
| Workflow file | `publish.yml`, the file name only, not the `.github/workflows/` path |
| Environment | leave empty; this workflow uses no GitHub environment |
| Package glob | `solrevdev.Logging.*` |

Scope it to allow pushing new packages, not only new versions, if the package does not exist on
nuget.org yet. A policy scoped to versions only cannot create a package. It can be narrowed
after the first release.

## Cutting a release

The `<Version>` in the csproj is the release number. A push to `master` publishes it as it
stands if it has no tag yet. Once that version is tagged, a later push bumps the patch, and a
`workflow_dispatch` chooses minor or major.

The job runs in this order:

1. Work out the version and, if the current one is already tagged, bump it.
2. Check the release state. If the target tag exists this is a retry. If not, refuse when
   `origin/master` has already moved.
3. When it is a retry, check out the tag so the pack uses the source the tag names.
4. Restore, build, test, pack, and check the expected `.nupkg` exists.
5. Commit the version bump, tag, and push branch and tag atomically.
6. Log in to NuGet and push the package.
7. Create the GitHub Release.

### Why the tag is pushed before the package

nuget.org and Git are separate systems and nothing here makes them atomic. A published version
cannot be recalled, and a published version whose source nobody can identify is the outcome
worth avoiding. So the order is chosen to decide which side is left dangling when a release
does not complete.

Steps 5 and 6 used to be the other way round. That left two ways to get a package nobody can
tie to a commit. If `master` advanced during the build, the package uploaded and then the
atomic push failed, because the branch was no longer a fast-forward. If a later run computed a
version that already had a tag, `--skip-duplicate` passed over the existing package while the
job packed and tagged a different tree.

With the tag first, a failure between the two leaves a tag with no package. Re-running checks
out that tag, packs the same source again, and `--skip-duplicate` makes the second push
harmless. The atomic branch and tag push still fails if `master` moved, and that failure now
costs a run rather than a wrong published version.

Both transitions were reproduced locally against a bare repository, with the NuGet push
replaced by an echo. With the new order, the run where `master` moved mid-build refused at the
git push and uploaded nothing. The retry case packed the tagged tree rather than the newer
`master`.

The early `origin/master` check in step 2 is a cheap pre-flight rather than a guarantee. It
fails a doomed run in seconds instead of after a build, and `master` can still move during the
build that follows. The atomic push is what protects the release.

## Adding a property

Three applications write these names into one Seq and each has a test that asserts the
contract sets on every request. That decides how a name is added and why one is never
changed.

**Adding one.**

1. Add the constant to `LoggingContract`. Nothing else may type the name as a literal.
2. Put it in a NEW set, or in `OptInProperties` if the enricher sets it, or in
   `RefusalProperties` if only a refusal event carries it. Never add it to
   `EnricherProperties`, `HostProperties`, `RequiredProperties`, `AuthenticatedProperties`,
   `OptionalProperties` or `AllProperties`. Their membership is frozen: a consumer's test
   compares its summaries against them, so a new member fails that test on upgrade for a
   property the application never asked for.
3. Make it opt-in, behind a `RequestLogOptions` setting that defaults to off. An application
   that upgrades and changes nothing must write exactly what it wrote before.
4. Extend the drift test in `LoggingContractTests` so the enricher is run with the option off
   and on, and sets exactly what the sets say in each mode.
5. Release it as a minor version, with a changelog entry that says no existing API or output
   changed. Do not write "nothing changed": see "Adding a public type" below.

**Never renaming one.** A renamed property is a query that returns half the traffic and looks
like a quiet day: the events written before the rename, and by any application not yet
upgraded, keep the old name for as long as Seq retains them. A dashboard, a saved signal or an
alert that filters on the name fails silently. So a name is never renamed and its meaning
never changes, not in a major version either. If a name turns out wrong, add the right one
beside it and leave the old one written.

**What a consumer does on upgrade.** For a minor version, nothing: no test changes and no
output changes. To use a new property, switch its option on, then assert the name in the
scenarios that produce it. Do not assert an opt-in name on every request; each can be absent
for an ordinary reason. Check your own property names against `AllProperties`,
`ExtendedProperties` and `RefusalProperties` together, so none collides with a name this
package may write.

### Adding a public type

A new public type name is a source break for some consumers, and it was measured, not
feared. Every public type is in the one namespace, so an application with a type of its own
by that name, in a file that imports the namespace, gets CS0104 from the version bump alone.
On the 1.3.0 release candidate one application got it on five types, each a local copy of
something ported from it.

The names are still right, so the answer is to say so. List every new public type name in the
changelog entry and under "Upgrading" in the README, so a consumer can search for a clash
first. `PublicSurfaceTests` fails when a public type is added or removed, which is the
reminder. A minor version may add public types. It may not claim that nothing changed.

### Trying a build before it is released

Pack a prerelease locally and leave the csproj alone:

```bash
dotnet pack -c Release -p:Version=1.3.0-rc.1 -o ./artifacts-local
```

`artifacts-local/` is git-ignored. The prerelease suffix matters: NuGet caches a package by id
and version and never looks again, so a locally packed `1.3.0` would shadow the published
`1.3.0` on every machine that restored it. An `-rc.N` version cannot collide with the final
one. Raise N for each new local build, or clear the cached copy first.

The publish workflow cannot release a prerelease. It reads `<Version>` from the csproj and
refuses anything that is not three numbers, so `-p:Version` on a local command line is the
only place the suffix exists and the workflow never sees it.

## Considered, not done

**A second package with no ASP.NET dependency.** Two of the three applications asked for one
during the 1.3.0 release candidate, so a worker or a Core project could use the pieces below
without taking the ASP.NET Core shared framework. It needs a new package id, which is the
owner's decision, so it was not done. These types would move, and none of them touches an
ASP.NET type today:

| Type | Depends on |
| --- | --- |
| `SelfLogOutput` | The base library only |
| `TransportExceptionLog` | The base library only |
| `HeaderValue` | The base library only |
| `EventNames`, `EventNameViolation`, `EventNameViolationKind` | The base library only |
| `ILoggedRefusal`, `LoggedRefusal` | The base library only |
| `BootstrapLogIdentity`, `BootstrapHost` | The base library, and one `LoggingContract` constant |
| `RefusalEvents` | The base library only |
| `LoggingContract` | The base library only. It would have to move too, or the split package would retype names |

Their XML docs point at ASP.NET-bound types with `cref`, which costs nothing at run time and
would become plain text in a split.

**Keep them that way.** Do not give any of these a parameter, field or base type from
`Microsoft.AspNetCore.*`, `Microsoft.Extensions.DependencyInjection` or Serilog. That is what
keeps the split a matter of moving files. `PortableTypesTests` fails if a signature on one of
them names such a type.

**A `Use` method for the anti-forgery feature.** A form-binding minimal-API endpoint is
refused by the framework before any endpoint filter runs, so `AntiforgeryRefusalEndpointFilter`
cannot log it. The README gives six lines of middleware for that. It is not shipped, because
the anti-forgery feature is set for every endpoint the middleware checks and only the
application knows whether an invalid one is then refused.

## Branding assets

`docs/assets/branding/logging-icon.svg` is the source mark. The two PNGs beside it are
rendered from it, and the 128px one is what the csproj packs as `icon.png`:

```bash
rsvg-convert --width=128 --height=128 docs/assets/branding/logging-icon.svg \
  -o docs/assets/branding/logging-icon-128.png
rsvg-convert --width=512 --height=512 docs/assets/branding/logging-icon.svg \
  -o docs/assets/branding/logging-icon-512.png
```

nuget.org renders an embedded icon file and ignores the deprecated `PackageIconUrl`, and it
does not render SVG, so the PNG has to be in the package. Re-render both after editing the SVG
and commit all three together, or the package ships a mark the repository no longer shows. The
README points at the SVG, because GitHub does render it.

## Why these test packages

`xunit.v3` 3.2.2 with `xunit.runner.visualstudio` 3.1.5 and `Microsoft.NET.Test.Sdk` 18.9.0.
That is the combination the sibling applications run on the .NET 10 SDK, so a fix proved here
can be pasted into either without a framework argument. The xunit v4 packages move to
Microsoft.Testing.Platform, which is a migration in its own right and not one to make inside a
library that other applications depend on.

`TreatWarningsAsErrors` and `GenerateDocumentationFile` are both on, so a public member without
XML docs fails the build. This package exists to carry reasons between applications, and a
member shipped without its reason is the drift it was written to stop.

The test project uses `Microsoft.NET.Sdk.Razor` with `AddRazorSupportForMvc`, and references
`Microsoft.AspNetCore.TestHost`. The two pages under `Pages/` are compiled into the test
assembly so the refusal filters are tested through the framework's own page pipeline. The
anti-forgery filter in particular is only right if an always-run result filter sees a check
that short-circuits, and a hand-built filter context cannot prove that.

Several test classes swap the process-global `Serilog.Log.Logger` to capture events, so they share
one xUnit collection and never run at the same time. Collections are the unit of parallelism.
A new test class that captures Serilog output belongs in that collection too, or it will see an
empty sink at random.

## Where the code came from

| Piece | Source |
| --- | --- |
| `ClientAddress.Describe` | Two of the three applications, byte-identical in both |
| Forwarded-for reconstruction | Two of the three |
| Referer sanitising | The same two |
| `RequestLogLevel` | All three, merged |
| The two-middleware split | Two of the three, with the third's trace id |
| `LoggingContract` | New, replacing three sets of hand-typed constants |
| `RequestNavigation`, `RequestQuery` | One of the three, 1.3.0. The query key allowlist became an option |
| `SelfLogOutput`, `BootstrapLogIdentity` | The same one, 1.3.0. The closing sentence and the default application name became parameters. `BootstrapHost` is new: the ported order is right for `WebApplication.CreateBuilder` only |
| `TransportExceptionLog` | The same one, 1.3.0 |
| `EventNames` | A naming rule one application held in a test of its own, 1.3.0, as a helper that returns data |
| `AntiforgeryRefusalLogFilter`, `FormRefusalLogFilter` | The same one, 1.3.0. Its own refusal state became the `ILoggedRefusal` interface |

The package is the union of the three copies rather than the smallest common part. Everything
in it was measured in one of those applications first, which is why the comments record what
was observed rather than what the code does.
