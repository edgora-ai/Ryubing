# Building Ryubing Canary Local

This is a source-only local variant based on Canary 1.3.351, upstream revision
`475615f0431b4995b03c930c5089d20f6f3570e0`. The fork commit identifies the local changes;
the pinned upstream revision alone does not. See [the modification record](LOCAL_CHANGES.zh-CN.md)
for implementation scope, prior results and incomplete acceptance.

## Requirements and dependency sources

- Apple Silicon macOS and the .NET SDK selected by `global.json` (tested with **10.0.301**).
- Python **3.10 or newer** for the local packager and offline tests (tested with 3.13).
- The usual macOS command-line signing/native inspection tools. Packaging uses only ad-hoc
  signing of newly created output; it is not notarization and requires no credential export.
- Exact centrally pinned packages, including `Ryujinx.LibHac 0.21.0-alpha.133`.

The checked-in `nuget.config` routes LibHac to its original alpha feed, which may be
unavailable. If necessary, provide a **separate local** NuGet configuration outside this
repository, preserving the package-source mapping and exact version, with `LibHacAlpha`
pointing at a verified package folder. Do not downgrade a dependency, disable TLS validation,
commit credentials, or pretend that mirror availability establishes original-feed authenticity.
The retained package used for the original local build had SHA-256
`554bf2315368f193335c0ac60d73d73fb8c318aaf3f8a13bddf4ef30e160b631`;
its nuspec declared version `0.21.0-alpha.133+9f3a2cff` and source revision
`9f3a2cff862c324e08079a18bd60ba2d35815975`. That metadata is not an independent source or
binary-authenticity attestation. No package archive is distributed with this local change set.

## Source and work-directory layout

Run from the checkout root. Resolve aliases before using the packager: it rejects symlink
components rather than following them. Keep generated outputs, raw receipts and retained
fixtures outside the repository.

```sh
SOURCE="$(pwd -P)"
WORK="$(mktemp -d "$(dirname "$SOURCE")/ryubing-local-check.XXXXXX")"
mkdir "$WORK/fixtures"
NUGET_CONFIG="${LOCAL_NUGET_CONFIG:-$SOURCE/nuget.config}"

export RYUBING_CANARY_LOCAL_SOURCE_ROOT="$SOURCE"
export RYUBING_CANARY_LOCAL_TEST_ROOT="$WORK/fixtures"
export RYUBING_CANARY_LOCAL_PYTHON="$(command -v python3)"
```

The focused tests require their fixture directory **and test assembly directory** to be
inside the parent of `SOURCE`. The sibling work-directory layout above satisfies that
contract without putting private outputs into Git. The helpers intentionally retain fixtures,
including failed attempts. Do not use a root containing another installation's user data.

## Focused regression tests

The project is deliberately invoked directly: it is not included in the inherited solution
or Forgejo CI. Running the root solution does not establish that these focused checks ran.

```sh
dotnet restore tests/ryubing-canary-local/Ryubing.Canary.Local.Tests.csproj \
  --configfile "$NUGET_CONFIG" -r osx-arm64 -p:RuntimeIdentifiers=osx-arm64 \
  -p:PublishSingleFile=false -p:PublishTrimmed=false

dotnet test tests/ryubing-canary-local/Ryubing.Canary.Local.Tests.csproj \
  -c Release -r osx-arm64 -p:RuntimeIdentifiers=osx-arm64 \
  -p:PublishSingleFile=false -p:PublishTrimmed=false \
  -p:ExtraDefineConstants=DISABLE_UPDATER --no-restore \
  --disable-build-servers -p:UseSharedCompilation=false -m:2 \
  --results-directory "$WORK/results"

python3 -B distribution/macos/tests/test_local_packager.py \
  --fixture-root "$WORK/fixtures"
```

The NUnit project uses a test-only NUnit 4.5.1 override required by Avalonia Headless 12.1.1;
the upstream production dependency pins are unchanged. Typography tests use real Skia font
tables and shaped glyphs, not drawing stubs. They do not establish visible installed-UI,
game, audible-output, physical motion, rumble or reconnection acceptance.

## Publish and package only osx-arm64

Restore again for the self-contained publish properties after running the test configuration.
Keep the locale generator's upstream target framework; do not force every project to net10.

```sh
dotnet restore src/Ryujinx/Ryujinx.csproj --configfile "$NUGET_CONFIG" \
  -r osx-arm64 -p:RuntimeIdentifiers=osx-arm64 -p:SelfContained=true \
  -p:ExtraDefineConstants=DISABLE_UPDATER

dotnet publish src/Ryujinx/Ryujinx.csproj -c Release -r osx-arm64 \
  --self-contained true --no-restore -p:RuntimeIdentifiers=osx-arm64 \
  -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:ExtraDefineConstants=DISABLE_UPDATER -p:Version=1.3.351 \
  -p:InformationalVersion=1.3.351-ryubing-canary-local+475615f.local1 \
  --disable-build-servers -p:UseSharedCompilation=false -m:2 \
  -o "$WORK/publish"
```

Validate the publish using absolute source, entitlement and output paths:

```sh
python3 -B "$SOURCE/distribution/macos/create_local_app_bundle.py" \
  "$WORK/publish" "$WORK/bundle" "$SOURCE/distribution/macos/entitlements.xml" \
  --source-root "$SOURCE" --version 1.3.351 \
  --revision 475615f0431b4995b03c930c5089d20f6f3570e0 --validate-only
```

After reviewing the actual native inventory, repeat that complete command without
`--validate-only` to create a **new, absent** bundle output. It must not be an installation
directory. Existing or partial output is retained, never automatically replaced.

Read [the packaging policy](../distribution/macos/README.local.md) before using the output.
Do **not** run `create_macos_build_ava.sh` or `create_macos_pr_build_ava.sh`: their inherited
universal-build, cleanup and xattr-removal behavior is incompatible with this local route.
Do not enable inherited Forgejo release/triage workflows with upstream targets or secrets.
They are not GitHub Actions; this fork does not automatically publish releases.

## Runtime and installation boundaries

The output is `Ryubing Canary Local.app`, with bundle ID
`local.ryubing.RyubingCanaryLocal` and default data under
`~/Library/Application Support/Ryubing-Canary-Local`. Explicit `-r` / `--root-data-dir`
requires an existing, independent writable directory. There is no legacy fallback, config
import or key search in another installation. Supply only resources you are authorized to use.

The packager does not install or launch the app. Before any manual installation, preserve
existing applications, refuse a pre-existing destination, verify every copied payload against
the manifest, and run `codesign --verify --deep --strict`. Do not strip security attributes
or weaken OS security to make a copy pass. A valid ad-hoc signature is not notarization.

The original local install passed its byte/signature checks, but its complete visible Chinese
UI and settings-save/restart sequence remains unaccepted after interrupted verification.
Do not infer that acceptance from a successful build, headless test, window creation or API return.
