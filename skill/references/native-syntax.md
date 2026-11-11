# Native syntax trees

Use the native endpoint only when the normalized graph cannot answer an exact syntax
question. Tree shapes identify themselves with `parserId` and `format`; inspect these
fields instead of assuming a universal shape.

```bash
curl -s -X POST "http://localhost:<port>/api/syntax-tree" \
  -H "Content-Type: application/json" \
  -d '{"filePath":"/full/path/to/file"}'
```

Compact view and `maxDepth: 2` are the defaults. Compact nodes retain `kind`, a
`[start,length]` span, significant token text, children, and truncation/missing markers.
Request `"view":"full"` for parser-native debugging metadata.

If `truncated`, increase depth or narrow with `start` and
`length`, expressed as zero-based UTF-16 offsets. A 501 response means that worker does
not provide native trees; use file reading plus normalized graph queries.

A `.vue` file's tree is that of its script blocks, at their offsets in the file. Each
template expression is in the tree too, as a statement of its own wrapped in parentheses
or braces at the offset where the template writes it. The rest of the template and the
styles are absent.

Keep language- or parser-specific traversal advice here rather than in `SKILL.md`.
