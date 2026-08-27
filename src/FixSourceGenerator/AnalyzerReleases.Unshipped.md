### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
FIX009 | FixSourceGenerator | Error | Invalid attribute value (e.g. non-integer `number`/`major`/`minor`).
FIX010 | FixSourceGenerator | Error | [FixView] target message not found in any loaded schema.
FIX011 | FixSourceGenerator | Error | [FixView] struct is not declared as a `partial ref struct`.
FIX012 | FixSourceGenerator | Error | FixView property does not match any field of the target message.
FIX013 | FixSourceGenerator | Error | [FixField] override references an unknown field.
FIX014 | FixSourceGenerator | Error | FixView property type incompatible with the FIX field type.
FIX015 | FixSourceGenerator | Error | Multiple FixView properties target the same field.
