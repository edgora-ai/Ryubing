# Ryubing Canary Local packaging policy

This entry point packages only the independent local developer build of Canary **1.3.351**,
source **475615f0431b4995b03c930c5089d20f6f3570e0**, marker
`ryubing-canary-local+475615f.local1`. It does not build, launch or install the app.
Do not run the upstream universal release wrappers: their cleanup and xattr removal are not
part of this local build policy.

## Identity and coexistence

- App: `Ryubing Canary Local.app`; bundle ID: `local.ryubing.RyubingCanaryLocal`.
- Default data: `~/Library/Application Support/Ryubing-Canary-Local`.
- Dedicated portable marker: `portable-ryubing-canary-local`; no generic portable fallback.
- Keep the internal `Ryujinx` executable, assemblies, icons and `avares://Ryujinx` resources.
- No document, URL or UTI registrations. Keep `DOTNET_DefaultStackSize=200000` and the
  original `entitlements.xml`; the packager rejects changed entitlements or stack settings.
- No old application/data migration, shared macOS defaults changes, credentials or user
  data in the bundle. Root selection and runtime data isolation are enforced separately.

Automatic updates are disabled at public checks and every network/download/extraction/
installation boundary, even without `DISABLE_UPDATER`. Startup cleanup is intentionally a
no-op. The build flag remains defense in depth. `updater.sh` refuses even direct execution
with status 78, without inspecting or modifying the supplied installation. This is not a
claim that the app is up to date: updates require a separately reviewed source rebuild.
The upstream Canary channel is retained for original Canary behavior, not update access.

## Inputs, validation and signing

Use a fresh **self-contained osx-arm64 single-file .NET 10 GUI publish**, not an upstream
universal bundle. Follow [LOCAL_BUILD.md](../../docs/LOCAL_BUILD.md) for the SDK, dependencies,
explicit test invocation and sibling work-directory layout. Supply canonical absolute paths
and verify writable storage/capacity before packaging. Version and revision are mandatory
and must match the upstream base pin; the fork's local commit identifies additional changes.
The caller owns source snapshots, actual-publish inventory/native allowlist review and final
installation verification.

```sh
python3 -B "$SOURCE/distribution/macos/create_local_app_bundle.py" \
  "$PUBLISH" "$NEW_OUTPUT" "$SOURCE/distribution/macos/entitlements.xml" \
  --source-root "$SOURCE" --version 1.3.351 \
  --revision 475615f0431b4995b03c930c5089d20f6f3570e0 --validate-only
```

`--validate-only` is read-only: no input execution, native tools, copy, output creation or
signing. After successful review, the same command without that flag packages into an
**absent** output with an existing task-owned parent. It refuses installation directories,
symlink components, unknown publish files, wrong architectures and unresolved native
library dependencies. Existing or partial output is never cleaned, reused or replaced.

The required native set is `libSDL3.dylib`, `libAvaloniaNative.dylib`, `libSkiaSharp.dylib`,
`libHarfBuzzSharp.dylib`, `libMoltenVK.dylib` and `libarmeilleure-jitsupport.dylib`.
Recheck this against the actual Canary publish before packaging; do not silently broaden
or weaken validation to accept an unexpected payload. Known x64-only `libsoundio.dylib`
is recorded as excluded and retained unchanged in publish. An arm64 SoundIO is not dropped.
All included non-system dyld loads must resolve inside the packaged native set.

The packager preserves input bytes/modes/xattrs, hashes the embedded .NET metadata and all
managed payloads before and after native fixups/signing, scans the entire bounded input
for a unique bundle marker (including .NET 10 markers beyond 4 MiB), and rechecks all source
inputs, including excluded SoundIO. The inspection limit is 512 MiB per .NET bundle.
It never strips quarantine or other attributes. It signs only its new owned copies ad hoc
and requires `codesign --verify --deep --strict`; it does not claim notarization.
`packaging-manifest.json` records tool commands, source and output hashes, modes, xattrs,
embedded bundle commitments, exclusions, pin and build marker. Failure retains incomplete
output and its failure receipt; do not install that output.

Canary's actual `LICENSE.txt` and `distribution/legal/THIRDPARTY.md` are required and copied
unchanged. Font OFL, authors, license and notice files under `Assets/Fonts` are retained.
No Nextendo `LICENSE.md`, Apple/Nintendo font files, services or toast code are imported.
The CJK font policy only chooses host system fallbacks; Latin, embedded mono/icons, emoji
selection and guest firmware fonts keep their existing paths.

## Focused tests

Run `distribution/macos/tests/test_local_packager.py` with an existing absolute
`--fixture-root` (or `RYUBING_CANARY_LOCAL_TEST_ROOT`). It retains every synthetic fixture
and CLI result, executes no fixture binary and performs no signing. The direct updater test
executes only the refusal script against owned sentinels.

Invoke `tests/ryubing-canary-local/Ryubing.Canary.Local.Tests.csproj` explicitly; the inherited
solution/CI does not discover it. Set `RYUBING_CANARY_LOCAL_TEST_ROOT`,
`RYUBING_CANARY_LOCAL_SOURCE_ROOT` and optionally `RYUBING_CANARY_LOCAL_PYTHON`. Both the
fixture directory and test assembly directory must be under the parent of the canonical
source root; the sibling layout in LOCAL_BUILD.md satisfies this contract. The shared
`TestDirectory` helper retains fixtures. Typography uses Avalonia's headless window host
with **real Skia drawing/font tables**, not drawing stubs. It checks nonzero glyphs and
shaped runs for SC/TC, mixed Latin, punctuation, mono, icons and emoji, and exercises the
FluentAvalonia 3 `FANavigationView` without the old accessibility workaround. Actual
installed-app GUI/settings persistence and native/signature/install checks remain separate
acceptance steps; neither these tests nor prior window creation complete visible GUI acceptance.
