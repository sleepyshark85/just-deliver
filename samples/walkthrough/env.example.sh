# The variables the walkthrough needs. Copy outside the repository, fill in, then `source` it from the repository root.
# jd itself needs nothing set: its state, the state's key and the records are in its data directory, the region is in each environment
# definition, and it runs as the Azure CLI sign-in (`az login`). Placeholders only: never commit real values.

# The walkthrough's own working directory: descriptors, release sets, catalog copy, policy variants.
export JD_WALK="<absolute path to a directory>"

# az signed in as the deployer in its own config directory, apart from any personal login (as the Azure tests do).
export AZURE_CONFIG_DIR="$JD_WALK/az"

# jd from source, built once into the walkthrough directory (no restore warnings on every call); run from the repository root.
dotnet build src/jd.cli --configuration Release --nologo --verbosity quiet --output "$JD_WALK/jd"
jd() { "$JD_WALK/jd/jd" "$@"; }
