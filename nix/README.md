* Using the flake
  - Ensure nix profile is enabled
  - nix run github:<githubuser>/<reponame>/<branch> (For ephemeral execution)
  - nix profile install github:<githubuser>/<reponame>/<branch> (To install)

* Updating the flake for new SubTubular version
  1. Change the version number in flake.nix
  2. Try to build `nix build -vv .\#default`
  3. Build will fail due to mismatching hash and will list the new hash
  4. Update flake.nix with the new hash
  5. Run `dotnet restore` in root directory to update the packages.json.lock files.
  6. Re-build to recreate flake.lock.
  7. Commit the changes (flake.nix, flake.lock, packages.json.lock [2x]) to git
