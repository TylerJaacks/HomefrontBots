# Releasing HomefrontBots

Maintainer notes: CI, the HomefrontSDK NuGet package and GitHub Releases. Not needed to build, use or work on the
mod; see the [README](README.md) for that.

## Which SDK a build uses

`HomefrontBots.csproj` builds against the HomefrontSDK checkout beside this one (`..\HomefrontSDK`) when there is
one, as in a developer's tree. Without it, as in CI, it uses the `HomefrontSDK` package from GitHub Packages
(`https://nuget.pkg.github.com/TylerJaacks/index.json`, set in `nuget.config`) at version `*`, the latest stable
release, with `ExcludeAssets="runtime"`: the game already has the SDK loaded, so the mod compiles against it without
copying it.

- `-p:UseLocalSdk=false` forces the package even with a checkout beside it; `-p:HomefrontSDKVersion=1.0.0` pins a
  version.
- GitHub's feed needs a token even for public packages. CI passes its `GITHUB_TOKEN`; to restore the package locally,
  set `GITHUB_TOKEN` to a personal access token (classic) with `read:packages`.
- A newly published version is picked up by the next restore; locally, `dotnet restore --force-evaluate` looks
  again. `dotnet list HomefrontBots.csproj package` shows the version a build used.

The mod must run with the SDK it was built against. The SDK's mod load context resolves `HomefrontSDK` by name
only, so a mod built against another SDK version loads anyway, and a binding that changed fails only when it's
called (`MissingMethodException`). That's why the HomefrontBots release notes name the SDK release.

## CI and releases

Each repository's GitHub Actions workflow (`.github/workflows/build.yml`) builds on every push and pull request to
`main` and uploads the result as an artifact. A `v*` tag (`git tag v0.6.0 && git push origin v0.6.0`) also builds
with that version and makes a GitHub Release:

| Repository | On a tag |
|---|---|
| HomefrontSDK | publishes the `HomefrontSDK` package to GitHub Packages; releases `HomefrontSDK-<tag>.zip` and the `.nupkg` |
| HomefrontBots | restores the latest package (with the workflow's `GITHUB_TOKEN`); releases `HomefrontBots.dll` and `.pdb`, the notes naming the SDK release it was built against |
| HomefrontModLoader | releases `msimg32.dll`. It doesn't use the SDK to build: it loads `HomefrontSDK.dll` from the server at run time |

To release a new SDK with the bots: tag HomefrontSDK, wait for its workflow to publish the package, then tag
HomefrontBots.

The HomefrontSDK package's settings (Manage Actions access) must list the HomefrontBots repository, or its workflow
can't read the package.
