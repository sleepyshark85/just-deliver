# Landing Zone Definition — Schema Validation

The landing zone definition uses **JSON Schema** to validate YAML files. JSON Schema is a standard, language-independent way to describe and validate JSON/YAML data.

---

## Schema File

**Location:** `definitions/landing-zone-definition.schema.json`

This file defines:
- ✅ Required vs optional fields
- ✅ Field types (string, number, array, object)
- ✅ Field formats (email, hostname, CIDR blocks)
- ✅ Allowed enum values
- ✅ Regex patterns for naming
- ✅ Min/max constraints

---

## Validation Tools

### 1. **Command Line — ajv-cli (fastest)**

Install:
```bash
npm install -g ajv-cli
```

Validate a single file:
```bash
ajv validate -s definitions/landing-zone-definition.schema.json \
             -d definitions/landing-zone-example.yaml
```

Validate all definitions:
```bash
ajv validate -s definitions/landing-zone-definition.schema.json \
             -d 'definitions/lz-*.yaml'
```

Exit codes:
- `0` = Valid
- `1` = Validation error

Output on error:
```
landing-zone-example.yaml invalid
[0].spec.subscriptions[0].name should match pattern "^sub-[a-z0-9-]+$"
```

### 2. **VS Code — JSON Schema Validation**

Add to `.vscode/settings.json`:
```json
{
  "yaml.schemas": {
    "definitions/landing-zone-definition.schema.json": "definitions/lz-*.yaml"
  }
}
```

Result: Inline errors and autocomplete in VS Code when editing YAML files.

### 3. **Python — jsonschema**

Install:
```bash
pip install jsonschema pyyaml
```

Create `validate-lz.py`:
```python
import json
import yaml
from jsonschema import validate, ValidationError

with open('definitions/landing-zone-definition.schema.json') as f:
    schema = json.load(f)

with open('definitions/landing-zone-example.yaml') as f:
    data = yaml.safe_load(f)

try:
    validate(instance=data, schema=schema)
    print("✅ Valid landing zone definition")
except ValidationError as e:
    print(f"❌ Invalid: {e.message}")
    print(f"   Path: {'.'.join(str(p) for p in e.path)}")
```

Run:
```bash
python validate-lz.py
```

### 4. **JavaScript/Node.js — ajv**

Install:
```bash
npm install ajv yaml
```

Create `validate-lz.js`:
```javascript
const Ajv = require('ajv');
const fs = require('fs');
const yaml = require('yaml');

const ajv = new Ajv();
const schema = JSON.parse(fs.readFileSync('definitions/landing-zone-definition.schema.json'));
const data = yaml.parse(fs.readFileSync('definitions/landing-zone-example.yaml', 'utf-8'));

const validate = ajv.compile(schema);
if (validate(data)) {
  console.log('✅ Valid landing zone definition');
} else {
  console.log('❌ Invalid:', validate.errors);
}
```

Run:
```bash
node validate-lz.js
```

### 5. **GitHub Actions — Automated Validation**

Create `.github/workflows/validate-lz.yml`:
```yaml
name: Validate Landing Zone Definitions

on: [push, pull_request]

jobs:
  validate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      
      - name: Install ajv-cli
        run: npm install -g ajv-cli
      
      - name: Validate all landing zone definitions
        run: |
          ajv validate -s definitions/landing-zone-definition.schema.json \
                       -d 'definitions/lz-*.yaml'
```

Result: Every PR validates landing zone definitions automatically.

---

## What Gets Validated

### Structural Validation
- ✅ Required fields present (apiVersion, kind, metadata, spec, etc.)
- ✅ Field types correct (string, number, array, etc.)
- ✅ No unexpected fields

### Format Validation
- ✅ Email format (`owner.email`)
- ✅ Hostname format (`identity.domain`)
- ✅ Date-time format (timestamps)

### Pattern Validation
- ✅ Naming conventions
  - `lz-team-a` (landing zone names)
  - `sub-team-a-dev` (subscription names)
  - `vnet-team-a-dev` (VNet names)
  - `grp-team-a-developers` (group names)
- ✅ CIDR blocks (e.g., `10.1.1.0/24`)

### Enum Validation
- ✅ Environment: must be `development`, `test`, or `production`
- ✅ Region: must be approved (westeurope, eastus, etc.)
- ✅ RBAC role: must be valid Azure role
- ✅ Data classification: Public, Internal, Confidential, Restricted

### Constraint Validation
- ✅ CostCenter format: must match `CC-[0-9]+`
- ✅ Budget: minimum $100, maximum $1,000,000
- ✅ At least one subscription required
- ✅ At least one VNet required per subscription
- ✅ At least one subnet required per VNet
- ✅ At least one access group required

---

## Example Validation Errors

### Error 1: Invalid Name Format
```yaml
metadata:
  name: "teamA"  # ❌ Should be "lz-team-a"
```

Error:
```
[0].metadata.name should match pattern "^lz-[a-z0-9-]+$"
```

### Error 2: Missing Required Field
```yaml
metadata:
  name: "lz-team-a"
  # ❌ Missing: displayName
```

Error:
```
[0].metadata should have required property 'displayName'
```

### Error 3: Invalid Environment Value
```yaml
subscriptions:
  - environment: "staging"  # ❌ Should be development, test, or production
```

Error:
```
[0].spec.subscriptions[0].environment should be equal to one of the allowed values
```

### Error 4: Invalid CIDR Block
```yaml
addressSpace: "10.1.1.0/33"  # ❌ /33 is invalid (max /32)
```

Error:
```
[0].spec.networking.vnets[0].addressSpace should match pattern "...(/[0-9]|[1-2][0-9]|3[0-2])$"
```

### Error 5: Invalid Email
```yaml
owner:
  email: "not-an-email"  # ❌ Invalid email format
```

Error:
```
[0].metadata.owner.email should match format "email"
```

---

## Integration with IDP

The IDP backend should:

1. **Accept** landing zone definition files (YAML)
2. **Validate** against JSON schema before processing
3. **Reject** with clear error messages if invalid
4. **Only proceed** to merging and provisioning if valid

Example API endpoint:
```
POST /api/landing-zones/validate
Content-Type: application/yaml

<yaml file content>

Response (valid):
{
  "valid": true,
  "message": "Landing zone definition is valid"
}

Response (invalid):
{
  "valid": false,
  "errors": [
    {
      "path": "metadata.name",
      "message": "should match pattern \"^lz-[a-z0-9-]+$\""
    }
  ]
}
```

---

## IDE Integration

### VS Code
1. Install **YAML** extension (redhat.vscode-yaml)
2. Add schema to `.vscode/settings.json`:
   ```json
   {
     "yaml.schemas": {
       "definitions/landing-zone-definition.schema.json": "definitions/lz-*.yaml"
     }
   }
   ```
3. Open any `lz-*.yaml` file — you'll see:
   - ✅ Syntax highlighting
   - ✅ Type hints and autocomplete
   - ✅ Real-time error squiggles
   - ✅ Documentation on hover

### JetBrains IDEs (IntelliJ, PyCharm, etc.)
1. Go to **Settings → Languages & Frameworks → Schemas and DTDs → JSON Schema Mappings**
2. Add new mapping:
   - Name: `Landing Zone Definition`
   - Schema: `definitions/landing-zone-definition.schema.json`
   - File path pattern: `definitions/lz-*.yaml`
3. Apply — same features as VS Code

---

## Schema Maintenance

When updating the schema:

1. **Test changes locally** with sample definitions
2. **Validate all existing definitions** pass new schema
3. **Bump schema version** if breaking changes:
   ```json
   "$id": "https://just-deliver.litmos.com/schemas/landing-zone-definition.schema.v2.json"
   ```
4. **Document breaking changes** in a CHANGELOG
5. **Update definitions** if they no longer conform

---

## Testing the Schema

Example test script (`test-schema.sh`):
```bash
#!/bin/bash

SCHEMA="definitions/landing-zone-definition.schema.json"

# Test cases
VALID_FILES=(
  "definitions/landing-zone-minimal-example.yaml"
  "definitions/landing-zone-example.yaml"
)

INVALID_FILES=(
  "tests/invalid-no-name.yaml"
  "tests/invalid-bad-cidr.yaml"
  "tests/invalid-role.yaml"
)

echo "Testing valid definitions..."
for file in "${VALID_FILES[@]}"; do
  if ajv validate -s "$SCHEMA" -d "$file" > /dev/null; then
    echo "✅ $file"
  else
    echo "❌ $file should be valid!"
    exit 1
  fi
done

echo ""
echo "Testing invalid definitions..."
for file in "${INVALID_FILES[@]}"; do
  if ! ajv validate -s "$SCHEMA" -d "$file" > /dev/null 2>&1; then
    echo "✅ $file (correctly rejected)"
  else
    echo "❌ $file should be invalid!"
    exit 1
  fi
done

echo ""
echo "All tests passed! ✅"
```

Run:
```bash
bash test-schema.sh
```

---

## Quick Reference

| Tool | Best For | Install |
|------|----------|---------|
| **ajv-cli** | CLI validation, CI/CD | `npm install -g ajv-cli` |
| **VS Code** | IDE hints and autocomplete | Install YAML extension |
| **jsonschema** | Python projects | `pip install jsonschema pyyaml` |
| **ajv** | Node.js/JavaScript | `npm install ajv yaml` |
| **GitHub Actions** | Automated PR validation | Built-in |

---

## Next Steps

1. **Generate test cases** for invalid YAML (wrong formats, missing fields)
2. **Integrate schema validation** into IDP API
3. **Add schema validation** to CI/CD pipeline
4. **Publish schema** to schema registry (optional, for discovery)
5. **Document schema** to teams (already done in LANDING-ZONE-DEFINITIONS.md)
