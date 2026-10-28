# XML delivery format

XML is an alternative physical format for every file source (the 8 data sources and `delivery_manifest`). Field names, required/optional, types and value rules are exactly those of [input-contracts.md](input-contracts.md) and the CSV columns: the XML adapter only converts records into the same `column name -> text` dictionary and calls the shared `RowMapper`, which is the only place with field semantics. An XML record and a CSV row with the same values therefore produce the identical canonical record.

## Layout

```xml
<?xml version="1.0" encoding="UTF-8"?>
<delivery source="transfer_instructions" schemaVersion="1.0" businessDate="2026-10-27">
  <record>
    <source_row_id>SRC-001</source_row_id>
    <instruction_id>TRF-1</instruction_id>
    <amount>100.50</amount>
    <currency>GBP</currency>
    <external_reference/>
    ...
  </record>
  <record>...</record>
</delivery>
```

- Root `<delivery>` with the required attributes `source` (manifest source name), `schemaVersion` and `businessDate` (`yyyy-MM-dd`). They must match the manifest entry of the delivery; a mismatch fails the file.
- One `<record>` per row; one child **element** per column, named exactly like the CSV column (no attributes on column elements).
- Amounts: the value is a decimal string in the amount element and the ISO 4217 code is a separate `<currency>` element (the same two columns as CSV). `<amount currency="GBP">` is not supported (rejected by the XSD).
- Blank optional values: an empty element (`<external_reference/>`) and an absent element are equivalent. A blank required value is `REQUIRED_FIELD_MISSING`.
- `delivery_manifest`: optional `<declared_total_gbp>` style elements, one per lower-case ISO 4217 code.
- Encoding follows the BOM / XML declaration (UTF-8 default, UTF-16, windows-1250, ...). There is no encoding option.
- Namespace: none by default. `XmlSourceOptions.Namespace` makes `delivery`/`record`/columns live in one namespace (default namespace declaration on the root); the XSD is then generated for it at start-up.
- Whitespace between elements is formatting and ignored. Whitespace inside a value is part of the value and never trimmed. CDATA and character references are decoded. Comments and processing instructions are ignored.

## XSD

One XSD per source and schema version is generated from the shared `ISourceSchema` column specs by `XsdGenerator` and shipped as an embedded resource (`Proofly.Connectors/Xml/Xsd/<source>.<version>.xsd`). A test fails when an embedded file differs from the generator output; to refresh after a column change run `PROOFLY_REGEN_XSD=1 dotnet test --filter XsdGeneration` (then run again without the variable).

The XSD is structural and lexical: element names, required (`minOccurs`), max lengths, enumerations, and the *shape* of dates, timestamps (offset required), integers, currencies (`[A-Z]{3}`) and amounts (at most 6 fraction digits). Typed elements accept an empty value so a blank required value is reported by the mapper. Calendar validity, ISO 4217 membership, sign rules and cross-field rules stay in the mapper.

### Customer-supplied XSD

`XmlSourceOptions.CustomSchemaPath` points to an additional XSD that must declare the global element `delivery`. The document is validated against it in addition to the built-in XSD (both in the same single pass). External includes/imports are not resolved. A missing or invalid XSD is a configuration error at start-up.

## Errors

| Situation | Result |
|---|---|
| Not well-formed, DOCTYPE/DTD present, invalid encoding, value > 1,000,000 characters | file-level: `SourceFormatException` with line and position |
| Wrong root, wrong `source`, unsupported or mismatching `schemaVersion`, wrong `businessDate`, wrong namespace, extra root attribute, a child of the root that is not `<record>` | file-level: `SchemaValidation.Fail` (validate) / `SourceFormatException` (parse), with line and position |
| Required element absent (XSD) | row rejected `REQUIRED_FIELD_MISSING` with the field, line and raw excerpt; file continues |
| Any other XSD violation in a record (built-in or custom): bad enum, lexical shape, too long, unknown/duplicate/nested element, attribute, stray text | row rejected `INVALID_VALUE` with the XSD message, line/position and raw excerpt; file continues |
| Passes the XSD but fails value semantics | row rejected by the mapper with its reason (same as CSV) |

The control amount of a rejected row is taken from the mapper when amount and currency are themselves valid, so declared totals still reconcile. Row numbers are 1-based record ordinals.

## Security and scale

- `DtdProcessing.Prohibit` and no resolver: any `<!DOCTYPE>` is rejected before entities are read (XXE, external DTDs, parameter entities and entity-expansion "billion laughs" cannot run). `xsi:schemaLocation` hints are ignored; nothing is fetched.
- Streaming `XmlReader` with `XmlSchemaValidator`; only the current record is in memory. 100,000 records are tested for constant memory.
