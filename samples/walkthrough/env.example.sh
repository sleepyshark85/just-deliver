# The variables jd and the walkthrough need. Copy outside the repository, fill in, then `source` it from the repository root.
# Placeholders only: never commit real values.

# The service principal that runs the commands (Pulumi's Azure provider and the records' identity).
export ARM_TENANT_ID="<tenant id>"
export ARM_CLIENT_ID="<deployer client id>"
export ARM_CLIENT_SECRET="<deployer client secret>"
export ARM_SUBSCRIPTION_ID="<subscription id>"

# The region of everything (or pass --region to jd env up).
export JD_REGION="<region>"

# Pulumi state: the same values on every run, or the stacks are lost.
export PULUMI_BACKEND_URL="file://<absolute path to an existing directory>"
export PULUMI_CONFIG_PASSPHRASE="<passphrase>"     # or PULUMI_CONFIG_PASSPHRASE_FILE
# Optional: export PULUMI_HOME=...  JD_SCRATCH_DIR=...

# The release records (the evidence): an existing directory.
export JD_RECORDS_DIR="<absolute path to an existing directory>"

# The walkthrough's own working directory: descriptors, release sets, catalog copy, policy variants.
export JD_WALK="<absolute path to an existing directory>"

# az signed in as the deployer in its own config directory, apart from any personal login (as the Azure tests do).
export AZURE_CONFIG_DIR="$JD_WALK/az"

# jd from source, run from the repository root.
jd() { dotnet run --project src/jd.cli -- "$@"; }
