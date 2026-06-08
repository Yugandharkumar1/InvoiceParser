# RuleDefinitions

This folder contains one JSON file per carrier, storing that carrier's extraction rules.

**File naming:** `{carrierId}.rules.json`

Rules in these files are:
- Loaded at parse time instead of from the database
- Version-controlled alongside the application code
- Automatically deployed to any environment — rules never disappear when the database changes

## File format

```json
[
  {
    "Id": 1,
    "CarrierId": 5,
    "FieldName": "invoice_number",
    "RegexPattern": "Invoice\\s*#\\s*(\\d+)",
    "FieldType": "string",
    "TargetTable": "t_invoice",
    "SortOrder": 10,
    "IsActive": true,
    "ConditionType": "regex_match",
    "ConditionSource": "line_text",
    "TransformationsJson": null
  }
]
```

## Managing rules

Use the **Extraction Rules** section in the application UI to create, edit, or delete rules.
The UI writes directly to these files — no database interaction required.

Do **not** edit the JSON files manually while the application is running, as concurrent
writes may cause issues. Stop the application first or use the UI.
