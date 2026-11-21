# Releasing

The packages are released together and share one version, set in `src/Directory.Build.props` (`VersionPrefix` and `VersionSuffix`). Pushing a tag `v<version>` makes CI build, test and publish them to nuget.org.

## Once

- Add a `NUGET_API_KEY` secret to the repository's `nuget` environment on GitHub, with an API key from nuget.org that can push `Naqel.Core` and `Naqel.Http`.

## Each release

1. Check that `main` is green in CI, on Linux and Windows.
2. Run the soak test on both runtimes and check that it reports no wrong results:

   ```bash
   dotnet run --project tests/Naqel.Soak -c Release -f net10.0 -- --minutes 10
   dotnet run --project tests/Naqel.Soak -c Release -f net48 -- --minutes 10
   ```

3. Set the version in `src/Directory.Build.props` and give its section in `CHANGELOG.md` the release date.
4. Move every line of each project's `PublicAPI.Unshipped.txt` into its `PublicAPI.Shipped.txt`, below the `#nullable enable` line, so later changes to that API show up as changes.
5. Commit, push `main`, then tag and push the tag:

   ```bash
   git tag v0.1.0-preview.1
   git push origin v0.1.0-preview.1
   ```

6. CI checks that the tag matches the package version, then publishes. Packages on nuget.org can be unlisted but never deleted, so a broken release is fixed with a new version.
