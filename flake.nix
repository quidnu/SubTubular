{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
    flake-utils.url = "github:numtide/flake-utils";
    nuget-packageslock2nix = {
      url = "github:mdarocha/nuget-packageslock2nix";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs = { nixpkgs, flake-utils, nuget-packageslock2nix, ... }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = import nixpkgs { inherit system; };
      in
      {
        packages.default = pkgs.buildDotnetModule rec {
          pname = "SubTubular";
          version = "3.1.2";
          src = pkgs.fetchFromGitHub {
            owner = "h0lg";
            repo = "SubTubular";
            tag = "v${version}";
            hash = "sha256-KQBQlOP4b/Eg6Sn99VB1UJD4i62uu71vnDOh9/nOzmA=";
          };
          nugetDeps = nuget-packageslock2nix.lib {
            inherit system;
            name = "SubTubular";
            lockfiles = [
              ./SubTubular/packages.lock.json
              ./Tests/packages.lock.json
            ];
          };
          buildInputs = with pkgs; [
          ];

          postPatch = ''
    substituteInPlace SubTubular/SubTubular.csproj \
      --replace-fail "git describe --long --always --dirty --exclude=* --abbrev=8" "echo ${version}"
  '';
        };

        devShells.default = pkgs.mkShell {
          buildInputs = [
            pkgs.dotnetCorePackages.sdk_8_0
          ];
        };
      });
}