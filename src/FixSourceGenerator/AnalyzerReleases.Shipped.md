## Release 0.1.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
FIX001 | FixSourceGenerator | Error | Missing required attribute.
FIX002 | FixSourceGenerator | Error | Malformed schema file.
FIX003 | FixSourceGenerator | Warning | Unsupported schema construct.
FIX004 | FixSourceGenerator | Error | Duplicate definition in schema.
FIX005 | FixSourceGenerator | Error | Unresolved reference (field/component/group).
FIX006 | FixSourceGenerator | Warning | Unknown FIX field type, fallback to raw byte span.
FIX007 | FixSourceGenerator | Warning | Missing group counter (NUMINGROUP) field.
FIX008 | FixSourceGenerator | Error | Circular component reference.
